using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Platforms.Android
{
    /// <summary>
    /// Tracks the physical link with <c>ConnectivityManager</c> callbacks.
    ///
    /// <para><b>Why the request excludes VPNs.</b> Once the tunnel is up it becomes the
    /// default network, so <c>registerDefaultNetworkCallback</c> would report our own
    /// tunnel and go quiet exactly when the real link underneath it changes.
    /// <c>NET_CAPABILITY_NOT_VPN</c> filters the callback down to physical networks, which
    /// is the only thing worth watching.</para>
    ///
    /// <para><b>Why it also sets the underlying network.</b> A VpnService that never calls
    /// <c>setUnderlyingNetworks</c> leaves the system believing the tunnel is backed by
    /// whatever was there when it was established. After a Wi-Fi to mobile handover that is
    /// a network that no longer exists, and the consequences are quiet: traffic accounting
    /// is attributed wrongly, and — the part that actually hurts — the system's idea of
    /// whether the VPN has connectivity stops matching reality, which feeds into how
    /// aggressively it is allowed to run in the background.</para>
    ///
    /// <para>This replaces a timer. A handover is delivered here in milliseconds; noticing
    /// it by watching byte counters stop takes tens of seconds and costs a poll running the
    /// entire time in between.</para>
    /// </summary>
    public sealed class AndroidNetworkMonitor : INetworkMonitor
    {
        private readonly object _sync = new();
        private ConnectivityManager? _cm;
        private Callback? _callback;
        private VpnCallback? _vpnCallback;
        private WakeReceiver? _wake;

        private Network? _current;
        private Network? _tunnel;
        private bool _tunnelValidated;
        private NetworkTransport _transport = NetworkTransport.None;

        public event EventHandler<NetworkChangedEventArgs>? NetworkChanged;
        public event EventHandler? TunnelValidationLost;
        public event EventHandler? DeviceWoke;

        public bool IsOnline { get { lock (_sync) return _current is not null; } }
        public NetworkTransport Transport { get { lock (_sync) return _transport; } }

        public void Start()
        {
            if (_cm is not null) return;

            _cm = global::Android.App.Application.Context
                .GetSystemService(Context.ConnectivityService) as ConnectivityManager;
            if (_cm is null) return;

            var request = new NetworkRequest.Builder()
                .AddCapability(NetCapability.Internet)!
                .AddCapability(NetCapability.NotVpn)!
                .Build();

            _callback = new Callback(this);
            try { _cm.RegisterNetworkCallback(request!, _callback); }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("Horus", $"Network callback registration failed: {ex.Message}");
                _cm = null;
                return;
            }

            // A second subscription, this one for the tunnel itself.
            //
            // NetworkRequest.Builder adds NET_CAPABILITY_NOT_VPN implicitly, so a request
            // built the ordinary way can never match a VPN network — removing it is what
            // makes our own tunnel visible here at all. Once it is, Android's continuous
            // validation of that network becomes a free push notification: when traffic
            // stops reaching the internet through the tunnel, VALIDATED is withdrawn and
            // onCapabilitiesChanged fires, typically within a couple of seconds of the
            // platform re-probing.
            try
            {
                var vpnRequest = new NetworkRequest.Builder()
                    .AddTransportType(TransportType.Vpn)!
                    .RemoveCapability(NetCapability.NotVpn)!
                    .Build();

                _vpnCallback = new VpnCallback(this);
                _cm.RegisterNetworkCallback(vpnRequest!, _vpnCallback);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("Horus", $"VPN validation callback failed: {ex.Message}");
                _vpnCallback = null;
            }

            // ACTION_SCREEN_ON cannot be declared in a manifest, so this has to be a
            // runtime registration.
            try
            {
                _wake = new WakeReceiver(this);
                var filter = new IntentFilter();
                filter.AddAction(Intent.ActionUserPresent);
                filter.AddAction(Intent.ActionScreenOn);
                if (OperatingSystem.IsAndroidVersionAtLeast(23))
                    filter.AddAction(PowerManager.ActionDeviceIdleModeChanged);

                global::Android.App.Application.Context.RegisterReceiver(_wake, filter);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("Horus", $"Wake receiver failed: {ex.Message}");
                _wake = null;
            }
        }

        public void Stop()
        {
            try { if (_cm is not null && _callback is not null) _cm.UnregisterNetworkCallback(_callback); }
            catch { }
            try { if (_cm is not null && _vpnCallback is not null) _cm.UnregisterNetworkCallback(_vpnCallback); }
            catch { }
            try { if (_wake is not null) global::Android.App.Application.Context.UnregisterReceiver(_wake); }
            catch { }

            _cm = null;
            _callback = null;
            _vpnCallback = null;
            _wake = null;

            lock (_sync) { _current = null; _tunnel = null; _transport = NetworkTransport.None; }
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
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("Horus", $"reportNetworkConnectivity failed: {ex.Message}");
            }
        }

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

            if (lost)
            {
                Diag.Write("[net] the system withdrew VALIDATED from the tunnel");
                TunnelValidationLost?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnTunnelLost(Network network)
        {
            lock (_sync)
            {
                if (_tunnel is null || !_tunnel.Equals(network)) return;
                _tunnel = null;
                _tunnelValidated = false;
            }
        }

        private void OnWake(string? action)
        {
            Diag.Write($"[net] device woke ({action})");
            DeviceWoke?.Invoke(this, EventArgs.Empty);
        }

        private void OnNetworkUp(Network network)
        {
            NetworkTransport transport;
            bool handover;

            lock (_sync)
            {
                // A different Network object with the tunnel already up is a handover: the
                // old link is gone and every established connection on it is dead, even
                // though nothing has reported an error yet.
                handover = _current is not null && !_current.Equals(network);
                _current = network;
                _transport = transport = ReadTransport(network);
            }

            PushUnderlying(network);
            NetworkChanged?.Invoke(this, new NetworkChangedEventArgs(transport, true, handover));
        }

        private void OnNetworkDown(Network network)
        {
            bool nowOffline;
            lock (_sync)
            {
                if (_current is null || !_current.Equals(network)) return;
                _current = null;
                _transport = NetworkTransport.None;
                nowOffline = true;
            }

            if (nowOffline)
            {
                PushUnderlying(null);
                NetworkChanged?.Invoke(this, new NetworkChangedEventArgs(NetworkTransport.None, false, false));
            }
        }

        private static void PushUnderlying(Network? network)
        {
            try { HorusVpnTunnelService.SetUnderlyingNetwork(network); }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("Horus", $"setUnderlyingNetworks failed: {ex.Message}");
            }
        }

        private NetworkTransport ReadTransport(Network network)
        {
            var caps = _cm?.GetNetworkCapabilities(network);
            if (caps is null) return NetworkTransport.Other;
            if (caps.HasTransport(TransportType.Wifi)) return NetworkTransport.Wifi;
            if (caps.HasTransport(TransportType.Cellular)) return NetworkTransport.Cellular;
            if (caps.HasTransport(TransportType.Ethernet)) return NetworkTransport.Ethernet;
            return NetworkTransport.Other;
        }

        private sealed class Callback : ConnectivityManager.NetworkCallback
        {
            private readonly AndroidNetworkMonitor _owner;
            public Callback(AndroidNetworkMonitor owner) => _owner = owner;

            public override void OnAvailable(Network network) => _owner.OnNetworkUp(network);
            public override void OnLost(Network network) => _owner.OnNetworkDown(network);
        }

        /// <summary>Watches the tunnel's own validation state.</summary>
        private sealed class VpnCallback : ConnectivityManager.NetworkCallback
        {
            private readonly AndroidNetworkMonitor _owner;
            public VpnCallback(AndroidNetworkMonitor owner) => _owner = owner;

            public override void OnCapabilitiesChanged(Network network, NetworkCapabilities caps) =>
                _owner.OnTunnelCapabilities(network, caps);

            public override void OnLost(Network network) => _owner.OnTunnelLost(network);
        }

        /// <summary>
        /// Screen on, unlock, and Doze ending. Registered at runtime because
        /// <c>ACTION_SCREEN_ON</c> is not deliverable to a manifest-declared receiver.
        /// </summary>
        private sealed class WakeReceiver : BroadcastReceiver
        {
            private readonly AndroidNetworkMonitor _owner;
            public WakeReceiver(AndroidNetworkMonitor owner) => _owner = owner;

            public override void OnReceive(Context? context, Intent? intent) =>
                _owner.OnWake(intent?.Action);
        }
    }
}
