using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Horus.Presentation.Navigation;
using Horus.Presentation.ViewModels;

namespace Horus.Platforms.Windows.Views
{
    /// <summary>
    /// The Windows client's own screens, handed to the shared root page. Each view is built
    /// once, on first use, and kept: the home graph and the application list hold state that
    /// should survive a trip to another tab.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsScreens(IServiceProvider services) : IPlatformScreens
    {
        private DesktopHomeView? _home;
        private AppsView? _apps;

        public Microsoft.Maui.Controls.View? Create(AppScreen screen) => screen switch
        {
            AppScreen.Home => _home ??= new DesktopHomeView(
                services.GetRequiredService<MainViewModel>(), services.GetRequiredService<HomeTelemetry>()),
            AppScreen.Apps => _apps ??= new AppsView(services.GetRequiredService<AppsViewModel>()),
            _ => null
        };

        public Microsoft.Maui.Controls.View? SettingsSection() =>
            ActivatorUtilities.CreateInstance<WindowsSettingsSection>(services);

        public void VisibilityChanged(AppScreen screen, bool visible)
        {
            switch (screen)
            {
                case AppScreen.Home: _home?.SetVisible(visible); break;
                case AppScreen.Apps: _apps?.SetVisible(visible); break;
            }
        }
    }
}
