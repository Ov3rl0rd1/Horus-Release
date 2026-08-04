using CommunityToolkit.Mvvm.ComponentModel;

namespace Horus.Presentation.Navigation
{
    /// <summary>
    /// App-wide screen router (singleton) with a real back stack, so the Android
    /// hardware back button and in-view back arrows can pop nested screens.
    ///
    /// <see cref="Go"/> pushes forward history (and auto-pops when navigating to the
    /// immediately-previous screen, so an in-view "back" arrow doesn't create a loop).
    /// <see cref="GoBack"/> pops one level. <see cref="Reset"/> clears history at auth
    /// boundaries (login/logout/startup) so you can't back across them.
    /// </summary>
    public partial class Navigator : ObservableObject
    {
        private const int MaxHistory = 32;

        [ObservableProperty] private AppScreen _currentScreen = AppScreen.Login;

        private readonly List<AppScreen> _history = new();

        /// <summary>Forward navigation (tabs, links, opening a nested screen).</summary>
        public void Go(AppScreen screen)
        {
            if (screen == CurrentScreen) return;
            RunOnMain(() =>
            {
                // Navigating to the previous screen == a back action: pop instead of push.
                if (_history.Count > 0 && _history[^1] == screen)
                    _history.RemoveAt(_history.Count - 1);
                else
                {
                    _history.Add(CurrentScreen);
                    if (_history.Count > MaxHistory) _history.RemoveAt(0);
                }
                CurrentScreen = screen;
            });
        }

        /// <summary>Pop one level. Returns false when there is nothing to go back to.</summary>
        public bool GoBack()
        {
            if (_history.Count == 0) return false;
            var prev = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            RunOnMain(() => CurrentScreen = prev);
            return true;
        }

        /// <summary>Jump to a screen and clear history (auth boundaries).</summary>
        public void Reset(AppScreen screen) =>
            RunOnMain(() => { _history.Clear(); CurrentScreen = screen; });

        public bool CanGoBack => _history.Count > 0;

        private static void RunOnMain(Action action)
        {
            if (MainThread.IsMainThread) action();
            else MainThread.BeginInvokeOnMainThread(action);
        }
    }
}
