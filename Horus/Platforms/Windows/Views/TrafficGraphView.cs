using Microsoft.Maui.Graphics;
using Horus.Platforms.Windows.Tunnel;

namespace Horus.Platforms.Windows.Views
{
    /// <summary>
    /// Download as a filled area, upload as a line, over the last two minutes, on an axis
    /// that rounds to a readable number. Redrawn once a second while on screen so the graph
    /// scrolls even when nothing new has arrived.
    /// </summary>
    internal sealed class TrafficGraphView : GraphicsView
    {
        private readonly GraphDrawable _drawable;
        private IDispatcherTimer? _timer;

        public TrafficGraphView(TrafficHistory history)
        {
            _drawable = new GraphDrawable(history)
            {
                Down = Ui.C("HzGold"),
                Up = Ui.C("NeonViolet"),
                Grid = Ui.C("HzBorder"),
                Text = Ui.C("HzTextMuted"),
            };
            Drawable = _drawable;
            BackgroundColor = Colors.Transparent;
            HeightRequest = 170;
        }

        public void SetRunning(bool running)
        {
            if (running)
            {
                _timer ??= CreateTimer();
                if (!_timer.IsRunning) _timer.Start();
                Invalidate();
            }
            else
            {
                _timer?.Stop();
            }
        }

        private IDispatcherTimer CreateTimer()
        {
            var t = Dispatcher.CreateTimer();
            t.Interval = TimeSpan.FromSeconds(1);
            t.Tick += (_, _) => Invalidate();
            return t;
        }

        private sealed class GraphDrawable(TrafficHistory history) : IDrawable
        {
            private const float LabelGutter = 18;

            public Color Down = Colors.Gold;
            public Color Up = Colors.Violet;
            public Color Grid = Colors.Gray;
            public Color Text = Colors.Gray;

            public void Draw(ICanvas canvas, RectF rect)
            {
                var points = history.Window(DateTime.UtcNow);
                var peak = points.Count == 0 ? 0 : points.Max(p => Math.Max(p.DownBps, p.UpBps));
                var ceiling = TrafficHistory.CeilingFor(peak);

                var plot = new RectF(rect.X, rect.Y + LabelGutter, rect.Width, rect.Height - LabelGutter - 1);
                DrawGrid(canvas, plot, ceiling);
                if (points.Count < 2) return;

                var span = (float)TrafficHistory.Span.TotalSeconds;
                float X(double age) => plot.Right - (float)(age / span) * plot.Width;
                float Y(double bps) => plot.Bottom - (float)Math.Min(1, bps / ceiling) * plot.Height;

                // Download: area under the line, fading to nothing at the baseline.
                var area = new PathF();
                area.MoveTo(Math.Max(plot.Left, X(points[0].AgeSeconds)), plot.Bottom);
                foreach (var p in points) area.LineTo(Clamp(X(p.AgeSeconds), plot), Y(p.DownBps));
                area.LineTo(Clamp(X(points[^1].AgeSeconds), plot), plot.Bottom);
                area.Close();
                canvas.SetFillPaint(new LinearGradientPaint
                {
                    StartColor = Down.WithAlpha(0.35f),
                    EndColor = Down.WithAlpha(0.02f),
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(0, 1),
                }, plot);
                canvas.FillPath(area);

                canvas.StrokeLineJoin = LineJoin.Round;
                canvas.StrokeLineCap = LineCap.Round;
                Line(canvas, points, plot, X, Y, p => p.DownBps, Down, 2f);
                Line(canvas, points, plot, X, Y, p => p.UpBps, Up, 1.6f);
            }

            private static float Clamp(float x, RectF plot) => Math.Clamp(x, plot.Left, plot.Right);

            private static void Line(ICanvas canvas, IReadOnlyList<TrafficPoint> points, RectF plot,
                Func<double, float> x, Func<double, float> y, Func<TrafficPoint, double> value, Color color, float width)
            {
                var path = new PathF();
                path.MoveTo(Clamp(x(points[0].AgeSeconds), plot), y(value(points[0])));
                for (var i = 1; i < points.Count; i++) path.LineTo(Clamp(x(points[i].AgeSeconds), plot), y(value(points[i])));
                canvas.StrokeColor = color;
                canvas.StrokeSize = width;
                canvas.DrawPath(path);
            }

