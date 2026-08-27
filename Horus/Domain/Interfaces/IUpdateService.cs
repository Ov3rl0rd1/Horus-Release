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
        /// Whether the tunnel must be brought down before the install starts.
        ///
        /// <b>True only on Windows</b>, where the installer replaces files while a wintun
        /// adapter and its routes are live, and a process killed mid-install leaves the
        /// machine with no internet until it is rebooted.
        ///
        /// <b>False on Android</b>, and that is a correction rather than a tuning: the
        /// VpnService dies with the process and the system reclaims the interface, so there
        /// is nothing to leak. Stopping it first bought nothing and cost everything — the
        /// tunnel went down for an install that then needed a confirmation the app could
        /// not raise from the background, and stayed down while the attempt repeated every
        /// two minutes.
        /// </summary>
        bool RequiresTunnelDown { get; }

        /// <summary>
        /// Whether an install could start right now, and if not, why.
        ///
        /// Must be cheap and side-effect free: it is called <b>before</b> the tunnel is
        /// taken down, which is the whole point. Discovering the answer inside
        /// <see cref="InstallAsync"/> meant the VPN was already off by the time the install
        /// turned out to be impossible.
        /// </summary>
        UpdateBlocker CheckReadiness();

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

        /// <summary>
        /// Why a ready update cannot be applied, or <see cref="UpdateBlocker.None"/>. Drives
        /// the Home screen notice, so the user learns why nothing is happening.
        /// </summary>
        UpdateBlocker Blocker { get; }

        event EventHandler? BlockerChanged;

        /// <summary>
        /// Re-evaluates a parked update immediately. Called when a permission may have
        /// changed — on app resume — which is the only signal Android offers for it. A
        /// parked update does nothing at all until this is called, so there is no retry
        /// loop and no battery cost while it waits.
        /// </summary>
        void RetryNow();

        /// <summary>Starts the background loop. Idempotent.</summary>
        void Start();

        void Stop();

        /// <summary>Forces a check now, ignoring the poll schedule. For the Settings screen.</summary>
        Task<UpdateManifest?> CheckNowAsync(CancellationToken ct = default);
    }
}
