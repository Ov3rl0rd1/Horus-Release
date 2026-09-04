using Horus.Protocols;

namespace Horus.Domain.Models
{
    /// <summary>
    /// Config for one xray-core run. A single xray process serves every protocol; the
    /// node supplies the outbound and this wraps it with the inbound, DNS and routing the
    /// app owns.
    ///
    /// <para><b>The outbound is no longer generated here.</b> The API hands over a complete
    /// xray outbound the node built, so this class carries it rather than the parsed share
    /// link it used to reconstruct one from. What the app still owns is everything
    /// <i>around</i> it — the SOCKS inbound the bridge dials, the resolvers, the local
    /// ranges that must not be proxied — and none of that depends on which protocol the
    /// node chose.</para>
    /// </summary>
    public class XrayConfig : ProtocolConfig
    {
        /// <summary>The complete outbound as the node described it, address already resolved.</summary>
        public required System.Text.Json.Nodes.JsonNode Outbound { get; set; }

        /// <summary>Stable offer id from the node's profile. Identity for logs and fallback.</summary>
        public override string OfferId => Offer;

        /// <summary>Backing value; <see cref="OfferId"/> is the abstract member it satisfies.</summary>
        public required string Offer { get; init; }

        /// <summary>Human-readable name for the UI, supplied by the node.</summary>
        public override string DisplayName => string.IsNullOrWhiteSpace(Label) ? Offer : Label;

        public string Label { get; init; } = string.Empty;

        /// <summary>The outbound's <c>protocol</c> field, for logging only.</summary>
        public string ProtocolName { get; init; } = "unknown";

        /// <summary>
        /// Literal address the outbound dials, or null when it dials nothing the app can
        /// route around — an olcRTC room has no address. Drives the platform bypass route,
        /// without which the core's own socket is carried by the tunnel it is feeding.
        /// </summary>
        public string? NodeAddress { get; init; }

        /// <summary>
        /// Geo-category routing. Disabled unless the caller has confirmed the .dat files
        /// are installed — naming a category the core cannot resolve makes XrayStart fail
        /// outright rather than degrading.
        /// </summary>
        public GeoRoutingOptions Geo { get; set; } = GeoRoutingOptions.Disabled;

        /// <summary>
        /// Port of the SOCKS5 inbound. Hardcoded on the other side too — in
        /// <c>HevSocksTunnel</c>'s YAML — so the two must never drift apart.
        /// </summary>
        public const int DefaultSocksPort = 1080;

        /// <summary>
        /// SOCKS5 inbound the platform TUN bridge dials. Must stay in sync with
        /// hev-socks5-tunnel's config on Android (<c>127.0.0.1:1080</c>).
        /// </summary>
        public string SocksAddress { get; set; } = "127.0.0.1";
        public int SocksPort { get; set; } = DefaultSocksPort;

        /// <summary>
        /// xray log level: debug | info | warning | error | none.
        ///
        /// <c>info</c> in every configuration, deliberately. A failing outbound reports
        /// itself at info ("failed to process outbound traffic > …"); at <c>warning</c> the
        /// log contains nothing but the startup banner, so a tunnel that connects and
        /// carries nothing looks identical to a healthy one — which is precisely the case
        /// this log exists to explain.
        ///
        /// The volume is modest, the file is truncated on every connect, and access
        /// logging — the part that would record where the user goes — stays off.
        /// </summary>
        public string LogLevel { get; set; } = "info";

        /// <summary>
        /// Resolvers for the core's own DNS client. Required on Android: the Go resolver
        /// finds no nameservers (no <c>/etc/resolv.conf</c>) and fails every lookup without
        /// sending a packet. DoH first so a poisoned or hijacked UDP resolver on the
        /// carrier network cannot silently redirect the direct path.
        /// </summary>
        public IReadOnlyList<string> DnsServers { get; set; } =
            ["https://1.1.1.1/dns-query", "1.1.1.1", "8.8.8.8"];

        /// <summary>
        /// File xray writes its error log to. The core is linked as a shared library, so
        /// its stdout/stderr go nowhere on Android — this file is the only way to see what
        /// the proxy half of the pipeline is doing. Null omits the setting.
        /// </summary>
        public string? LogFilePath { get; set; }

        /// <summary>Skip TLS certificate verification. Never enable in shipping builds.</summary>
        public bool AllowInsecure { get; set; }

        public override string ToConfig() => XrayConfigBuilder.Build(this);
    }
}
