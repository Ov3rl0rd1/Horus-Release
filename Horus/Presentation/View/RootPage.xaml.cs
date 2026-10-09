using Horus.Presentation.ViewModels;

namespace Horus.Presentation.View;

public partial class RootPage : ContentPage
{
    private readonly ShellViewModel _shell;

    public RootPage(ShellViewModel vm, Horus.Presentation.Navigation.IPlatformScreens? screens = null)
    {
        InitializeComponent();
        _shell = vm;
        BindingContext = vm;

        if (screens is not null) AttachPlatformScreens(screens);
    }

    /// <summary>
    /// Puts a platform's own views in place of the shared ones it replaces. Nothing is
    /// registered on Android, so this never runs there.
    /// </summary>
    private void AttachPlatformScreens(Horus.Presentation.Navigation.IPlatformScreens screens)
    {
        _shell.PlatformScreens = screens;

        if (screens.Create(Horus.Presentation.Navigation.AppScreen.Home) is { } home)
        {
            PlatformHomeHost.Content = home;
            PlatformHomeHost.IsVisible = true;

            // Out of the tree and unbound, not just hidden: a hidden view still runs its
            // pulse animation and follows every property change of the view-model.
            SharedDesktopHome.BindingContext = null;
            (SharedDesktopHome.Parent as Layout)?.Children.Remove(SharedDesktopHome);
        }

        if (screens.Create(Horus.Presentation.Navigation.AppScreen.Apps) is { } apps)
        {
            AppsHost.Content = apps;
            _shell.HasAppsScreen = true;
        }

        if (screens.SettingsSection() is { } section)
            SettingsScreen.PlatformSection = section;
    }

    /// <summary>
    /// Second trigger for startup routing, alongside <c>App.OnStart</c>. The call is
    /// idempotent; having both means a missed lifecycle callback can't strand the app on
    /// the blank startup screen.
    /// </summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _shell.EnsureStartedAsync();
    }

    /// <summary>
    /// Android hardware back: close the payment overlay, pop a nested screen (e.g. Split),
    /// or retrace tabs. Returns true when handled; false lets the OS exit the app.
    /// </summary>
    protected override bool OnBackButtonPressed() => _shell.Back();
}
