using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    /// <summary>
    /// Manages the local (offline) configuration file.
    /// Available in all builds but only actively used in ADMIN_MODE when
    /// the API server is unreachable.
    /// </summary>
    public interface ILocalConfigService
    {
        LocalConfig Config { get; }

        Task LoadAsync();
        Task SaveAsync();

        // ── Server management ────────────────────────────────────────────────
        Task AddServerAsync(LocalServerEntry entry);
        Task UpdateServerAsync(LocalServerEntry entry);
        Task RemoveServerAsync(string id);
        Task SetDefaultServerAsync(string id);

        // ── Routing rules ────────────────────────────────────────────────────
        Task SaveRoutingRulesAsync(RoutingRulesFile rules);

        // ── Split tunneling ──────────────────────────────────────────────────
        Task SaveSplitTunnelingAsync(SplitTunnelingMode mode, IEnumerable<string> entries);

        // ── GeoIP ────────────────────────────────────────────────────────────
        Task SetGeoDbPathAsync(string path);
    }

    /// <summary>
    /// Tracks whether the app is currently running in local (offline) mode
    /// and provides the API health-check mechanism.
    /// </summary>
    public interface ILocalModeService
    {
        bool IsLocalMode { get; }

        event EventHandler<bool> LocalModeChanged;

        /// <summary>
        /// Probes the API server. If unreachable, switches to local mode automatically.
        /// Returns true if API is reachable.
        /// </summary>
        Task<bool> ProbeApiAsync(CancellationToken ct = default);

        /// <summary>Forces local mode on or off (admin toggle).</summary>
        void SetLocalMode(bool enabled);
    }
}
