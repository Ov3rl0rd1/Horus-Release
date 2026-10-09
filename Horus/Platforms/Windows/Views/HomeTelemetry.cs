using Horus.Application;
using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Platforms.Windows.Tunnel;

namespace Horus.Platforms.Windows.Views
{
    /// <summary>
    /// What the home screen's graph shows: throughput history and the round trip to the node.
    ///
    /// <para>Outlives the view, so leaving the home screen and coming back keeps the last two
    /// minutes. The latency probe runs only while the home screen is visible and the tunnel is
    /// up — it is a TCP handshake to the node every two seconds, and nobody needs it measured
    /// while they are looking at the server list or playing with the window minimised.</para>
    /// </summary>
    public sealed class HomeTelemetry
    {
        private readonly WindowsVpnController _vpn;
        private readonly object _gate = new();
        private bool _visible;
        private bool _probing;

        public HomeTelemetry(ITrafficMonitorService traffic, IVpnController vpn)
        {
            _vpn = (WindowsVpnController)vpn;
            traffic.TrafficUpdated += OnTraffic;
            _vpn.StateChanged += OnState;
            Latency.Updated += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        }

        public TrafficHistory Traffic { get; } = new();
        public NodeLatencyMonitor Latency { get; } = new();

        /// <summary>Raised off the UI thread when there is something new to draw.</summary>
        public event EventHandler? Changed;

        public void SetVisible(bool visible)
        {
            _visible = visible;
            UpdateProbe();
        }

        private void OnTraffic(object? sender, TrafficUpdatedEventArgs e)
        {
            if (_vpn.State != VpnState.Connected) return;
            Traffic.Add(DateTime.UtcNow, e.Stats.SpeedDownBps, e.Stats.SpeedUpBps);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void OnState(object? sender, VpnStateChangedEventArgs e)
        {
            if (e.NewState is VpnState.Disconnected or VpnState.Error)
            {
                Traffic.Clear();
                lock (_gate)
                {
                    Latency.Stop();
                    Latency.Clear();
                    _probing = false;
                }
            }
            UpdateProbe();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateProbe()
        {
            lock (_gate)
            {
                var want = _visible && _vpn.State == VpnState.Connected;
                if (want == _probing) return;
                _probing = want;
                // A minimised window counts as not looking (AppVisibility follows the window's lifecycle).
                if (want) Latency.Start(() => AppVisibility.IsForeground ? _vpn.NodeEndpoint : null, () => _vpn.Path?.InterfaceIndex ?? 0);
                else Latency.Stop();
            }
        }
    }
}
