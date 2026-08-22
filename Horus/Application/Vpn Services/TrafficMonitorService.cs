using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    /// <summary>
    /// Samples the platform tunnel counters and turns them into rates.
    ///
    /// <para>The counters are cumulative totals owned by hev-socks5-tunnel, so speeds have
    /// to be differentiated here. Previously the protocol layer pushed raw counter values in
    /// as if they were already rates, and only did so when the core happened to print a log
    /// line — which at the default log level is never.</para>
    ///
    /// <para><b>The sampling rate follows who is watching.</b> This used to run at 1 Hz for
    /// as long as the tunnel was up — all night, on a phone in a pocket, doing a P/Invoke,
    /// an allocation and an event dispatch every second for a graph nobody could see. It
    /// was the only periodic timer in the app and the largest avoidable battery cost in it.
    /// Now: 1 Hz while a window is on screen, 15 s while the app is backgrounded but the
    /// screen is on, 60 s while the screen is off.</para>
    ///
    /// <para>Nothing is lost from the totals — the counters are cumulative, so a slower
    /// sample still reads the exact same number. What is lost is resolution in the speed
    /// graph for periods when nobody was looking at it, which is the intended trade.</para>
    ///
    /// <para>Wakeups are aligned to the interval boundary so the platform can coalesce them,
    /// and the wait is cut short when the app comes to the front rather than running out the
    /// 60-second sleep chosen while the screen was off.</para>
    /// </summary>
    public class TrafficMonitorService : ITrafficMonitorService
    {
        /// <summary>A window is on screen: the user may be watching the graph.</summary>
        private static readonly TimeSpan VisibleInterval = TimeSpan.FromSeconds(1);

        /// <summary>App backgrounded, screen on: only the totals matter.</summary>
        private static readonly TimeSpan BackgroundInterval = TimeSpan.FromSeconds(15);

        /// <summary>Screen off: nobody is waiting on anything.</summary>
        private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(60);

        private readonly IVpnPlatformService _platform;
        private readonly IDeviceConditions _device;
        private readonly object _lock = new();

        /// <summary>Completed to cut the current wait short. Swapped, never reused.</summary>
        private TaskCompletionSource? _wake;

        private TrafficStats _stats = new();
        private DateTime _sessionStart;
        private CancellationTokenSource? _cts;

        // Previous cumulative sample, for the delta.
        private long _lastUpTotal;
        private long _lastDownTotal;
        private DateTime _lastSampleAt;
        private bool _hasBaseline;

        public TrafficMonitorService(IVpnPlatformService platform, IDeviceConditions device)
        {
            _platform = platform;
            _device = device;

            // Coming back to the app is the one moment a stale graph is visible, so the
            // pending wait is cut short rather than left to expire.
            AppVisibility.BecameForeground += (_, __) => WakeNow();
        }

        /// <summary>Cuts the current wait short so the next sample happens immediately.</summary>
        private void WakeNow() => Interlocked.Exchange(ref _wake, null)?.TrySetResult();

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

        /// <summary>
        /// How often to sample right now. Read fresh on every iteration, so a change in
        /// visibility takes effect on the next tick rather than the next connect.
        /// </summary>
        private TimeSpan CurrentInterval()
        {
            if (AppVisibility.IsForeground) return VisibleInterval;

            try { return _device.Read().IsInteractive ? BackgroundInterval : IdleInterval; }
            catch { return BackgroundInterval; } // unreadable: assume the middle case
        }

        private async Task RunTickLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var interval = CurrentInterval();
                var intervalMs = (long)interval.TotalMilliseconds;

                var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Exchange(ref _wake, wake);

                using (var registration = ct.Register(() => wake.TrySetResult()))
                {
                    var deadline = Cadence.AlignToNext(Cadence.NowMs(), intervalMs);
                    if (!await Cadence.WaitUntilAsync(deadline, wake.Task, ct).ConfigureAwait(false))
                        return;
                }

                if (ct.IsCancellationRequested) return;

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
