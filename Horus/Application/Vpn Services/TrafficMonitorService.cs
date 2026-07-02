using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    public class TrafficMonitorService : ITrafficMonitorService
    {
        private readonly object _lock = new();

        private TrafficStats _stats = new();
        private DateTime _sessionStart;
        private long _lastUp;
        private long _lastDown;
        private CancellationTokenSource? _cts;

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
                _lastUp = 0;
                _lastDown = 0;
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
                _lastUp = 0;
                _lastDown = 0;
            }
        }

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

        private async Task RunTickLoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
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
