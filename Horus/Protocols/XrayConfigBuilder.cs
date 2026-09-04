using Horus.Domain.Models;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Horus.Protocols
{
    /// <summary>
    /// Wraps the node's outbound in a runnable xray-core <c>config.json</c>.
    ///
    /// The shape is one SOCKS5 inbound (dialled by the platform TUN bridge) plus three
    /// outbounds — the node's proxy, <c>freedom</c> and <c>blackhole</c> — with routing
    /// that keeps private/loopback ranges off the tunnel.
    ///
    /// <para><b>This no longer builds the proxy outbound.</b> The API used to hand over
    /// share links and this reconstructed an outbound per protocol, which meant the app
    /// carried its own model of the core's schema and could drift from it — the
    /// <c>hysteria</c>-versus-<c>hysteria2</c> transport name and the finalmask layout were
    /// both found that way. The node now ships the outbound it wants used, so all that is
    /// left is the envelope, and none of the envelope depends on the protocol.</para>
    ///
    /// Geo predicates are emitted only when the caller confirms the .dat assets exist;
    /// otherwise routing stays free of them so no assets are needed.
    /// </summary>
    public static class XrayConfigBuilder
    {
        public const string ProxyTag = "proxy";
        public const string DirectTag = "direct";
        public const string BlockTag = "block";

        /// <summary>
        /// Multicast and broadcast, which are dropped rather than forwarded anywhere.
        ///
        /// Sending them down <c>direct</c> looks harmless and is not. On a host whose
        /// "direct" is simply the OS route table — Windows, where only the node and the
        /// resolvers have host routes around the tunnel — the core re-emits the packet, the
        /// route table hands it straight back to the tunnel, and each pass allocates another
        /// SOCKS5 UDP association. Windows chatters constantly on a fresh interface (SSDP,
        /// mDNS, LLMNR), so the amplification is immediate: a thousand sessions in three
        /// seconds, and a tunnel too busy to carry anything real. Nothing is lost by
        /// dropping them — link-local discovery has no meaning through a tunnel.
        /// </summary>
        private static readonly string[] DropRanges =
        [
            "224.0.0.0/4", "255.255.255.255/32", "ff00::/8"
        ];

        /// <summary>
        /// Unicast ranges that must never be routed into the tunnel.
        ///
        /// Shared with the platform tunnel services through <see cref="LocalNetworks"/>:
        /// the core's routing table and the TUN's own routes have to agree about what is
        /// local, and keeping two lists in two files is how they stop agreeing.
        /// </summary>
        private static readonly string[] DirectRanges = LocalNetworks.Direct;

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static string Build(XrayConfig cfg)
        {
            var log = new Dictionary<string, object?> { ["loglevel"] = cfg.LogLevel };
            if (!string.IsNullOrEmpty(cfg.LogFilePath))
            {
                // The core is a shared library with no usable stdout, so routing the error
                // log to a file is the only way to see it. Access logging stays off — it
                // records every destination the user visits.
                log["error"] = cfg.LogFilePath;
                log["access"] = "none";
            }

            var root = new Dictionary<string, object?>
            {
                ["log"] = log,

                // Explicit resolvers, because the core cannot use the platform one:
                // Go reads its nameservers from /etc/resolv.conf, which does not exist on
                // Android, so its resolver has none and every lookup fails without emitting
                // a packet. Routing is AsIs so proxied domains are resolved at the node and
                // never reach this; these serve the direct/freedom path only.
                ["dns"] = new Dictionary<string, object?>
                {
                    ["servers"] = cfg.DnsServers,
                    ["queryStrategy"] = "UseIPv4"
                },

                ["inbounds"] = new object[] { BuildSocksInbound(cfg) },
                ["outbounds"] = new object[]
                {
                    BuildProxyOutbound(cfg),
                    new Dictionary<string, object?>
                    {
                        ["tag"] = DirectTag,
                        ["protocol"] = "freedom",
                        ["settings"] = new Dictionary<string, object?> { ["domainStrategy"] = "UseIP" }
                    },
                    new Dictionary<string, object?>
                    {
                        ["tag"] = BlockTag,
                        ["protocol"] = "blackhole"
                    }
                },
                ["routing"] = new Dictionary<string, object?>
                {
                    ["domainStrategy"] = "AsIs",
                    ["rules"] = BuildRoutingRules(cfg)
                }
            };

            return JsonSerializer.Serialize(root, Options);
        }

        // ── Routing ──────────────────────────────────────────────────────────

        /// <summary>
        /// Order matters: the last rule is a catch-all to the proxy, so anything that must
        /// bypass the tunnel has to be matched before it.
        ///
        /// <b>Resolvers are deliberately not exempted.</b> An earlier version sent them
        /// <c>direct</c> to keep the core's own lookups off a proxy that might not be up
        /// yet. That is a DNS leak by construction: every query the device makes leaves in
        /// clear over the physical link, addressed to a public resolver, while the user is
        /// told their traffic is protected. Nothing here needs the exemption — the node is
        /// pre-resolved before the core starts (<c>ShareLink.ResolvedHost</c>) and
        /// <c>domainStrategy: AsIs</c> leaves proxied names to be resolved at the node — so
        /// resolver traffic falls through to the catch-all and is carried like everything
        /// else.
        /// </summary>
        private static object[] BuildRoutingRules(XrayConfig cfg)
        {
            var rules = new List<object>
            {
                // Before everything else: these must not reach direct or proxy. See DropRanges.
                new Dictionary<string, object?>
                {
                    ["type"] = "field",
                    ["ip"] = DropRanges,
                    ["outboundTag"] = BlockTag
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "field",
                    ["ip"] = DirectRanges,
                    ["outboundTag"] = DirectTag
                }
            };

            AddGeoRules(rules, cfg.Geo);

            rules.Add(new Dictionary<string, object?>
            {
                ["type"] = "field",
                ["network"] = "tcp,udp",
                ["outboundTag"] = ProxyTag
            });

            return [.. rules];
        }

        /// <summary>
        /// Emits the geo-category rules, exceptions first.
        ///
        /// <para><b>Order is the whole design.</b> A geo set is tens of thousands of entries
        /// and cannot be edited, so a user exception can only win by being matched earlier.
        /// Exceptions therefore come first and send their targets to the proxy; the category
        /// rules follow and send everything else in the set out direct.</para>
        ///
        /// <para>Nothing is emitted unless the caller has confirmed the <c>.dat</c> files are
        /// installed. Naming a category the core cannot resolve is not a soft failure — the
        /// config is rejected and the tunnel never comes up.</para>
        /// </summary>
        private static void AddGeoRules(List<object> rules, GeoRoutingOptions geo)
        {
            if (!geo.HasAnything) return;

            if (geo.ProxyDomainExceptions.Count > 0)
            {
                rules.Add(new Dictionary<string, object?>
                {
                    ["type"] = "field",
                    ["domain"] = geo.ProxyDomainExceptions,
                    ["outboundTag"] = ProxyTag
                });
            }

            if (geo.ProxyIpExceptions.Count > 0)
            {
                rules.Add(new Dictionary<string, object?>
                {
                    ["type"] = "field",
                    ["ip"] = geo.ProxyIpExceptions,
                    ["outboundTag"] = ProxyTag
                });
            }

            if (geo.DirectSites.Count > 0)
            {
                rules.Add(new Dictionary<string, object?>
                {
                    ["type"] = "field",
                    ["domain"] = geo.DirectSites,
                    ["outboundTag"] = DirectTag
                });
            }

            if (geo.DirectIps.Count > 0)
            {
                rules.Add(new Dictionary<string, object?>
                {
                    ["type"] = "field",
                    ["ip"] = geo.DirectIps,
                    ["outboundTag"] = DirectTag
                });
            }
        }

        /// <summary>
        /// The dialable IP literals in the resolver list, including the host of a DoH URL
        /// (<c>https://1.1.1.1/dns-query</c> still connects to 1.1.1.1:443).
        ///
        /// Nothing in the routing config uses these any more — resolver traffic is carried
        /// like all other traffic. They are still worth naming because a platform has to
        /// know which addresses must <i>not</i> get a host route around the tunnel: giving
        /// one to a resolver is the same DNS leak, just written in the route table instead
        /// of the config.
        /// </summary>
        public static string[] ResolverIps(IReadOnlyList<string> servers)
        {
            var addresses = new List<string>();

            foreach (var server in servers)
            {
                var candidate = server;

                if (server.Contains("://", StringComparison.Ordinal)
                    && Uri.TryCreate(server, UriKind.Absolute, out var uri))
                    candidate = uri.Host;

                if (System.Net.IPAddress.TryParse(candidate, out var ip))
                    addresses.Add(ip.ToString());
            }

            return [.. addresses.Distinct()];
        }

        // ── Inbound ──────────────────────────────────────────────────────────

        private static Dictionary<string, object?> BuildSocksInbound(XrayConfig cfg) => new()
        {
            ["tag"] = "socks-in",
            ["listen"] = cfg.SocksAddress,
            ["port"] = cfg.SocksPort,
            ["protocol"] = "socks",
            ["settings"] = new Dictionary<string, object?>
            {
                ["auth"] = "noauth",
                ["udp"] = true
            },
            ["sniffing"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["destOverride"] = new[] { "http", "tls", "quic" },
                ["routeOnly"] = false
            }
        };

        // ── Outbounds ────────────────────────────────────────────────────────

        /// <summary>
        /// The node's own outbound, with only the routing tag forced.
        ///
        /// <para><b>Nothing is generated here any more.</b> This used to hold one builder
        /// per protocol — VLESS, Hysteria, olcRTC — each reconstructing an outbound from a
        /// parsed share link, and each a place where the app's idea of the core's schema
        /// could drift from the core's. The node now ships the outbound it actually wants
        /// used, so the only thing left to do is make sure it carries the tag the routing
        /// rules point at.</para>
        ///
        /// <para>The tag is overwritten rather than trusted: the profiles do set
        /// <c>"tag": "proxy"</c>, but a node that forgets would produce a config where every
        /// routing rule points at an outbound that does not exist, and xray answers that
        /// with a startup failure rather than anything diagnosable.</para>
        /// </summary>
        private static JsonNode BuildProxyOutbound(XrayConfig cfg)
        {
            var outbound = cfg.Outbound.DeepClone();

            if (outbound is JsonObject obj) obj["tag"] = ProxyTag;

            return outbound;
        }
    }
}
