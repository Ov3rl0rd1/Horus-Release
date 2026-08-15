using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    /// <summary>A place to look for a newer release.</summary>
    public interface IUpdateSource
    {
        UpdateOrigin Origin { get; }

        /// <summary>
        /// The newest published release, or null if the source is unreachable or has
        /// nothing. Must not throw — an unavailable source is an expected condition here,
        /// not an error, since the whole reason the fallback exists is that GitHub is
        /// routinely unreachable for these users.
        /// </summary>
        Task<UpdateManifest?> FetchLatestAsync(CancellationToken ct);
    }

    /// <summary>Applies a downloaded payload. One implementation per platform.</summary>
    public interface IUpdateInstaller
    {
        /// <summary>False when this build cannot update itself, e.g. an unknown install layout.</summary>
        bool IsSupported { get; }

        /// <summary>
        /// The tail of the asset name this platform needs — <c>-win-x64.msi</c>,
        /// <c>-android-arm64-v8a.apk</c>. Matched as a suffix because GitHub publishes
        /// versioned names and the site serves the same files unversioned.
        /// </summary>
        string? AssetSuffix { get; }

        /// <summary>
        /// True when installing will end this process. Callers use it to persist state
        /// before handing over, and to stop expecting anything after.
        /// </summary>
        bool TerminatesProcess { get; }

        /// <summary>
        /// Hands the verified payload to the platform. Returning normally means the
        /// install was <i>started</i>, not that it finished — on both platforms the
        /// installer outlives the caller.
        /// </summary>
        Task InstallAsync(string payloadPath, AppVersion version, CancellationToken ct);
    }

    /// <summary>Battery, network and screen state the deferral policy reads.</summary>
    public interface IDeviceConditions
    {
        DeviceConditions Read();
    }

    /// <summary>
    /// A quiet, non-interactive message to the user. Deliberately minimal: the product
    /// decision is that updates never interrupt, so this is the only thing the update path
    /// is allowed to show.
    /// </summary>
    public interface IUserNotifier
    {
        Task NotifyAsync(string title, string message);
    }

    /// <summary>Checks for, downloads and applies updates in the background.</summary>
    public interface IUpdateService
    {
        /// <summary>Version this process is running, from the assembly.</summary>
        AppVersion CurrentVersion { get; }

        /// <summary>Set when the running build is newer than the one recorded last run.</summary>
        AppVersion? JustUpdatedFrom { get; }

        /// <summary>Starts the background loop. Idempotent.</summary>
        void Start();

        void Stop();

        /// <summary>Forces a check now, ignoring the poll schedule. For the Settings screen.</summary>
        Task<UpdateManifest?> CheckNowAsync(CancellationToken ct = default);
    }
}
