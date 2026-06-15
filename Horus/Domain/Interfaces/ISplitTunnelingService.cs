using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    /// <summary>
    /// Per-app (Android) or per-process (Windows) split tunneling control.
    /// On platforms that don't support it, IsSupported returns false.
    /// </summary>
    public interface ISplitTunnelingService
    {
        bool IsSupported { get; }
        SplitTunnelingMode Mode { get; set; }

        /// <summary>
        /// All installed apps/known processes that can be managed.
        /// Android: package names + display labels. Windows: process image names.
        /// </summary>
        Task<IReadOnlyList<AppOrProcessEntry>> GetAvailableEntriesAsync();

        IReadOnlyList<string> SelectedEntries { get; }
        Task SetSelectedEntriesAsync(IEnumerable<string> entries);

        /// <summary>Applies the current selection to the active VPN tunnel.</summary>
        Task ApplyAsync();

        event EventHandler SelectionChanged;
    }
}
