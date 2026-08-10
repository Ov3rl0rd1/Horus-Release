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

        /// <summary>
        /// Entries forced to bypass the tunnel in every mode, from app configuration. The
        /// user can inspect them but not toggle them — they exist because the app is known
        /// to break behind a VPN.
        /// </summary>
        IReadOnlyList<string> AlwaysDirectEntries { get; }

        /// <summary>
        /// Fills in <see cref="AppOrProcessEntry.IconPath"/> for entries that don't have a
        /// cached icon yet, reporting each one as it becomes available.
        ///
        /// Separate from <see cref="GetAvailableEntriesAsync"/> on purpose: rendering an
        /// app icon means decoding another package's resources, which is far too slow to
        /// block the list on. The list appears first, icons arrive after.
        /// </summary>
        Task LoadIconsAsync(
            IReadOnlyList<AppOrProcessEntry> entries,
            Action<AppOrProcessEntry> onReady,
            CancellationToken ct = default);

        /// <summary>Applies the current selection to the active VPN tunnel.</summary>
        Task ApplyAsync();

        event EventHandler SelectionChanged;
    }
}
