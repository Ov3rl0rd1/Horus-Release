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
    /// a child process, wait for the wintun adapter it created, then install routes with
    /// <c>route add</c>, set DNS with <c>netsh</c>, keep a journal of every route for crash
    /// recovery, and kill orphaned bridges left by earlier crashes. The core's TUN inbound
    /// creates the adapter in-process and sets its address, MTU, metric, DNS server and
    /// routes through IP Helper; the routes live on the adapter, so they vanish with it — a
    /// crash cannot strand them, and there is nothing to journal or sweep. The core also
    /// pins its own sockets to the physical interface, so no host route is needed to keep
    /// its connection to the node out of the tunnel.</para>
    ///
    /// <para><b>What is left here.</b> Confirming the adapter actually came up, the one
    /// thing the core cannot do — a name-resolution policy (NRPT) that stops Windows asking
    /// the physical adapter's resolvers in parallel — and the counters for the speed graph.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsVpnService : IVpnPlatformService
    {
        /// <summary>How long the adapter gets to report itself up after the core started.</summary>
        private static readonly TimeSpan AdapterTimeout = TimeSpan.FromSeconds(15);

        /// <summary>Tags our NRPT rules so cleanup removes ours and nobody else's.</summary>
        private const string NrptComment = "Horus VPN";

        private TunnelState _state = TunnelState.Stopped;
        private int _ifIndex;

        public WindowsVpnService()
        {
            // A previous run may have died with its DNS policy in place: unlike routes, an
            // NRPT rule is not tied to the adapter and outlives a crash. Builds before this
            // one also left route journals; their routes are gone with their adapters, but
            // their host routes on the physical interface are not.
            _ = Task.Run(async () =>
            {
                try { await ClearDnsPolicyAsync(); }
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
        /// Called once the core is running with its TUN inbound. Waits for the adapter and
        /// applies the DNS policy; the routes are already in place.
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

                // Not awaited into the connect: the tunnel already carries DNS, and the policy
                // only closes the parallel-query path to the router. A second here or there is
                // not worth delaying the user's connection for.
                _ = ApplyDnsPolicyAsync(options.DnsServers);

                SetState(TunnelState.Started);
            }
            catch
            {
                _ifIndex = 0;
                SetState(TunnelState.Error);
                throw;
            }
        }

        public async Task StopTunnelAsync()
        {
            if (_state == TunnelState.Stopped) return;

            SetState(TunnelState.Stopping);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await ClearDnsPolicyAsync().WaitAsync(timeout.Token);
            }
            catch (Exception ex) { Diag.Warn("tunnel", $"DNS policy not cleared: {ex.Message}"); }

            _ifIndex = 0;
            SetState(TunnelState.Stopped);
        }

        /// <summary>Per-destination policy is the core's routing; nothing to do here.</summary>
        public Task ApplyRoutingRulesAsync(IEnumerable<RoutingRule> rules) => Task.CompletedTask;

        public Task SetDnsAsync(string[] dnsServers) => ApplyDnsPolicyAsync(dnsServers);

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

        // ── DNS policy ───────────────────────────────────────────────────────

        /// <summary>
        /// Binds all name resolution to the tunnel's resolver.
        ///
        /// <para>Setting the adapter's DNS server is half the job: since Windows 10 1703 the
        /// resolver asks every interface's servers in parallel and takes the first answer, so
        /// the router still sees every lookup — and where a provider answers blocked names
        /// with its own stub address, its answer can win the race and the site "does not
        /// open". The NRPT binds the catch-all namespace <c>.</c> to our server whatever the
        /// interface order.</para>
        /// </summary>
        private static async Task ApplyDnsPolicyAsync(string[] dnsServers)
        {
            if (dnsServers.Length == 0) return;
            try
            {
                await ClearDnsPolicyAsync();
                var servers = string.Join(",", dnsServers.Select(s => $"'{s}'"));
                await RunPowerShellAsync(
                    $"Add-DnsClientNrptRule -Namespace '.' -NameServers {servers} -Comment '{NrptComment}'; " +
                    "Clear-DnsClientCache");
                Diag.Info("tunnel", $"DNS policy bound to {string.Join(", ", dnsServers)}");
            }
            catch (Exception ex)
            {
                // Not fatal: DNS still goes through the tunnel; only the parallel-query path reopens.
                Diag.Warn("tunnel", $"DNS policy not applied: {ex.Message}");
            }
        }

        private static Task ClearDnsPolicyAsync() =>
            RunPowerShellAsync(
                $"Get-DnsClientNrptRule | Where-Object {{ $_.Comment -eq '{NrptComment}' }} | " +
                "ForEach-Object { Remove-DnsClientNrptRule -Name $_.Name -Force }",
                fatal: false);

        // ── Leftovers from builds that used host routes ──────────────────────

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
