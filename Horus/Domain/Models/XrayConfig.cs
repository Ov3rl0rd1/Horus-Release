using Horus.Protocols;

namespace Horus.Domain.Models
{
    /// <summary>
    /// Config for one xray-core run. A single xray process serves every protocol —
    /// <see cref="Link"/> decides which outbound becomes the <c>proxy</c> tag, and
    /// <see cref="ProtocolType"/> reports it for logging and fallback bookkeeping.
    /// </summary>
    public class XrayConfig : ProtocolConfig
    {
        public override ProtocolType ProtocolType => Link.Protocol;

        /// <summary>Parsed share link the proxy outbound is generated from.</summary>
        public required ShareLink Link { get; init; }

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
