namespace Horus.Domain.Models
{
    /// <summary>
    /// Which geo categories are routed where, and what the user has excluded from them.
    ///
    /// <para><b>Scaffolding.</b> The pieces are here — options, asset management, config
    /// generation — but nothing switches this on yet and there is no settings screen. The
    /// shape is what matters: everything below is a free-form category string rather than a
    /// hardcoded "ru", because the useful category names are not the obvious ones and
    /// depend on which provider's files are installed.</para>
    ///
    /// <para><b>The naming trap.</b> There is no <c>geosite:ru</c> in any of the common
    /// providers, and the two things one might mean by it are opposites:</para>
    /// <list type="bullet">
    /// <item><c>geosite:category-ru</c> — Russian sites. Sending these <b>direct</b> keeps
    /// domestic traffic off the node: faster, and it stops banks and government sites
    /// seeing a foreign address.</item>
    /// <item><c>geosite:ru-blocked</c> — sites blocked <i>in</i> Russia. These are the ones
    /// that must go <b>through</b> the proxy.</item>
    /// </list>
    /// <para>A configuration that confuses the two produces a tunnel that proxies exactly
    /// the wrong half of the internet, and looks like it is working.</para>
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

        /// <summary>Categories routed straight out, e.g. <c>geosite:category-ru</c>.</summary>
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
    }
}
