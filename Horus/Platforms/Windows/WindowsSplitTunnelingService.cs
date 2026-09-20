using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Platforms.Windows.Wfp;
using Horus.Protocols;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Horus.Platforms.Windows
{
    /// <summary>
    /// Per-process split tunneling on Windows.
    ///
    /// <para><b>Why it takes three mechanisms.</b> Windows has no per-application routing.
    /// WFP can decide whether a process may use an interface but cannot move it to another
    /// one — redirects live at <c>ALE_*_REDIRECT</c>, layers only a kernel callout may attach
    /// to. So the work is split: the <b>route table</b> sets the default for everyone,
    /// <b>WinDivert</b> reports which process is opening which connection, and a host route
    /// per destination moves that traffic off the default. WFP is the guard rail.</para>
    ///
    /// <para><b>Both modes reduce to the same operation.</b> The tunnel holds the default
    /// route, so everything is tunnelled unless a host route says otherwise. Blacklist steers
    /// the <i>selected</i> apps' destinations onto the physical path; Whitelist steers the
    /// <i>unselected</i> ones. One mechanism, one inversion.</para>
    ///
    /// <para><b>The limitation, stated plainly.</b> A host route is per destination, not per
    /// process. If a steered app and a tunnelled app talk to the same address, both follow
    /// the route. For "the bank goes direct, everything else through the VPN" that is
    /// harmless. For Whitelist it would be a leak, which is why the selected apps are also
    /// blocked on the physical interface: if one ever rides a route installed for another
    /// process, the connection fails instead of leaving unprotected.</para>
    ///
    /// <para>Requires WinDivert (<c>WinDivert.dll</c> + <c>WinDivert64.sys</c>) next to the
    /// app; without it <see cref="IsSupported"/> is false and Settings hides the screen
    /// rather than offering switches that do nothing.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class WindowsSplitTunnelingService : ISplitTunnelingService
    {
        /// <summary>Must match the adapter alias <see cref="WindowsVpnService"/> creates.</summary>
        private const string TunAlias = "Horus";

        /// <summary>
        /// A ceiling on host routes. The input is "every destination every watched process
        /// talks to", and a browser alone produces hundreds. Past this the steering stops
        /// rather than filling the route table; the tunnel keeps working and the unsteered
        /// traffic is over-protected, never leaked.
        /// </summary>
        private const int MaxSteeredRoutes = 256;

        private readonly string _nativeDir;
        private readonly string _routeJournal;
        private readonly List<string> _selected = [];
        private SplitTunnelingMode _mode = SplitTunnelingMode.Disabled;

        private WfpEngine? _wfp;
        private ProcessFlowWatcher? _watcher;

        /// <summary>Destinations already given a host route, so a busy process is not re-run.</summary>
        private readonly HashSet<string> _steered = new(StringComparer.Ordinal);

        /// <summary>Executables already carrying a leak guard, keyed by full path.</summary>
        private readonly HashSet<string> _guarded = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>pid → image path. Cleared with the rest; pid reuse is bounded by a session.</summary>
        private readonly Dictionary<uint, string?> _pidPaths = [];

        private readonly object _gate = new();

        private string? _gateway;
        private int _ifIndex;

        /// <summary>
        /// Only when the WinDivert driver is actually present. <c>ApplyAsync</c> returns
        /// silently without it, so claiming support would give the user a screen of
        /// switches that quietly do nothing.
        /// </summary>
        public bool IsSupported => File.Exists(Path.Combine(_nativeDir, "WinDivert.dll"));

        public SplitTunnelingMode Mode
        {
            get => _mode;
            set
            {
                _mode = value;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public IReadOnlyList<string> SelectedEntries => _selected.AsReadOnly();

        /// <summary>Config-forced bypass is an Android concept (package names); nothing to do here.</summary>
        public IReadOnlyList<string> AlwaysDirectEntries => [];

        public event EventHandler? SelectionChanged;

        /// <summary>Windows process entries carry no icons.</summary>
        public Task LoadIconsAsync(
            IReadOnlyList<AppOrProcessEntry> entries,
            Action<AppOrProcessEntry> onReady,
            CancellationToken ct = default) => Task.CompletedTask;

        public WindowsSplitTunnelingService()
        {
            _nativeDir = Path.Combine(AppContext.BaseDirectory, "Resources", "Native");
            _routeJournal = Path.Combine(FileSystem.AppDataDirectory, "split-routes.txt");
        }

        public async Task<IReadOnlyList<AppOrProcessEntry>> GetAvailableEntriesAsync()
        {
            return await Task.Run(() =>
            {
                var result = new List<AppOrProcessEntry>();
                try
                {
                    foreach (var proc in Process.GetProcesses())
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(proc.MainModule?.FileName)) continue;
                            var exe = Path.GetFileName(proc.MainModule.FileName);
                            if (string.IsNullOrEmpty(exe)) continue;

                            if (!result.Any(e => e.Id.Equals(exe, StringComparison.OrdinalIgnoreCase)))
                            {
                                result.Add(new AppOrProcessEntry
                                {
                                    Id = exe,
                                    DisplayName = proc.MainWindowTitle is { Length: > 0 } t
                                        ? t : Path.GetFileNameWithoutExtension(exe),
                                    // Deliberately null: IconPath feeds an Image source, and
                                    // an .exe path there renders as a broken image. Extracting
                                    // the real icon would need a Win32 shell call.
                                    IconPath = null,
                                    IsSystem = IsSystemProcess(proc.MainModule?.FileName)
                                });
                            }
                        }
                        catch { /* Access denied to some system processes */ }
                        finally { proc.Dispose(); }
                    }
                }
                catch { }

                return result.OrderBy(e => e.IsSystem).ThenBy(e => e.DisplayName).ToList();
            });
        }

        public Task SetSelectedEntriesAsync(IEnumerable<string> entries)
        {
            _selected.Clear();
            _selected.AddRange(entries);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        // ── Apply / stop ─────────────────────────────────────────────────────

        public async Task ApplyAsync()
        {
            await StopAsync();

            // Routes from a session that died without unwinding. They live on the physical
            // interface and survive everything, so a stale one keeps sending that destination
            // out of the tunnel forever, invisibly.
            await SweepJournalAsync();

            if (!IsSupported) return;
            if (_mode == SplitTunnelingMode.Disabled || _selected.Count == 0) return;

            // The physical next hop has to be read while the answer is still the physical
            // one. Asked after the tunnel took the default route, "how do I reach the
            // internet" answers "through the tunnel", and every host route built from it
            // would point back into what it is meant to escape.
            if (!ResolvePhysicalPath())
            {
                Debug.WriteLine("[Horus] split: no physical gateway; steering disabled");
                return;
            }

            try
            {
                if (_mode == SplitTunnelingMode.Whitelist)
                {
                    _wfp = new WfpEngine();
                    _wfp.Open();
                    GuardRunningSelection();
                }

                _watcher = new ProcessFlowWatcher(OnFlow);
                _watcher.Start();
            }
            catch (Exception ex)
            {
                // Half-configured is worse than off: a watcher with no guard, or a guard with
                // no watcher, both send traffic somewhere nobody intended.
                Debug.WriteLine($"[Horus] split: {ex.Message}");
                await StopAsync();
            }
        }

        public async Task StopAsync()
        {
            _watcher?.Dispose();
            _watcher = null;

            _wfp?.Dispose();
            _wfp = null;

            lock (_gate)
            {
                _guarded.Clear();
                _pidPaths.Clear();
            }

            await RemoveSteeredRoutesAsync();
        }

        // ── Steering ─────────────────────────────────────────────────────────

        private void OnFlow(FlowEvent flow)
        {
            // A host route for a LAN or loopback address would override the on-link route the
            // machine already has, breaking the printer and the NAS in a way that outlives
            // the VPN being switched off.
            if (LocalNetworks.IsDirectRange(flow.Remote)) return;

            var path = ResolveProcessPath(flow.ProcessId);
            if (path is null) return;

            var selected = _selected.Any(e =>
                e.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));

            // A selected app in Whitelist needs no route — it uses the default, which is the
            // tunnel — but it does need the guard, and it may have started after Apply ran.
            if (_mode == SplitTunnelingMode.Whitelist && selected)
            {
                Guard(path);
                return;
            }

            // Blacklist steers what was picked; Whitelist steers everything that was not.
            var wantsDirect = _mode == SplitTunnelingMode.Blacklist ? selected : !selected;
            if (!wantsDirect) return;

            _ = SteerAsync(flow.Remote);
        }

        private async Task SteerAsync(IPAddress destination)
        {
            var key = destination.ToString();

            lock (_gate)
            {
                if (_steered.Count >= MaxSteeredRoutes) return;
                if (!_steered.Add(key)) return;
            }

            try
            {
                await AppendJournalAsync(key);
                await RunRouteAsync($"add {key} mask 255.255.255.255 {_gateway} metric 1 if {_ifIndex}");
            }
            catch (Exception ex)
            {
                lock (_gate) _steered.Remove(key);
                Debug.WriteLine($"[Horus] split: route add {key}: {ex.Message}");
            }
        }

        private async Task RemoveSteeredRoutesAsync()
        {
            string[] pending;
            lock (_gate)
            {
                pending = [.. _steered];
                _steered.Clear();
            }

            foreach (var key in pending)
            {
                try { await RunRouteAsync($"delete {key}"); }
                catch (Exception ex) { Debug.WriteLine($"[Horus] split: route delete {key}: {ex.Message}"); }
            }

            TryDeleteJournal();
        }

        // ── Crash recovery ───────────────────────────────────────────────────

        /// <summary>
        /// Every steered destination is written down before its route is added, so a process
        /// that dies mid-session leaves a list of what to undo. WFP needs nothing like this —
        /// a dynamic session cleans itself up — but routes are ordinary system state and
        /// nothing removes them on our behalf.
        /// </summary>
        private async Task AppendJournalAsync(string destination)
        {
            try { await File.AppendAllTextAsync(_routeJournal, destination + Environment.NewLine); }
            catch (Exception ex) { Debug.WriteLine($"[Horus] split: journal: {ex.Message}"); }
        }

        private async Task SweepJournalAsync()
        {
            if (!File.Exists(_routeJournal)) return;

            string[] lines;
            try { lines = await File.ReadAllLinesAsync(_routeJournal); }
            catch { return; }

            foreach (var line in lines)
            {
                if (!IPAddress.TryParse(line.Trim(), out var ip)) continue;
                try { await RunRouteAsync($"delete {ip}"); } catch { }
            }

            TryDeleteJournal();
        }

        private void TryDeleteJournal()
        {
            try { if (File.Exists(_routeJournal)) File.Delete(_routeJournal); }
            catch { }
        }

        // ── Leak guard ───────────────────────────────────────────────────────

        /// <summary>
        /// Blocks the selected applications on the <b>physical</b> interface, so a whitelisted
        /// app cannot ride a host route installed for some other process. Without it the
        /// per-destination nature of the routes would be a leak rather than an inconvenience.
        /// </summary>
        private void GuardRunningSelection()
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (path is null) continue;

                    if (_selected.Any(e => e.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))
                        Guard(path);
                }
                catch { /* access denied is ordinary for system processes */ }
                finally { proc.Dispose(); }
            }
        }

        private void Guard(string exePath)
        {
            if (_wfp is null) return;

            lock (_gate)
            {
                if (!_guarded.Add(exePath)) return;
            }

            var alias = PhysicalAlias();
            if (alias.Length == 0) return;

            var luid = WfpEngine.LookupInterfaceLuid(alias);
            if (luid is null) return;

            try { _wfp.BlockApp(exePath, luid.Value); }
            catch (Exception ex) { Debug.WriteLine($"[Horus] split: guard {exePath}: {ex.Message}"); }
        }

        // ── Platform lookups ─────────────────────────────────────────────────

        /// <summary>The next hop and interface currently carrying this machine to the internet.</summary>
        private bool ResolvePhysicalPath()
        {
            var route = new MibIpForwardRow();
            var probe = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0);

            if (GetBestRoute(probe, 0, ref route) != 0) return false;

            _gateway = new IPAddress(BitConverter.GetBytes(route.ForwardNextHop)).ToString();
            _ifIndex = (int)route.ForwardIfIndex;
            return _ifIndex != 0;
        }

        private string PhysicalAlias()
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.Name.Equals(TunAlias, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (nic.GetIPProperties().GetIPv4Properties()?.Index == _ifIndex) return nic.Name;
                }
                catch { /* an adapter without IPv4 properties is simply not the one */ }
            }

            return string.Empty;
        }

        /// <summary>
        /// Image path for a pid, cached. The cache matters: the watcher fires on every
        /// connection the machine makes, and opening a process handle per event would cost
        /// more than the steering it feeds.
        /// </summary>
        private string? ResolveProcessPath(uint pid)
        {
            lock (_gate)
            {
                if (_pidPaths.TryGetValue(pid, out var cached)) return cached;
            }

            string? path = null;
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);

            if (handle != IntPtr.Zero)
            {
                try
                {
                    var buffer = new StringBuilder(1024);
                    var size = buffer.Capacity;
                    if (QueryFullProcessImageName(handle, 0, buffer, ref size))
                        path = buffer.ToString();
                }
                finally { CloseHandle(handle); }
            }

            lock (_gate)
            {
                // Failures are cached too: a process we cannot open will not become openable,
                // and retrying on every connection it makes is pure cost.
                _pidPaths[pid] = path;
            }

            return path;
        }

        private static bool IsSystemProcess(string? path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return path.StartsWith(winDir, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task RunRouteAsync(string arguments)
        {
            using var proc = Process.Start(new ProcessStartInfo("route", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("route did not start");

            await proc.WaitForExitAsync();
        }

        // ── Interop ──────────────────────────────────────────────────────────

        private const uint ProcessQueryLimitedInformation = 0x1000;

        [StructLayout(LayoutKind.Sequential)]
        private struct MibIpForwardRow
        {
            public uint ForwardDest, ForwardMask, ForwardPolicy, ForwardNextHop, ForwardIfIndex;
            public uint ForwardType, ForwardProto, ForwardAge, ForwardNextHopAS;
            public uint ForwardMetric1, ForwardMetric2, ForwardMetric3, ForwardMetric4, ForwardMetric5;
        }

        [DllImport("iphlpapi.dll")]
        private static extern int GetBestRoute(uint dwDestAddr, uint dwSourceAddr, ref MibIpForwardRow pBestRoute);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(
            IntPtr process, uint flags, StringBuilder exeName, ref int size);
    }
}
