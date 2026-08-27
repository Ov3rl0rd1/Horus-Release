namespace Horus.Domain.Interfaces
{
    /// <summary>
    /// Owns the <c>geoip.dat</c> / <c>geosite.dat</c> pair the core needs before it can
    /// resolve a <c>geoip:</c> or <c>geosite:</c> rule.
    ///
    /// <para><b>Downloaded, never bundled.</b> The current Russian rule set is about 18 MB
    /// of geoip and 74 MB of geosite. The APK already carries ~53 MB of core per ABI, and
    /// adding 92 MB of data that goes stale within days would roughly triple it for a
    /// feature most users will not enable. They are fetched on demand instead, onto an
    /// unmetered network, and verified against the SHA-256 sidecars the provider publishes
    /// beside them — the same discipline the app update payload gets.</para>
    ///
    /// <para><b>Absence is normal.</b> Everything that consumes this must treat missing
    /// assets as "no geo routing" rather than as an error. A config that names a category
    /// the core cannot resolve does not degrade — <c>XrayStart</c> fails and the tunnel does
    /// not come up at all.</para>
    /// </summary>
    public interface IGeoAssetService
    {
        /// <summary>True when both files are present and the core has been pointed at them.</summary>
        bool IsAvailable { get; }

        /// <summary>Where the files live, or null when they are not installed.</summary>
        string? AssetDirectory { get; }

        DateTimeOffset? LastUpdatedUtc { get; }

        /// <summary>
        /// Points the core at the asset directory if the files are there. Must run before
        /// <c>XrayStart</c>; calling it when they are absent is a no-op that returns false.
        /// </summary>
        bool Activate();

        /// <summary>
        /// Downloads or refreshes the pair. Returns false on any failure — an unavailable
        /// provider is an expected condition here, not an error, since the whole point is
        /// that these users are behind a filter.
        /// </summary>
        Task<bool> UpdateAsync(CancellationToken ct = default);

        /// <summary>Removes the downloaded files. For a settings screen and for tests.</summary>
        void Clear();
    }
}
