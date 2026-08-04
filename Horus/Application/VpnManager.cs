using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Protocols;

namespace Horus.Application
{
    public class VpnManager
    {
        public event EventHandler<VpnStateChangedEventArgs>? StateChanged;
        public event EventHandler<ConnectionErrorEventArgs>? ConnectionError;
        public event EventHandler<ServerChangedEventArgs>? ServerChanged;
        public event EventHandler<ProtocolFallbackEventArgs>? ProtocolFallback;

        /// <summary>Raw stdout/stderr lines from the xray process.</summary>
        public event EventHandler<string>? ProtocolOutputReceived;

        private readonly IVpnPlatformService _platform;
        private readonly ProtocolFactory _protocolFactory;
        private readonly IRoutingService _routing;
        private readonly ITrafficMonitorService _traffic;
        private readonly IApiService _api;
        private readonly IErrorReportingService _errorReporting;

        /// <summary>
        /// Outbound fallback order. All three live inside the same xray instance, so a
        /// fallback re-renders the config with a different proxy outbound rather than
        /// swapping binaries.
        ///
        /// VLESS/REALITY leads because it is the one outbound whose xray schema is
        /// standard and verified; the Hysteria2 outbound targets the custom core's own
        /// shape and is still unconfirmed against a live node.
        /// </summary>
        private static readonly ProtocolType[] FallbackOrder =
            [ProtocolType.Vless, ProtocolType.Hysteria2, ProtocolType.OlcRtc];

        /// <summary>How often the core is polled for liveness while connected.</summary>
        private static readonly TimeSpan CoreWatchInterval = TimeSpan.FromSeconds(5);

        public VpnState State { get; private set; } = VpnState.Disconnected;
        public IVpnProtocol? ActiveProtocol { get; private set; }
        public ServerInfo? ActiveServer { get; private set; }
        public ProtocolType? ActiveProtocolType { get; private set; }
        public string? LastProtocolLog { get; private set; }

        /// <summary>Egress IP seen without the proxy — i.e. the device's real address.</summary>
        public string? LastPreflightDirectIp { get; private set; }

        /// <summary>
        /// Egress IP seen *through* xray's SOCKS5 inbound. When this differs from
        /// <see cref="LastPreflightDirectIp"/> the proxy half of the pipeline is proven
        /// working, so any remaining failure is in the TUN half.
        /// </summary>
        public string? LastPreflightProxiedIp { get; private set; }

        private readonly System.Text.StringBuilder _protocolLogBuffer = new();
        private CancellationTokenSource? _watchdogCts;

        public VpnManager(
            IVpnPlatformService platform,
            ProtocolFactory protocolFactory,
            IRoutingService routing,
            ITrafficMonitorService traffic,
            IApiService api,
            IErrorReportingService errorReporting)
        {
            _platform = platform;
            _protocolFactory = protocolFactory;
            _routing = routing;
            _traffic = traffic;
            _api = api;
            _errorReporting = errorReporting;

            // Without this, OnRevoke, another VPN app taking over, or a tunnel fault all
            // leave the UI claiming to be connected while carrying nothing.
            _platform.TunnelStateChanged += OnTunnelStateChanged;
        }

