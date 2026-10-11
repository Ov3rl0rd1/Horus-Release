using Microsoft.Maui.Controls.Shapes;

namespace Horus.Platforms.Windows.Views
{
    /// <summary>
    /// The app's theme, reached from code. The Windows screens are built in C# so they live
    /// with the platform and never touch the shared XAML; they still take every colour, font
    /// and style from <c>App.xaml</c>, so they match the rest of the app.
    /// </summary>
    internal static class Ui
    {
        public static Color C(string key) =>
            Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c ? c : Colors.White;

        public static Style? S(string key) =>
            Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var v) == true ? v as Style : null;

        public static string F(string key) =>
            Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var v) == true && v is string s ? s : string.Empty;

        public static Border Card(View content, Thickness? padding = null)
        {
            var b = new Border { Style = S("HzCard"), Content = content };
            if (padding is { } p) b.Padding = p;
            return b;
        }

        public static Label Eyebrow(string text) => new() { Text = text, Style = S("HzEyebrow") };

        public static Label Title(string text, double size = 20) =>
            new() { Text = text, Style = S("HzDisplay"), FontSize = size };

        public static Label Body(string? text = null, double? size = null)
        {
            var l = new Label { Text = text, Style = S("HzBodyLabel") };
            if (size is { } s) l.FontSize = s;
            return l;
        }

        public static Label Dim(string? text = null, double? size = null)
        {
            var l = new Label { Text = text, Style = S("HzDim"), LineBreakMode = LineBreakMode.WordWrap };
            if (size is { } s) l.FontSize = s;
            return l;
        }

        public static Label Strong(string? text = null, double size = 15) =>
            new() { Text = text, Style = S("HzSemiBold"), FontSize = size };

        /// <summary>A rounded pill — the route chips and the mode buttons.</summary>
        public static Border Pill(View content, Color background, Color stroke, Thickness? padding = null) => new()
        {
            Content = content,
            BackgroundColor = background,
            Stroke = stroke,
            StrokeThickness = 1,
            Padding = padding ?? new Thickness(10, 4),
            StrokeShape = new RoundRectangle { CornerRadius = 99 }
        };

        public static Label PillText(string? text, Color color, double size = 12) => new()
        {
            Text = text,
            TextColor = color,
            FontSize = size,
            FontAttributes = FontAttributes.Bold,
            FontFamily = F("FontBold"),
            VerticalOptions = LayoutOptions.Center
        };

        public static TapGestureRecognizer Tap(Action action)
        {
            var t = new TapGestureRecognizer();
            t.Tapped += (_, _) => action();
            return t;
        }

        /// <summary>"1.2 МБ", "640 КБ" — totals.</summary>
        public static string Bytes(long b) => b switch
        {
            >= 1L << 30 => $"{b / (double)(1L << 30):0.0} ГБ",
            >= 1L << 20 => $"{b / (double)(1L << 20):0.0} МБ",
            >= 1L << 10 => $"{b / 1024.0:0} КБ",
            _ => $"{b} Б"
        };

        /// <summary>Bits per second, the way speeds are quoted.</summary>
        public static string Rate(double bytesPerSecond)
        {
            var bits = bytesPerSecond * 8;
            return bits >= 1_000_000 ? $"{bits / 1_000_000:0.0} Мбит/с" : $"{bits / 1_000:0} Кбит/с";
        }
    }
}
