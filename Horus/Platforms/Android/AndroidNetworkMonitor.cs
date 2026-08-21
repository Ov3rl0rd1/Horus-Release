using Android.Content;
using Android.Net;
using Android.OS;
using Horus.Application.Diagnostics;
using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Platforms.Android
{
    /// <summary>
    /// Tracks the physical links with <c>ConnectivityManager</c> callbacks, and reports the
    /// platform's own opinion of whether the tunnel is carrying anything.
    ///
    /// <para><b>Why the requests exclude — and separately include — VPNs.</b> Once the
    /// tunnel is up it becomes the default network, so <c>registerDefaultNetworkCallback</c>
    /// would report our own tunnel and go quiet exactly when the real link underneath it
    /// changes. <c>NET_CAPABILITY_NOT_VPN</c> filters the main callback down to physical
    /// networks. A second, opposite request deliberately removes that filter so the tunnel
    /// itself becomes visible, which turns Android's continuous validation of it into a
    /// free push notification about our own health.</para>
    ///
    /// <para><b>Why a set and not a single network.</b> The main subscription is a LISTEN,
    /// so on a phone with Wi-Fi and mobile both up, <c>onAvailable</c> fires for both. The
    /// previous version kept whichever arrived last, which meant
    /// <c>setUnderlyingNetworks</c> could name a network that was not the one carrying
    /// traffic — misattributing accounting and skewing how aggressively the system lets us
    /// run in the background. Networks are now kept as a set and ranked, because
    /// <c>setUnderlyingNetworks</c> reads array order as priority.</para>
    ///
    /// <para><b>Why capability changes are throttled.</b> <c>onCapabilitiesChanged</c>
    /// arrives tens of times a minute on a mobile link as signal strength and bandwidth
    /// estimates move. Acting on each one would make this the most expensive thing in the
    /// app. Only a changed network set, or 15 seconds elapsed, gets through — the same
    /// filter Rethink applies for the same reason.</para>
    /// </summary>
    public sealed class AndroidNetworkMonitor : INetworkMonitor
    {
        /// <summary>
        /// How often a bare capability change may cause work. Anything that changes the set
        /// of networks bypasses this.
        /// </summary>
        private static readonly TimeSpan CapabilityThrottle = TimeSpan.FromSeconds(15);

        private readonly object _sync = new();
        private ConnectivityManager? _cm;

        private Callback? _listen;
        private Callback? _defaultCallback;
        private VpnCallback? _vpnCallback;
        private WakeReceiver? _wake;
        private object? _diagnostics; // ConnectivityDiagnosticsManager callback, API 30+

        /// <summary>Every physical network the platform has told us about, by handle.</summary>
        private readonly Dictionary<long, Entry> _networks = [];

        /// <summary>
        /// The default physical network, as named by the platform rather than guessed.
        /// Null below API 28 only if registration failed.
        /// </summary>
        private Network? _default;

        private Network? _tunnel;
        private bool _tunnelValidated;
        private long _rankedFirstHandle = -1;
        private NetworkTransport _transport = NetworkTransport.None;
        private DateTimeOffset _lastCapabilityWork = DateTimeOffset.MinValue;

        private readonly record struct Entry(Network Network, NetworkTransport Transport, bool NotMetered, bool Validated);

        public event EventHandler<NetworkChangedEventArgs>? NetworkChanged;
        public event EventHandler<string>? TunnelSuspect;
        public event EventHandler? DeviceWoke;
        public event EventHandler<bool>? DeviceIdleChanged;

        public bool IsOnline { get { lock (_sync) return _networks.Count > 0; } }
        public NetworkTransport Transport { get { lock (_sync) return _transport; } }

        public void Start()
        {
            if (_cm is not null) return;

            _cm = global::Android.App.Application.Context
                .GetSystemService(Context.ConnectivityService) as ConnectivityManager;
            if (_cm is null) return;

            RegisterListen();
            RegisterDefault();
            RegisterVpnValidation();
            RegisterDiagnostics();
            RegisterWakeReceiver();

            StateSnapshot.Register("network", 20, Describe);
        }

        /// <summary>
        /// Every physical network with internet, as a LISTEN. This is the set that gets
        /// ranked and handed to <c>setUnderlyingNetworks</c>.
        /// </summary>
        private void RegisterListen()
        {
            try
            {
                var request = new NetworkRequest.Builder()
                    .AddCapability(NetCapability.Internet)!
                    .AddCapability(NetCapability.NotVpn)!
                    .Build();

                _listen = new Callback(this, isDefault: false);
                _cm!.RegisterNetworkCallback(request!, _listen);
            }
            catch (Exception ex)
            {
                Diag.Error("net", $"network callback registration failed: {ex.Message}");
                _cm = null;
            }
        }

        /// <summary>
        /// Identifies <i>the</i> default physical network, which a LISTEN cannot.
        ///
        /// <para>Three API levels, three different answers, and the reason is a platform
        /// behaviour change: before Android P, <c>registerDefaultNetworkCallback</c>
        /// returned the real default and is the cheapest option. From P it returns our own
        /// VPN, so a REQUEST is needed instead — that requires CHANGE_NETWORK_STATE, which
        /// is why the manifest declares it. From S there is
        /// <c>registerBestMatchingNetworkCallback</c>, which gives the same answer without
        /// holding a request open, and is preferred wherever it exists.</para>
        ///
        /// <para>Failure here is not fatal: the ranking falls back to
        /// validated-then-unmetered-then-transport, which is a good ordering, just not an
        /// authoritative one.</para>
        /// </summary>
        private void RegisterDefault()
        {
            try
            {
                _defaultCallback = new Callback(this, isDefault: true);
                var handler = new Handler(Looper.MainLooper!);

                if (OperatingSystem.IsAndroidVersionAtLeast(31))
                {
                    var request = new NetworkRequest.Builder()
                        .AddCapability(NetCapability.Internet)!
                        .AddCapability(NetCapability.NotVpn)!
                        .Build();
                    _cm!.RegisterBestMatchingNetworkCallback(request!, _defaultCallback, handler);
                }
                else if (OperatingSystem.IsAndroidVersionAtLeast(28))
                {
                    var request = new NetworkRequest.Builder()
                        .AddCapability(NetCapability.Internet)!
                        .AddCapability(NetCapability.NotVpn)!
                        .Build();
                    _cm!.RequestNetwork(request!, _defaultCallback, handler);
                }
                else
                {
                    // API 26–27: predates the change that made this return the VPN.
                    _cm!.RegisterDefaultNetworkCallback(_defaultCallback, handler);
                }
            }
            catch (Exception ex)
            {
                Diag.Warn("net", $"default-network callback failed, ranking by heuristic: {ex.Message}");
                _defaultCallback = null;
            }
        }

        /// <summary>
        /// Watches our own tunnel's validation state.
        ///
        /// <c>NetworkRequest.Builder</c> adds <c>NET_CAPABILITY_NOT_VPN</c> implicitly, so a
        /// request built the ordinary way can never match a VPN network — removing it is
        /// what makes the tunnel visible here at all. Once it is, Android's continuous
        /// validation becomes a push notification: when traffic stops reaching the internet
        /// through the tunnel, VALIDATED is withdrawn, typically within a couple of seconds
        /// of the platform re-probing.
        /// </summary>
        private void RegisterVpnValidation()
        {
            try
            {
                var vpnRequest = new NetworkRequest.Builder()
                    .AddTransportType(TransportType.Vpn)!
                    .RemoveCapability(NetCapability.NotVpn)!
                    .Build();

                _vpnCallback = new VpnCallback(this);
                _cm!.RegisterNetworkCallback(vpnRequest!, _vpnCallback);
            }
            catch (Exception ex)
            {
                Diag.Warn("net", $"VPN validation callback failed: {ex.Message}");
                _vpnCallback = null;
            }
        }

        /// <summary>
        /// Subscribes to the system's own data-stall detection — the machinery behind the
        /// exclamation mark on the Wi-Fi icon.
        ///
        /// <para>It costs nothing: the telemetry is collected by the platform whether or not
        /// anyone listens. <c>clearCapabilities()</c> is mandatory, not tidiness — without
        /// it the builder's implicit NOT_VPN keeps our own tunnel out of the request and
        /// nothing is ever reported.</para>
        /// </summary>
        private void RegisterDiagnostics()
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(30)) return;

            try
            {
                var request = new NetworkRequest.Builder()
                    .ClearCapabilities()!
                    .AddTransportType(TransportType.Vpn)!
                    .Build();

                var manager = global::Android.App.Application.Context
                    .GetSystemService("connectivity_diagnostics") as ConnectivityDiagnosticsManager;
                if (manager is null) return;

                var callback = new DiagnosticsCallback(this);
                manager.RegisterConnectivityDiagnosticsCallback(
                    request!, global::Java.Util.Concurrent.Executors.NewSingleThreadExecutor()!, callback);

                _diagnostics = callback;
                Diag.Info("net", "connectivity diagnostics registered");
            }
            catch (Exception ex)
            {
                Diag.Warn("net", $"connectivity diagnostics unavailable: {ex.Message}");
            }
        }

        /// <summary>
        /// Screen on, unlock, and Doze ending. Registered at runtime because
        /// <c>ACTION_SCREEN_ON</c> is not deliverable to a manifest-declared receiver.
        /// </summary>
        private void RegisterWakeReceiver()
        {
            try
            {
                _wake = new WakeReceiver(this);
                var filter = new IntentFilter();
                filter.AddAction(Intent.ActionUserPresent);
                filter.AddAction(Intent.ActionScreenOn);
                filter.AddAction(Intent.ActionScreenOff);
                if (OperatingSystem.IsAndroidVersionAtLeast(23))
                    filter.AddAction(PowerManager.ActionDeviceIdleModeChanged);

                global::Android.App.Application.Context.RegisterReceiver(_wake, filter);
            }
            catch (Exception ex)
            {
                Diag.Warn("net", $"wake receiver failed: {ex.Message}");
                _wake = null;
            }
        }

        public void Stop()
        {
            SafeUnregister(_listen);
            SafeUnregister(_defaultCallback);
            SafeUnregister(_vpnCallback);

            if (_diagnostics is not null && OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                try
                {
                    (global::Android.App.Application.Context.GetSystemService("connectivity_diagnostics")
                        as ConnectivityDiagnosticsManager)
                        ?.UnregisterConnectivityDiagnosticsCallback(
                            (ConnectivityDiagnosticsManager.ConnectivityDiagnosticsCallback)_diagnostics);
                }
                catch { }
            }

            try { if (_wake is not null) global::Android.App.Application.Context.UnregisterReceiver(_wake); }
            catch { }

            StateSnapshot.Unregister("network");

            _cm = null;
            _listen = null;
            _defaultCallback = null;
            _vpnCallback = null;
            _diagnostics = null;
            _wake = null;

            lock (_sync)
            {
                _networks.Clear();
                _default = null;
                _tunnel = null;
                _rankedFirstHandle = -1;
                _transport = NetworkTransport.None;
            }
        }

        private void SafeUnregister(ConnectivityManager.NetworkCallback? callback)
        {
            try { if (_cm is not null && callback is not null) _cm.UnregisterNetworkCallback(callback); }
            catch { }
        }

        /// <summary>
        /// Asks the platform to re-validate the tunnel now. Android answers by re-running
        /// its own connectivity probe through the VPN and updating the network's
        /// capabilities, which arrives back here as a capability change — so a local hunch
        /// becomes the platform's verdict without us sending a packet ourselves.
        /// </summary>
        public void ReportTunnelSuspect()
        {
            Network? tunnel;
            lock (_sync) tunnel = _tunnel;
            if (tunnel is null || _cm is null) return;

            try { _cm.ReportNetworkConnectivity(tunnel, false); }
            catch (Exception ex) { Diag.Warn("net", $"reportNetworkConnectivity failed: {ex.Message}"); }
        }

        // ── Physical networks ────────────────────────────────────────────────

        private void OnNetworkUp(Network network, bool isDefault)
        {
            if (isDefault)
            {
                lock (_sync) _default = network;
                Publish("default network changed");
                return;
            }

            lock (_sync) _networks[network.NetworkHandle] = Read(network);
            Publish("network available");
        }

        private void OnNetworkDown(Network network, bool isDefault)
        {
            if (isDefault)
            {
                lock (_sync) { if (_default?.NetworkHandle == network.NetworkHandle) _default = null; }
                Publish("default network lost");
                return;
            }

            lock (_sync) _networks.Remove(network.NetworkHandle);
            Publish("network lost");
        }

        /// <summary>
        /// A capability change on a physical network. Throttled hard: these arrive tens of
        /// times a minute on mobile, and the only fields that matter here — metered and
        /// validated — change rarely.
        /// </summary>
        private void OnCapabilities(Network network, NetworkCapabilities caps, bool isDefault)
        {
            if (isDefault) return; // the default callback's set membership is all we use

            bool interesting;
            lock (_sync)
            {
                var entry = new Entry(
                    network,
                    ReadTransport(caps),
                    caps.HasCapability(NetCapability.NotMetered),
                    caps.HasCapability(NetCapability.Validated));

                // A network we did not know about, or one whose meaningful properties moved,
                // is worth acting on regardless of the clock.
                interesting = !_networks.TryGetValue(network.NetworkHandle, out var old)
                              || old.NotMetered != entry.NotMetered
                              || old.Validated != entry.Validated
                              || old.Transport != entry.Transport;

                _networks[network.NetworkHandle] = entry;

                if (!interesting)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now - _lastCapabilityWork < CapabilityThrottle) return;
                    _lastCapabilityWork = now;
                }
                else
                {
                    _lastCapabilityWork = DateTimeOffset.UtcNow;
                }
            }

            Publish("capabilities changed");
        }

        /// <summary>
        /// Recomputes the ranking, pushes it to the tunnel, and raises a change only when
        /// the network actually carrying traffic is different from last time.
        ///
        /// <para>That last part is what stops a second network merely appearing — mobile
        /// coming up alongside Wi-Fi — from being reported as a handover and costing a
        /// probe. Only a change at the head of the ranking means established connections
        /// are dead.</para>
        /// </summary>
        private void Publish(string reason)
        {
            Network[] ranked;
            NetworkTransport transport;
            bool handover;
            bool online;

            lock (_sync)
            {
                var order = Rank();
                ranked = [.. order.Select(e => e.Network)];
                online = ranked.Length > 0;

                var first = order.Count > 0 ? order[0] : (Entry?)null;
                var firstHandle = first?.Network.NetworkHandle ?? -1;

                handover = online && _rankedFirstHandle != -1 && _rankedFirstHandle != firstHandle;

                _rankedFirstHandle = firstHandle;
                _transport = transport = first?.Transport ?? NetworkTransport.None;
            }

            PushUnderlying(ranked.Length > 0 ? ranked : null);

            Diag.Trace("net",
                $"{reason}: {ranked.Length} network(s), first={transport}, handover={handover}");

            NetworkChanged?.Invoke(this, new NetworkChangedEventArgs(transport, online, handover));
        }

        /// <summary>
        /// Priority order for <c>setUnderlyingNetworks</c>, where index 0 is preferred.
        ///
        /// <para>The default network first when the platform has named one; otherwise
        /// validated before unvalidated, unmetered before metered, and Wi-Fi before
        /// Ethernet before mobile. Caller holds <see cref="_sync"/>.</para>
        /// </summary>
        private List<Entry> Rank()
        {
            var defaultHandle = _default?.NetworkHandle;

            return [.. _networks.Values
                .OrderByDescending(e => defaultHandle is not null && e.Network.NetworkHandle == defaultHandle)
                .ThenByDescending(e => e.Validated)
                .ThenByDescending(e => e.NotMetered)
                .ThenBy(e => e.Transport switch
                {
                    NetworkTransport.Wifi => 0,
                    NetworkTransport.Ethernet => 1,
                    NetworkTransport.Cellular => 2,
                    _ => 3
                })];
        }

        private Entry Read(Network network)
        {
            var caps = _cm?.GetNetworkCapabilities(network);
            return new Entry(
                network,
                ReadTransport(caps),
                caps?.HasCapability(NetCapability.NotMetered) ?? false,
                caps?.HasCapability(NetCapability.Validated) ?? false);
        }

        private static NetworkTransport ReadTransport(NetworkCapabilities? caps)
        {
            if (caps is null) return NetworkTransport.Other;
            if (caps.HasTransport(TransportType.Wifi)) return NetworkTransport.Wifi;
            if (caps.HasTransport(TransportType.Cellular)) return NetworkTransport.Cellular;
            if (caps.HasTransport(TransportType.Ethernet)) return NetworkTransport.Ethernet;
            return NetworkTransport.Other;
        }

        private static void PushUnderlying(Network[]? networks)
        {
            try { HorusVpnTunnelService.SetUnderlyingNetwork(networks); }
            catch (Exception ex) { Diag.Warn("net", $"setUnderlyingNetworks failed: {ex.Message}"); }
        }

        // ── Tunnel health signals ────────────────────────────────────────────

        private void OnTunnelCapabilities(Network network, NetworkCapabilities caps)
        {
            var validated = caps.HasCapability(NetCapability.Validated);

            bool lost;
            lock (_sync)
            {
                _tunnel = network;
                // Only the transition matters. The capability is absent for a moment while
                // the tunnel is still coming up, and treating that as a failure would tear
                // down every connection during its first seconds.
                lost = _tunnelValidated && !validated;
                _tunnelValidated = validated;
            }

            if (lost) RaiseSuspect("the system withdrew VALIDATED from the tunnel");
        }

        private void OnTunnelLost(Network network)
        {
            lock (_sync)
            {
                if (_tunnel is null || _tunnel.NetworkHandle != network.NetworkHandle) return;
                _tunnel = null;
                _tunnelValidated = false;
            }
        }

        private void RaiseSuspect(string reason)
        {
            Diag.Warn("net", reason);
            TunnelSuspect?.Invoke(this, reason);
        }

        private void OnWake(string? action)
        {
            // Screen off is tracked so the traffic and health loops can slow down; it is
            // not a wake and must not trigger a probe.
            if (action == Intent.ActionScreenOff)
            {
                Diag.Trace("net", "screen off");
                return;
            }

            // Doze is two events on one action, and they mean opposite things. Entering is
            // the app's only warning that nothing will be watching for a while, which is
            // what the core needs to hear; leaving is not a wake in the sense the health
            // monitor cares about, because the user is not necessarily back.
            if (action == PowerManager.ActionDeviceIdleModeChanged)
            {
                var idle = IsDeviceIdle();
                Diag.Info("net", idle ? "entering doze" : "leaving doze");
                DeviceIdleChanged?.Invoke(this, idle);

                if (idle) return;
            }

            Diag.Info("net", $"device woke ({action})");
            DeviceWoke?.Invoke(this, EventArgs.Empty);
        }

        private static bool IsDeviceIdle()
        {
            try
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(23)) return false;

                return global::Android.App.Application.Context
                    .GetSystemService(Context.PowerService) is PowerManager pm && pm.IsDeviceIdleMode;
            }
            catch (Exception ex)
            {
                Diag.Warn("net", $"could not read idle mode: {ex.Message}");
                return false;
            }
        }

        // ── Diagnostics snapshot ─────────────────────────────────────────────

        private IEnumerable<KeyValuePair<string, string?>> Describe()
        {
            lock (_sync)
            {
                yield return new("online", (_networks.Count > 0).ToString());
                yield return new("transport", _transport.ToString());
                yield return new("networkCount", _networks.Count.ToString());
                yield return new("defaultKnown", (_default is not null).ToString());
                yield return new("tunnelVisible", (_tunnel is not null).ToString());
                yield return new("tunnelValidated", _tunnelValidated.ToString());
                yield return new("deviceIdle", IsDeviceIdle().ToString());

                var i = 0;
                foreach (var entry in Rank())
                {
                    yield return new(
                        $"net[{i++}]",
                        $"{entry.Transport}, validated={entry.Validated}, unmetered={entry.NotMetered}" +
                        (entry.Network.NetworkHandle == (_default?.NetworkHandle ?? -1) ? ", default" : ""));
                }
            }
        }

        // ── Callbacks ────────────────────────────────────────────────────────

        private sealed class Callback(AndroidNetworkMonitor owner, bool isDefault)
            : ConnectivityManager.NetworkCallback
        {
            public override void OnAvailable(Network network) => owner.OnNetworkUp(network, isDefault);
            public override void OnLost(Network network) => owner.OnNetworkDown(network, isDefault);

            public override void OnCapabilitiesChanged(Network network, NetworkCapabilities caps) =>
                owner.OnCapabilities(network, caps, isDefault);
        }

        /// <summary>Watches the tunnel's own validation state.</summary>
        private sealed class VpnCallback(AndroidNetworkMonitor owner) : ConnectivityManager.NetworkCallback
        {
            public override void OnCapabilitiesChanged(Network network, NetworkCapabilities caps) =>
                owner.OnTunnelCapabilities(network, caps);

            public override void OnLost(Network network) => owner.OnTunnelLost(network);
        }

        /// <summary>
        /// The platform's data-stall telemetry. Only TCP and DNS detection are acted on —
        /// the other methods are noisy enough that reacting to them would produce spurious
        /// reconnects.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
        private sealed class DiagnosticsCallback(AndroidNetworkMonitor owner)
            : ConnectivityDiagnosticsManager.ConnectivityDiagnosticsCallback
        {
            public override void OnDataStallSuspected(ConnectivityDiagnosticsManager.DataStallReport report)
            {
                try
                {
                    var method = report.DetectionMethod;
                    const int detectionTcp = 2;   // DETECTION_METHOD_TCP_METRICS
                    const int detectionDns = 1;   // DETECTION_METHOD_DNS_EVENTS

                    if (method == detectionTcp || method == detectionDns)
                        owner.RaiseSuspect($"the system suspects a data stall (method {method})");
                }
                catch (Exception ex) { Diag.Warn("net", $"data-stall report unreadable: {ex.Message}"); }
            }

            public override void OnNetworkConnectivityReported(Network network, bool hasConnectivity)
            {
                if (!hasConnectivity) owner.RaiseSuspect("the system reported the tunnel has no connectivity");
            }
        }

        /// <summary>
        /// Screen on, unlock, screen off, and Doze ending. Registered at runtime because
        /// <c>ACTION_SCREEN_ON</c> is not deliverable to a manifest-declared receiver.
        /// </summary>
        private sealed class WakeReceiver(AndroidNetworkMonitor owner) : BroadcastReceiver
        {
            public override void OnReceive(Context? context, Intent? intent) => owner.OnWake(intent?.Action);
        }
    }
}
