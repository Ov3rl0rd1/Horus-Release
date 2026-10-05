namespace Horus.Domain.Models
{
    /// <summary>
    /// Stand-in for the app's <c>Diag</c> logger, whose real implementation writes under
    /// MAUI's <c>FileSystem.CacheDirectory</c>. The linked API client only logs through it.
    /// </summary>
    public static class Diag
    {
        public static readonly List<string> Lines = [];

        public static void Info(string category, string message, string? detail = null, bool userAction = false)
        {
            lock (Lines) Lines.Add($"{category}: {message}");
        }
    }
}
