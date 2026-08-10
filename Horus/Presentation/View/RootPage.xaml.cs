using Horus.Presentation.ViewModels;

namespace Horus.Presentation.View;

public partial class RootPage : ContentPage
{
    private readonly ShellViewModel _shell;

    public RootPage(ShellViewModel vm)
    {
        InitializeComponent();
        _shell = vm;
        BindingContext = vm;
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
