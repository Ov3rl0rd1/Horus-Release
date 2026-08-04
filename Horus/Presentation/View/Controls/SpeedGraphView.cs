namespace Horus.Presentation.View.Controls;

/// <summary>
/// Live speed graph: bar heights represent connection speed and the graph scrolls
/// right→left (newest sample enters at the right). Driven by <see cref="Level"/>
/// (0..1) while <see cref="IsActive"/> is true.
/// </summary>
public class SpeedGraphView : ContentView
{
    private const int BarCount = 26;

    private readonly GraphicsView _gv = new();
    private readonly BarsDrawable _drawable = new();
    private IDispatcherTimer? _timer;

    public static readonly BindableProperty IsActiveProperty = BindableProperty.Create(
        nameof(IsActive), typeof(bool), typeof(SpeedGraphView), false,
        propertyChanged: (b, _, _) => ((SpeedGraphView)b).OnActiveChanged());

    public static readonly BindableProperty LevelProperty = BindableProperty.Create(
        nameof(Level), typeof(double), typeof(SpeedGraphView), 0.1);

    public static readonly BindableProperty BarColorProperty = BindableProperty.Create(
        nameof(BarColor), typeof(Color), typeof(SpeedGraphView), Color.FromArgb("#F0C46A"),
        propertyChanged: (b, _, n) => { ((SpeedGraphView)b)._drawable.BarColor = (Color)n; ((SpeedGraphView)b)._gv.Invalidate(); });

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public Color BarColor { get => (Color)GetValue(BarColorProperty); set => SetValue(BarColorProperty, value); }

    public SpeedGraphView()
    {
        _gv.Drawable = _drawable;
        Content = _gv;
    }

    private void OnActiveChanged()
    {
        _drawable.Active = IsActive;
        if (IsActive)
        {
            _timer ??= CreateTimer();
            _timer.Start();
        }
        else
        {
            _timer?.Stop();
            _drawable.Reset();
            _gv.Invalidate();
        }
    }

    private IDispatcherTimer CreateTimer()
    {
        var t = Dispatcher.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(650);
        t.Tick += (_, _) =>
        {
            _drawable.Push(Level);
            _gv.Invalidate();
        };
        return t;
    }

    private sealed class BarsDrawable : IDrawable
    {
        private readonly double[] _hist = new double[BarCount];
        private readonly Random _rng = new();
        public Color BarColor = Color.FromArgb("#F0C46A");
        public bool Active;

        public void Push(double level)
        {
            var v = Math.Clamp(level, 0.05, 1.0);
            v *= 0.8 + _rng.NextDouble() * 0.4; // small jitter so bars feel alive
            Array.Copy(_hist, 1, _hist, 0, _hist.Length - 1);
            _hist[^1] = Math.Clamp(v, 0.05, 1.0);
        }

        public void Reset() => Array.Clear(_hist);

        public void Draw(ICanvas canvas, RectF rect)
        {
            float gap = 3f;
            float barW = (rect.Width - gap * (BarCount - 1)) / BarCount;
            if (barW <= 0) return;
            float baseY = rect.Height;
            canvas.FillColor = BarColor;
            canvas.Alpha = Active ? 0.8f : 0.3f;
            for (int i = 0; i < BarCount; i++)
            {
                float h = (float)Math.Max(3, _hist[i] * (rect.Height - 3));
                float x = i * (barW + gap);
                canvas.FillRoundedRectangle(x, baseY - h, barW, h, 2f);
            }
            canvas.Alpha = 1f;
        }
    }
}
