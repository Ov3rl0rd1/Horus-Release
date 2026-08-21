using Horus.Application.Diagnostics;
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
        /// The gate every automatic path reads.
        ///
        /// <para>Two sources, and both have to agree. The in-memory flag is the fast one,
        /// set the moment the user presses connect. <see cref="VpnIntent"/> is the durable
        /// one, and it is the one that can be cleared from outside this class — a revoke
        /// arrives at the tunnel service, not here, and without consulting the persisted
        /// value the manager would keep trying to reconnect into a VPN slot another app now
        /// holds.</para>
        /// </summary>
        private bool WantsConnection => _userWantsConnection && VpnIntent.Active;

        /// <summary>
        /// The protocol that just failed <i>while working</i>, so the next attempt starts
        /// after it. This is what fixes the reported case: Hysteria2 connects fine on
        /// Wi-Fi, the phone moves to mobile where that operator blocks it, and without this
        /// every reconnect picks Hysteria2 again because it is first in the fallback order.
        /// </summary>
        private ProtocolType? _demotedProtocol;

        private int _reconnectAttempt;
        private CancellationTokenSource? _reconnectCts;

        /// <summary>
        /// How long a tunnel may be diagnosed as "the link is down, not us" before it is
        /// rebuilt anyway. Long enough to sit out a lift or a tunnel on a train, short
        /// enough that a misdiagnosis costs minutes rather than the rest of the day.
        /// </summary>
        private static readonly TimeSpan DeadTunnelGrace = TimeSpan.FromMinutes(4);

        /// <summary>Silence longer than this means the previous unhealthy episode ended.</summary>
        private static readonly TimeSpan UnhealthyEpisodeGap = TimeSpan.FromMinutes(6);

        private DateTimeOffset? _unhealthySince;
        private DateTimeOffset? _lastUnhealthyAt;

        /// <summary>
        /// Every "look at the tunnel" request funnels through here so a burst of network
        /// events becomes one action. A lift or a train tunnel used to produce a probe per
        /// transition — five to ten concurrent six-second probes racing to declare the
        /// tunnel dead. See <see cref="RecoveryTrigger"/>.
        /// </summary>
        private readonly RecoveryTrigger _recovery;

        /// <summary>
        /// Set when a network change replaced the link carrying the tunnel, consumed by the
        /// debounced recovery. A handover is the one case where the sessions are known to be
        /// dead rather than merely suspected, so it earns a cheap targeted response the
        /// other triggers do not.
        /// </summary>
        private int _handoverPending;

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

            _recovery = new RecoveryTrigger(OnRecoveryDue);

            _health.Unhealthy += OnUnhealthy;
            _network.NetworkChanged += OnNetworkChanged;

            // The two push signals. Neither costs a timer, and between them they cover the
            // cases a periodic check is worst at: the platform noticing the tunnel stopped
            // carrying, and the user coming back to a device that broke while it slept.
            _network.TunnelSuspect += OnTunnelSuspect;
            _network.DeviceWoke += OnDeviceWoke;
            _network.DeviceIdleChanged += OnDeviceIdleChanged;

            // A crash or a system kill leaves the intent set but nothing running. Adopting
            // it here is what lets the sticky service restart, the boot receiver and the
            // after-update path all reuse the ordinary connect path rather than each
            // carrying their own idea of whether the VPN should be on.
            _userWantsConnection = VpnIntent.Active;

            StateSnapshot.Register("vpn", 10, DescribeState);

            _network.Start();
        }

        /// <summary>
        /// What the app believed at the moment a diagnostics archive was collected. Most
        /// support exchanges are spent establishing exactly these facts.
        /// </summary>
        private IEnumerable<KeyValuePair<string, string?>> DescribeState()
        {
            yield return new("state", State.ToString());
            yield return new("userWantsConnection", _userWantsConnection.ToString());
            yield return new("tunnelState", _platform.CurrentState.ToString());
            yield return new("protocol", ActiveProtocolType?.ToString());
            yield return new("demoted", _demotedProtocol?.ToString());
            yield return new("server", ActiveServer?.Name);
            yield return new("reconnectAttempt", _reconnectAttempt.ToString());
            yield return new("pendingRecovery", _recovery.Pending);
            yield return new("unhealthySince", _unhealthySince?.ToLocalTime().ToString("HH:mm:ss"));
            yield return new("socksPort", _endpoint.SocksPort.ToString());
            yield return new("coreRunning", XrayProtocol.IsCoreRunning.ToString());
            yield return new("coreVersion", XrayProtocol.CoreVersion);
            yield return new("corePaused", XrayProtocol.IsPaused?.ToString() ?? "unsupported");
            yield return new("preflightDirectIp", LastPreflightDirectIp);
            yield return new("preflightProxiedIp", LastPreflightProxiedIp);

            long[] counters;
            try { counters = _platform.GetTunnelStats(); } catch { counters = []; }
            yield return new("tunnelCounters", counters.Length >= 4
                ? $"txp {counters[0]}, tx {counters[1]}B, rxp {counters[2]}, rx {counters[3]}B"
                : "bridge not running");

            foreach (var kv in VpnIntent.Describe()) yield return new("intent." + kv.Key, kv.Value);
            foreach (var kv in UserPreferences.Describe()) yield return new("pref." + kv.Key, kv.Value);
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
            VpnIntent.Set();
            SetState(VpnState.Connecting, null);
            _protocolLogBuffer.Clear();

            // Once per connect, not per attempt: the fallback loop would otherwise wipe the
            // failing protocol's log on its way to trying the next one, which is exactly the
            // log needed to explain why the first one failed.
            //
            // Rotated rather than deleted. Deleting meant a session that died was
            // diagnosable right up until the app restarted and connected, at which point
            // the only record of what went wrong was erased — losing exactly the log worth
            // having.
            DiagnosticPaths.Rotate(DiagnosticPaths.XrayLog);

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

                // …and proof the service is viable, so a later system restart gets a full
                // budget rather than inheriting the failures that preceded this success.
                VpnIntent.ResetRestartBudget();

                // A connection that came up clears the dead-tunnel clock.
                _unhealthySince = null;
                _lastUnhealthyAt = null;

                _endpoint = new TunnelHealthMonitor.Endpoint(tunnelOptions.SocksPort);
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
            VpnIntent.Clear();
            _recovery.Cancel();
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

            // Neither check answered, which for these users usually means our own API is
            // blocked rather than that the tunnel is broken. This used to accept the
            // protocol on that basis, and that is how a tunnel that could not dial at all
            // reached the user showing ЗАЩИЩЕНО. Ask the core directly instead: a SOCKS5
            // CONNECT needs nothing of ours and its reply code says whether the core can
            // resolve and reach anything at all.
            var canDial = await SocksProbe.CanDialAsync(
                socksPort, "cloudflare.com", 443, TimeSpan.FromSeconds(8), ct);

            OnProtocolOutput(this, canDial
                ? "[preflight] Both checks silent, but the core dialled out — accepting."
                : "[preflight] Both checks silent and the core could not dial — rejecting.");

            return canDial;
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

            var now = DateTimeOffset.UtcNow;

            // A gap means the previous episode ended and this is a new one.
            if (_lastUnhealthyAt is { } last && now - last > UnhealthyEpisodeGap) _unhealthySince = null;
            _unhealthySince ??= now;
            _lastUnhealthyAt = now;

            if (e.Health == TunnelHealth.NoInternet)
            {
                // Holding rather than tearing down is right: killing the tunnel would drop
                // the user's traffic to the clear the moment connectivity returned, and on
                // Android it would also lose the foreground service that keeps the process
                // — and therefore the whole app — alive.
                //
                // But holding *indefinitely* was a liveness bug. The only way out was a
                // network-change event, and when the link never changes — Wi-Fi up the
                // whole time, the tunnel dead underneath it — no event ever arrives and the
                // VPN stays dead until the user notices. Observed on a real device.
                //
                // So the classifier gets a deadline. It is a heuristic; the invariant is
                // not. A tunnel that has carried nothing for minutes gets rebuilt whatever
                // the diagnosis says, because a wrong reconnect costs one attempt and a
                // missed one costs the user their VPN.
                var dead = now - _unhealthySince.Value;
                if (dead < DeadTunnelGrace)
                {
                    OnProtocolOutput(this,
                        $"[health] no usable link; holding for now ({dead.TotalSeconds:F0}s of " +
                        $"{DeadTunnelGrace.TotalSeconds:F0}s)");
                    _health.Start(CurrentEndpoint());
                    return;
                }

                OnProtocolOutput(this,
                    $"[health] still carrying nothing after {dead.TotalMinutes:F0} min — rebuilding anyway");
            }

            if (e.Health == TunnelHealth.OutboundDead && ActiveProtocolType is { } failing)
            {
                _demotedProtocol = failing;
                OnProtocolOutput(this, $"[health] demoting {failing} for the next attempt");
            }

            _ = RecoverAsync(e.Health.ToString());
        }

        /// <summary>
        /// The platform believes the tunnel has stopped carrying — either it withdrew
        /// <c>NET_CAPABILITY_VALIDATED</c> after its own probe through the VPN failed, or
        /// <c>ConnectivityDiagnosticsManager</c> reported a suspected data stall.
        ///
        /// This is as authoritative as it gets and it arrives without us asking, so it goes
        /// straight through rather than being fed back into the counter heuristics. The
        /// link underneath is still checked, because a phone that has simply lost signal
        /// produces the same signal and reconnecting would be pointless.
        /// </summary>
        private void OnTunnelSuspect(object? sender, string reason)
        {
            if (State != VpnState.Connected) return;

            if (!_network.IsOnline)
            {
                OnProtocolOutput(this, $"[health] {reason}, but the link is down too — waiting");
                return;
            }

            OnProtocolOutput(this, $"[health] {reason}");
            OnUnhealthy(this, new TunnelHealthEventArgs(TunnelHealth.OutboundDead, reason));
        }

        /// <summary>
        /// The user is back. Whatever broke while the phone slept broke unobserved and
        /// costs nothing until now — so this is the moment to look, and looking here is
        /// what replaces checking all night.
        /// </summary>
        private void OnDeviceWoke(object? sender, EventArgs e)
        {
            if (State != VpnState.Connected) return;

            // Cut the current sleep short. The loop had picked a 90-second interval while
            // the screen was off, and waiting it out was most of the delay the user saw.
            _health.WakeNow();
            _network.ReportTunnelSuspect();
        }

        /// <summary>
        /// The device entered or left Doze.
        ///
        /// <para>Passed straight to the core, which has housekeeping loops that would
        /// otherwise keep waking the device all night to tidy structures nothing is
        /// touching. Not gated on <see cref="VpnState.Connected"/>: the loops belong to the
        /// core, not to the tunnel, and they keep running between a teardown and the next
        /// connect.</para>
        ///
        /// <para>Entering Doze deliberately does <i>not</i> stop the health monitor. Doze
        /// is exactly when a tunnel dies unobserved, and its idle cadence is already 60
        /// seconds with aligned wakeups.</para>
        /// </summary>
        private void OnDeviceIdleChanged(object? sender, bool idle)
        {
            XrayProtocol.SetPaused(idle);
        }

        /// <summary>
        /// A handover is the one event counters cannot detect: every connection through the
        /// old link is already dead, and nothing reports it until something tries to use one.
        ///
        /// <para>Routed through <see cref="_recovery"/> rather than acted on directly. These
        /// events do not arrive one at a time: a lift, a train tunnel or the edge of Wi-Fi
        /// coverage produces five to ten transitions in twenty seconds, and this used to
        /// start a six-second probe for every one of them. The debounce turns that burst
        /// into a single check once the link settles.</para>
        /// </summary>
        private void OnNetworkChanged(object? sender, NetworkChangedEventArgs e)
        {
            OnProtocolOutput(this, $"[net] {e.Transport}, online={e.IsOnline}, handover={e.IsHandover}");

            if (!WantsConnection) return;
            if (!e.IsOnline) return; // nothing to do until a link exists

            if (e.IsHandover) Interlocked.Exchange(ref _handoverPending, 1);

            _recovery.Request($"network {e.Transport}{(e.IsHandover ? " (handover)" : "")}");
        }

        /// <summary>
        /// What the debounce eventually fires. By this point the link has been stable for
        /// <see cref="RecoveryTrigger.Debounce"/>, so the state read here is worth acting on
        /// — unlike the state during the burst that queued it.
        /// </summary>
        private Task OnRecoveryDue(string reason)
        {
            if (!WantsConnection) return Task.CompletedTask;

            var handover = Interlocked.Exchange(ref _handoverPending, 0) == 1;

            switch (State)
            {
                case VpnState.Connected:
                    // Drop the sessions before probing. Everything established over the old
                    // link is already dead, and without this the transport would sit on the
                    // dead path until an idle timeout measured in minutes — so the probe
                    // that follows would be measuring a connection nothing was going to
                    // rescue, and would conclude the tunnel needs rebuilding. Resetting
                    // costs one call and leaves the instance and the TUN standing; the
                    // probe's own dial is then what establishes the replacement.
                    if (handover) XrayProtocol.ResetConnections();

                    _health.ProbeNow(reason);
                    break;

                // Back online after a drop we chose not to tear down, or after a failed
                // attempt. Immediate, because the debounce has already provided the wait.
                case VpnState.Disconnected:
                    ScheduleReconnect(reason, immediate: true);
                    break;

                // Mid-flight. Whatever is running will reach a terminal state and the
                // ordinary paths take it from there.
                default:
                    break;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Connects at launch if the user asked for that and the VPN was on when the app
        /// last stopped.
        ///
        /// <para>Both conditions, not either. The preference alone would reconnect for
        /// someone who deliberately disconnected and then opened the app to change a
        /// setting; the intent alone would surprise someone who never asked for a launch to
        /// mean anything.</para>
        /// </summary>
        public async Task TryAutoConnectAsync(CancellationToken ct = default)
        {
            if (!UserPreferences.AutoConnectOnLaunch) return;
            if (!VpnIntent.Active) return;
            if (State != VpnState.Disconnected) return;

            Diag.Info("connect", "auto-connect on launch");
            try { await ConnectAsync(ActiveServer, ct); }
            catch (Exception ex) { Diag.Warn("connect", $"auto-connect failed: {ex.Message}"); }
        }

        /// <summary>
        /// Tears the connection down and brings it back, with backoff. Gated on
        /// <see cref="_userWantsConnection"/> so it can never fight a user who pressed
        /// disconnect.
        /// </summary>
        private async Task RecoverAsync(string reason)
        {
            if (!WantsConnection) return;

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
            if (!WantsConnection) return;

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
                    if (ct.IsCancellationRequested || !WantsConnection) return;

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

            // Consults the persisted intent as well: a revoke clears it from inside the
            // tunnel service, and retrying then would mean fighting whichever app the user
            // just handed the VPN slot to.
            var willRetry = WantsConnection;
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
            // Every connect, health and recovery line goes to logcat as well, on one tag.
            // This is the whole diagnostic surface of the tunnel in a shipping build; see
            // Diag for why the previous channel did not exist in Release.
            Diag.Write(line);

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
