// What the XAML compiler generates for the two pages whose code-behind is compiled here:
// their x:Name fields and InitializeComponent. Keep in step with RootPage.xaml and
// SettingsView.xaml — a renamed x:Name shows up here as a missing field.
namespace Horus.Presentation.View
{
    public partial class RootPage
    {
        private void InitializeComponent() { }
        private ContentView PlatformHomeHost = null!;
        private Horus.Presentation.View.Screens.HomeViewDesktop SharedDesktopHome = null!;
        private ContentView AppsHost = null!;
        private Horus.Presentation.View.Screens.SettingsView SettingsScreen = null!;
    }
}
namespace Horus.Presentation.View.Screens
{
    public partial class HomeViewDesktop : ContentView { }
    public partial class SettingsView
    {
        private void InitializeComponent() { }
        private ContentView PlatformSectionHost = null!;
        private VerticalStackLayout SharedConnectionSection = null!;
    }
}
