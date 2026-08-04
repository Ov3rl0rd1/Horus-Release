using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    /// <summary>
    /// Samples the platform tunnel counters once a second and turns them into rates.
    ///
    /// The counters are cumulative totals owned by hev-socks5-tunnel, so speeds have to
    /// be differentiated here. Previously the protocol layer pushed raw counter values in
    /// as if they were already rates, and only did so when the core happened to print a
    /// log line — which at the default log level is never.
    /// </summary>
    public class TrafficMonitorService : ITrafficMonitorService
    {
        private readonly IVpnPlatformService _platform;
        private readonly object _lock = new();

        private TrafficStats _stats = new();
        private DateTime _sessionStart;
        private CancellationTokenSource? _cts;

        // Previous cumulative sample, for the delta.
        private long _lastUpTotal;
        private long _lastDownTotal;
        private DateTime _lastSampleAt;
        private bool _hasBaseline;

        public TrafficMonitorService(IVpnPlatformService platform)
        {
            _platform = platform;
        }

        public TrafficStats CurrentStats
        {
            get { lock (_lock) return _stats; }
        }

        public event EventHandler<TrafficUpdatedEventArgs>? TrafficUpdated;

        public void Start()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _sessionStart = DateTime.UtcNow;
            lock (_lock)
            {
                _stats = new TrafficStats { ConnectedAt = _sessionStart };
                ResetBaseline();
            }
            _ = RunTickLoopAsync(_cts.Token);
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts = null;
        }

        public void Reset()
        {
            lock (_lock)
            {
                _stats = new TrafficStats();
                ResetBaseline();
            }
        }

        /// <summary>
        /// Accepts an externally measured sample. Kept for callers that have their own
        /// counters; the tunnel feed goes through <see cref="SampleTunnel"/>.
        /// </summary>
        public void Feed(TrafficStatisticsEventArgs e)
        {
            lock (_lock)
            {
                _stats.BytesUpTotal = e.TotalUp;
                _stats.BytesDownTotal = e.TotalDown;
                _stats.SpeedUpBps = e.UpBps;
                _stats.SpeedDownBps = e.DownBps;
            }
        }

        // ── Private ──────────────────────────────────────────────────────────

        private void ResetBaseline()
        {
            _lastUpTotal = 0;
            _lastDownTotal = 0;
            _lastSampleAt = DateTime.UtcNow;
            _hasBaseline = false;
        }

        /// <summary>
        /// Reads the cumulative tunnel counters and differentiates them.
        ///
        /// Index layout comes from <c>hev_socks5_tunnel_stats</c>:
        /// <c>[tx_packets, tx_bytes, rx_packets, rx_bytes]</c>. "tx"/"rx" are relative to
        /// the tunnel device, so tx is what the device sent into the tunnel — upload.
        /// Only the byte counters are used; the packet counts are not traffic volume.
        /// </summary>
        private void SampleTunnel()
        {
            long[] counters;
            try { counters = _platform.GetTunnelStats(); }
            catch { return; }

            if (counters.Length < 4) return;

            long upTotal = counters[1];   // tx_bytes
            long downTotal = counters[3]; // rx_bytes

            var now = DateTime.UtcNow;
            var elapsed = (now - _lastSampleAt).TotalSeconds;

            lock (_lock)
            {
                _stats.BytesUpTotal = upTotal;
                _stats.BytesDownTotal = downTotal;

                // A reconnect restarts hev's counters from zero. Treat any backwards step
                // as a new baseline rather than emitting a negative or absurd rate.
                bool wrapped = upTotal < _lastUpTotal || downTotal < _lastDownTotal;

                if (!_hasBaseline || wrapped || elapsed <= 0)
                {
                    _stats.SpeedUpBps = 0;
                    _stats.SpeedDownBps = 0;
                }
                else
                {
                    _stats.SpeedUpBps = (long)((upTotal - _lastUpTotal) / elapsed);
                    _stats.SpeedDownBps = (long)((downTotal - _lastDownTotal) / elapsed);
                }
            }

            _lastUpTotal = upTotal;
            _lastDownTotal = downTotal;
            _lastSampleAt = now;
            _hasBaseline = true;
        }

        private async Task RunTickLoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                SampleTunnel();

                TrafficStats snapshot;
                lock (_lock)
                {
                    _stats.SessionDuration = DateTime.UtcNow - _sessionStart;
                    snapshot = new TrafficStats
                    {
                        BytesUpTotal = _stats.BytesUpTotal,
                        BytesDownTotal = _stats.BytesDownTotal,
                        SpeedUpBps = _stats.SpeedUpBps,
                        SpeedDownBps = _stats.SpeedDownBps,
                        SessionDuration = _stats.SessionDuration,
                        ConnectedAt = _stats.ConnectedAt,
                        ConnectedServer = _stats.ConnectedServer
                    };
                }
                TrafficUpdated?.Invoke(this, new TrafficUpdatedEventArgs(snapshot));
            }
        }
    }
}
