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
        /// Hysteria2 leads: it is QUIC-based with salamander masking and port hopping, so
        /// it survives conditions that kill a TCP/REALITY connection. Both its config and
        /// the VLESS one are validated against the real core by
        /// <c>Horus.Tests/XrayConfigBuilderTests</c>.
        /// </summary>
        private static readonly ProtocolType[] FallbackOrder =
            [ProtocolType.Hysteria2, ProtocolType.Vless, ProtocolType.OlcRtc];

        /// <summary>
        /// Backoff between automatic reconnect attempts. Deliberately reaching minutes:
        /// the common cause of a dead tunnel is a device with no usable network, and
        /// retrying every few seconds through that costs battery and fixes nothing.
        /// </summary>
        private static readonly TimeSpan[] ReconnectBackoff =
        [
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(2)
        ];

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
        private readonly TunnelHealthMonitor _health;
        private readonly INetworkMonitor _network;

        /// <summary>
        /// True between an explicit connect and an explicit disconnect. Everything
        /// automatic — reconnects after a handover, after a protocol dies, after the system
        /// reclaims the tunnel — is gated on it, so a user who pressed disconnect never has
        /// the app quietly turn itself back on.
        /// </summary>
        private bool _userWantsConnection;

        /// <summary>
        /// The protocol that just failed <i>while working</i>, so the next attempt starts
        /// after it. This is what fixes the reported case: Hysteria2 connects fine on
        /// Wi-Fi, the phone moves to mobile where that operator blocks it, and without this
        /// every reconnect picks Hysteria2 again because it is first in the fallback order.
        /// </summary>
        private ProtocolType? _demotedProtocol;

        private int _reconnectAttempt;
        private CancellationTokenSource? _reconnectCts;

        public VpnManager(
            IVpnPlatformService platform,
            ProtocolFactory protocolFactory,
            IRoutingService routing,
            ITrafficMonitorService traffic,
            IApiService api,
            IErrorReportingService errorReporting,
            TunnelHealthMonitor health,
            INetworkMonitor network)
        {
            _platform = platform;
            _protocolFactory = protocolFactory;
            _routing = routing;
            _traffic = traffic;
            _api = api;
            _errorReporting = errorReporting;
            _health = health;
            _network = network;

            // Without this, OnRevoke, another VPN app taking over, or a tunnel fault all
            // leave the UI claiming to be connected while carrying nothing.
            _platform.TunnelStateChanged += OnTunnelStateChanged;

            _health.Unhealthy += OnUnhealthy;
            _network.NetworkChanged += OnNetworkChanged;
            _network.Start();
        }

        /// <summary>
        /// Connects through whichever server the API binds the account to. The
        /// <paramref name="server"/> argument is what the UI displays — server
        /// <i>selection</i> is server-side (<c>GET /servers/connect</c> takes no id).
        /// </summary>
        public async Task ConnectAsync(ServerInfo? server = null, CancellationToken ct = default)
        {
            if (State != VpnState.Disconnected) return;

            _userWantsConnection = true;
            SetState(VpnState.Connecting, null);
            _protocolLogBuffer.Clear();

            // Once per connect, not per attempt: the fallback loop would otherwise wipe the
            // failing protocol's log on its way to trying the next one, which is exactly the
            // log needed to explain why the first one failed.
            DiagnosticPaths.Truncate(DiagnosticPaths.XrayLog);

            try
            {
                var connection = await _api.GetServerConnectionAsync(ct);

                bool granted = await _platform.RequestPermissionsAsync();
                if (!granted)
                    throw new InvalidOperationException(
                        OperatingSystem.IsWindows()
                            // On Windows this is about elevation, not a user-facing consent
                            // dialog — creating a TUN adapter needs administrator rights.
                            ? "Для создания туннеля нужны права администратора. " +
                              "Запустите Horus от имени администратора."
                            : "Разрешение на VPN не выдано.");

                // Only try outbounds this node actually published, with anything that just
                // failed on this network moved to the back rather than dropped — the same
                // protocol may well be the best choice again once the link changes.
                var available = OrderProtocols(
                    FallbackOrder.Where(p => connection.LinkFor(p) != null).ToArray());

                if (available.Length == 0)
                    throw new InvalidOperationException(
                        "The server did not offer any protocol this build supports.");

                var (protocol, usedType, usedConfig) =
                    await ConnectWithFallbackAsync(connection, available, ct);

                ActiveProtocol = protocol;
                ActiveProtocolType = usedType;

                // Establish TUN, apply routing
                var tunnelOptions = BuildTunnelOptions(server, usedConfig);
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

                // A connection that came up is proof the backoff can start over; leaving
                // the counter high would make the next unrelated hiccup wait two minutes.
                _reconnectAttempt = 0;

                _endpoint = new TunnelHealthMonitor.Endpoint(
                    tunnelOptions.SocksPort,
                    tunnelOptions.NodeAddress,
                    usedConfig is XrayConfig nodeConfig ? nodeConfig.Link.Port : 443);
                _health.Start(_endpoint);
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
            // Recorded before the early return: pressing disconnect while already
            // disconnected still has to cancel a pending automatic reconnect, or the app
            // turns itself back on seconds after the user told it not to.
            _userWantsConnection = false;
            _reconnectCts?.Cancel();
            _reconnectAttempt = 0;
            _demotedProtocol = null;

            if (State == VpnState.Disconnected) return;

            SetState(VpnState.Disconnecting, null);
            try
            {
                _health.Stop();
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
        /// Returns true when the proxy demonstrably reached the internet with an egress
        /// address that differs from the device's own. Never throws — a transport failure
        /// here is an answer, not an exception.
        /// </summary>
        private async Task<bool> RunPreflightAsync(int socksPort, CancellationToken ct)
        {
            LastPreflightDirectIp = null;
            LastPreflightProxiedIp = null;

            LastPreflightDirectIp = await _api.GetEgressIpAsync(null, ct);
            LastPreflightProxiedIp = await _api.GetEgressIpAsync(
                $"socks5://127.0.0.1:{socksPort}", ct);

            var direct = LastPreflightDirectIp ?? "—";
            var proxied = LastPreflightProxiedIp ?? "—";
            OnProtocolOutput(this, $"[preflight] direct={direct} proxied={proxied}");

            _errorReporting.SetContext("preflightDirectIp", direct);
            _errorReporting.SetContext("preflightProxiedIp", proxied);
            _errorReporting.SetContext("protocol", ActiveProtocolType?.ToString());
            _errorReporting.SetContext("coreVersion", XrayProtocol.CoreVersion);

            if (LastPreflightProxiedIp is not null)
            {
                if (LastPreflightProxiedIp == LastPreflightDirectIp)
                {
                    OnProtocolOutput(this,
                        "[preflight] Proxied IP equals the direct IP — traffic is not leaving via the node.");
                    return false;
                }

                return true;
            }

            // Proxy gave no answer. That only condemns the outbound if the same check
            // succeeds without it — otherwise the probe itself is broken (no connectivity,
            // endpoint down) and rejecting the protocol would be a false negative that
            // blocks a perfectly good tunnel.
            if (LastPreflightDirectIp is not null)
            {
                OnProtocolOutput(this,
                    "[preflight] Proxy did not answer while the direct check did — outbound is dead.");
                return false;
            }

            OnProtocolOutput(this,
                "[preflight] Inconclusive: neither check answered. Accepting the protocol.");
            return true;
        }

        // ── Health and recovery ─────────────────────────────────────────────

        /// <summary>
        /// The tunnel stopped working. What to do depends entirely on <i>why</i>, which is
        /// the whole reason <see cref="TunnelHealthMonitor"/> bothers to distinguish them:
        ///
        /// <list type="bullet">
        /// <item><b>No internet</b> — the tunnel is not at fault and reconnecting cannot
        /// help. Everything is left standing and we wait for the network monitor to say a
        /// link is back. This is the case that used to burn battery reconnecting in a loop
        /// on a train or in a lift.</item>
        /// <item><b>Outbound dead</b> — the link is fine and the node is reachable, but
        /// this protocol is not carrying. Demote it and reconnect, so the fallback order
        /// starts at the next one.</item>
        /// <item><b>Core or tunnel dead</b> — a component the system reclaimed. Rebuild the
        /// same connection from scratch.</item>
        /// </list>
        /// </summary>
        private void OnUnhealthy(object? sender, TunnelHealthEventArgs e)
        {
            if (State != VpnState.Connected) return;

            OnProtocolOutput(this, $"[health] {e.Health}: {e.Detail}");

            if (e.Health == TunnelHealth.NoInternet)
            {
                // Deliberately not a teardown. Killing the tunnel here would drop the
                // user's traffic to the clear the moment connectivity returned, and on
                // Android it would also lose the foreground service that keeps the process
                // — and therefore the whole app — alive.
                OnProtocolOutput(this, "[health] no usable link; holding the tunnel and waiting");
                _health.Start(CurrentEndpoint());
                return;
            }

            if (e.Health == TunnelHealth.OutboundDead && ActiveProtocolType is { } failing)
            {
                _demotedProtocol = failing;
                OnProtocolOutput(this, $"[health] demoting {failing} for the next attempt");
            }

            _ = RecoverAsync(e.Health.ToString());
        }

        /// <summary>
        /// A handover is the one event counters cannot detect: every connection through the
        /// old link is already dead, and nothing reports it until something tries to use
        /// one. Probing immediately turns a stall the user would notice into a reconnect
        /// they mostly will not.
        /// </summary>
        private void OnNetworkChanged(object? sender, NetworkChangedEventArgs e)
        {
            OnProtocolOutput(this, $"[net] {e.Transport}, online={e.IsOnline}, handover={e.IsHandover}");

            if (!_userWantsConnection) return;

            if (!e.IsOnline) return; // nothing to do until a link exists

            // Back online after a drop that we chose not to tear down.
            if (State == VpnState.Connected) _health.ProbeNow($"network changed to {e.Transport}");
            else if (State == VpnState.Disconnected) ScheduleReconnect("network returned", immediate: true);
        }

        /// <summary>
        /// Tears the connection down and brings it back, with backoff. Gated on
        /// <see cref="_userWantsConnection"/> so it can never fight a user who pressed
        /// disconnect.
        /// </summary>
        private async Task RecoverAsync(string reason)
        {
            if (!_userWantsConnection) return;

            var protocol = ActiveProtocolType?.ToString() ?? "Unknown";

            // Captured before the teardown, which clears it. Without this the reconnect
            // would come back in Auto mode and the Home card would forget the server the
            // user picked.
            var server = ActiveServer;

            SetState(VpnState.Reconnecting, reason);

            _health.Stop();
            await SafeTeardownAsync();
            ActiveProtocolType = null;
            SetState(VpnState.Disconnected, reason);
            ConnectionError?.Invoke(this, new ConnectionErrorEventArgs(protocol, reason, true));

            ScheduleReconnect(reason, immediate: false, server);
        }

        private void ScheduleReconnect(string reason, bool immediate, ServerInfo? server = null)
        {
            if (!_userWantsConnection) return;

            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            _reconnectCts = new CancellationTokenSource();
            var ct = _reconnectCts.Token;

            var target = server ?? ActiveServer;
            var delay = immediate
                ? TimeSpan.Zero
                : ReconnectBackoff[Math.Min(_reconnectAttempt, ReconnectBackoff.Length - 1)];

            OnProtocolOutput(this, $"[recover] reconnecting in {delay.TotalSeconds:F0}s ({reason})");

            _ = Task.Run(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested || !_userWantsConnection) return;

                    switch (State)
                    {
                        case VpnState.Disconnected:
                            break;

                        // Someone else already got there — a manual connect, or an earlier
                        // scheduled attempt. Nothing to do and nothing to retry.
                        case VpnState.Connected:
                        case VpnState.Connecting:
                        case VpnState.Reconnecting:
                            return;

                        // Mid-teardown or wedged in Error. Come back rather than abandoning
                        // recovery for the rest of the session.
                        default:
                            ScheduleReconnect(reason, immediate: false, target);
                            return;
                    }

                    _reconnectAttempt++;
                    await ConnectAsync(target, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    OnProtocolOutput(this, $"[recover] attempt failed: {ex.Message}");
                    ScheduleReconnect(reason, immediate: false, target);
                }
            }, ct);
        }

        private TunnelHealthMonitor.Endpoint CurrentEndpoint() => _endpoint;
        private TunnelHealthMonitor.Endpoint _endpoint;

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
        ///
        /// A revoke — the user granting the VPN slot to another app — is the one case that
        /// must <i>not</i> come back automatically. There is nothing to come back to: the
        /// consent is gone, and retrying would fight the other app for it.
        /// </summary>
        private async Task HandleUnexpectedDropAsync(string reason)
        {
            if (State != VpnState.Connected) return;

            var protocol = ActiveProtocolType?.ToString() ?? "Unknown";
            SetState(VpnState.Disconnecting, reason);

            _health.Stop();
            await SafeTeardownAsync();
            ActiveProtocolType = null;
            SetState(VpnState.Disconnected, reason);

            var willRetry = _userWantsConnection;
            ConnectionError?.Invoke(this, new ConnectionErrorEventArgs(protocol, reason, willRetry));

            if (willRetry) ScheduleReconnect(reason, immediate: false);
        }

        // ── Connect helpers ─────────────────────────────────────────────────

        private async Task<(IVpnProtocol Protocol, ProtocolType Type, ProtocolConfig Config)> ConnectWithFallbackAsync(
            ServerConnection connection, ProtocolType[] available, CancellationToken ct)
        {
            Exception? lastEx = null;

            for (int i = 0; i < available.Length; i++)
            {
                var protocolType = available[i];
                IVpnProtocol? protocol = null;

                try
                {
                    var config = await _protocolFactory.CreateConfigAsync(protocolType, connection, ct);
                    protocol = _protocolFactory.Create(protocolType);
                    Attach(protocol);

                    if (config is XrayConfig xc)
                    {
                        OnProtocolOutput(this,
                            $"[{protocolType}] {xc.Link.Host} -> {xc.Link.DialAddress}:{xc.Link.Port}" +
                            (xc.Link.ResolvedHost is null ? " (DNS FAILED, using hostname)" : ""));

                        // Public handshake parameters only — never the credential. These are
                        // what a server-side provisioning mistake corrupts, and comparing
                        // them against a known-good link is the fastest way to spot it.
                        OnProtocolOutput(this, xc.Link.Protocol == ProtocolType.Vless
                            ? $"[{protocolType}] sni={xc.Link.Sni} fp={xc.Link.Fingerprint} " +
                              $"flow={xc.Link.Flow ?? "-"} pbk={xc.Link.PublicKey} sid={xc.Link.ShortId} " +
                              $"net={xc.Link.Network} sec={xc.Link.Security}"
                            : $"[{protocolType}] sni={xc.Link.Sni} alpn=[{string.Join(',', xc.Link.Alpn)}] " +
                              $"obfs={xc.Link.Obfs ?? "-"} hop={xc.Link.PortRange ?? "-"}");
                    }

                    await protocol.ConnectAsync(config, ct);

                    // Starting is not the same as working: the core comes up happily and
                    // only fails per-connection at dial time, which used to leave the app
                    // reporting ЗАЩИЩЕНО over a tunnel carrying nothing. Prove the outbound
                    // actually reaches the internet before accepting it — and do it before
                    // the TUN exists, so a rejected protocol costs the user nothing.
                    if (!await RunPreflightAsync(
                            config is XrayConfig pc ? pc.SocksPort : XrayConfig.DefaultSocksPort, ct))
                        throw new InvalidOperationException(
                            $"{protocolType} came up but carried no traffic " +
                            $"(preflight: direct={LastPreflightDirectIp ?? "—"}, " +
                            $"proxied={LastPreflightProxiedIp ?? "—"}).");

                    if (lastEx != null)
                        ProtocolFallback?.Invoke(this, new ProtocolFallbackEventArgs(
                            available[i - 1].ToString(), protocolType.ToString(), lastEx.Message));

                    return (protocol, protocolType, config);
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

        private static TunnelOptions BuildTunnelOptions(ServerInfo? server, ProtocolConfig config) => new()
        {
            // Single source for the three places that must agree: hev's YAML, the Android
            // Builder.AddAddress call, and the Windows route/netsh commands.
            TunAddress = HevTunnelConfig.Ipv4Address,
            TunPrefix = 30,
            Mtu = HevTunnelConfig.Mtu,
            DnsServers = ["1.1.1.1", "8.8.8.8"],
            BypassApps = [],
            AllTraffic = true,
            BypassIps = OffTunnelAddresses(config),
            NodeAddress = NodeAddress(config),

            // Whatever the core actually bound, not the conventional default — the bridge
            // has to dial the same one.
            SocksPort = config is XrayConfig xc ? xc.SocksPort : XrayConfig.DefaultSocksPort
        };

        /// <summary>
        /// The only thing that gets a host route around the tunnel: the node itself.
        /// Without it the core's transport is carried by the tunnel it is feeding and
        /// deadlocks.
        ///
        /// <list type="bullet">
        /// <item>the node — always;</item>
        /// <item>nothing else, on purpose.</item>
        /// </list>
        ///
        /// The resolvers deliberately do <b>not</b> appear here. Exempting them would make
        /// DNS work by sending every query out in clear over the physical link — the same
        /// leak the routing config used to have, relocated into the route table. They are
        /// carried by the tunnel like everything else.
        ///
        /// Only a literal IP is usable, since a hostname cannot be a route; a failed
        /// pre-resolution therefore yields nothing and the platform decides whether it can
        /// proceed. See <see cref="TunnelOptions.BypassIps"/>.
        /// </summary>
        private static string[] OffTunnelAddresses(ProtocolConfig config) =>
            NodeAddress(config) is { } node ? [node] : [];

        /// <summary>The node's literal IP, or null when pre-resolution failed.</summary>
        private static string? NodeAddress(ProtocolConfig config) =>
            config is XrayConfig xc && System.Net.IPAddress.TryParse(xc.Link.DialAddress, out var ip)
                ? ip.ToString()
                : null;

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

        /// <summary>
        /// The fallback order with a just-failed protocol moved to the back instead of
        /// removed. Removing it would be wrong: Hysteria2 failing on one operator's mobile
        /// network says nothing about the Wi-Fi the phone reaches ten minutes later, and a
        /// permanently excluded protocol is a permanently degraded connection.
        /// </summary>
        private ProtocolType[] OrderProtocols(ProtocolType[] available)
        {
            if (_demotedProtocol is not { } demoted || available.Length < 2) return available;
            if (!available.Contains(demoted)) return available;

            return [.. available.Where(p => p != demoted), demoted];
        }

        private void SetState(VpnState newState, string? reason)
        {
            var old = State;
            State = newState;
            StateChanged?.Invoke(this, new VpnStateChangedEventArgs(old, newState, reason));
        }
    }
}
