using Horus.Domain.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horus.Protocols
{
    /// <summary>
    /// Renders an xray-core <c>config.json</c> from a parsed share link.
    ///
    /// The shape is one SOCKS5 inbound (dialled by the platform TUN bridge) plus three
    /// outbounds — the selected proxy, <c>freedom</c> and <c>blackhole</c> — with routing
    /// that keeps private/loopback ranges off the tunnel.
    ///
    /// Routing deliberately avoids <c>geoip:</c> / <c>geosite:</c> predicates so xray never
    /// needs the geoip.dat / geosite.dat assets to be present next to the binary.
    /// </summary>
    public static class XrayConfigBuilder
    {
        public const string ProxyTag = "proxy";
        public const string DirectTag = "direct";
        public const string BlockTag = "block";

        /// <summary>Hop interval used when the share link doesn't specify one.</summary>
        private const int DefaultHopIntervalSeconds = 30;

        /// <summary>The core rejects a non-zero hop interval below this.</summary>
        private const int MinHopIntervalSeconds = 5;

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

        private static Dictionary<string, object?> BuildProxyOutbound(XrayConfig cfg) =>
            cfg.Link.Protocol switch
            {
                ProtocolType.Vless => BuildVless(cfg),
                ProtocolType.Hysteria2 => BuildHysteria(cfg),
                ProtocolType.OlcRtc => BuildOlcRtc(cfg),
                _ => throw new NotSupportedException(
                    $"No xray outbound generator for {cfg.Link.Protocol}.")
            };

        private static Dictionary<string, object?> BuildVless(XrayConfig cfg)
        {
            var link = cfg.Link;

            var user = new Dictionary<string, object?>
            {
                ["id"] = link.Credential,
                ["encryption"] = link.Encryption,
                ["level"] = 0
            };
            if (!string.IsNullOrEmpty(link.Flow)) user["flow"] = link.Flow;

            return new Dictionary<string, object?>
            {
                ["tag"] = ProxyTag,
                ["protocol"] = "vless",
                ["settings"] = new Dictionary<string, object?>
                {
                    ["vnext"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            // Dial the resolved IP; REALITY's serverName still carries the
                            // hostname, so the SNI presented on the wire is unchanged.
                            ["address"] = link.DialAddress,
                            ["port"] = link.Port,
                            ["users"] = new object[] { user }
                        }
                    }
                },
                ["streamSettings"] = BuildStreamSettings(cfg)
            };
        }

        /// <summary>
        /// Hysteria2 outbound, as the Horus xray fork defines it. This is <b>not</b> the
        /// sing-box / mihomo shape, and upstream xray-core has no hysteria at all — the
        /// names below come from the fork's own conf structs:
        ///
        /// <list type="bullet">
        /// <item>the proxy is registered as <c>hysteria</c>, not <c>hysteria2</c>
        /// (<c>proxy/hysteria</c>), with the version carried in <c>settings.version</c>;</item>
        /// <item>the stream transport is likewise <c>hysteria</c>
        /// (<c>infra/conf.TransportProtocol</c> maps only that spelling);</item>
        /// <item>the auth password lives on the <b>transport</b>
        /// (<c>HysteriaConfig.Auth</c> → <c>hysteriaSettings.auth</c>), not on the outbound;</item>
        /// <item>salamander obfuscation and UDP port hopping are <b>finalmask</b> features,
        /// not hysteria ones — <c>HysteriaConfig.Build</c> explicitly warns that
        /// "congestion &amp; up &amp; down &amp; udphop move to finalmask/quicParams".</item>
        /// </list>
        /// </summary>
        private static Dictionary<string, object?> BuildHysteria(XrayConfig cfg)
        {
            var link = cfg.Link;

            var tls = new Dictionary<string, object?>
            {
                // The node domain, deliberately NOT the link's sni. Hysteria2 here runs
                // behind a real Let's Encrypt certificate issued for the node hostname,
                // while the link's sni carries the REALITY camouflage domain — presenting
                // that name fails certificate validation before auth is ever attempted,
                // and the only symptom is a silent handshake timeout.
                // Trade-off: a deployment whose HY2 certificate covers some other name
                // would need this to honour link.Sni instead.
                ["serverName"] = link.Host,
                ["allowInsecure"] = cfg.AllowInsecure,
                // The node's listener negotiates h3; offering nothing fails the handshake.
                ["alpn"] = link.Alpn.Count > 0 ? link.Alpn : new[] { "h3" }
            };

            var stream = new Dictionary<string, object?>
            {
                ["network"] = "hysteria",
                ["security"] = "tls",
                ["tlsSettings"] = tls,
                ["hysteriaSettings"] = new Dictionary<string, object?>
                {
                    ["version"] = 2,           // HysteriaConfig.Build rejects anything else
                    ["auth"] = link.Credential
                }
            };

            var finalmask = new Dictionary<string, object?>();

            if (!string.IsNullOrEmpty(link.Obfs))
            {
                // Mask entries are {type, settings} — see conf's udpmaskLoader.
                finalmask["udp"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = link.Obfs,
                        ["settings"] = new Dictionary<string, object?>
                        {
                            ["password"] = link.ObfsPassword ?? string.Empty
                        }
                    }
                };
            }

            if (!string.IsNullOrEmpty(link.PortRange))
            {
                var udpHop = new Dictionary<string, object?> { ["ports"] = link.PortRange };

                // The core errors on a non-zero interval below 5 seconds; omitting it
                // lets the core pick its own default.
                var interval = link.HopInterval ?? DefaultHopIntervalSeconds;
                if (interval >= MinHopIntervalSeconds) udpHop["interval"] = interval;

                finalmask["quicParams"] = new Dictionary<string, object?> { ["udpHop"] = udpHop };
            }

            if (finalmask.Count > 0) stream["finalmask"] = finalmask;

            return new Dictionary<string, object?>
            {
                ["tag"] = ProxyTag,
                ["protocol"] = "hysteria",
                ["settings"] = new Dictionary<string, object?>
                {
                    ["version"] = 2,
                    ["address"] = link.DialAddress,
                    ["port"] = link.Port
                },
                ["streamSettings"] = stream
            };
        }

        /// <summary>
        /// olcRTC outbound. Keys follow the fork's own sample client config: the outbound
        /// is signalling-based, so it carries a room rather than an address, and it has no
        /// streamSettings at all.
        ///
        /// The four values come from the node's own registration
        /// (<c>olcrtc_provider</c>, <c>_transport</c>, <c>_room_id</c>, <c>_room_key</c>)
        /// and reach the app as a structured block rather than a URI —
        /// <see cref="ShareLinkParser.FromOlcRtc"/> projects it onto a link so everything
        /// downstream sees one shape.
        ///
        /// <para>The defaults are what the node registers when it has nothing better to
        /// say. They are kept because a node that announces a room but omits a transport
        /// should still be dialable, and getting them wrong fails loudly at handshake
        /// rather than silently carrying nothing.</para>
        /// </summary>
        private static Dictionary<string, object?> BuildOlcRtc(XrayConfig cfg)
        {
            var link = cfg.Link;

            var settings = new Dictionary<string, object?>
            {
                ["provider"] = link.Params.TryGetValue("provider", out var p) && !string.IsNullOrWhiteSpace(p)
                    ? p : "wbstream",
                ["transport"] = link.Params.TryGetValue("transport", out var t) && !string.IsNullOrWhiteSpace(t)
                    ? t : "vp8channel",
                ["roomId"] = link.Params.TryGetValue("roomid", out var r) && !string.IsNullOrWhiteSpace(r)
                    ? r : link.Host,
                ["key"] = link.Credential
            };

            // The account's stable identity on the node. Sent when the API supplied it so
            // the node's telemetry can attribute the session; older nodes ignore it.
            if (link.Params.TryGetValue("uuid", out var uuid) && !string.IsNullOrWhiteSpace(uuid))
                settings["uuid"] = uuid;

            if (link.Params.TryGetValue("dnsServer", out var dns))
                settings["dnsServer"] = dns;

            return new Dictionary<string, object?>
            {
                ["tag"] = ProxyTag,
                ["protocol"] = "olcrtc",
                ["settings"] = settings
            };
        }

        // ── Stream settings ──────────────────────────────────────────────────

        /// <summary>
        /// Stream settings for the link-driven transports (VLESS). Hysteria builds its own,
        /// because its transport, security and masking are all fixed by the protocol rather
        /// than taken from the link.
        /// </summary>
        private static Dictionary<string, object?> BuildStreamSettings(XrayConfig cfg)
        {
            var link = cfg.Link;
            var network = link.Network;
            var security = link.Security;

            var stream = new Dictionary<string, object?>
            {
                ["network"] = network,
                ["security"] = security
            };

            // "xhttp" is accepted as an alias for splithttp, but its settings object is
            // still required — a bare network name yields a transport with no path and the
            // node rejects the request.
            if (network.Equals("xhttp", StringComparison.OrdinalIgnoreCase)
                || network.Equals("splithttp", StringComparison.OrdinalIgnoreCase))
            {
                var xhttp = new Dictionary<string, object?>
                {
                    ["path"] = link.Params.TryGetValue("path", out var path) ? path : "/",
                    ["mode"] = link.Params.TryGetValue("mode", out var mode) ? mode : "auto"
                };
                if (link.Params.TryGetValue("host", out var host) && host.Length > 0)
                    xhttp["host"] = host;

                stream["xhttpSettings"] = xhttp;
            }

            if (link.IsReality)
            {
                stream["realitySettings"] = new Dictionary<string, object?>
                {
                    ["serverName"] = link.Sni ?? link.Host,
                    ["fingerprint"] = link.Fingerprint,
                    ["publicKey"] = link.PublicKey ?? string.Empty,
                    ["shortId"] = link.ShortId ?? string.Empty,
                    ["spiderX"] = link.SpiderX ?? "/"
                };
            }
            else if (string.Equals(security, "tls", StringComparison.OrdinalIgnoreCase))
            {
                stream["tlsSettings"] = new Dictionary<string, object?>
                {
                    ["serverName"] = link.Sni ?? link.Host,
                    ["fingerprint"] = link.Fingerprint,
                    ["allowInsecure"] = cfg.AllowInsecure
                };
            }

            return stream;
        }
    }
}
