using Horus.Presentation.View.Controls;
using Horus.Presentation.ViewModels;

namespace Horus.Presentation.View.Screens;

public partial class SplitTunnelingView : ContentView
{
    public SplitTunnelingView() => InitializeComponent();

    private void OnAppToggled(object? sender, ToggledEventArgs e)
    {
        // The row's IsDirect is already updated by the two-way binding; persist it.
        if (sender is PillToggle { BindingContext: SplitAppRow row } &&
            BindingContext is SettingsViewModel vm)
        {
            vm.ApplyAppCommand.Execute(row);
        }
    }
}
