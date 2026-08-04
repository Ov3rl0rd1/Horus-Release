using Microsoft.Maui.Controls.Shapes;
using Path = Microsoft.Maui.Controls.Shapes.Path;

namespace Horus.Presentation.View.Controls;

public enum IconKind
{
    Home, Servers, Settings, Search, Auto, Check, Close, ChevronRight, ChevronLeft, Refresh
}

/// <summary>
/// Crisp, tintable vector icon built from the original design's SVG paths (24×24 viewBox).
/// Uses a MAUI <see cref="Path"/> so it scales and recolors without rasterization.
/// </summary>
public class IconView : ContentView
{
    // SVG path data lifted from the handoff HTML (circles/ellipses rewritten as arcs).
    private static readonly Dictionary<IconKind, string> Data = new()
    {
        [IconKind.Home] = "M12 3.5 L12 11.5 M7.2 6.2 a7.5 7.5 0 1 0 9.6 0",
        [IconKind.Servers] = "M3.5 12 a8.5 8.5 0 1 0 17 0 a8.5 8.5 0 1 0 -17 0 " +
                             "M3.5 12 L20.5 12 " +
                             "M8.2 12 a3.8 8.5 0 1 0 7.6 0 a3.8 8.5 0 1 0 -7.6 0",
        [IconKind.Settings] = "M4 7.5 L20 7.5 M6.9 7.5 a2.6 2.6 0 1 0 5.2 0 a2.6 2.6 0 1 0 -5.2 0 " +
                              "M4 16.5 L20 16.5 M12.4 16.5 a2.6 2.6 0 1 0 5.2 0 a2.6 2.6 0 1 0 -5.2 0",
        [IconKind.Search] = "M4.5 11 a6.5 6.5 0 1 0 13 0 a6.5 6.5 0 1 0 -13 0 M16 16 L20.5 20.5",
        [IconKind.Auto] = "M13 2 L4.5 13.5 L11 13.5 L9.5 22 L19 10 L12.5 10 L13 2 Z",
        [IconKind.Check] = "M4.5 12.5 L9.5 17.5 L19.5 7",
        [IconKind.Close] = "M5 5 L19 19 M19 5 L5 19",
        [IconKind.ChevronRight] = "M9 5 L16 12 L9 19",
        [IconKind.ChevronLeft] = "M15 5 L8 12 L15 19",
        [IconKind.Refresh] = "M21 12 a9 9 0 1 1 -2.64 -6.36 M21 3 L21 9 L15 9",
    };

    private static readonly PathGeometryConverter Converter = new();

    private readonly Path _path = new()
    {
        Aspect = Stretch.Uniform,
        StrokeLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        HorizontalOptions = LayoutOptions.Center,
        VerticalOptions = LayoutOptions.Center,
    };

    public static readonly BindableProperty KindProperty = BindableProperty.Create(
        nameof(Kind), typeof(IconKind), typeof(IconView), IconKind.Home,
        propertyChanged: (b, _, _) => ((IconView)b).Rebuild());

    public static readonly BindableProperty ColorProperty = BindableProperty.Create(
        nameof(Color), typeof(Color), typeof(IconView), Colors.White,
        propertyChanged: (b, _, _) => ((IconView)b).ApplyColor());

    public static readonly BindableProperty StrokeWidthProperty = BindableProperty.Create(
        nameof(StrokeWidth), typeof(double), typeof(IconView), 1.9,
        propertyChanged: (b, _, n) => ((IconView)b)._path.StrokeThickness = (double)n);

    public static readonly BindableProperty IconSizeProperty = BindableProperty.Create(
        nameof(IconSize), typeof(double), typeof(IconView), 22.0,
        propertyChanged: (b, _, _) => ((IconView)b).ApplySize());

    public IconKind Kind { get => (IconKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    public double IconSize { get => (double)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

    public IconView()
    {
        _path.StrokeThickness = StrokeWidth;
        Content = _path;
        Rebuild();
        ApplySize();
    }

    private void ApplySize()
    {
        WidthRequest = IconSize;
        HeightRequest = IconSize;
        _path.WidthRequest = IconSize;
        _path.HeightRequest = IconSize;
    }

    private void Rebuild()
    {
        _path.Data = (Geometry?)Converter.ConvertFromInvariantString(Data[Kind]);
        ApplyColor();
    }

    private void ApplyColor()
    {
        if (Kind == IconKind.Auto)
        {
            _path.Fill = Color;
            _path.Stroke = null;
        }
        else
        {
            _path.Stroke = Color;
            _path.Fill = null;
        }
    }
}
