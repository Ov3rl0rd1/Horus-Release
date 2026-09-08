namespace Horus.Domain.Models
{
    /// <summary>
    /// Which geo categories are routed where, and what the user has excluded from them.
    ///
    /// <para>Everything below is a free-form category string rather than a hardcoded "ru",
    /// because the useful category names depend on which provider's files are installed.
    /// The names this app actually ships with are in <see cref="GeoCategories"/>.</para>
    ///
    /// <para><b>The naming trap.</b> There is no <c>geosite:ru</c> in any of the common
    /// upstream providers, and the two things one might mean by it are opposites:</para>
    /// <list type="bullet">
    /// <item><c>geosite:category-ru</c> — Russian sites. Sending these <b>direct</b> keeps
    /// domestic traffic off the node: faster, and it stops banks and government sites
    /// seeing a foreign address.</item>
    /// <item><c>geosite:ru-blocked</c> — sites blocked <i>in</i> Russia. These are the ones
    /// that must go <b>through</b> the proxy.</item>
    /// </list>
    /// <para>A configuration that confuses the two produces a tunnel that proxies exactly
    /// the wrong half of the internet, and looks like it is working. The HorusGeo build
    /// resolves this by naming them <c>ru</c> and <c>ru-exclude</c> — see
    /// <see cref="GeoCategories"/>.</para>
    /// </summary>
    public sealed class GeoRoutingOptions
    {
        public static readonly GeoRoutingOptions Disabled = new();

        /// <summary>
        /// Off unless the assets are present. Generating a config that names a category the
        /// core cannot resolve makes <c>XrayStart</c> fail outright, so the caller must
        /// check <see cref="Interfaces.IGeoAssetService.IsAvailable"/> first.
        /// </summary>
        public bool Enabled { get; init; }

        /// <summary>Categories routed straight out, e.g. <c>geosite:ru</c>.</summary>
        public IReadOnlyList<string> DirectSites { get; init; } = [];

        /// <summary>IP categories routed straight out, e.g. <c>geoip:ru</c>.</summary>
        public IReadOnlyList<string> DirectIps { get; init; } = [];

        /// <summary>
        /// Domains the user wants proxied even though a direct category covers them.
        ///
        /// Matched before the category rules, which is the only ordering that can work: a
        /// geo set is thousands of entries and cannot be edited, so an exception has to win
        /// by being earlier rather than by being more specific.
        /// </summary>
        public IReadOnlyList<string> ProxyDomainExceptions { get; init; } = [];

        /// <summary>IPs and CIDRs proxied despite a direct category covering them.</summary>
        public IReadOnlyList<string> ProxyIpExceptions { get; init; } = [];

        public bool HasAnything =>
            Enabled && (DirectSites.Count > 0 || DirectIps.Count > 0);

        /// <summary>
        /// The configuration the shipped rule files are built for: everything Russian goes
        /// direct, and the blocked-media set is pulled back out of it into the tunnel.
        ///
        /// <para><paramref name="userProxyDomains"/> are appended <i>after</i>
        /// <c>ru-exclude</c> and before the direct categories, so both kinds of exception
        /// still win over the category that contains them.</para>
        /// </summary>
        public static GeoRoutingOptions ForRussianBypass(
            IReadOnlyList<string>? userProxyDomains = null,
            IReadOnlyList<string>? userProxyIps = null) => new()
            {
                Enabled = true,
                DirectSites = [GeoCategories.DirectSites],
                DirectIps = [GeoCategories.DirectIps],
                ProxyDomainExceptions = userProxyDomains is { Count: > 0 }
                    ? [GeoCategories.ProxySites, .. userProxyDomains]
                    : [GeoCategories.ProxySites],
                ProxyIpExceptions = userProxyIps ?? []
            };
    }

    /// <summary>
    /// The category names present in the <c>.dat</c> files this app installs, which are
    /// built by <c>Ov3rl0rd1/horus-geoip</c> and <c>Ov3rl0rd1/horus-geosite</c>.
    ///
    /// <para><b>These are a contract with the files, not a preference.</b> Naming a
    /// category a <c>.dat</c> does not carry is not a soft failure — xray rejects the
    /// config and the tunnel never comes up. The builds carry exactly three categories
    /// between them; <c>Horus.Tests/GeoAssetContractTests.cs</c> pins the names.</para>
    ///
    /// <para>Notably absent: <c>geoip:private</c>. The geoip build carries only <c>RU</c>,
    /// so the local ranges have to stay written out as literal CIDRs — which they are, in
    /// <see cref="Protocols.LocalNetworks"/>, for unrelated reasons.</para>
    /// </summary>
    public static class GeoCategories
    {
        /// <summary>Russian domains: <c>.ru</c>/<c>.su</c>/<c>.рф</c> plus the named services.</summary>
        public const string DirectSites = "geosite:ru";

        /// <summary>Russian networks — about 23 000 prefixes, half of them IPv6.</summary>
        public const string DirectIps = "geoip:ru";

        /// <summary>
        /// Blocked-in-Russia media, which must be proxied even though the whole of
        /// <c>.ru</c> is direct.
        ///
        /// <para>This category exists because the <c>.dat</c> format cannot express "this
        /// suffix, except that domain". <c>ru</c> carries a rule for the entire <c>.ru</c>
        /// zone, so a blocked <c>.ru</c> site matches it no matter what is removed from the
        /// list. The only way out is a separate rule placed <b>above</b> it.</para>
        /// </summary>
        public const string ProxySites = "geosite:ru-exclude";
    }

    /// <summary>What a check against the provider found.</summary>
    public enum GeoAssetState
    {
        /// <summary>Nothing installed.</summary>
        Missing,

        /// <summary>Installed and matching what the provider publishes.</summary>
        UpToDate,

        /// <summary>Installed, but the provider publishes different bytes.</summary>
        UpdateAvailable,

        /// <summary>The provider could not be reached, so nothing is known either way.</summary>
        Unknown
    }

    /// <summary>
    /// Result of <see cref="Interfaces.IGeoAssetService.CheckAsync"/>.
    ///
    /// <para><see cref="State"/> is deliberately four-valued. "Could not reach the
    /// provider" and "you are up to date" are the same non-event to a progress spinner and
    /// completely different to a user who is trying to work out why a site is not
    /// bypassing — and for these users an unreachable GitHub is the normal state with the
    /// tunnel down, not an error worth an alarm.</para>
    /// </summary>
    public sealed record GeoAssetCheck(GeoAssetState State, DateTimeOffset? InstalledAtUtc)
    {
        public static readonly GeoAssetCheck Missing = new(GeoAssetState.Missing, null);
    }
}
