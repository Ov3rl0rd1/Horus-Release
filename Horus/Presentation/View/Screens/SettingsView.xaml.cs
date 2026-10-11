namespace Horus.Presentation.View.Screens;

public partial class SettingsView : ContentView
{
    public SettingsView() => InitializeComponent();

    /// <summary>
    /// A platform's own connection section, replacing the shared one; set by the root page.
    /// Null on Android, which keeps the shared section.
    /// </summary>
    public Microsoft.Maui.Controls.View? PlatformSection
    {
        get => PlatformSectionHost.Content;
        set
        {
            PlatformSectionHost.Content = value;
            SharedConnectionSection.IsVisible = value is null;
        }
    }
}
