namespace Horus.Presentation.View.Screens;

public partial class OnboardingView : ContentView
{
    public OnboardingView()
    {
        InitializeComponent();

        // Gold → lilac gradient on the hero's second line (per the design).
        HeroGradient.FormattedText = GradientText.Build(
            "Без блокировок.",
            Color.FromArgb("#F0C46A"),
            Color.FromArgb("#C7A6F5"),
            fontFamily: "Unbounded",
            fontSize: 26);
    }
}
