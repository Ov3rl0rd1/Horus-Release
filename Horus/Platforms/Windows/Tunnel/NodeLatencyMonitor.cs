using System.Diagnostics;

namespace Horus.Platforms.Windows.Tunnel
{
    /// <summary>
    /// The round trip to the node, sampled while someone is looking.
    ///
    /// <para>The number a player cares about: every packet of a game goes to the node first,
    /// so this is the floor under any in-game ping. Measured as a TCP handshake pinned to the
    /// physical interface (<see cref="PhysicalProbe"/>) — the path the core's own connection
    /// takes. A reset from a UDP-only port is one round trip too, so it counts.</para>
    ///
    /// <para><b>Silence before the first answer is not loss.</b> A node whose firewall drops
    /// packets to closed ports never answers on the Hysteria2 port, and reporting that as
    /// 100% loss would tell a player their connection is broken when it is fine. Until a port
    /// has answered, misses only move the probe to the next candidate port; after that a miss
    /// is a lost sample.</para>
    /// </summary>
    public sealed class NodeLatencyMonitor
    {
        public const int Capacity = 60;
        private const int Window = 15;
        private const int MissesBeforeNextPort = 2;
        private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
        private static readonly int[] FallbackPorts = [443, 80];

        private readonly TimeSpan _interval;
        private readonly Func<string, int, int, CancellationToken, Task<int?>> _measure;
        private readonly object _gate = new();
        private readonly Queue<int?> _samples = new();
        private CancellationTokenSource? _cts;

        // Probe state, touched only by the sampling loop.
        private (string Host, int Port)? _endpoint;
        private int[] _ports = [];
        private int _portIndex;
        private int _misses;

        public NodeLatencyMonitor()
            : this(DefaultInterval, (host, port, ifIndex, ct) => PhysicalProbe.ConnectAsync(host, port, ifIndex, ProbeTimeout, true, ct))
        {
        }

        /// <summary>For tests: a different pace and a fake probe.</summary>
        public NodeLatencyMonitor(TimeSpan interval, Func<string, int, int, CancellationToken, Task<int?>> measure)
        {
            _interval = interval;
            _measure = measure;
        }

        /// <summary>Raised on the thread pool after each sample.</summary>
        public event EventHandler? Updated;

        /// <summary>Some port of the node has answered since it was last changed.</summary>
        public bool Answering { get; private set; }

        /// <summary>The port being probed — for the diagnostics line.</summary>
        public int? Port => _ports.Length == 0 ? null : _ports[_portIndex];

        public IReadOnlyList<int?> Samples
        {
            get { lock (_gate) return [.. _samples]; }
        }

        /// <summary>Most recent sample in ms; null when it got no answer, or before any.</summary>
        public int? Latest
        {
            get { lock (_gate) return _samples.Count == 0 ? null : _samples.Last(); }
        }

        /// <summary>Median of the recent answered samples — steadier than the last one for a label.</summary>
        public int? Typical
        {
            get
            {
                var answered = Answered();
                if (answered.Count == 0) return null;
                var sorted = answered.Order().ToList();
                return sorted[sorted.Count / 2];
            }
        }

        /// <summary>
        /// Mean change between consecutive answered samples — what a player feels as
        /// stutter, separately from how far away the node is.
        /// </summary>
        public int? Jitter
        {
            get
            {
                var answered = Answered();
                if (answered.Count < 3) return null;
                var sum = 0.0;
                for (var i = 1; i < answered.Count; i++) sum += Math.Abs(answered[i] - answered[i - 1]);
                return (int)Math.Round(sum / (answered.Count - 1));
            }
        }

        /// <summary>Share of the recent samples that got no answer, 0..1.</summary>
        public double Loss
        {
            get
            {
                lock (_gate)
                {
                    var recent = _samples.TakeLast(Window).ToList();
                    return recent.Count == 0 ? 0 : recent.Count(s => s is null) / (double)recent.Count;
                }
            }
        }

        /// <param name="endpoint">The node now; null pauses sampling (disconnected).</param>
        /// <param name="interfaceIndex">The physical interface to probe from; 0 for the route table.</param>
        public void Start(Func<(string Host, int Port)?> endpoint, Func<int> interfaceIndex)
        {
            Stop();
            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        if (endpoint() is { } ep) await SampleAsync(ep, interfaceIndex(), cts.Token);
                        await Task.Delay(_interval, cts.Token);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { Debug.WriteLine($"[Horus] latency: {ex.Message}"); }
                }
            });
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts = null;
        }

        /// <summary>Forget the history — after a disconnect, or when the node changes.</summary>
        public void Clear()
        {
            lock (_gate) _samples.Clear();
            Answering = false;
            _endpoint = null;
            _ports = [];
            _portIndex = 0;
            _misses = 0;
        }

        private async Task SampleAsync((string Host, int Port) ep, int ifIndex, CancellationToken ct)
        {
            if (_endpoint != ep)
            {
                Clear();
                _endpoint = ep;
                _ports = [ep.Port, .. FallbackPorts.Where(p => p != ep.Port)];
            }

            var ms = await _measure(ep.Host, _ports[_portIndex], ifIndex, ct);
            ct.ThrowIfCancellationRequested();

            if (ms is null && !Answering)
            {
                if (++_misses >= MissesBeforeNextPort)
                {
                    _misses = 0;
                    _portIndex = (_portIndex + 1) % _ports.Length;
                }
                Updated?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (ms is not null) Answering = true;
            lock (_gate)
            {
                _samples.Enqueue(ms);
                while (_samples.Count > Capacity) _samples.Dequeue();
            }
            Updated?.Invoke(this, EventArgs.Empty);
        }

        private List<int> Answered()
        {
            lock (_gate)
                return _samples.TakeLast(Window).Where(s => s is not null).Select(s => s!.Value).ToList();
        }
    }
}
