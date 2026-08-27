using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    /// <summary>
    /// Decides which notices the Home screen shows, and performs their actions.
    ///
    /// Entirely event-driven: it recomputes when the account changes, when a permission
    /// re-read finds something different, or when the updater reports that it is blocked.
    /// Nothing here polls, and nothing here runs while the app is in the background —
    /// a banner nobody can see is not worth a wakeup.
    /// </summary>
    public interface INoticeService
    {
        /// <summary>Current notices, highest priority first. Never null.</summary>
        IReadOnlyList<AppNotice> Current { get; }

        event EventHandler? Changed;

        /// <summary>Recomputes from current state. Called on resume and on every input change.</summary>
        void Refresh();

        /// <summary>Runs the action for a notice — opening a settings screen, or the payment sheet.</summary>
        Task ActAsync(NoticeKind kind);

        /// <summary>Hides a notice for a while. Ignored for kinds that are not dismissible.</summary>
        void Dismiss(NoticeKind kind);
    }
}
