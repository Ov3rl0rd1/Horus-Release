using System.Runtime.Versioning;
using Horus.Application;
using Horus.Application.Diagnostics;
using Horus.Application.Routing;
using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Platforms.Windows.Tunnel;
using Horus.Protocols;
using Change = Horus.Platforms.Windows.Tunnel.NetworkPathSelector.Change;

namespace Horus.Platforms.Windows
{
    /// <summary>
    /// Runs the VPN on Windows.
    ///
    /// <para><b>Separate from Android's VpnManager on purpose.</b> That class is built around
    /// a phone: Doze, a foreground service that must not be rebuilt from the background, a
    /// platform that revokes the VPN slot. None of that exists here, and what does — a game
    /// that must not be dropped, a desktop with several adapters, per-application routing —
    /// pulled the shared code in directions that were risks for the other platform.</para>
    ///
    /// <para><b>The core owns the TUN.</b> Connecting is two phases. Candidates (the node's
    /// offers, in order) are first proved on a core with only its SOCKS inbound — no
    /// adapter, so a fallback from one protocol to the next costs a few milliseconds and
    /// disturbs nothing. The working one is then started with the TUN inbound
    /// (<see cref="WindowsTunnelConfig"/>), which creates the adapter, its routes and DNS
    /// in-process.</para>
    ///
    /// <para><b>Never drop the game for nothing.</b> Every action that ends connections is
    /// gated on evidence, and the cheapest one that could work is tried first:</para>
    /// <list type="number">
    /// <item>A network change only resets sessions when the path the core was bound to is
    /// actually gone (<see cref="NetworkPathSelector"/>). Wi-Fi flapping on a PC that runs on
    /// a cable does nothing.</item>
    /// <item>A suspicion from the byte counts (<see cref="ProxyStallDetector"/>) buys a probe,
    /// nothing more.</item>
    /// <item>A failed probe first resets the transport's sessions and probes again, then
    /// swaps the proxy outbound for the next offer <i>in place</i> — the TUN, and every
    /// connection not carried by the dead outbound, stay up. Only when no offer works is
    /// the core rebuilt.</item>
    /// </list>
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public sealed class WindowsVpnController : IVpnController
    {
        private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan BlindProbeInterval = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan[] Backoff =
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)];

        /// <summary>Failed recoveries before the tunnel is dropped to give the user back a direct connection (unless the kill switch holds it).</summary>
        private const int MaxHeldRecoveries = 4;

        private static readonly (string Host, int Port)[] ProbeTargets = [("cloudflare.com", 443), ("1.1.1.1", 443)];

        private readonly IApiService _api;
        private readonly IAuthService _auth;
        private readonly ProtocolFactory _factory;
        private readonly WindowsVpnService _tunnel;
        private readonly ITrafficMonitorService _traffic;
        private readonly WindowsNetworkMonitor _network;
        private readonly WindowsSplitTunnelingService _split;
        private readonly WindowsConnectionMonitor _connections;
        private readonly IErrorReportingService _errors;
        private readonly SiteRuleStore? _siteRules;

        /// <summary>Connect, disconnect, recover and rule changes never overlap.</summary>
        private readonly SemaphoreSlim _ops = new(1, 1);
        private readonly ProxyStallDetector _stall = new();

        private Session? _session;
        private CancellationTokenSource? _sessionCts;
        private CancellationTokenSource? _retryCts;
        private bool _userWantsConnection;
        private string? _demotedOffer;
        private int _failedRecoveries;
        private int _reconnectAttempt;
        private string _lastHealth = "—";
        private bool _loopWarned;
        private CancellationTokenSource? _rulesDebounce;

        public WindowsVpnController(
            IApiService api,
            IAuthService auth,
            ProtocolFactory factory,
            IVpnPlatformService tunnel,
            ITrafficMonitorService traffic,
            INetworkMonitor network,
            ISplitTunnelingService split,
            WindowsConnectionMonitor connections,
            IErrorReportingService errors,
            SiteRuleStore? siteRules = null)
        {
            _api = api;
            _auth = auth;
            _factory = factory;
            _tunnel = (WindowsVpnService)tunnel;
            _traffic = traffic;
            _network = (WindowsNetworkMonitor)network;
            _split = (WindowsSplitTunnelingService)split;
            _connections = connections;
            _errors = errors;
            _siteRules = siteRules;

            _userWantsConnection = VpnIntent.Active;

            _network.PathChanged += OnPathChanged;
            _split.RulesChanged += (_, _) => ScheduleRulesReload("split tunneling");
            if (_siteRules is not null) _siteRules.Changed += (_, _) => ScheduleRulesReload("site rules");

            StateSnapshot.Register("vpn", 10, Describe);
            _network.Start();
        }

        public VpnState State { get; private set; } = VpnState.Disconnected;
        public ServerInfo? ActiveServer { get; private set; }
        public string? ActiveOfferLabel { get; private set; }
        public string? ActiveOfferId { get; private set; }

        public event EventHandler<VpnStateChangedEventArgs>? StateChanged;
        public event EventHandler<ConnectionErrorEventArgs>? ConnectionError;

        private bool WantsConnection => _userWantsConnection && VpnIntent.Active;

        /// <summary>One running connection: what the node offered and what the core runs now.</summary>
        private sealed class Session
        {
            public required ServerConnection Connection { get; init; }
            public required List<ConnectionCandidate> Candidates { get; init; }
            public required int Current { get; set; }
            public required XrayConfig Config { get; set; }
            public required int SocksPort { get; init; }
            public required bool FromCache { get; init; }
        }

        // ── Connect / disconnect ─────────────────────────────────────────────

        public async Task ConnectAsync(ServerInfo? server = null, CancellationToken ct = default)
        {
            if (State != VpnState.Disconnected) return;

            _userWantsConnection = true;
            VpnIntent.Set();
            _retryCts?.Cancel();
            SetState(VpnState.Connecting, null);
            DiagnosticPaths.Rotate(DiagnosticPaths.XrayLog);

            await _ops.WaitAsync(ct);
            try
            {
                await ConnectCoreAsync(server, ct);
                _reconnectAttempt = 0;
                VpnIntent.ResetRestartBudget();
            }
            catch (OperationCanceledException)
            {
                await TeardownAsync();
                SetState(VpnState.Disconnected, "Cancelled");
                throw;
            }
            catch (Exception ex)
            {
                await TeardownAsync();
                Diag.Warn("connect", $"connect failed: {ex.Message}");
                SetState(VpnState.Disconnected, ex.Message);
                ConnectionError?.Invoke(this, new ConnectionErrorEventArgs(ActiveOfferId ?? "Unknown", ex.Message, false));
                throw;
            }
            finally
            {
                _ops.Release();
            }
        }

        private async Task ConnectCoreAsync(ServerInfo? server, CancellationToken ct)
        {
            if (!await _tunnel.RequestPermissionsAsync())
                throw new InvalidOperationException(
                    "Для создания туннеля нужны права администратора. Запустите Horus от имени администратора.");
            if (!_tunnel.IsSupported)
                throw new InvalidOperationException("Не найден wintun.dll рядом с приложением — переустановите Horus.");

            await BindServerIfNeededAsync(server, ct);
            var (connection, fromCache) = await FetchConnectionAsync(server, ct);

            var candidates = Horus.DevTools.DevOutbound.Restrict(Order(connection.Candidates()));
            if (candidates.Count == 0)
                throw new InvalidOperationException("Сервер не предложил ни одного поддерживаемого способа подключения.");

            // Asked before anything captures this process's traffic: afterwards the "direct"
            // answer would come back through the tunnel.
            var directIp = await SafeEgressAsync(null, ct);

            var (index, config) = await ProveFirstWorkingAsync(candidates, directIp, fromCache, ct);

            // Phase two: the same outbound, now with the TUN.
            var run = BuildRunConfig(config);
            XrayInterop.Stop();
            XrayInterop.Test(run);
            XrayInterop.Start(run);
            Diag.Info("connect", $"core up with TUN ({config.OfferId}, mtu {WindowsPreferences.TunSettings.Mtu}, split {_split.CurrentRules.Mode}/{_split.CurrentRules.Processes.Count})");

            await _tunnel.StartTunnelAsync(new TunnelOptions
            {
                TunAddress = WindowsTunnelConfig.Address,
                TunPrefix = 30,
                Mtu = WindowsPreferences.TunSettings.Mtu,
                DnsServers = [WindowsTunnelConfig.DnsAddress],
                BypassApps = [],
                AllTraffic = true,
                NodeAddress = config.NodeAddress,
                SocksPort = config.SocksPort
            }, ct);

            if (_traffic is TrafficMonitorService tms) tms.Reset();
            _traffic.Start();

            _session = new Session
            {
                Connection = connection,
                Candidates = candidates,
                Current = index,
                Config = config,
                SocksPort = config.SocksPort,
                FromCache = fromCache
            };

            ActiveServer = connection.Server?.ToServerInfo() ?? server;
            ActiveOfferId = config.OfferId;
            ActiveOfferLabel = config.DisplayName;
            _failedRecoveries = 0;
            _loopWarned = false;

            SetState(VpnState.Connected, null);
            StartHealthLoop();
        }

        public async Task DisconnectAsync()
        {
            _userWantsConnection = false;
            VpnIntent.Clear();
            _retryCts?.Cancel();
            _demotedOffer = null;
            _reconnectAttempt = 0;

            if (State == VpnState.Disconnected) return;

            StopHealthLoop();
            await _ops.WaitAsync();
            try
            {
                SetState(VpnState.Disconnecting, null);
                await TeardownAsync();
                SetState(VpnState.Disconnected, null);
            }
            finally
            {
                _ops.Release();
            }
        }

        public async Task TryRestoreOrAutoConnectAsync(CancellationToken ct = default)
        {
            if (State != VpnState.Disconnected) return;

            var restoring = VpnIntent.Active;
            if (!restoring && !UserPreferences.AutoConnectOnLaunch) return;

            if (!_auth.IsAuthenticated)
            {
                Diag.Warn("connect", "startup connect skipped: no session");
                return;
            }

            Diag.Info("connect", restoring ? "restoring tunnel — the VPN was on when the app last ran" : "auto-connect on launch");
            try { await ConnectAsync(ActiveServer, ct); }
            catch (Exception ex) { Diag.Warn("connect", $"startup connect failed: {ex.Message}"); }
        }

        private async Task TeardownAsync()
        {
            StopHealthLoop();
            try { _traffic.Stop(); } catch { }
            try { await _tunnel.StopTunnelAsync(); } catch (Exception ex) { Diag.Warn("connect", $"tunnel stop: {ex.Message}"); }
            XrayInterop.Stop();
            _session = null;
            ActiveServer = null;
            ActiveOfferId = null;
            ActiveOfferLabel = null;
        }

        // ── Phase one: prove a candidate on a SOCKS-only core ────────────────

        private async Task<(int Index, XrayConfig Config)> ProveFirstWorkingAsync(
            List<ConnectionCandidate> candidates, string? directIp, bool fromCache, CancellationToken ct)
        {
            Exception? last = null;
            for (var i = 0; i < candidates.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = candidates[i];
                try
                {
                    var config = await CreateConfigAsync(candidate, socksPort: null, ct);
                    Diag.Info("connect", $"[{config.OfferId}] {config.ProtocolName} -> {config.NodeAddress ?? "no address"} (socks {config.SocksPort})");

                    var probeJson = config.ToConfig();
                    XrayInterop.Stop();
                    XrayInterop.Test(probeJson);
                    XrayInterop.Start(probeJson);

                    if (await PreflightAsync(config.SocksPort, directIp, ct))
                        return (i, config);

                    throw new InvalidOperationException($"{candidate.Id} came up but carried no traffic.");
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    last = ex;
                    XrayInterop.Stop();
                    Diag.Warn("connect", $"{candidate.Id} failed: {ex.Message}");
                    _errors.RecordConnectionFailure(candidate.Id, ex.Message);
                }
            }

            if (fromCache) ConnectionCache.Invalidate("every cached endpoint failed");
            try { await _errors.BuildArchiveAsync(ct); } catch { }

            throw new InvalidOperationException(
                "Не удалось подключиться ни одним способом " +
                $"({string.Join(", ", candidates.Select(c => c.ToString()))}). Последняя ошибка: {last?.Message}", last);
        }

        private async Task<XrayConfig> CreateConfigAsync(ConnectionCandidate candidate, int? socksPort, CancellationToken ct)
        {
            var config = (XrayConfig)await _factory.CreateConfigAsync(candidate, ct);
            config.LogLevel = WindowsPreferences.CoreLogLevel;
            // The core binds its sockets to the physical interface itself; a name pinned at
            // connect time goes stale the moment the network changes.
            config.DirectInterface = null;
            if (socksPort is { } port) config.SocksPort = port;
            return config;
        }

        private string BuildRunConfig(XrayConfig config) =>
            WindowsTunnelConfig.Build(config.ToConfig(), WindowsPreferences.TunSettings, _split.CurrentRules);

        /// <summary>
        /// Whether the proxy reaches the internet with an address other than ours — the same
        /// test the shared preflight runs, against the SOCKS inbound.
        /// </summary>
        private async Task<bool> PreflightAsync(int socksPort, string? directIp, CancellationToken ct)
        {
            var proxied = await SafeEgressAsync($"socks5://127.0.0.1:{socksPort}", ct);
            Diag.Info("connect", $"[preflight] direct={directIp ?? "—"} proxied={proxied ?? "—"}");

            if (proxied is not null) return proxied != directIp;

            // Our API may simply be unreachable; ask the core to dial something neutral.
            return await SocksProbe.CanDialAsync(socksPort, "cloudflare.com", 443, TimeSpan.FromSeconds(8), ct);
        }

        private async Task<string?> SafeEgressAsync(string? proxy, CancellationToken ct)
        {
            try { return await _api.GetEgressIpAsync(proxy, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        // ── Health ───────────────────────────────────────────────────────────

        private void StartHealthLoop()
        {
            StopHealthLoop();
            _stall.Reset();
            var cts = new CancellationTokenSource();
            _sessionCts = cts;
            _ = Task.Run(() => HealthLoopAsync(cts.Token));
        }

        private void StopHealthLoop()
        {
            _sessionCts?.Cancel();
            _sessionCts = null;
        }

        private async Task HealthLoopAsync(CancellationToken ct)
        {
            var sinceBlindProbe = TimeSpan.Zero;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(HealthInterval, ct);
                    if (State != VpnState.Connected || _session is not { } session) continue;
                    if (!_network.IsOnline) { _lastHealth = "offline — waiting"; continue; }

                    if (!_tunnel.AdapterIsUp)
                    {
                        _lastHealth = "adapter gone";
                        await RecoverAsync("the tunnel adapter disappeared", ct);
                        continue;
                    }

                    bool needProbe;
                    if (_connections.IsSupported)
                    {
                        var list = _connections.Connections();
                        CheckForLoop(list, session);
                        var verdict = _stall.Feed(list, Environment.TickCount64);
                        _lastHealth = verdict.ToString();
                        needProbe = verdict == StallVerdict.Stalled;
                    }
                    else
                    {
                        // A core without connection listing gives no evidence; look now and then.
                        sinceBlindProbe += HealthInterval;
                        needProbe = sinceBlindProbe >= BlindProbeInterval;
                        if (needProbe) sinceBlindProbe = TimeSpan.Zero;
                    }

                    if (!needProbe) continue;

                    if (await ProbeAsync(session.SocksPort, ct))
                    {
                        _stall.Reset();
                        _lastHealth = "probe ok";
                        continue;
                    }

                    await RecoverAsync("the proxy stopped answering", ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Diag.Error("health", $"health loop died: {ex.Message}"); }
        }

        private static async Task<bool> ProbeAsync(int socksPort, CancellationToken ct)
        {
            foreach (var (host, port) in ProbeTargets)
            {
                if (await SocksProbe.CanDialAsync(socksPort, host, port, ProbeTimeout, ct))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// A connection from the TUN to the node itself means the core's own socket was not
        /// pinned to the physical interface and went round through the tunnel. The fork
        /// refuses such sockets rather than letting them loop, so this should never be seen;
        /// if it is, it is the first thing to know.
        /// </summary>
        private void CheckForLoop(IReadOnlyList<CoreConnection> list, Session session)
        {
            if (_loopWarned || session.Config.NodeAddress is not { } node) return;
            var loop = list.FirstOrDefault(c => AppTrafficGrouping.HostOf(c.Destination) == node);
            if (loop is null) return;
            _loopWarned = true;
            Diag.Error("health", $"routing loop: a TUN connection is going to the node itself ({loop.Destination} via {loop.Outbound ?? "?"}) — outbound binding did not hold");
        }

        // ── Network changes ──────────────────────────────────────────────────

        private void OnPathChanged(object? sender, NetworkPathChange e)
        {
            switch (e.Kind)
            {
                case Change.Preferred:
                    // New connections take the new path; the open ones keep working on the old.
                    Diag.Info("net", "a better path appeared; open connections stay where they are");
                    return;

                case Change.Lost:
                    _lastHealth = "offline";
                    return;

                case Change.Restored:
                case Change.Replaced:
                    if (State == VpnState.Connected)
                        _ = Task.Run(() => AfterHandoverAsync(_sessionCts?.Token ?? CancellationToken.None));
                    else if (State == VpnState.Disconnected && WantsConnection && _auth.IsAuthenticated)
                        ScheduleReconnect("network is back", immediate: true);
                    return;
            }
        }

        /// <summary>
        /// The path the core was bound to is gone, so every session over it is dead. Dropping
        /// them is what makes the next dial take the new path at once rather than after an
        /// idle timeout; then make sure the proxy still answers from here.
        /// </summary>
        private async Task AfterHandoverAsync(CancellationToken ct)
        {
            try
            {
                var closed = XrayInterop.ResetConnections();
                Diag.Info("net", $"handover: reset {closed} pooled session(s)");
                await Task.Delay(TimeSpan.FromSeconds(1.5), ct);

                if (_session is { } s && !await ProbeAsync(s.SocksPort, ct))
                    await RecoverAsync("no answer after the network changed", ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Diag.Warn("net", $"handover check failed: {ex.Message}"); }
        }

        // ── Recovery ─────────────────────────────────────────────────────────

        /// <summary>
        /// The proxy is not answering. Tries, in order, the things that keep the TUN — and
        /// every connection that does not depend on the dead outbound — standing.
        /// </summary>
        private async Task RecoverAsync(string reason, CancellationToken ct)
        {
            if (!WantsConnection || _session is null) return;
            if (!await _ops.WaitAsync(0, ct)) return; // someone else is already on it

            try
            {
                if (_session is not { } session || State != VpnState.Connected) return;

                Diag.Warn("recover", $"{reason} ({session.Config.OfferId})");
                SetState(VpnState.Reconnecting, reason);

                // 1. The transport's own sessions, which is all a NAT rebinding or a stale QUIC
                //    path needs.
                XrayInterop.ResetConnections();
                await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
                if (await ProbeAsync(session.SocksPort, ct)) { Recovered("sessions reset"); return; }

                // 2. The next offers, swapped in place.
                if (XrayLive.IsSupported)
                {
                    for (var step = 1; step < session.Candidates.Count; step++)
                    {
                        var index = (session.Current + step) % session.Candidates.Count;
                        var candidate = session.Candidates[index];
                        try
                        {
                            var config = await CreateConfigAsync(candidate, session.SocksPort, ct);
                            var outbound = WindowsTunnelConfig.OutboundOf(config.ToConfig());
                            if (outbound is null) continue;

                            var result = XrayLive.ReplaceOutbound(outbound);
                            if (result == XrayLive.Result.Unsupported) break;
                            if (result == XrayLive.Result.Failed)
                            {
                                Diag.Warn("recover", $"{candidate.Id} rejected: {XrayLive.LastError}");
                                continue;
                            }

                            if (await ProbeAsync(session.SocksPort, ct))
                            {
                                _demotedOffer = session.Config.OfferId;
                                session.Current = index;
                                session.Config = config;
                                ActiveOfferId = config.OfferId;
                                ActiveOfferLabel = config.DisplayName;
                                Recovered($"switched to {candidate.Id}");
                                return;
                            }
                            Diag.Warn("recover", $"{candidate.Id} swapped in but does not answer either");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { Diag.Warn("recover", $"{candidate.Id}: {ex.Message}"); }
                    }
                }

                // 3. Nothing works from here. Hold the tunnel for a while — a dead link usually
                //    comes back — then give the user their connection back unless the kill
                //    switch says not to.
                _failedRecoveries++;
                if (_failedRecoveries >= MaxHeldRecoveries && !WindowsPreferences.KillSwitch)
                {
                    Diag.Warn("recover", $"{_failedRecoveries} recoveries failed; dropping the tunnel rather than black-holing the machine");
                    var offer = ActiveOfferId ?? "Unknown";
                    await TeardownAsync();
                    _demotedOffer = offer;
                    SetState(VpnState.Disconnected, reason);
                    ConnectionError?.Invoke(this, new ConnectionErrorEventArgs(offer, reason, true));
                    ScheduleReconnect(reason, immediate: false);
                    return;
                }

                SetState(VpnState.Connected, null);
                Diag.Warn("recover", $"no offer answers; holding the tunnel (attempt {_failedRecoveries})");
            }
            catch (OperationCanceledException) { }
            finally
            {
                _ops.Release();
            }

            void Recovered(string how)
            {
                _failedRecoveries = 0;
                _stall.Reset();
                Diag.Info("recover", $"recovered: {how}");
                SetState(VpnState.Connected, null);
            }
        }

        private void ScheduleReconnect(string reason, bool immediate)
        {
            if (!WantsConnection) return;

            _retryCts?.Cancel();
            var cts = new CancellationTokenSource();
            _retryCts = cts;
            var delay = immediate ? TimeSpan.Zero : Backoff[Math.Min(_reconnectAttempt, Backoff.Length - 1)];
            Diag.Info("recover", $"reconnecting in {delay.TotalSeconds:0}s ({reason})");

            _ = Task.Run(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, cts.Token);
                    if (cts.IsCancellationRequested || !WantsConnection || State != VpnState.Disconnected) return;
                    _reconnectAttempt++;
                    await ConnectAsync(ActiveServer, cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Diag.Warn("recover", $"reconnect failed: {ex.Message}");
                    ScheduleReconnect(reason, immediate: false);
                }
            });
        }

        // ── Live rule changes ────────────────────────────────────────────────

        private void ScheduleRulesReload(string why)
        {
            if (State != VpnState.Connected) return;

            _rulesDebounce?.Cancel();
            var cts = new CancellationTokenSource();
            _rulesDebounce = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    // Toggling several apps in a row is one change, not five.
                    await Task.Delay(400, cts.Token);
                    await ReloadRulesAsync(why, cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Diag.Warn("split", $"rules not applied: {ex.Message}"); }
            });
        }

        /// <summary>
        /// Applies the current split-tunnel and site rules to the running core without a
        /// restart. Open connections keep their route — the connections screen offers to
        /// restart an application's connections so they pick the new one up.
        /// </summary>
        private async Task ReloadRulesAsync(string why, CancellationToken ct)
        {
            await _ops.WaitAsync(ct);
            try
            {
                if (_session is not { } session || State != VpnState.Connected) return;

                // Site rules are read when the config is built; rebuild it rather than patch it.
                var config = await CreateConfigAsync(session.Candidates[session.Current], session.SocksPort, ct);
                var run = BuildRunConfig(config);
                XrayInterop.Test(run);

                var result = XrayLive.ReloadRouting(WindowsTunnelConfig.RoutingOf(run));
                switch (result)
                {
                    case XrayLive.Result.Ok:
                        session.Config = config;
                        Diag.Info("split", $"rules reloaded live ({why}): {_split.CurrentRules.Mode}, {_split.CurrentRules.Processes.Count} app(s)");
                        break;

                    case XrayLive.Result.Unsupported:
                        // An older core: the only way to new rules is a restart, which is a
                        // brief drop for everything. Said in the log so it is not a mystery.
                        Diag.Warn("split", $"core cannot reload rules live; restarting it ({why})");
                        XrayInterop.Stop();
                        XrayInterop.Start(run);
                        session.Config = config;
                        break;

                    default:
                        Diag.Error("split", $"core rejected the new rules: {XrayLive.LastError}");
                        break;
                }
            }
            finally
            {
                _ops.Release();
            }
        }

        // ── Connection helpers (same semantics as the shared manager) ────────

        private async Task<(ServerConnection Connection, bool FromCache)> FetchConnectionAsync(ServerInfo? server, CancellationToken ct)
        {
            var cached = ConnectionCache.Read(server?.Id ?? ActiveServer?.Id);
            if (cached is not null) return (cached, true);

            var fresh = await _api.GetServerConnectionAsync(ct);
            ConnectionCache.Write(fresh);
            return (fresh, false);
        }

        private async Task BindServerIfNeededAsync(ServerInfo? server, CancellationToken ct)
        {
            if (server is null || ActiveServer?.Id == server.Id) return;

            Diag.Info("connect", $"binding to server {server.Id} ({server.Location})");
            var bound = await _api.SelectServerAsync(server.Id, ct);
            Diag.Info("connect", $"[api] bound to {bound.Name} ({bound.Location})");
        }

        /// <summary>
        /// The node's order, with a protocol that just failed moved to the back — and, when
        /// the user prefers it, the UDP-native transport moved to the front: game traffic is
        /// UDP, and over VLESS it rides inside TCP, where one lost segment stalls every packet
        /// behind it.
        /// </summary>
        private List<ConnectionCandidate> Order(IReadOnlyList<ConnectionCandidate> offered)
        {
            var ordered = offered.ToList();

            if (WindowsPreferences.PreferUdpTransport)
                ordered = [.. ordered.Where(c => c.ProtocolName == "hysteria"), .. ordered.Where(c => c.ProtocolName != "hysteria")];

            if (_demotedOffer is { } demoted && ordered.Count > 1 && ordered.Any(c => c.Id != demoted))
                ordered = [.. ordered.Where(c => c.Id != demoted), .. ordered.Where(c => c.Id == demoted)];

            return ordered;
        }

        private IEnumerable<KeyValuePair<string, string?>> Describe()
        {
            yield return new("state", State.ToString());
            yield return new("engine", "windows (core-owned TUN)");
            yield return new("userWantsConnection", _userWantsConnection.ToString());
            yield return new("offer", ActiveOfferId);
            yield return new("demoted", _demotedOffer);
            yield return new("server", ActiveServer?.Name);
            yield return new("socksPort", _session?.SocksPort.ToString());
            yield return new("node", _session?.Config.NodeAddress);
            yield return new("path", _network.CurrentPath?.Describe());
            yield return new("adapterIndex", _tunnel.InterfaceIndex.ToString());
            yield return new("health", _lastHealth);
            yield return new("failedRecoveries", _failedRecoveries.ToString());
            yield return new("liveControl", XrayLive.IsSupported.ToString());
            yield return new("coreRunning", XrayInterop.IsRunning().ToString());
            yield return new("coreVersion", XrayInterop.Version());
            yield return new("split", $"{_split.CurrentRules.Mode}, {_split.CurrentRules.Processes.Count} app(s)");

            long[] counters;
            try { counters = _tunnel.GetTunnelStats(); } catch { counters = []; }
            yield return new("tunnelCounters", counters.Length >= 4
                ? $"txp {counters[0]}, tx {counters[1]}B, rxp {counters[2]}, rx {counters[3]}B"
                : "no adapter");

            foreach (var kv in WindowsPreferences.Describe()) yield return new("pref." + kv.Key, kv.Value);
            foreach (var kv in ConnectionCache.Describe()) yield return new("cache." + kv.Key, kv.Value);
            foreach (var kv in VpnIntent.Describe()) yield return new("intent." + kv.Key, kv.Value);
        }

        private void SetState(VpnState state, string? reason)
        {
            var old = State;
            State = state;
            if (old != state) Diag.Info("vpn", $"{old} -> {state}{(reason is null ? string.Empty : $" ({reason})")}");
            StateChanged?.Invoke(this, new VpnStateChangedEventArgs(old, state, reason));
        }
    }
}