            private void DrawGrid(ICanvas canvas, RectF plot, double ceiling)
            {
                canvas.StrokeColor = Grid;
                canvas.StrokeSize = 1;
                canvas.SaveState();
                canvas.StrokeDashPattern = [3, 4];
                canvas.DrawLine(plot.Left, plot.Top, plot.Right, plot.Top);
                canvas.DrawLine(plot.Left, plot.Center.Y, plot.Right, plot.Center.Y);
                canvas.RestoreState();
                canvas.StrokeColor = Grid;
                canvas.StrokeSize = 1;
                canvas.DrawLine(plot.Left, plot.Bottom, plot.Right, plot.Bottom);

                canvas.Font = Microsoft.Maui.Graphics.Font.Default;
                canvas.FontSize = 11;
                canvas.FontColor = Text;
                canvas.DrawString(TrafficHistory.AxisLabel(ceiling), plot.Left, plot.Top - LabelGutter, plot.Width, LabelGutter - 3,
                    Microsoft.Maui.Graphics.HorizontalAlignment.Left, Microsoft.Maui.Graphics.VerticalAlignment.Bottom);
                canvas.DrawString("2 мин", plot.Left, plot.Top - LabelGutter, plot.Width, LabelGutter - 3,
                    Microsoft.Maui.Graphics.HorizontalAlignment.Right, Microsoft.Maui.Graphics.VerticalAlignment.Bottom);
            }
        }
    }

    /// <summary>
    /// The round trip to the node as a strip of bars, one per sample: green, gold or red by
    /// how a game would feel it, and a red tick where a probe got no answer.
    /// </summary>
    internal sealed class LatencyStripView : GraphicsView
    {
        public LatencyStripView(NodeLatencyMonitor latency)
        {
            Drawable = new Strip(latency)
            {
                Good = Ui.C("HzGreen"),
                Fair = Ui.C("HzGold"),
                Bad = Ui.C("HzRed"),
                Track = Ui.C("HzSurface2"),
            };
            BackgroundColor = Colors.Transparent;
            HeightRequest = 30;
        }

        /// <summary>Under 60 ms nobody notices; past 120 ms shooters do.</summary>
        public static Color ColorFor(int ms, Color good, Color fair, Color bad) => ms < 60 ? good : ms < 120 ? fair : bad;

        private sealed class Strip(NodeLatencyMonitor latency) : IDrawable
        {
            public Color Good = Colors.Green;
            public Color Fair = Colors.Gold;
            public Color Bad = Colors.Red;
            public Color Track = Colors.DarkGray;

            public void Draw(ICanvas canvas, RectF rect)
            {
                const int slots = NodeLatencyMonitor.Capacity;
                const float gap = 2;
                var w = (rect.Width - gap * (slots - 1)) / slots;
                if (w <= 0) return;

                var samples = latency.Samples;
                var top = Math.Max(100, samples.Where(s => s is not null).Select(s => s!.Value).DefaultIfEmpty(0).Max() * 1.2);

                // Newest at the right, like the traffic graph.
                for (var slot = 0; slot < slots; slot++)
                {
                    var x = rect.Left + slot * (w + gap);
                    var index = samples.Count - slots + slot;
                    canvas.FillColor = Track;
                    canvas.FillRoundedRectangle(x, rect.Top, w, rect.Height, 1.5f);
                    if (index < 0) continue;

                    if (samples[index] is { } ms)
                    {
                        var h = (float)Math.Max(3, ms / top * rect.Height);
                        canvas.FillColor = ColorFor(ms, Good, Fair, Bad);
                        canvas.FillRoundedRectangle(x, rect.Bottom - h, w, h, 1.5f);
                    }
                    else
                    {
                        canvas.FillColor = Bad.WithAlpha(0.8f);
                        canvas.FillRoundedRectangle(x, rect.Top, w, 4, 1.5f);
                    }
                }
            }
        }
    }
}
