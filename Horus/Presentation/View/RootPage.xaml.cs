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
    /// Android hardware back: close the payment overlay, pop a nested screen (e.g. Split),
    /// or retrace tabs. Returns true when handled; false lets the OS exit the app.
    /// </summary>
    protected override bool OnBackButtonPressed() => _shell.Back();
}
