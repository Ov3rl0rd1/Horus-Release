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

    /// <summary>
    /// Jumps the list to the first app under the tapped letter. Uses the view-model's
    /// index rather than searching the CollectionView, so the lookup stays O(n) over
    /// plain rows instead of touching realised cells.
    /// </summary>
    private void OnAlphabetTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Label { BindingContext: string letter }) return;
        if (BindingContext is not SettingsViewModel vm) return;

        var index = vm.IndexOfLetter(letter);
        if (index < 0) return;

        AppList.ScrollTo(index, position: ScrollToPosition.Start, animate: false);
    }

    /// <summary>
    /// Clears the search field. Setting the bound property is enough — the view-model
    /// debounces and cancels any filter already in flight, so this cannot stack up work.
    /// </summary>
    private void OnClearSearch(object? sender, TappedEventArgs e)
    {
        if (BindingContext is SettingsViewModel vm) vm.AppSearch = string.Empty;
    }
}
