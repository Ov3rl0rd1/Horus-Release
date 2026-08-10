using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    /// <summary>Used on platforms that don't support per-app/per-process split tunneling.</summary>
    public class StubSplitTunnelingService : ISplitTunnelingService
    {
        public bool IsSupported => false;

        public SplitTunnelingMode Mode { get; set; } = SplitTunnelingMode.Disabled;

        public IReadOnlyList<string> SelectedEntries => [];

        public IReadOnlyList<string> AlwaysDirectEntries => [];

        public event EventHandler? SelectionChanged;

        public Task<IReadOnlyList<AppOrProcessEntry>> GetAvailableEntriesAsync() =>
            Task.FromResult<IReadOnlyList<AppOrProcessEntry>>([]);

        public Task SetSelectedEntriesAsync(IEnumerable<string> entries) => Task.CompletedTask;

        public Task ApplyAsync() => Task.CompletedTask;

        public Task LoadIconsAsync(
            IReadOnlyList<AppOrProcessEntry> entries,
            Action<AppOrProcessEntry> onReady,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
