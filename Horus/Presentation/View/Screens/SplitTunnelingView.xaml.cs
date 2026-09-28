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

    /// <summary>Show only processes that have a window — the useful default on a desktop.</summary>
    private void OnShowWindowed(object? sender, TappedEventArgs e)
    {
        if (BindingContext is SettingsViewModel vm) vm.WindowedOnly = true;
    }

    /// <summary>Show everything, for the case the user really is after a background service.</summary>
    private void OnShowAllProcesses(object? sender, TappedEventArgs e)
    {
        if (BindingContext is SettingsViewModel vm) vm.WindowedOnly = false;
    }

    /// <summary>
    /// Must match HeightRequest on the strip's entries. The drag turns finger travel into
    /// entries by dividing by it, so it is a contract with the markup rather than a guess.
    /// </summary>
    private const double StripEntryHeight = 14;

    /// <summary>Which entry the drag began on; -1 when no drag is in progress.</summary>
    private int _panFrom = -1;

    /// <summary>The entry the list is currently parked at, so an unchanged drag does no work.</summary>
    private int _panAt = -1;

    /// <summary>
    /// Drags along the strip, phone-book style: the list follows the finger and a bubble shows
    /// the letter it is on.
    ///
    /// <para>The start position comes from the entry the gesture began on rather than from a
    /// coordinate. MAUI's pan gives travel since the start, not where the start was, and every
    /// way of recovering the absolute position needs a layout measurement that is wrong the
    /// moment the strip is rebuilt by a search. Knowing the starting entry and the entry
    /// height is enough, and both are exact.</para>
    /// </summary>
    private void OnAlphabetPan(object? sender, PanUpdatedEventArgs e)
    {
        if (BindingContext is not SettingsViewModel vm) return;

        var letters = vm.AlphabetIndex;
        if (letters.Count == 0) return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panFrom = sender is Label { BindingContext: string start } ? SlotOf(letters, start) : -1;
                _panAt = -1;
                if (_panFrom >= 0) MoveTo(vm, letters, _panFrom);
                break;

            case GestureStatus.Running when _panFrom >= 0:
                MoveTo(vm, letters, Math.Clamp(
                    _panFrom + (int)Math.Round(e.TotalY / StripEntryHeight), 0, letters.Count - 1));
                break;

            default:
                _panFrom = -1;
                _panAt = -1;
                LetterBubble.IsVisible = false;
                break;
        }
    }

    private void MoveTo(SettingsViewModel vm, IReadOnlyList<string> letters, int slot)
    {
        if (slot == _panAt) return;
        _panAt = slot;

        // Centred strip, centred bubble, fixed entry height: the offset from the middle is
        // all that is needed to line the bubble up with the finger.
        LetterBubble.TranslationY = (slot - (letters.Count - 1) / 2.0) * StripEntryHeight;

        // A gap stands for letters the strip had no room for. Showing it would be meaningless,
        // so the nearest real letter is used — which is also where the list should go.
        var letter = NearestLetter(letters, slot);
        if (letter is null) return;

        LetterBubbleText.Text = letter;
        LetterBubble.IsVisible = true;

        var index = vm.IndexOfLetter(letter);
        if (index >= 0) AppList.ScrollTo(index, position: ScrollToPosition.Start, animate: false);
    }

    /// <summary>Position of an entry in the strip, or -1. Entries are unique, so this is exact.</summary>
    private static int SlotOf(IReadOnlyList<string> letters, string letter)
    {
        for (var i = 0; i < letters.Count; i++)
            if (letters[i] == letter) return i;
        return -1;
    }

    private static string? NearestLetter(IReadOnlyList<string> letters, int slot)
    {
        for (var reach = 0; reach < letters.Count; reach++)
        {
            if (slot - reach >= 0 && letters[slot - reach] != SettingsViewModel.IndexGap)
                return letters[slot - reach];

            if (slot + reach < letters.Count && letters[slot + reach] != SettingsViewModel.IndexGap)
                return letters[slot + reach];
        }

        return null;
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
