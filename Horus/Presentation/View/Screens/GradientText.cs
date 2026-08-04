namespace Horus.Presentation.View.Screens;

/// <summary>
/// Builds a <see cref="FormattedString"/> whose characters are colored along a linear
/// gradient — a pure-MAUI stand-in for CSS gradient text (MAUI Labels take a single
/// <c>TextColor</c>, not a brush).
/// </summary>
public static class GradientText
{
    public static FormattedString Build(string text, Color from, Color to, string? fontFamily = null, double fontSize = 26)
    {
        var fs = new FormattedString();
        int n = Math.Max(text.Length - 1, 1);
        for (int i = 0; i < text.Length; i++)
        {
            double t = (double)i / n;
            var c = Color.FromRgba(
                from.Red + (to.Red - from.Red) * t,
                from.Green + (to.Green - from.Green) * t,
                from.Blue + (to.Blue - from.Blue) * t,
                1.0);
            fs.Spans.Add(new Span
            {
                Text = text[i].ToString(),
                TextColor = c,
                FontSize = fontSize,
                FontFamily = fontFamily
            });
        }
        return fs;
    }
}
