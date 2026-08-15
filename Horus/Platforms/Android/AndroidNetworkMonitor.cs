using Android.Content;
using Android.Net;
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

        private Network? _current;
        private NetworkTransport _transport = NetworkTransport.None;

        public event EventHandler<NetworkChangedEventArgs>? NetworkChanged;

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
            }
        }

        public void Stop()
        {
            try { if (_cm is not null && _callback is not null) _cm.UnregisterNetworkCallback(_callback); }
            catch { }
            _cm = null;
            _callback = null;

            lock (_sync) { _current = null; _transport = NetworkTransport.None; }
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
    }
}
