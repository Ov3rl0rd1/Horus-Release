using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    /// <summary>
    /// Owns the <c>geoip.dat</c> / <c>geosite.dat</c> pair the core needs before it can
    /// resolve a <c>geoip:</c> or <c>geosite:</c> rule.
    ///
    /// <para><b>Downloaded, never bundled.</b> Not for size — the Russia-only build is
    /// about 400 KB against the 53 MB of core the APK already carries per ABI — but for
    /// staleness. <c>geosite</c> is rebuilt daily and <c>geoip</c> monthly, and a rule set
    /// frozen at the last app release is one that quietly stops matching the services it
    /// was built for. Fetching separately also keeps a routing-data change from needing an
    /// app update to reach anyone.</para>
    ///
    /// <para><b>Verified, not trusted.</b> Each file is checked against the
    /// <c>.sha256sum</c> the provider publishes beside it. These are inputs to routing
    /// decisions: a substituted geosite would silently send traffic the wrong way, which is
    /// worse than having none.</para>
    ///
    /// <para><b>Absence is normal.</b> Everything that consumes this must treat missing
    /// assets as "no geo routing" rather than as an error. A config that names a category
    /// the core cannot resolve does not degrade — <c>XrayStart</c> fails and the tunnel does
    /// not come up at all.</para>
    /// </summary>
    public interface IGeoAssetService
    {
        /// <summary>
        /// Whether this platform can route anything <c>direct</c> at all.
        ///
        /// <para>Geo routing works by sending matched traffic out through <c>freedom</c>,
        /// which is only genuinely direct where the host gives the core's own sockets a way
        /// around the tunnel. The two supported platforms get there differently:</para>
        ///
        /// <list type="bullet">
        /// <item><b>Android</b> — the app's UID is excluded from the tunnel and the core
        /// runs in-process, so every socket it opens is already outside. Nothing to
        /// configure.</item>
        /// <item><b>Windows</b> — <c>direct</c> is otherwise just the OS route table, and a
        /// default route pointing at the TUN sends a "direct" packet straight back into it,
        /// where hev hands it to the SOCKS5 inbound and routing emits it again: an unbounded
        /// loop costing a session per turn. The node escapes only because it gets an
        /// explicit <c>/32</c>, which does not scale to the 23 000 prefixes of
        /// <c>geoip:ru</c>. What does scale is pinning the outbound to the physical adapter
        /// — <c>sockopt.interface</c>, which the core turns into <c>IP_UNICAST_IF</c> and
        /// which overrides the route lookup for that socket alone. See
        /// <see cref="IDirectPathProvider"/>.</item>
        /// </list>
        ///
        /// <para>True here means "the mechanism exists on this platform", not "the next
        /// connect will use it": on Windows the pin is resolved per attempt and can come
        /// back empty, and the connect path drops geo routing when it does.</para>
        /// </summary>
        bool IsSupported { get; }

        /// <summary>True when both files are present and the core has been pointed at them.</summary>
        bool IsAvailable { get; }

        /// <summary>True when both files are on disk, whether or not the core has been told.</summary>
        bool IsInstalled { get; }

        /// <summary>Where the files live, or null when they are not installed.</summary>
        string? AssetDirectory { get; }

        DateTimeOffset? LastUpdatedUtc { get; }

        /// <summary>
        /// Points the core at the asset directory if the files are there. Must run before
        /// <c>XrayStart</c>; calling it when they are absent is a no-op that returns false.
        /// </summary>
        bool Activate();

        /// <summary>
        /// Asks the provider whether what is installed is still current, without
        /// downloading either rule file.
        ///
        /// <para>Compares the published <c>.sha256sum</c> sidecars — a hundred bytes each —
        /// against the digests of the installed copies. Deliberately not the GitHub
        /// releases API: the sidecar already <i>is</i> the version identity, it is served
        /// from <c>raw.githubusercontent.com</c> rather than an API host with a 60-per-hour
        /// unauthenticated limit, and it stays correct if the release branch is ever
        /// updated without cutting a release.</para>
        ///
        /// <para>Never throws. An unreachable provider reports
        /// <see cref="GeoAssetState.Unknown"/>, which for these users is an ordinary
        /// condition rather than a failure.</para>
        /// </summary>
        Task<GeoAssetCheck> CheckAsync(CancellationToken ct = default);

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
