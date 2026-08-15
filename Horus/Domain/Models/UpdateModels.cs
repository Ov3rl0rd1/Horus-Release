namespace Horus.Domain.Models
{
    /// <summary>How soon an available update should be applied.</summary>
    public enum UpdateUrgency
    {
        /// <summary>Nothing newer is published.</summary>
        None = 0,

        /// <summary>
        /// Third component only. Downloaded on an unmetered network and installed at a
        /// moment that costs the user nothing — night, charging, screen off — or when the
        /// deferral window runs out, whichever comes first.
        /// </summary>
        Deferred = 1,

        /// <summary>
        /// First or second component changed. Fetched and applied as soon as it can be,
        /// on whatever network is available.
        /// </summary>
        Immediate = 2
    }

    /// <summary>Where a manifest came from. Recorded in diagnostics, never shown to users.</summary>
    public enum UpdateOrigin { GitHub, Site }

    /// <summary>One downloadable file in a release.</summary>
    /// <param name="Name">File name as published, e.g. <c>Horus-0.1.0-win-x64.msi</c>.</param>
    /// <param name="Url">Absolute download URL.</param>
    /// <param name="Sha256">
    /// Lower-case hex digest from <c>SHA256SUMS.txt</c>, or null when the release did not
    /// publish one. A null digest is not fatal but is treated as a reason to be careful:
    /// see <see cref="UpdatePlan.RequiresDigest"/>.
    /// </param>
    public sealed record UpdateAsset(string Name, string Url, string? Sha256);

    /// <summary>A published release, normalised across the two sources.</summary>
    public sealed record UpdateManifest(
        AppVersion Version,
        string Tag,
        UpdateOrigin Origin,
        IReadOnlyList<UpdateAsset> Assets)
    {
        /// <summary>
        /// Finds the asset for a platform by matching the parts of the name that are
        /// stable across both sources. GitHub publishes
        /// <c>Horus-0.1.0-android-arm64-v8a.apk</c> while the site serves the same bytes as
        /// <c>Horus-android-arm64-v8a.apk</c>, so matching on a full name would work for
        /// one source and silently fail for the other.
        /// </summary>
        public UpdateAsset? Find(string suffix) =>
            Assets.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What the update service decided to do, and why.</summary>
    public sealed record UpdatePlan(
        UpdateManifest Manifest,
        UpdateAsset Asset,
        UpdateUrgency Urgency,
        DateTimeOffset FirstSeenUtc)
    {
        /// <summary>
        /// A missing checksum is only tolerable over TLS from our own host. Anything from
        /// a redirect chain we do not control has to be verifiable.
        /// </summary>
        public bool RequiresDigest => Manifest.Origin == UpdateOrigin.GitHub;

        /// <summary>How long a deferred update is allowed to wait for a good moment.</summary>
        public static readonly TimeSpan DeferralWindow = TimeSpan.FromHours(24);

        public DateTimeOffset DeadlineUtc => FirstSeenUtc + DeferralWindow;

        public bool PastDeadline(DateTimeOffset now) =>
            Urgency == UpdateUrgency.Deferred && now >= DeadlineUtc;
    }

    /// <summary>A snapshot of the conditions the deferred-install policy cares about.</summary>
    /// <param name="HasNetwork">Any usable network at all.</param>
    /// <param name="IsUnmetered">Wi-Fi or wired — safe to pull ~60 MB over.</param>
    /// <param name="IsCharging">On external power.</param>
    /// <param name="IsInteractive">Screen on. The user is watching; a restart would be rude.</param>
    public readonly record struct DeviceConditions(
        bool HasNetwork,
        bool IsUnmetered,
        bool IsCharging,
        bool IsInteractive)
    {
        /// <summary>Assumed when a platform cannot answer: never blocks, never pretends.</summary>
        public static readonly DeviceConditions Unknown = new(true, true, true, false);
    }
}