        /// <summary>
        /// Connects through whichever server the API binds the account to. The
        /// <paramref name="server"/> argument is what the UI displays — server
        /// <i>selection</i> is server-side (<c>GET /servers/connect</c> takes no id).
        /// </summary>
        public async Task ConnectAsync(ServerInfo? server = null, CancellationToken ct = default)
        {
            if (State != VpnState.Disconnected) return;

            SetState(VpnState.Connecting, null);
            _protocolLogBuffer.Clear();

            try
            {
                var connection = await _api.GetServerConnectionAsync(ct);

                bool granted = await _platform.RequestPermissionsAsync();
                if (!granted)
                    throw new InvalidOperationException("VPN permission denied by user.");

                // Only try outbounds this node actually published.
                var available = FallbackOrder.Where(p => connection.LinkFor(p) != null).ToArray();
                if (available.Length == 0)
                    throw new InvalidOperationException(
                        "The server did not offer any protocol this build supports.");

                var (protocol, usedType) = await ConnectWithFallbackAsync(connection, available, ct);

                ActiveProtocol = protocol;
                ActiveProtocolType = usedType;

                // Prove the proxy half works before building the TUN. If this shows a
                // foreign IP and the tunnel still fails afterwards, the fault is entirely
                // in the TUN half — which is most of the triage work, done up front.
                await RunPreflightAsync(ct);

                // Establish TUN, apply routing
                var tunnelOptions = BuildTunnelOptions(server);
                await _platform.StartTunnelAsync(tunnelOptions, ct);

                // Routing rules are advisory today: the Android platform hook is a no-op
                // and xray does its own routing from the generated config. Never let them
                // fail an otherwise-working connection.
                try
                {
                    await _routing.RefreshFromServerAsync(ct);
                    await _routing.ApplyAsync();
                }
                catch (Exception ex)
                {
                    OnProtocolOutput(this, $"[routing] skipped: {ex.Message}");
                }

                if (_traffic is TrafficMonitorService tms) tms.Reset();
                _traffic.Start();

                var previous = ActiveServer;
                ActiveServer = server;
                if (previous != null && server != null)
                    ServerChanged?.Invoke(this, new ServerChangedEventArgs(previous, server));

                SetState(VpnState.Connected, null);
                StartCoreWatchdog();
            }
            catch (OperationCanceledException)
            {
                await SafeTeardownAsync();
                SetState(VpnState.Disconnected, "Cancelled");
                throw;
            }
            catch (Exception ex)
            {
                await SafeTeardownAsync();
                SetState(VpnState.Disconnected, ex.Message);
                ConnectionError?.Invoke(this, new ConnectionErrorEventArgs(
                    ActiveProtocolType?.ToString() ?? "Unknown", ex.Message, false));
                throw;
            }
        }

        public async Task DisconnectAsync()
        {
            if (State == VpnState.Disconnected) return;

            SetState(VpnState.Disconnecting, null);
            try
            {
                StopCoreWatchdog();
                _traffic.Stop();
                await _platform.StopTunnelAsync();
                await DetachProtocolAsync();

                ActiveServer = null;
                ActiveProtocolType = null;
                SetState(VpnState.Disconnected, null);
            }
            catch (Exception ex)
            {
                SetState(VpnState.Error, ex.Message);
                throw;
            }
        }

        public async Task ReconnectAsync(ServerInfo? server = null, CancellationToken ct = default)
        {
            var target = server ?? ActiveServer;
            await DisconnectAsync();
            await ConnectAsync(target, ct);
        }

        // ── Preflight ───────────────────────────────────────────────────────

        /// <summary>
        /// Fetches the egress IP twice — once directly, once through xray's SOCKS5
        /// inbound — and records both. The pair pre-triages nearly every connection
        /// failure:
        ///
        /// <list type="bullet">
        /// <item>real / server — proxy chain is good; a later failure is the TUN.</item>
        /// <item>real / real — xray is egressing via <c>freedom</c>, or the outbound
        /// silently fell back.</item>
        /// <item>real / timeout — the outbound is broken: wrong schema, bad credential,
        /// or the node is down.</item>
        /// </list>
        ///
        /// Never throws: a failed preflight is a diagnostic, not a reason to abort a
        /// connection that might still work.
        /// </summary>
        private async Task RunPreflightAsync(CancellationToken ct)
        {
            LastPreflightDirectIp = null;
            LastPreflightProxiedIp = null;

            LastPreflightDirectIp = await FetchEgressIpAsync(null, ct);
            LastPreflightProxiedIp = await FetchEgressIpAsync(
                $"socks5://127.0.0.1:{XrayConfig.DefaultSocksPort}", ct);

            var direct = LastPreflightDirectIp ?? "—";
            var proxied = LastPreflightProxiedIp ?? "—";
            OnProtocolOutput(this, $"[preflight] direct={direct} proxied={proxied}");

            _errorReporting.SetContext("preflightDirectIp", direct);
            _errorReporting.SetContext("preflightProxiedIp", proxied);
            _errorReporting.SetContext("protocol", ActiveProtocolType?.ToString());
            _errorReporting.SetContext("coreVersion", XrayProtocol.CoreVersion);

            if (LastPreflightProxiedIp is null)
                OnProtocolOutput(this,
                    "[preflight] Proxy did not answer — the outbound is not carrying traffic.");
            else if (LastPreflightProxiedIp == LastPreflightDirectIp)
                OnProtocolOutput(this,
                    "[preflight] Proxied IP equals the direct IP — traffic is not leaving via the node.");
        }

