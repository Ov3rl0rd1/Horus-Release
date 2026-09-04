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
        private readonly IAuthService _auth;
        private readonly IErrorReportingService _errorReporting;

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
        public string? ActiveOfferId { get; private set; }

        /// <summary>What the node calls the active offer. Shown to the user.</summary>
        public string? ActiveOfferLabel { get; private set; }
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
        /// Whether a connect can succeed at all right now.
        ///
        /// <para>Every route into the API needs a session, and at process start there is a
        /// window where the intent has been restored but the session has not been read back
        /// from storage yet. The network monitor starts in this constructor and its first
        /// callbacks arrive within milliseconds, so without this gate the debounced recovery
        /// fires into that window: observed twice on device as
        /// <c>attempt failed: Нет активной сессии</c>, three seconds after launch.</para>
        ///
        /// <para>The cost was not just a wasted attempt. The backoff ladder is advanced
        /// before the attempt runs, so a failure that had nothing to do with the network
        /// pushed the next real try from 3 seconds out to 10 — and on a cold start after a
        /// reboot, where the session is always loaded late, that would happen every time.</para>
        /// </summary>
        private bool CanAttemptConnection => _auth.IsAuthenticated;

        /// <summary>
        /// The protocol that just failed <i>while working</i>, so the next attempt starts
        /// after it. This is what fixes the reported case: Hysteria2 connects fine on
        /// Wi-Fi, the phone moves to mobile where that operator blocks it, and without this
        /// every reconnect picks Hysteria2 again because it is first in the fallback order.
        /// </summary>
        private string? _demotedOffer;

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

        /// <summary>
        /// How recently the tunnel must have carried something for a platform report of
        /// trouble to be disbelieved.
        ///
        /// <para>Deliberately longer than RethinkDNS's 30 seconds, because our sampling is
        /// coarser: the health monitor reads the counters every 90 seconds while the screen
        /// is off, so a shorter window would just mean "we have not looked recently" rather
        /// than "nothing came back".</para>
        /// </summary>
        private static readonly TimeSpan CarriedRecentlyWindow = TimeSpan.FromMinutes(3);

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
            IAuthService auth,
            IErrorReportingService errorReporting,
            TunnelHealthMonitor health,
            INetworkMonitor network)
        {
            _platform = platform;
            _protocolFactory = protocolFactory;
            _routing = routing;
            _traffic = traffic;
            _api = api;
            _auth = auth;
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
            yield return new("offer", ActiveOfferId);
            yield return new("demoted", _demotedOffer?.ToString());
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

            yield return new("usingCachedEndpoints", _usingCachedConnection.ToString());

            foreach (var kv in ConnectionCache.Describe()) yield return new("cache." + kv.Key, kv.Value);
            foreach (var kv in VpnIntent.Describe()) yield return new("intent." + kv.Key, kv.Value);
            foreach (var kv in UserPreferences.Describe()) yield return new("pref." + kv.Key, kv.Value);
        }

        /// <summary>
        /// Connects, binding the account to <paramref name="server"/> first when one was
        /// chosen.
        ///
        /// <para>Selection and connection are now two calls, not one. The API used to pick
        /// a node as a side effect of handing out links, which meant the app could not
        /// honour a user's choice at all; <c>POST /servers/select</c> reserves the slot and
        /// <c>GET /servers/connect</c> only reads back what the account is bound to. Passing
        /// null means "leave the binding alone" — the connect endpoint still auto-picks for
        /// an account that has never been bound.</para>
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
                await BindServerIfNeededAsync(server, ct);

                var connection = await FetchConnectionAsync(server, ct);

                bool granted = await _platform.RequestPermissionsAsync();
                if (!granted)
                    throw new InvalidOperationException(
                        OperatingSystem.IsWindows()
                            // On Windows this is about elevation, not a user-facing consent
                            // dialog — creating a TUN adapter needs administrator rights.
                            ? "Для создания туннеля нужны права администратора. " +
                              "Запустите Horus от имени администратора."
                            : "Разрешение на VPN не выдано.");

                // Only what this node actually published, in our preference order, with
                // anything that just failed on this network moved to the back rather than
                // dropped — the same protocol may well be the best choice again once the
                // link changes.
                var available = OrderCandidates(connection.Candidates());

                if (available.Count == 0)
                    throw new InvalidOperationException(
                        "Сервер не предложил ни одного поддерживаемого способа подключения.");

                var (protocol, usedConfig) =
                    await ConnectWithFallbackAsync(available, ct);

                ActiveProtocol = protocol;
                ActiveOfferId = usedConfig.OfferId;
                ActiveOfferLabel = usedConfig.DisplayName;

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

                // The API is the authority on which node this actually is. In Auto mode
                // nothing was passed in, and even when it was, the connect response carries
                // a display name the ping catalogue does not have — so prefer it and fall
                // back to the caller's idea only if the API said nothing.
                var previous = ActiveServer;
                ActiveServer = connection.Server?.ToServerInfo() ?? server;
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
                    ActiveOfferId ?? "Unknown", ex.Message, false));
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
            _demotedOffer = null;

            if (State == VpnState.Disconnected) return;

            SetState(VpnState.Disconnecting, null);
            try
            {
                _health.Stop();
                _traffic.Stop();
                await _platform.StopTunnelAsync();
                await DetachProtocolAsync();

                ActiveServer = null;
                ActiveOfferId = null;
            ActiveOfferLabel = null;
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
            _errorReporting.SetContext("offer", ActiveOfferId);
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

            if (e.Health == TunnelHealth.OutboundDead && ActiveOfferId is { } failing)
            {
                _demotedOffer = failing;
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

            // The platform's verdict is authoritative about its own probe, not about our
            // tunnel. Those are the same thing while the device is awake and different
            // while it is not: in Doze the probe is deferred or refused like any other
            // background request, so VALIDATED is withdrawn from a tunnel that is carrying
            // traffic perfectly well. Acting on it anyway is how a rebuild — with all the
            // background-start restrictions that implies — was being triggered every time
            // the screen had been off for a while.
            //
            // So it is checked against evidence: if bytes came back through the tunnel
            // recently, the report is about the probe, not about us. RethinkDNS applies the
            // same rule with a 30-second window and ignores the report outright.
            var since = Environment.TickCount64 - _health.LastCarriedAtMs;
            if (_health.LastCarriedAtMs != 0 && since < CarriedRecentlyWindow.TotalMilliseconds)
            {
                OnProtocolOutput(this,
                    $"[health] {reason}, but the tunnel carried traffic {since / 1000}s ago — ignoring");
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
                    if (!CanAttemptConnection)
                    {
                        // Startup has not restored the session yet. Dropping the request is
                        // right rather than deferring it: App.OnStart calls
                        // TryRestoreOrAutoConnectAsync once the session is in place, which
                        // covers exactly this case and does it without burning an attempt.
                        Diag.Info("recover", $"no session yet, leaving {reason} to startup");
                        break;
                    }

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
        /// Brings the tunnel back at startup, for either of two distinct reasons.
        ///
        /// <para><b>Restore.</b> <see cref="VpnIntent"/> says the user turned the VPN on and
        /// never turned it off, yet nothing is running — so it died with the process and the
        /// user was never told. This is the safety net under a system that declined to
        /// restart the sticky service, and it is not gated on any preference: the user
        /// already asked, and asking again by leaving them unprotected is not a choice they
        /// made.</para>
        ///
        /// <para><b>Auto-connect.</b> The user asked for every launch to start protected,
        /// even one after an explicit disconnect. That is what
        /// <see cref="UserPreferences.AutoConnectOnLaunch"/> means, and it is off by
        /// default because hijacking a launch surprises someone who opened the app to
        /// change a setting.</para>
        ///
        /// <para>Must be called <i>after</i> the session has been restored — see
        /// <see cref="CanAttemptConnection"/> for what happens when something tries earlier.</para>
        /// </summary>
        public async Task TryRestoreOrAutoConnectAsync(CancellationToken ct = default)
        {
            if (State != VpnState.Disconnected) return;

            var restoring = VpnIntent.Active;
            if (!restoring && !UserPreferences.AutoConnectOnLaunch) return;

            if (!CanAttemptConnection)
            {
                Diag.Warn("connect", "startup connect skipped: no session");
                return;
            }

            Diag.Info("connect", restoring
                ? "restoring tunnel — intent survived, nothing was running"
                : "auto-connect on launch");

            try { await ConnectAsync(ActiveServer, ct); }
            catch (Exception ex) { Diag.Warn("connect", $"startup connect failed: {ex.Message}"); }
        }

        /// <summary>
        /// Tears the connection down and brings it back, with backoff. Gated on
        /// <see cref="_userWantsConnection"/> so it can never fight a user who pressed
        /// disconnect.
        /// </summary>
        private async Task RecoverAsync(string reason)
        {
            if (!WantsConnection) return;

            var protocol = ActiveOfferId ?? "Unknown";

            // Captured before the teardown, which clears it. Without this the reconnect
            // would come back in Auto mode and the Home card would forget the server the
            // user picked.
            var server = ActiveServer;

            SetState(VpnState.Reconnecting, reason);

            _health.Stop();
            _traffic.Stop();

            // The core is replaced; the interface is not. Tearing the TUN down here meant
            // every reconnect had to build a foreground service from the background, which
            // Android 12+ restricts and Doze can refuse outright — so a reconnect that
            // happened while the screen was off could simply never come back. Leaving it up
            // also spares every app on the device from watching its network vanish and
            // return. RethinkDNS never destroys its service for a rebuild either.
            //
            // The interface is not held indefinitely: HoldTunnelDuringRecovery gives up
            // after enough consecutive failures, because a TUN with no working core behind
            // it is a black hole, and a user who cannot reach anything is worse off than one
            // with no VPN.
            if (!HoldTunnelDuringRecovery())
            {
                OnProtocolOutput(this,
                    $"[recover] {_reconnectAttempt} attempts failed; dropping the interface " +
                    "rather than black-holing traffic");
                await SafeTeardownAsync();
            }
            else
            {
                try { await DetachProtocolAsync(); } catch { /* already dead */ }
            }

            ActiveOfferId = null;
            ActiveOfferLabel = null;
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

                    // The backoff ladder exists to stop us hammering a network that is not
                    // working. A missing session is not that, so it must not cost a rung —
                    // otherwise an unrelated failure at startup makes the next genuine
                    // attempt wait ten seconds, then thirty.
                    if (IsSessionFailure(ex))
                        _reconnectAttempt = Math.Max(0, _reconnectAttempt - 1);

                    ScheduleReconnect(reason, immediate: false, target);
                }
            }, ct);
        }

        /// <summary>
        /// Whether the failure is about who we are rather than about the network.
        ///
        /// <para>Matched on type where possible. The string check covers
        /// <c>RequireSession</c>, which throws a plain
        /// <see cref="InvalidOperationException"/> before any request is made — narrow
        /// enough not to swallow a real transport error.</para>
        /// </summary>
        private static bool IsSessionFailure(Exception ex) =>
            ex is UnauthorizedAccessException
            || (ex is InvalidOperationException && ex.Message.Contains("сесси", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Whether the tunnel interface should stay up across this recovery.
        ///
        /// <para>Yes while there is reason to think the next attempt will work. After
        /// <see cref="MaxHeldAttempts"/> consecutive failures there is not, and holding a
        /// TUN whose core cannot dial stops being protection and becomes a black hole.</para>
        /// </summary>
        private bool HoldTunnelDuringRecovery() => _reconnectAttempt < MaxHeldAttempts;

        /// <summary>
        /// Consecutive failed reconnects before the interface is torn down. Four is where
        /// the backoff ladder reaches two minutes — by then the problem is not transient.
        /// </summary>
        private const int MaxHeldAttempts = 4;

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

            var protocol = ActiveOfferId ?? "Unknown";
            SetState(VpnState.Disconnecting, reason);

            _health.Stop();
            await SafeTeardownAsync();
            ActiveOfferId = null;
            ActiveOfferLabel = null;
            SetState(VpnState.Disconnected, reason);

            // Consults the persisted intent as well: a revoke clears it from inside the
            // tunnel service, and retrying then would mean fighting whichever app the user
            // just handed the VPN slot to.
            var willRetry = WantsConnection;
            ConnectionError?.Invoke(this, new ConnectionErrorEventArgs(protocol, reason, willRetry));

            if (willRetry) ScheduleReconnect(reason, immediate: false);
        }

        // ── Connect helpers ─────────────────────────────────────────────────

        private async Task<(IVpnProtocol Protocol, ProtocolConfig Config)> ConnectWithFallbackAsync(
            IReadOnlyList<ConnectionCandidate> available, CancellationToken ct)
        {
            Exception? lastEx = null;

            for (int i = 0; i < available.Count; i++)
            {
                var candidate = available[i];
                IVpnProtocol? protocol = null;

                try
                {
                    var config = await _protocolFactory.CreateConfigAsync(candidate, ct);
                    protocol = _protocolFactory.Create();
                    Attach(protocol);

                    if (config is XrayConfig xc) LogOfferParameters(xc);

                    await protocol.ConnectAsync(config, ct);

                    // Starting is not the same as working: the core comes up happily and
                    // only fails per-connection at dial time, which used to leave the app
                    // reporting ЗАЩИЩЕНО over a tunnel carrying nothing. Prove the outbound
                    // actually reaches the internet before accepting it — and do it before
                    // the TUN exists, so a rejected protocol costs the user nothing.
                    if (!await RunPreflightAsync(
                            config is XrayConfig pc ? pc.SocksPort : XrayConfig.DefaultSocksPort, ct))
                        throw new InvalidOperationException(
                            $"{candidate.Id} came up but carried no traffic " +
                            $"(preflight: direct={LastPreflightDirectIp ?? "—"}, " +
                            $"proxied={LastPreflightProxiedIp ?? "—"}).");

                    if (lastEx != null)
                        ProtocolFallback?.Invoke(this, new ProtocolFallbackEventArgs(
                            available[i - 1].ToString(), candidate.ToString(), lastEx.Message));

                    return (protocol, config);
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
                        candidate.Id, ex.Message, protocolLog);

                    if (i == available.Count - 1)
                    {
                        // Every endpoint failed. If they came from the device, the most
                        // likely explanation is that they are stale — the account was moved
                        // from another device, or the node re-provisioned — and the API has
                        // the answer. Dropping the cache here is what makes the next attempt
                        // ask for it.
                        if (_usingCachedConnection)
                        {
                            ConnectionCache.Invalidate("every cached endpoint failed");
                            _usingCachedConnection = false;
                        }

                        LastProtocolLog = protocolLog;
                        await TryBuildFailureArchiveAsync();
                        throw new InvalidOperationException(
                            "Не удалось подключиться ни одним способом " +
                            $"({string.Join(", ", available.Select(c => c.ToString()))}). " +
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

            // Deliberately NOT also calling _errorReporting.AppendLog here. It forwards to
            // Diag, so doing both wrote every protocol line twice — 45% of the event log
            // measured on a device, halving both the useful history in the ring buffer and
            // the time before the 512 KB file rotates.
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
            SocksPort = config is XrayConfig xc ? xc.SocksPort : XrayConfig.DefaultSocksPort,

            // False unless the user asked otherwise. Not calling setMetered at all — which
            // is what this used to do — leaves Android treating the tunnel as mobile data
            // and restricting every background app on the device in Doze.
            Metered = UserPreferences.MeteredConnection
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

        /// <summary>
        /// The node's literal IP, or null when there is not one.
        ///
        /// <para>Null in two different situations, and both are handled the same way on
        /// purpose. Pre-resolution can fail — in which case the connect has already been
        /// abandoned before reaching here. Or the protocol has no node address at all:
        /// olcRTC reaches a signalling provider rather than the node, so there is nothing
        /// to route around the tunnel. On Android neither matters, because the app's whole
        /// UID is excluded; 🔧 on Windows an olcRTC outbound would need the provider's
        /// address bypassed, which the API does not publish.</para>
        /// </summary>
        private static string? NodeAddress(ProtocolConfig config) =>
            config is XrayConfig xc && System.Net.IPAddress.TryParse(xc.NodeAddress, out var ip)
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
        /// Logs what the node offered, without the credential.
        ///
        /// <para>Much shorter than it used to be, and deliberately so. This used to print
        /// the REALITY handshake parameters field by field, because the app assembled them
        /// and a server-side provisioning mistake showed up as a subtly wrong config. The
        /// node builds the outbound now, so there is nothing here the app could have got
        /// wrong — what is worth recording is which offer was chosen and where it dials.</para>
        /// </summary>
        private void LogOfferParameters(XrayConfig config)
        {
            OnProtocolOutput(this,
                $"[{config.OfferId}] {config.ProtocolName} -> {config.NodeAddress ?? "no address"} " +
                $"(socks {config.SocksPort})");
        }

        /// <summary>
        /// The node's endpoints, from the device when they are usable and from the API when
        /// they are not.
        ///
        /// <para>The cache is tried first because the request in front of every reconnect was
        /// pure latency on the one path where there is no working tunnel to carry it — and
        /// it made recovery depend on the API being reachable, which the tunnel does not.
        /// Nothing here can tell whether stored keys are still valid; that is decided by the
        /// connect attempt, and <see cref="ConnectionCache.Invalidate"/> is called when every
        /// endpoint has failed.</para>
        ///
        /// <para>The binding step above is what keeps this honest for the "changed region on
        /// another device" case: a user who picks a different node goes through
        /// <c>select</c>, and the id mismatch discards the cache before it is read.</para>
        /// </summary>
        private async Task<ServerConnection> FetchConnectionAsync(ServerInfo? server, CancellationToken ct)
        {
            var cached = ConnectionCache.Read(server?.Id ?? ActiveServer?.Id);
            if (cached is not null)
            {
                _usingCachedConnection = true;
                return cached;
            }

            _usingCachedConnection = false;
            var fresh = await _api.GetServerConnectionAsync(ct);
            ConnectionCache.Write(fresh);
            return fresh;
        }

        /// <summary>
        /// Whether this attempt is running on stored endpoints. Decides whether a total
        /// failure should discard them and try the API once more.
        /// </summary>
        private bool _usingCachedConnection;

        /// <summary>
        /// Reserves a slot on the chosen node, when the user chose one.
        ///
        /// <para>Skipped for auto: asking the API to auto-pick on every connect would move
        /// an account off a node it is happily bound to, which costs a re-provision and
        /// loses the stability the binding exists to provide. A user who wants a different
        /// node says so on the Servers screen, and that is the only thing that moves them.</para>
        ///
        /// <para>Skipped too when the account is already on that node — <c>select</c> is
        /// idempotent, but a round trip on every connect is not free on a mobile link.</para>
        /// </summary>
        private async Task BindServerIfNeededAsync(ServerInfo? server, CancellationToken ct)
        {
            if (server is null) return;
            if (ActiveServer?.Id == server.Id) return;

            Diag.Info("connect", $"binding to server {server.Id} ({server.Location})");
            var bound = await _api.SelectServerAsync(server.Id, ct);

            // The API's name is the authoritative one: the ping catalogue has no display
            // name at all, so this is the first point at which the node can be called
            // anything better than its city.
            OnProtocolOutput(this, $"[api] bound to {bound.Name} ({bound.Location})");
        }

        /// <summary>
        /// The node's endpoints in the order they should be tried.
        ///
        /// <para>Primary key is this build's preference (see <see cref="FallbackOrder"/>),
        /// not the node's: olcRTC is capable of getting through where the others cannot,
        /// but it is a video-codec transport with timers running at frame rate, so paying
        /// for it before knowing it is needed would cost every user battery to help a few.
        /// The fallback loop reaches it when the cheaper options fail, which is exactly
        /// when it earns its cost.</para>
        ///
        /// <para>A just-failed protocol is moved to the back rather than removed. Removing
        /// it would be wrong: Hysteria2 failing on one operator's mobile network says
        /// nothing about the Wi-Fi the phone reaches ten minutes later, and a permanently
        /// excluded protocol is a permanently degraded connection.</para>
        /// </summary>
        private List<ConnectionCandidate> OrderCandidates(IReadOnlyList<ConnectionCandidate> offered)
        {
            // The node's order is kept as given: a profile lists its preferred offer first,
            // and the node is the side that knows what it is actually running. This used to
            // impose the app's own protocol preference, which stopped being expressible when
            // offers became free-form ids — and was the wrong place to decide anyway.
            var ordered = offered.ToList();

            if (_demotedOffer is not { } demoted || ordered.Count < 2) return ordered;
            if (ordered.All(c => c.Id == demoted)) return ordered;

            // Moved to the back rather than dropped: an offer that failed on one network
            // may be the best choice on the next one.
            return [.. ordered.Where(c => c.Id != demoted), .. ordered.Where(c => c.Id == demoted)];
        }

        private void SetState(VpnState newState, string? reason)
        {
            var old = State;
            State = newState;
            StateChanged?.Invoke(this, new VpnStateChangedEventArgs(old, newState, reason));
        }
    }
}
