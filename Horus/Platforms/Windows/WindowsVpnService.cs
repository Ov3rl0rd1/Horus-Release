using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Platforms.Windows.Tunnel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Horus.Platforms.Windows
{
    /// <summary>
    /// The Windows side of the tunnel, now that the core owns it.
    ///
    /// <para><b>What moved into the core.</b> This class used to start hev-socks5-tunnel as
    /// a child process, wait for the wintun adapter it created, then install host routes,
    /// set DNS, keep a journal of every route for crash recovery, and kill orphaned bridges
    /// left by earlier crashes. The core's TUN inbound creates the adapter in-process and
    /// sets its address, MTU, metric, DNS server and routes through IP Helper; the routes
    /// live on the adapter, so they vanish with it. It also keeps DNS from leaving by any
    /// other interface with WFP filters in a dynamic session, which Windows deletes with the
    /// process. Nothing this tunnel changes in the system outlives the app — a crash or a
    /// shutdown with the VPN on cannot leave the machine without name resolution.</para>
    ///
    /// <para><b>What is left here.</b> Confirming the adapter actually came up, the one
    /// thing the core cannot do, the counters for the speed graph, and removing what older
    /// builds left behind.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsVpnService : IVpnPlatformService
    {
        /// <summary>How long the adapter gets to report itself up after the core started.</summary>
        private static readonly TimeSpan AdapterTimeout = TimeSpan.FromSeconds(15);

        /// <summary>Tags the NRPT rules older builds added, so the sweep removes theirs and nobody else's.</summary>
        private const string LegacyNrptComment = "Horus VPN";

        private TunnelState _state = TunnelState.Stopped;
        private int _ifIndex;

        public WindowsVpnService()
        {
            // Older builds bound DNS with an NRPT rule, which is not tied to the adapter: one
            // that died with the VPN on left every lookup on the machine pointed at a resolver
            // that no longer existed. Older still, they left route journals; those routes are
            // gone with their adapters, but their host routes on the physical interface are not.
            _ = Task.Run(async () =>
            {
                try { await ClearLegacyDnsPolicyAsync(); }
                catch (Exception ex) { Debug.WriteLine($"[Horus] NRPT sweep: {ex.Message}"); }

                try { await SweepLegacyRoutesAsync(); }
                catch (Exception ex) { Debug.WriteLine($"[Horus] legacy route sweep: {ex.Message}"); }
            });
        }

        /// <summary>
        /// wintun.dll has to sit next to Horus.exe: the core loads it with
        /// <c>LOAD_LIBRARY_SEARCH_APPLICATION_DIR</c>, which is the directory of the
        /// process's executable, not of xray.dll.
        /// </summary>
        public bool IsSupported => File.Exists(Path.Combine(AppContext.BaseDirectory, "wintun.dll"));

        public TunnelState CurrentState => _state;

        /// <summary>The adapter's interface index while the tunnel is up, else 0.</summary>
        public int InterfaceIndex => _ifIndex;

        public event EventHandler<TunnelStateChangedEventArgs>? TunnelStateChanged;

        /// <summary>
        /// Whether this process can create a TUN adapter — wintun needs administrator rights.
        /// It does not relaunch elevated: reporting false lets the caller show a message and
        /// leaves the running app alone.
        /// </summary>
        public Task<bool> RequestPermissionsAsync()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return Task.FromResult(principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
        }

        /// <summary>
        /// Called once the core is running with its TUN inbound. Waits for the adapter; its
        /// routes and DNS are already in place.
        /// </summary>
        public async Task StartTunnelAsync(TunnelOptions options, CancellationToken ct = default)
        {
            SetState(TunnelState.Starting);
            try
            {
                if (!IsSupported)
                    throw new PlatformNotSupportedException(
                        $"Не найден wintun.dll рядом с {Path.GetFileName(Environment.ProcessPath)} — " +
                        "без него TUN-адаптер не создать. Переустановите приложение.");

                _ifIndex = await WaitForAdapterAsync(ct);
                SetState(TunnelState.Started);
            }
            catch
            {
                _ifIndex = 0;
                SetState(TunnelState.Error);
                throw;
            }
        }

        /// <summary>The adapter goes with the core; this only records that it went.</summary>
        public Task StopTunnelAsync()
        {
            if (_state == TunnelState.Stopped) return Task.CompletedTask;

            _ifIndex = 0;
            SetState(TunnelState.Stopped);
            return Task.CompletedTask;
        }

        /// <summary>Per-destination policy is the core's routing; nothing to do here.</summary>
        public Task ApplyRoutingRulesAsync(IEnumerable<RoutingRule> rules) => Task.CompletedTask;

        /// <summary>DNS is the core's: the adapter's server and the leak filters come with the TUN.</summary>
        public Task SetDnsAsync(string[] dnsServers) => Task.CompletedTask;

        /// <summary>
        /// <c>[tx_packets, tx_bytes, rx_packets, rx_bytes]</c> from the adapter — "tx" is what
        /// the machine sent into the tunnel, i.e. upload. Empty when there is no adapter,
        /// which is a different fact from four zeros on an idle one.
        /// </summary>
        public long[] GetTunnelStats()
        {
            if (!TryGetTunRow(out var row)) return [];
            return [(long)row.OutUcastPkts, (long)row.OutOctets, (long)row.InUcastPkts, (long)row.InOctets];
        }

        /// <summary>Whether the adapter exists and is up right now.</summary>
        public bool AdapterIsUp => TryGetTunRow(out var row) && row.OperStatus == IfOperStatusUp;

        // ── Adapter ──────────────────────────────────────────────────────────

        private async Task<int> WaitForAdapterAsync(CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + AdapterTimeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (TryGetTunRow(out var row) && row.OperStatus == IfOperStatusUp)
                    return (int)row.InterfaceIndex;
                await Task.Delay(200, ct);
            }

            throw new TimeoutException(
                $"Адаптер «{WindowsTunnelConfig.AdapterName}» не появился за {AdapterTimeout.TotalSeconds:0} с. " +
                "Обычно это заблокированный антивирусом wintun.dll или запуск без прав администратора.");
        }

        // Live interface queries through IP Helper: NetworkInterface caches the adapter list
        // for the process's lifetime and would report a removed adapter for tens of seconds.

        private const int IfOperStatusUp = 1;

        /// <summary>MIB_IF_ROW2, mapped by offset; the declared 1352 bytes match x64.</summary>
        [StructLayout(LayoutKind.Explicit, Size = 1352, CharSet = CharSet.Unicode)]
        private struct MibIfRow2
        {
            [FieldOffset(0)] public ulong InterfaceLuid;
            [FieldOffset(8)] public uint InterfaceIndex;
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
                if (ConvertInterfaceAliasToLuid(WindowsTunnelConfig.AdapterName, out var luid) != 0) return false;
                row.InterfaceLuid = luid;
                return GetIfEntry2(ref row) == 0;
            }
            catch
            {
                return false;
            }
        }

        // ── Leftovers from older builds ──────────────────────────────────────

        private static Task ClearLegacyDnsPolicyAsync() =>
            RunPowerShellAsync(
                $"$left = Get-DnsClientNrptRule | Where-Object {{ $_.Comment -eq '{LegacyNrptComment}' }}; " +
                "if ($left) { $left | ForEach-Object { Remove-DnsClientNrptRule -Name $_.Name -Force }; Clear-DnsClientCache }",
                fatal: false);

        /// <summary>
        /// Builds before the core owned the TUN installed host routes on the physical
        /// interface (the node and, for split tunneling, every steered destination) and wrote
        /// them down for exactly this. A stale one silently keeps its destination outside the
        /// tunnel forever, so they are removed once and the journals deleted.
        /// </summary>
        private static async Task SweepLegacyRoutesAsync()
        {
            foreach (var journal in new[]
                     {
                         Path.Combine(FileSystem.CacheDirectory, "tunnel-routes.txt"),
                         Path.Combine(FileSystem.AppDataDirectory, "split-routes.txt")
                     })
            {
                if (!File.Exists(journal)) continue;
                foreach (var line in await File.ReadAllLinesAsync(journal))
                {
                    var spec = line.Trim();
                    if (spec.Length == 0) continue;
                    // Either "a.b.c.d mask m.m.m.m" or a bare address.
                    await RunAsync("route", $"delete {spec}", fatal: false);
                }
                try { File.Delete(journal); } catch { }
            }
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

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Не удалось запустить {exe}.");
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0) return;

            var message = $"{exe} → код {proc.ExitCode}. {(await stdout).Trim()} {(await stderr).Trim()}".Trim();
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