        private async Task<string?> FetchEgressIpAsync(string? socksProxy, CancellationToken ct)
        {
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    Proxy = socksProxy is null ? null : new System.Net.WebProxy(socksProxy),
                    UseProxy = socksProxy is not null,
                    ConnectTimeout = TimeSpan.FromSeconds(5)
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };

                var url = $"{AppConfiguration.ApiBaseUrl.TrimEnd('/')}/ip";
                var text = await client.GetStringAsync(url, ct);
                var ip = text.Trim();
                return ip.Length is > 0 and <= 64 ? ip : null;
            }
            catch
            {
                return null;
            }
        }

        // ── Core watchdog ───────────────────────────────────────────────────

        /// <summary>
        /// xray now runs in-process, so there is no child process to watch. Poll the core
        /// for liveness instead and tear down if it stops, rather than leaving the UI
        /// claiming to be connected.
        /// </summary>
        private void StartCoreWatchdog()
        {
            StopCoreWatchdog();
            _watchdogCts = new CancellationTokenSource();
            _ = WatchCoreAsync(_watchdogCts.Token);
        }

        private void StopCoreWatchdog()
        {
            _watchdogCts?.Cancel();
            _watchdogCts?.Dispose();
            _watchdogCts = null;
        }

        private async Task WatchCoreAsync(CancellationToken ct)
        {
            try
            {
                using var timer = new PeriodicTimer(CoreWatchInterval);
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    if (State != VpnState.Connected) continue;
                    if (XrayProtocol.IsCoreRunning) continue;

                    OnProtocolOutput(this, "[xray] Core stopped unexpectedly.");
                    await HandleUnexpectedDropAsync("Соединение с ядром потеряно.");
                    return;
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
            catch { /* watchdog must never take the app down */ }
        }

        private void OnTunnelStateChanged(object? sender, TunnelStateChangedEventArgs e)
        {
            if (State != VpnState.Connected) return;
            if (e.State is not (TunnelState.Error or TunnelState.Stopped)) return;

            _ = HandleUnexpectedDropAsync(e.Error ?? "Туннель был закрыт системой.");
        }

        /// <summary>
        /// Tears down after the tunnel or the core dies underneath us. Moving out of
        /// <see cref="VpnState.Connected"/> first is what stops this re-entering: the
        /// teardown it performs raises further tunnel-state changes, and both callers
        /// gate on being Connected.
        /// </summary>
        private async Task HandleUnexpectedDropAsync(string reason)
        {
            if (State != VpnState.Connected) return;

            var protocol = ActiveProtocolType?.ToString() ?? "Unknown";
            SetState(VpnState.Disconnecting, reason);

            StopCoreWatchdog();
            await SafeTeardownAsync();
            ActiveProtocolType = null;
            SetState(VpnState.Disconnected, reason);
            ConnectionError?.Invoke(this, new ConnectionErrorEventArgs(protocol, reason, false));
        }

        // ── Connect helpers ─────────────────────────────────────────────────

        private async Task<(IVpnProtocol Protocol, ProtocolType Type)> ConnectWithFallbackAsync(
            ServerConnection connection, ProtocolType[] available, CancellationToken ct)
        {
            Exception? lastEx = null;

            for (int i = 0; i < available.Length; i++)
            {
                var protocolType = available[i];
                IVpnProtocol? protocol = null;

                try
                {
                    var config = _protocolFactory.CreateConfig(protocolType, connection);
                    protocol = _protocolFactory.Create(protocolType);
                    Attach(protocol);

                    await protocol.ConnectAsync(config, ct);

                    if (lastEx != null)
                        ProtocolFallback?.Invoke(this, new ProtocolFallbackEventArgs(
                            available[i - 1].ToString(), protocolType.ToString(), lastEx.Message));

                    return (protocol, protocolType);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    lastEx = ex;

                    // Release the half-started process before trying the next outbound —
                    // otherwise it keeps holding the SOCKS5 port.
                    if (protocol != null)
                    {
                        Detach(protocol);
                        try { await protocol.DisconnectAsync(); } catch { /* already dead */ }
                    }

                    var protocolLog = _protocolLogBuffer.ToString();
                    _errorReporting.RecordConnectionFailure(
                        protocolType.ToString(), ex.Message, protocolLog);

                    if (i == available.Length - 1)
                    {
                        LastProtocolLog = protocolLog;
                        await TryBuildFailureArchiveAsync();
                        throw new InvalidOperationException(
                            $"Не удалось подключиться ни по одному протоколу ({string.Join(", ", available)}). " +
                            $"Последняя ошибка: {ex.Message}", ex);
                    }

                    _protocolLogBuffer.Clear();
                }
            }

            throw new InvalidOperationException("No protocol could connect.");
        }

        private void Attach(IVpnProtocol protocol)
        {
            protocol.ErrorOccurred += OnProtocolError;
            protocol.StatisticsUpdated += OnProtocolStats;
            protocol.OutputReceived += OnProtocolOutput;
        }

        private void Detach(IVpnProtocol protocol)
        {
            protocol.ErrorOccurred -= OnProtocolError;
            protocol.StatisticsUpdated -= OnProtocolStats;
            protocol.OutputReceived -= OnProtocolOutput;
        }

        private async Task DetachProtocolAsync()
        {
            if (ActiveProtocol == null) return;
            Detach(ActiveProtocol);
            await ActiveProtocol.DisconnectAsync();
            ActiveProtocol = null;
        }

        /// <summary>Best-effort rollback so a failed connect never strands a TUN or a process.</summary>
        private async Task SafeTeardownAsync()
        {
            try { _traffic.Stop(); } catch { }
            try { await _platform.StopTunnelAsync(); } catch { }
            try { await DetachProtocolAsync(); } catch { }
            ActiveServer = null;
        }

        // ── Event handlers ──────────────────────────────────────────────────

        private void OnProtocolStats(object? sender, TrafficStatisticsEventArgs e)
        {
            if (_traffic is TrafficMonitorService tms)
                tms.Feed(e);
        }

        private void OnProtocolError(object? sender, ProtocolErrorEventArgs e)
        {
            var line = $"[{DateTime.UtcNow:HH:mm:ss}] [{e.Code}] {e.Message}";
            _protocolLogBuffer.AppendLine(line);
            ProtocolOutputReceived?.Invoke(this, line);
            _errorReporting.RecordException(
                new Exception(e.Message),
                $"Protocol/{sender?.GetType().Name}/{e.Code}");
        }

        private void OnProtocolOutput(object? sender, string line)
        {
            _protocolLogBuffer.AppendLine(line);
            // Also feed the rolling session log: the failure-only capture below never
            // fires for a "connected but nothing loads" session, which is the case most
            // in need of a log.
            _errorReporting.AppendLog(line);
            ProtocolOutputReceived?.Invoke(this, line);
        }

        // ── Private helpers ─────────────────────────────────────────────────

        private static TunnelOptions BuildTunnelOptions(ServerInfo? server) => new()
        {
            // Must match HevSocksTunnel.HEV_SOCKS5_TUNNEL_CONFIG → tunnel.ipv4: 198.18.0.1
            // and the HorusVpnTunnelService.Builder.AddAddress call.
            TunAddress = "198.18.0.1",
            TunPrefix = 30,
            Mtu = 8500,       // matches HevSocksTunnel mtu: 8500
            DnsServers = ["1.1.1.1", "8.8.8.8"],
            BypassApps = [],
            AllTraffic = true
        };

        /// <summary>
        /// Writes a diagnostics archive after every protocol has failed, so it is already
        /// on disk by the time the user is offered "Собрать логи". Nothing is uploaded —
        /// the API has no ingest endpoint.
        /// </summary>
        private async Task TryBuildFailureArchiveAsync()
        {
            try { await _errorReporting.BuildArchiveAsync(); }
            catch { /* non-fatal: the user can rebuild it from Settings */ }
        }

        private void SetState(VpnState newState, string? reason)
        {
            var old = State;
            State = newState;
            StateChanged?.Invoke(this, new VpnStateChangedEventArgs(old, newState, reason));
        }
    }
}
