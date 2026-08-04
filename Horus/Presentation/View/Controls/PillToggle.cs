using Microsoft.Maui.Controls.Shapes;

namespace Horus.Presentation.View.Controls;

/// <summary>
/// Custom pill switch matching the design's toggles (gold when on, sliding knob) —
/// the platform-default <see cref="Switch"/> looks out of place, especially on Android.
/// </summary>
public class PillToggle : ContentView
{
    private static readonly Color OnTrack = Color.FromArgb("#F0C46A");
    private static readonly Color OffTrack = Color.FromArgb("#29FFFFFF");
    private static readonly Color OnKnob = Color.FromArgb("#160A22");
    private static readonly Color OffKnob = Color.FromArgb("#EFEAF6");

    private readonly Border _track = new()
    {
        WidthRequest = 44,
        HeightRequest = 26,
        StrokeThickness = 0,
        BackgroundColor = OffTrack,
        StrokeShape = new RoundRectangle { CornerRadius = 13 },
    };

    private readonly Ellipse _knob = new()
    {
        WidthRequest = 20,
        HeightRequest = 20,
        Fill = OffKnob,
        HorizontalOptions = LayoutOptions.Start,
        VerticalOptions = LayoutOptions.Center,
        Margin = new Thickness(3, 0, 0, 0),
    };

    public event EventHandler<ToggledEventArgs>? Toggled;

    public static readonly BindableProperty IsToggledProperty = BindableProperty.Create(
        nameof(IsToggled), typeof(bool), typeof(PillToggle), false,
        BindingMode.TwoWay, propertyChanged: (b, _, _) => ((PillToggle)b).Render(true));

    public bool IsToggled { get => (bool)GetValue(IsToggledProperty); set => SetValue(IsToggledProperty, value); }

    public PillToggle()
    {
        WidthRequest = 44;
        HeightRequest = 26;
        Content = new Grid { Children = { _track, _knob } };
        GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(OnTap) });
        Render(false);
    }

    private void OnTap()
    {
        IsToggled = !IsToggled;
        Toggled?.Invoke(this, new ToggledEventArgs(IsToggled));
    }

    private void Render(bool animate)
    {
        _track.BackgroundColor = IsToggled ? OnTrack : OffTrack;
        _knob.Fill = IsToggled ? OnKnob : OffKnob;
        var target = IsToggled ? 18 : 0;
        if (animate)
            _knob.TranslateTo(target, 0, 160, Easing.CubicOut);
        else
            _knob.TranslationX = target;
    }
}
