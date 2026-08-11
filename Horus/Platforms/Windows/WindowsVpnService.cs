using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Protocols;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Horus.Platforms.Windows
{
    /// <summary>
    /// The Windows TUN bridge: the same hev-socks5-tunnel that carries the tunnel on
    /// Android, but hosted as a <b>child process</b> rather than in-process.
    ///
    /// That difference is forced, not stylistic. hev's Windows port is guarded by
    /// <c>#if defined(__MSYS__)</c> — it is a Cygwin build and links against
    /// <c>msys-2.0.dll</c>. Loading it into this process works right up until the first
    /// real call: <c>hev_socks5_tunnel_main_from_str</c> dies with an access violation,
    /// because the Cygwin runtime is not initialised for a thread the CLR created. Out of
    /// process it is entirely well behaved.
    ///
    /// The other structural difference from Android: there is no TUN fd to hand over. Given
    /// <c>tun_fd = -1</c> hev creates the wintun adapter itself and assigns the addresses
    /// from its own YAML, so this class does not create or own an adapter — it waits for
    /// hev's to appear, then does the two things hev deliberately leaves alone: routes and
    /// DNS.
    ///
    /// Required next to <c>Horus.exe</c> in <c>Resources\Native\</c>:
    /// <c>hev-socks5-tunnel.exe</c>, <c>msys-2.0.dll</c>, <c>wintun.dll</c>. All three in
    /// the same directory — hev resolves wintun with
    /// <c>LOAD_LIBRARY_SEARCH_APPLICATION_DIR</c>, which means the directory of the
    /// <i>exe</i>, not of this app.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class WindowsVpnService : IVpnPlatformService
    {
        private const string HevExeName = "hev-socks5-tunnel.exe";
        private const string TunAlias = HevTunnelConfig.TunnelName;

        /// <summary>How long hev gets to bring the adapter up before we call it a failure.</summary>
        private static readonly TimeSpan AdapterTimeout = TimeSpan.FromSeconds(20);

        private readonly string _nativeDir;

        private TunnelState _state = TunnelState.Stopped;
        private Process? _hev;
        private int _tunIfIndex;

        /// <summary>Routes this class installed, newest first, for exact teardown.</summary>
        private readonly List<string> _routes = [];

        /// <summary>Set while we are the ones killing hev, so its exit is not reported as a fault.</summary>
        private volatile bool _stopping;

        /// <summary>
        /// Holds the bridge for the app's lifetime. Static and never disposed on purpose:
        /// the guarantee comes from the kernel closing the handle when this process ends,
        /// so the one thing that must not happen is releasing it early.
        /// </summary>
        private static ChildProcessJob? _job;

        public WindowsVpnService()
        {
            _nativeDir = Path.Combine(AppContext.BaseDirectory, "Resources", "Native");

            // A previous run may have died before the job object existed, or been killed in
            // a way that left the bridge behind. Clearing it here means the fix for a
            // wedged machine is "start Horus", not "reboot".
            try { KillOrphanedHev(); }
            catch (Exception ex) { Debug.WriteLine($"[Horus] orphan sweep: {ex.Message}"); }

            // The job object undoes the bridge and, with it, everything bound to the tunnel
            // interface. It cannot undo what lives elsewhere: a name policy and the bypass
            // routes on the physical interface both survive a crash. Swept in the background
            // because nothing here is waiting on it.
            _ = Task.Run(async () =>
            {
                try { await ClearDnsPolicyAsync(); }
                catch (Exception ex) { Debug.WriteLine($"[Horus] NRPT sweep: {ex.Message}"); }

                try { await SweepAbandonedRoutesAsync(); }
                catch (Exception ex) { Debug.WriteLine($"[Horus] route sweep: {ex.Message}"); }
            });
        }

        private string HevExe => Path.Combine(_nativeDir, HevExeName);

        public bool IsSupported =>
            File.Exists(HevExe) &&
            File.Exists(Path.Combine(_nativeDir, "wintun.dll")) &&
            File.Exists(Path.Combine(_nativeDir, "msys-2.0.dll"));

        public TunnelState CurrentState => _state;

        public event EventHandler<TunnelStateChangedEventArgs>? TunnelStateChanged;

        /// <summary>
        /// Reports whether this process can create a TUN adapter. Creating a wintun adapter
        /// requires administrator rights.
        ///
        /// It does <b>not</b> relaunch elevated. The previous version spawned a new elevated
        /// instance and called <c>Environment.Exit(0)</c> — from inside the connect flow,
        /// after the server config had already been fetched. From the user's side the app
        /// simply vanished mid-click. Reporting false lets the caller show a real message
        /// and leaves the running app alone.
        /// </summary>
        public Task<bool> RequestPermissionsAsync()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);

            return Task.FromResult(
                principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
        }

        public async Task StartTunnelAsync(TunnelOptions options, CancellationToken ct = default)
        {
            SetState(TunnelState.Starting);
            _stopping = false;

            try
            {
                if (!IsSupported)
                    throw new PlatformNotSupportedException(
                        $"Не хватает файлов туннеля в {_nativeDir}: нужны {HevExeName}, " +
                        "msys-2.0.dll и wintun.dll. Ядро при этом работает — SOCKS5-прокси " +
                        $"доступен на 127.0.0.1:{XrayConfig.DefaultSocksPort}.");

                // Resolve the off-tunnel path to the node *before* anything is routed into
                // the tunnel, otherwise this lookup answers through the tunnel it is meant
                // to bypass.
                var bypass = ResolveBypassRoutes(options.BypassIps);

                // Refusing here is the whole point. With a default route into the tunnel
                // and no host route for the node, the core's own socket to the node is
                // carried by the tunnel it is feeding: the machine loses connectivity and
                // the UI reports success. A clear failure is strictly better.
                var nodeRouted = options.NodeAddress is { } node &&
                                 bypass.Any(b => b.Address == node);

                if (options.AllTraffic && !nodeRouted)
                    throw new InvalidOperationException(
                        "Не удалось построить маршрут в обход туннеля до узла " +
                        $"({options.NodeAddress ?? "адрес неизвестен"}) — подключение " +
                        "зациклилось бы на себе. Обычно это значит, что имя узла не " +
                        "разрешилось в IP перед подключением.");

                KillOrphanedHev();
                await StartHevAsync(options.SocksPort, ct);
                _tunIfIndex = await WaitForAdapterAsync(ct);

                // wintun reports 65535; anything above hev's own mtu is dropped by its
                // reader, so the IP subinterface has to be told the real figure.
                await RunAsync("netsh", $"interface ipv4 set subinterface {_tunIfIndex} mtu={HevTunnelConfig.Mtu} store=active");
                await RunAsync("netsh", $"interface ipv6 set subinterface {_tunIfIndex} mtu={HevTunnelConfig.Mtu} store=active");

                // Order matters: the node must have its own way out before the default
                // route starts pointing at the tunnel.
                foreach (var (address, gateway, ifIndex) in bypass)
                    await AddRouteAsync(address, "255.255.255.255", gateway, ifIndex);

                if (options.AllTraffic)
                {
                    // Two halves rather than 0.0.0.0/0: they are more specific than the
                    // physical default route, so they win without deleting it. Teardown is
                    // then just removing what we added, and a crash leaves the machine
                    // online instead of with no default route at all.
                    await AddRouteAsync("0.0.0.0", "128.0.0.0", options.TunAddress, _tunIfIndex);
                    await AddRouteAsync("128.0.0.0", "128.0.0.0", options.TunAddress, _tunIfIndex);
                }

                await SetSystemDnsAsync(options.DnsServers);

                SetState(TunnelState.Started);
            }
            catch
            {
                // Never leave a half-built tunnel: routes pointing at an adapter that is
                // about to disappear would take the machine offline.
                await TeardownAsync();
                SetState(TunnelState.Error);
                throw;
            }
        }

        public async Task StopTunnelAsync()
        {
            if (_state == TunnelState.Stopped) return;

            SetState(TunnelState.Stopping);
            await TeardownAsync();
            SetState(TunnelState.Stopped);
        }

        public Task ApplyRoutingRulesAsync(IEnumerable<RoutingRule> rules) =>
            // Per-destination policy is xray's job, from the generated config. Per-process
            // policy would be WinDivert; see WindowsSplitTunnelingService.
            Task.CompletedTask;

        public Task SetDnsAsync(string[] dnsServers) => SetSystemDnsAsync(dnsServers);

        /// <summary>
        /// <c>[tx_packets, tx_bytes, rx_packets, rx_bytes]</c>, matching the shape
        /// <c>hev_socks5_tunnel_stats</c> produces on Android — "tx" is what the device
        /// pushed into the tunnel, i.e. upload.
        ///
        /// The counters come from the adapter rather than from hev: its stats API is only
        /// reachable in-process, and this host runs it out of process.
        /// </summary>
        public long[] GetTunnelStats()
        {
            if (!TryGetTunRow(out var row)) return [0, 0, 0, 0];

            return [(long)row.OutUcastPkts, (long)row.OutOctets,
                    (long)row.InUcastPkts, (long)row.InOctets];
        }

        // ── hev child process ────────────────────────────────────────────────

        /// <summary>
        /// Clears a bridge left running by a previous crash. Its adapter still carries the
        /// name the new one wants, and <see cref="WaitForAdapterAsync"/> would happily
        /// return the dead one's interface index — a tunnel wired to a process nothing
        /// controls. Scoped to copies started from our own directory so an unrelated
        /// installation of the same tool is left alone.
        /// </summary>
        private void KillOrphanedHev()
        {
            foreach (var p in Process.GetProcessesByName("hev-socks5-tunnel"))
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, HevExe, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(5000);
                    }
                }
                catch { /* gone, or not ours to inspect */ }
                finally { p.Dispose(); }
            }
        }

        private async Task StartHevAsync(int socksPort, CancellationToken ct)
        {
            var logFile = DiagnosticPaths.HevLog;
            DiagnosticPaths.Truncate(logFile);

            var configPath = Path.Combine(FileSystem.CacheDirectory, "hev-tunnel.yml");
            await File.WriteAllTextAsync(
                configPath,
                HevTunnelConfig.Build(logFile, HevTunnelConfig.DefaultLogLevel, socksPort),
                ct);

            var psi = new ProcessStartInfo(HevExe, $"\"{configPath}\"")
            {
                // The exe's own directory: hev loads wintun.dll with
                // LOAD_LIBRARY_SEARCH_APPLICATION_DIR and imports msys-2.0.dll normally.
                WorkingDirectory = _nativeDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Не удалось запустить {HevExeName}.");

            // Before anything else the bridge might do, put it somewhere it cannot outlive
            // us. If that fails, kill it rather than proceed: an unsupervised bridge holding
            // the default routes is how a crash turns into a machine with no internet.
            try
            {
                _job ??= new ChildProcessJob();
                _job.Assign(proc);
            }
            catch
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                proc.Dispose();
                throw;
            }

            proc.EnableRaisingEvents = true;
            proc.Exited += OnHevExited;
            _hev = proc;
        }

        /// <summary>
        /// hev died on its own — a wintun failure, a missing driver, or a crash. The tunnel
        /// is gone with it, so say so instead of leaving the UI claiming to be protected.
        /// </summary>
        private void OnHevExited(object? sender, EventArgs e)
        {
            if (_stopping) return;
            SetState(TunnelState.Error, "Процесс туннеля завершился неожиданно.");
        }

        private async Task<int> WaitForAdapterAsync(CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + AdapterTimeout;

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (_hev is { HasExited: true })
                    throw new InvalidOperationException(
                        $"{HevExeName} завершился с кодом {_hev.ExitCode}. " +
                        HevLogTail());

                if (TryGetTunRow(out var row) && row.OperStatus == IfOperStatusUp)
                    return (int)row.InterfaceIndex;

                await Task.Delay(250, ct);
            }

            throw new TimeoutException(
                $"Адаптер \"{TunAlias}\" не появился за {AdapterTimeout.TotalSeconds:0} с. " +
                "Обычно это отсутствующий или заблокированный wintun.dll, либо запуск без " +
                "прав администратора. " + HevLogTail());
        }

        // ── Live interface queries ───────────────────────────────────────────
        //
        // Deliberately not System.Net.NetworkInformation. NetworkInterface caches the
        // adapter list for the lifetime of the process: after hev exits, an external view
        // sees the adapter gone in ~0.2 s while GetAllNetworkInterfaces still reports it
        // 20 s later. That would hand a reconnect the previous session's interface index —
        // routes installed against a dead interface, tunnel silently carrying nothing —
        // and would freeze the traffic counters. IP Helper answers from the live stack.

        private const int IfOperStatusUp = 1;

        /// <summary>
        /// MIB_IF_ROW2, mapped by offset rather than field-by-field. The full structure is
        /// ~40 fields of enums, GUIDs and fixed buffers; only these matter, and the
        /// declared <c>Size</c> matching the documented 1352 bytes on x64 is what keeps the
        /// offsets honest.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 1352, CharSet = CharSet.Unicode)]
        private struct MibIfRow2
        {
            [FieldOffset(0)] public ulong InterfaceLuid;
            [FieldOffset(8)] public uint InterfaceIndex;
            [FieldOffset(1124)] public uint Mtu;
            [FieldOffset(1156)] public uint OperStatus;
            [FieldOffset(1208)] public ulong InOctets;
            [FieldOffset(1216)] public ulong InUcastPkts;
            [FieldOffset(1280)] public ulong OutOctets;
            [FieldOffset(1288)] public ulong OutUcastPkts;
        }

        [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)]
        private static extern int ConvertInterfaceAliasToLuid(string alias, out ulong luid);

        [DllImport("iphlpapi.dll")]
        private static extern int GetIfEntry2(ref MibIfRow2 row);

        private static bool TryGetTunRow(out MibIfRow2 row)
        {
            row = default;

            try
            {
                if (ConvertInterfaceAliasToLuid(TunAlias, out var luid) != 0) return false;

                row.InterfaceLuid = luid;
                return GetIfEntry2(ref row) == 0;
            }
            catch
            {
                // The adapter vanishes the moment hev exits; a sampling race is not worth
                // failing the 1 Hz poll over.
                return false;
            }
        }

        /// <summary>Last few log lines, so a failure message carries hev's own reason.</summary>
        private static string HevLogTail()
        {
            try
            {
                if (!File.Exists(DiagnosticPaths.HevLog)) return string.Empty;

                using var stream = new FileStream(
                    DiagnosticPaths.HevLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                var lines = reader.ReadToEnd()
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                return lines.Length == 0 ? string.Empty : string.Join(" | ", lines.TakeLast(3));
            }
            catch { return string.Empty; }
        }

        // ── Routing ──────────────────────────────────────────────────────────

        /// <summary>
        /// Pairs each address that must stay off-tunnel with the next hop currently serving
        /// it. <c>GetBestRoute</c> answers "how would this machine reach that address right
        /// now" in one live call, returning both the next hop and the interface — asked
        /// while the tunnel is still down, so the answer is the physical path.
        ///
        /// For an address on the local segment the next hop is the address itself, which
        /// Windows treats as on-link; that is correct for a node on the LAN.
        /// </summary>
        private static List<(string Address, string Gateway, int IfIndex)> ResolveBypassRoutes(
            string[] addresses)
        {
            var result = new List<(string, string, int)>();

            foreach (var raw in addresses)
            {
                if (!IPAddress.TryParse(raw, out var ip) ||
                    ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    continue;

                var route = new MibIpForwardRow();
                var destination = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);

                if (GetBestRoute(destination, 0, ref route) != 0)
                    continue;

                var nextHop = new IPAddress(BitConverter.GetBytes(route.ForwardNextHop));
                result.Add((ip.ToString(), nextHop.ToString(), (int)route.ForwardIfIndex));
            }

            return result;
        }

        /// <summary>MIB_IPFORWARDROW — flat, IPv4-only, which is all the bypass needs.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MibIpForwardRow
        {
            public uint ForwardDest, ForwardMask, ForwardPolicy, ForwardNextHop, ForwardIfIndex;
            public uint ForwardType, ForwardProto, ForwardAge, ForwardNextHopAS;
            public uint ForwardMetric1, ForwardMetric2, ForwardMetric3, ForwardMetric4, ForwardMetric5;
        }

        [DllImport("iphlpapi.dll")]
        private static extern int GetBestRoute(uint dwDestAddr, uint dwSourceAddr, ref MibIpForwardRow pBestRoute);

        private async Task AddRouteAsync(string destination, string mask, string gateway, int ifIndex)
        {
            var spec = $"{destination} mask {mask}";
            await RunAsync("route", $"add {spec} {gateway} metric 1 if {ifIndex}");
            _routes.Insert(0, spec);
            RememberRoute(spec);
        }

        /// <summary>
        /// Where the routes we installed are written down, so a crash does not leave them
        /// behind for good.
        ///
        /// Routes on the tunnel interface need no such help — Windows drops them when the
        /// adapter goes, which the job object guarantees. The bypass routes are the problem:
        /// they live on the <i>physical</i> interface and survive anything. A stale one is
        /// worse than untidy — a leftover host route to a resolver silently keeps DNS
        /// outside the tunnel on the next connection, which is exactly the leak this build
        /// set out to close, and it is invisible unless you go looking at the route table.
        /// </summary>
        private static string RouteStateFile =>
            Path.Combine(FileSystem.CacheDirectory, "tunnel-routes.txt");

        private static void RememberRoute(string spec)
        {
            try { File.AppendAllLines(RouteStateFile, [spec]); }
            catch (Exception ex) { Debug.WriteLine($"[Horus] route state: {ex.Message}"); }
        }

        private static void ForgetRoutes()
        {
            try { if (File.Exists(RouteStateFile)) File.Delete(RouteStateFile); }
            catch (Exception ex) { Debug.WriteLine($"[Horus] route state: {ex.Message}"); }
        }

        /// <summary>Deletes routes a previous run installed and never got to remove.</summary>
        private static async Task SweepAbandonedRoutesAsync()
        {
            if (!File.Exists(RouteStateFile)) return;

            foreach (var spec in await File.ReadAllLinesAsync(RouteStateFile))
            {
                if (string.IsNullOrWhiteSpace(spec)) continue;
                await RunAsync("route", $"delete {spec}", fatal: false);
            }

            ForgetRoutes();
        }

        /// <summary>
        /// Points name resolution at the tunnel and keeps it there.
        ///
        /// Setting the interface's servers is only half the job. Since Windows 10 1703 the
        /// resolver queries every interface in parallel and takes the first answer, so the
        /// physical adapter's servers — typically the local router — still see every lookup
        /// even with a perfectly configured tunnel. That is a DNS leak that no amount of
        /// routing fixes, because the router is on-link and never enters the tunnel.
        ///
        /// The NRPT is the mechanism Windows provides for exactly this: a policy that binds
        /// a namespace to specific servers regardless of interface. <c>"."</c> is the
        /// catch-all namespace.
        /// </summary>
        private async Task SetSystemDnsAsync(string[] dnsServers)
        {
            if (dnsServers.Length == 0 || _tunIfIndex == 0) return;

            await RunAsync("netsh",
                $"interface ipv4 set dnsservers {_tunIfIndex} static {dnsServers[0]} primary no");

            for (int i = 1; i < dnsServers.Length; i++)
                await RunAsync("netsh",
                    $"interface ipv4 add dnsservers {_tunIfIndex} {dnsServers[i]} index={i + 1} no");

            // Also make the tunnel the preferred interface, so anything that does honour
            // interface order picks it first.
            await RunAsync("netsh", $"interface ipv4 set interface {_tunIfIndex} metric=1 store=active");

            var servers = string.Join(",", dnsServers.Select(s => $"'{s}'"));
            await RunPowerShellAsync(
                $"Add-DnsClientNrptRule -Namespace '.' -NameServers {servers} -Comment '{NrptComment}'",
                // Not fatal: without it the tunnel still carries DNS, it is only the
                // parallel-query edge case that reopens. Failing the whole connection
                // because a policy cmdlet is unavailable would be the worse trade.
                fatal: false);
        }

        /// <summary>Tags our NRPT rules so teardown removes ours and nobody else's.</summary>
        private const string NrptComment = "Horus VPN";

        private static Task ClearDnsPolicyAsync() =>
            RunPowerShellAsync(
                $"Get-DnsClientNrptRule | Where-Object {{ $_.Comment -eq '{NrptComment}' }} | " +
                "ForEach-Object { Remove-DnsClientNrptRule -Name $_.Name -Force }",
                fatal: false);

        // ── Teardown ─────────────────────────────────────────────────────────

        /// <summary>
        /// Undoes whatever got as far as being done, in reverse. Every step is
        /// individually guarded: a failure here is what strands the machine with routes
        /// pointing at a dead adapter, so one broken step must not skip the rest.
        /// </summary>
        private async Task TeardownAsync()
        {
            _stopping = true;

            // Before the routes: a name policy pointing at a tunnel that is going away
            // would outlive it, and unlike a route it is not tied to the interface.
            try { await ClearDnsPolicyAsync(); }
            catch (Exception ex) { Debug.WriteLine($"[Horus] NRPT cleanup: {ex.Message}"); }

            // Routes next, while the interface they name still exists.
            foreach (var spec in _routes.ToArray())
                await RunAsync("route", $"delete {spec}", fatal: false);

            _routes.Clear();
            ForgetRoutes();

            var proc = Interlocked.Exchange(ref _hev, null);
            if (proc is not null)
            {
                proc.Exited -= OnHevExited;
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Horus] hev shutdown: {ex.Message}");
                }
                finally { proc.Dispose(); }
            }

            // The adapter is created and destroyed by hev, so it goes with the process and
            // takes its DNS settings with it — there is nothing to restore.
            _tunIfIndex = 0;
        }

        // ── Process helper ───────────────────────────────────────────────────

        private static Task RunPowerShellAsync(string script, bool fatal = true) =>
            RunAsync("powershell", $"-NoProfile -NonInteractive -Command \"{script}\"", fatal);

        private static async Task RunAsync(string exe, string args, bool fatal = true)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Не удалось запустить {exe}.");

            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0) return;

            var message = $"{exe} {args} → код {proc.ExitCode}. {(await stdout).Trim()} {(await stderr).Trim()}".Trim();

            if (fatal) throw new InvalidOperationException(message);
            Debug.WriteLine($"[Horus] {message}");
        }

        private void SetState(TunnelState state, string? error = null)
        {
            _state = state;
            TunnelStateChanged?.Invoke(this, new TunnelStateChangedEventArgs(state, error));
        }
    }
}
