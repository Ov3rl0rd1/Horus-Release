namespace Horus.Platforms.Windows.Views
{
    /// <summary>One point of the speed graph: how long ago, and the two rates in bytes per second.</summary>
    public readonly record struct TrafficPoint(double AgeSeconds, double DownBps, double UpBps);

    /// <summary>
    /// The last two minutes of tunnel throughput, for the home screen's graph.
    ///
    /// <para>Kept by time rather than by count: the traffic monitor samples once a second
    /// while the window is on screen but every 15 s when it is not, and a graph indexed by
    /// sample would stretch fifteen seconds of a minimised window into one. The previous
    /// graph was neither — its bars were a single "level" with random jitter added.</para>
    ///
    /// <para>Pure, so <c>Horus.Tests</c> pins the window and the axis scale.</para>
    /// </summary>
    public sealed class TrafficHistory
    {
        public static readonly TimeSpan Span = TimeSpan.FromSeconds(120);

        /// <summary>The smallest top of the axis, in bits per second: an idle tunnel draws a flat line, not noise blown up to full height.</summary>
        public const double MinimumCeilingBits = 1_000_000;

        private readonly object _gate = new();
        private readonly List<(DateTime At, double Down, double Up)> _points = [];

        public void Add(DateTime at, double downBps, double upBps)
        {
            lock (_gate)
            {
                // Out-of-order samples would draw a line backwards; the monitor never sends
                // them, so this only guards against a clock step.
                if (_points.Count > 0 && at < _points[^1].At) _points.Clear();
                _points.Add((at, Math.Max(0, downBps), Math.Max(0, upBps)));
                // Keep one point older than the span, for the line's left end.
                var firstInside = _points.FindIndex(p => p.At >= at - Span);
                if (firstInside > 1) _points.RemoveRange(0, firstInside - 1);
            }
        }

        public void Clear()
        {
            lock (_gate) _points.Clear();
        }

        public bool IsEmpty
        {
            get { lock (_gate) return _points.Count == 0; }
        }

        /// <summary>
        /// The points to draw at <paramref name="now"/>, oldest first. One point just outside
        /// the span is kept so the line enters from the left edge instead of starting mid-air.
        /// </summary>
        public IReadOnlyList<TrafficPoint> Window(DateTime now)
        {
            lock (_gate)
            {
                var result = new List<TrafficPoint>(_points.Count);
                var span = Span.TotalSeconds;
                for (var i = 0; i < _points.Count; i++)
                {
                    var age = (now - _points[i].At).TotalSeconds;
                    var nextInside = i + 1 < _points.Count && (now - _points[i + 1].At).TotalSeconds <= span;
                    if (age > span && !nextInside) continue;
                    result.Add(new TrafficPoint(Math.Max(0, age), _points[i].Down, _points[i].Up));
                }
                return result;
            }
        }

        /// <summary>
        /// The top of the axis for a peak rate: the peak in bits rounded up to 1, 2 or 5 × 10ⁿ,
        /// never below <see cref="MinimumCeilingBits"/>. Returned in bytes per second.
        /// </summary>
        public static double CeilingFor(double peakBytesPerSecond)
        {
            var bits = Math.Max(peakBytesPerSecond * 8, MinimumCeilingBits);
            var magnitude = Math.Pow(10, Math.Floor(Math.Log10(bits)));
            foreach (var step in new[] { 1.0, 2.0, 5.0, 10.0 })
            {
                var candidate = step * magnitude;
                // A hair of tolerance, so exactly 10 Mbit/s is not pushed to 20 by rounding.
                if (bits <= candidate * 1.000001) return candidate / 8;
            }
            return 10 * magnitude / 8;
        }

        /// <summary>The axis label for a ceiling in bytes per second: "10 Мбит/с", "500 Кбит/с".</summary>
        public static string AxisLabel(double bytesPerSecond)
        {
            var bits = bytesPerSecond * 8;
            return bits >= 1_000_000_000 ? $"{bits / 1_000_000_000:0.#} Гбит/с"
                : bits >= 1_000_000 ? $"{bits / 1_000_000:0.#} Мбит/с"
                : $"{bits / 1_000:0} Кбит/с";
        }
    }
}
