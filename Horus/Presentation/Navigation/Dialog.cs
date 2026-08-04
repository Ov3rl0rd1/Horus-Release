namespace Horus.Presentation.Navigation
{
    /// <summary>
    /// Small alert/action-sheet helper that works without MAUI Shell (the v2 UI uses
    /// a custom root page). Resolves the current window's page on demand.
    /// </summary>
    public static class Dialog
    {
        private static Page? CurrentPage =>
            Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;

        public static Task Alert(string title, string message, string cancel = "OK") =>
            CurrentPage?.DisplayAlertAsync(title, message, cancel) ?? Task.CompletedTask;

        public static Task<bool> Confirm(string title, string message, string accept, string cancel) =>
            CurrentPage?.DisplayAlertAsync(title, message, accept, cancel) ?? Task.FromResult(false);

        public static Task<string?> ActionSheet(string title, string cancel, string? destruction, params string[] buttons) =>
            CurrentPage?.DisplayActionSheetAsync(title, cancel, destruction, buttons) ?? Task.FromResult<string?>(null);
    }
}
