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

        /// <summary>Ranges that must never be routed into the tunnel.</summary>
        private static readonly string[] DirectRanges =
        [
            "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",
            "169.254.0.0/16", "224.0.0.0/4", "255.255.255.255/32",
            "::1/128", "fc00::/7", "fe80::/10"
        ];

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
                    ["domainStrategy"] = "IPIfNonMatch",
                    ["rules"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "field",
                            ["ip"] = DirectRanges,
                            ["outboundTag"] = DirectTag
                        },
                        new Dictionary<string, object?>
                        {
                            ["type"] = "field",
                            ["network"] = "tcp,udp",
                            ["outboundTag"] = ProxyTag
                        }
                    }
                }
            };

            return JsonSerializer.Serialize(root, Options);
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
                ProtocolType.Hysteria2 => BuildHysteria2(cfg),
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
                            ["address"] = link.Host,
                            ["port"] = link.Port,
                            ["users"] = new object[] { user }
                        }
                    }
                },
                ["streamSettings"] = BuildStreamSettings(cfg)
            };
        }

        /// <summary>
        /// Hysteria2 outbound. Upstream xray-core has no hysteria2 transport — this targets
        /// the custom Horus build. If that build's schema differs, this method is the only
        /// place that needs changing.
        /// </summary>
        private static Dictionary<string, object?> BuildHysteria2(XrayConfig cfg)
        {
            var link = cfg.Link;

            var hysteria2Settings = new Dictionary<string, object?>
            {
                ["password"] = link.Credential
            };

            if (!string.IsNullOrEmpty(link.Obfs))
            {
                hysteria2Settings["obfs"] = new Dictionary<string, object?>
                {
                    ["type"] = link.Obfs,
                    ["password"] = link.ObfsPassword ?? string.Empty
                };
            }

            // UDP port hopping, from the "host:port,20000-30000" form of the link.
            if (!string.IsNullOrEmpty(link.PortRange))
            {
                hysteria2Settings["hopPorts"] = link.PortRange;
                hysteria2Settings["hopInterval"] = 30;
            }

            var stream = BuildStreamSettings(cfg, defaultNetwork: "hysteria2", defaultSecurity: "tls");
            stream["hysteria2Settings"] = hysteria2Settings;

            return new Dictionary<string, object?>
            {
                ["tag"] = ProxyTag,
                ["protocol"] = "hysteria2",
                ["settings"] = new Dictionary<string, object?>
                {
                    ["servers"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["address"] = link.Host,
                            ["port"] = link.Port,
                            ["password"] = link.Credential
                        }
                    }
                },
                ["streamSettings"] = stream
            };
        }

        /// <summary>
        /// olcRTC outbound. The API does not hand out <c>olcrtc://</c> links yet, so this
        /// path is unreachable in practice; every query parameter is forwarded verbatim so
        /// it starts working as soon as the backend begins returning one.
        /// </summary>
        private static Dictionary<string, object?> BuildOlcRtc(XrayConfig cfg)
        {
            var link = cfg.Link;

            var settings = new Dictionary<string, object?>
            {
                ["address"] = link.Host,
                ["port"] = link.Port,
                ["key"] = link.Credential
            };
            foreach (var (key, value) in link.Params)
                settings[key] = value;

            return new Dictionary<string, object?>
            {
                ["tag"] = ProxyTag,
                ["protocol"] = "olcrtc",
                ["settings"] = settings,
                ["streamSettings"] = BuildStreamSettings(cfg, defaultNetwork: "olcrtc", defaultSecurity: "none")
            };
        }

        // ── Stream settings ──────────────────────────────────────────────────

        private static Dictionary<string, object?> BuildStreamSettings(
            XrayConfig cfg,
            string? defaultNetwork = null,
            string? defaultSecurity = null)
        {
            var link = cfg.Link;
            var network = defaultNetwork ?? link.Network;
            var security = defaultSecurity is not null && link.Security == "none"
                ? defaultSecurity
                : link.Security;

            var stream = new Dictionary<string, object?>
            {
                ["network"] = network,
                ["security"] = security
            };

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
