using System.Text.Json;
using System.Text.Json.Nodes;
using Horus.Domain.Models;

namespace Horus.Platforms.Windows.Tunnel
{
    /// <summary>
    /// What the Windows tunnel needs from the core beyond the shared config.
    /// </summary>
    /// <param name="Name">Adapter name. What the route table and the counters look it up by.</param>
    /// <param name="Mtu">1500 by default: larger frames buy bulk throughput and cost latency
    /// for everything sharing the link, which is the wrong trade for a game.</param>
    /// <param name="CaptureIpv6">Route IPv6 into the tunnel too. Without it every IPv6
    /// destination leaves the machine directly, beside the VPN.</param>
    public sealed record WindowsTunSettings(
        string Name = WindowsTunnelConfig.AdapterName,
        int Mtu = WindowsTunnelConfig.DefaultMtu,
        bool CaptureIpv6 = true);

    /// <summary>Per-application routing, as the user chose it.</summary>
    /// <param name="Mode">Blacklist: these apps bypass the VPN. Whitelist: only these use it.</param>
    /// <param name="Processes">Executable file names (<c>game.exe</c>) or full paths.</param>
    public sealed record WindowsSplitRules(SplitTunnelingMode Mode, IReadOnlyList<string> Processes)
    {
        public static readonly WindowsSplitRules None = new(SplitTunnelingMode.Disabled, []);

        public bool IsActive => Mode != SplitTunnelingMode.Disabled && Processes.Count > 0;
    }

    /// <summary>
    /// Turns the shared xray config into the one the Windows client runs: the core owns
    /// the TUN, in-process.
    ///
    /// <para><b>Why the core and not a bridge.</b> The previous Windows tunnel ran
    /// hev-socks5-tunnel as a child process that created its own adapter and fed the
    /// core's SOCKS5 inbound. Every packet crossed a process boundary and a loopback
    /// SOCKS hop; the application's identity was lost at that hop, so per-process
    /// routing could only be attempted with WinDivert and host routes — which never
    /// worked. With the TUN inside the core, each connection still carries the source
    /// address of the application's own socket, so routing rules can match the process,
    /// and the core pins its own sockets to the physical interface
    /// (<c>autoOutboundsInterface</c>), so no host route is needed to keep it out of the
    /// tunnel.</para>
    ///
    /// <para><b>A transformation, not a second builder.</b> The shared
    /// <c>XrayConfigBuilder</c> stays the single source of the outbound envelope, the
    /// site rules and the geo rules, for both platforms. This only adds what the TUN
    /// needs and reorders nothing it does not have to, which keeps Android's config —
    /// and its tests — untouched.</para>
    ///
    /// <para>Pure and MAUI-free on purpose: <c>Horus.Tests</c> links it.</para>
    /// </summary>
    public static class WindowsTunnelConfig
    {
        public const string AdapterName = "Horus";
        public const string TunTag = "tun-in";
        public const string DnsOutTag = "dns-out";
        public const string DnsModuleTag = "dns-internal";
        public const string ProxyTag = "proxy";
        public const string DirectTag = "direct";
        public const string SocksTag = "socks-in";

        /// <summary>The interface's own address. Inside 198.18.0.0/15, the range reserved for benchmarking, so it collides with nothing real.</summary>
        public const string Address = "198.18.0.1";

        /// <summary>The resolver Windows is pointed at. On the TUN's /30, so it is reached through the tunnel with or without the default routes.</summary>
        public const string DnsAddress = "198.18.0.2";

        public const string Address6 = "fdfe:dcba:9876::1";
        public const int DefaultMtu = 1500;
        public const int SpeedMtu = 9000;

        /// <summary>
        /// Two halves rather than one default route: more specific than the physical
        /// default, so they win without replacing it, and a crash — which takes the
        /// adapter and its routes with it — leaves the machine's own route untouched.
        /// </summary>
        public static readonly string[] Routes4 = ["0.0.0.0/1", "128.0.0.0/1"];
        public static readonly string[] Routes6 = ["::/1", "8000::/1"];

        /// <summary>
        /// Shapes <paramref name="baseConfigJson"/> (the output of <c>XrayConfigBuilder.Build</c>)
        /// for the Windows tunnel: the TUN inbound with its routes, the DNS outbound, and the
        /// split-tunnel rules. Candidates are proved working on the unshaped config first,
        /// which has no TUN, so a fallback between them never creates and destroys adapters.
        /// </summary>
        public static string Build(string baseConfigJson, WindowsTunSettings tun, WindowsSplitRules split)
        {
            var root = JsonNode.Parse(baseConfigJson)?.AsObject()
                ?? throw new ArgumentException("Empty base config.", nameof(baseConfigJson));

            // The DNS module's own queries are tagged so routing can always send them through
            // the proxy, whatever the split mode says about everything else.
            if (root["dns"] is JsonObject dns) dns["tag"] = DnsModuleTag;

            var inbounds = root["inbounds"] as JsonArray ?? [];
            var sniffing = inbounds.OfType<JsonObject>().FirstOrDefault()?["sniffing"]?.DeepClone();
            inbounds.Insert(0, TunInbound(tun, sniffing));
            root["inbounds"] = inbounds;

            var outbounds = root["outbounds"] as JsonArray ?? [];
            foreach (var ob in outbounds.OfType<JsonObject>())
            {
                // The interface pin was the Windows workaround for an unbound direct socket.
                // The core binds every socket itself now, and a name resolved before connect
                // goes stale the moment Wi-Fi changes; the binding follows the route table.
                if (ob["tag"]?.GetValue<string>() == DirectTag && ob["streamSettings"] is JsonObject ss)
                {
                    if (ss["sockopt"] is JsonObject so) so.Remove("interface");
                    if (ss["sockopt"] is JsonObject { Count: 0 }) ss.Remove("sockopt");
                    if (ss.Count == 0) ob.Remove("streamSettings");
                }
            }
            outbounds.Add(DnsOutbound());
            root["outbounds"] = outbounds;

            var routing = root["routing"] as JsonObject ?? [];
            routing["rules"] = Rules(routing["rules"] as JsonArray ?? [], split);
            root["routing"] = routing;

            return root.ToJsonString(Indented);
        }

        /// <summary>
        /// The routing object of a full config, for <c>XrayReloadRouting</c>: a split-tunnel
        /// change rebuilds the config and hands the core just this part, without a restart.
        /// </summary>
        public static string RoutingOf(string configJson) =>
            (JsonNode.Parse(configJson)?["routing"] ?? new JsonObject()).ToJsonString();

        /// <summary>The outbound with <paramref name="tag"/>, for <c>XrayReplaceOutbound</c>.</summary>
        public static string? OutboundOf(string configJson, string tag = ProxyTag) =>
            (JsonNode.Parse(configJson)?["outbounds"] as JsonArray)?
                .OfType<JsonObject>()
                .FirstOrDefault(o => o["tag"]?.GetValue<string>() == tag)?
                .ToJsonString();

        /// <summary>
        /// The strings xray's <c>process</c> condition matches, from what the user picked.
        ///
        /// <para>xray compares exactly: a bare name against the executable's file name with
        /// <c>.exe</c> trimmed, a path against the full image path with forward slashes. It
        /// trims <c>.exe</c> case-sensitively, so <c>GAME.EXE</c> would otherwise never
        /// match; the lower-cased name is emitted beside the original for the same reason.</para>
        /// </summary>
        public static IReadOnlyList<string> ProcessMatchers(IEnumerable<string> entries)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(string s)
            {
                if (s.Length > 0 && seen.Add(s)) result.Add(s);
            }

            foreach (var raw in entries)
            {
                var entry = raw?.Trim().Trim('"') ?? string.Empty;
                if (entry.Length == 0) continue;

                if (entry.Contains('\\') || entry.Contains('/'))
                {
                    // A full path: xray wants forward slashes. Kept as well as the bare name,
                    // so an app updated into a new versioned folder (Discord, many launchers)
                    // still matches by name. Split by hand rather than with Path, which only
                    // knows the separators of the OS it runs on.
                    var slashed = entry.Replace('\\', '/');
                    Add(slashed);
                    entry = slashed[(slashed.LastIndexOf('/') + 1)..];
                    if (entry.Length == 0) continue;
                }

                // As it is on disk: xray trims only a lower-case ".exe", so "GAME.EXE" can
                // only ever match itself.
                Add(entry);
                var name = entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? entry[..^4] : entry;
                Add(name);
                Add(name.ToLowerInvariant());
            }

            return result;
        }

        // ── Pieces ───────────────────────────────────────────────────────────

        private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

        private static JsonObject TunInbound(WindowsTunSettings tun, JsonNode? sniffing)
        {
            var gateway = new JsonArray(Address + "/30");
            if (tun.CaptureIpv6) gateway.Add(Address6 + "/126");

            var settings = new JsonObject
            {
                ["name"] = tun.Name,
                ["mtu"] = tun.Mtu,
                ["gateway"] = gateway,
                ["dns"] = new JsonArray(DnsAddress),
                // Explicit even though routes imply it: the binding is what keeps the core's
                // own connection to the node out of the tunnel, and it must not hinge on an
                // implicit default.
                ["autoOutboundsInterface"] = "auto"
            };

            // Installed by the core on the adapter, so they exist exactly as long as it does:
            // a crash cannot strand them.
            var routes = new JsonArray();
            foreach (var r in Routes4) routes.Add(r);
            if (tun.CaptureIpv6) foreach (var r in Routes6) routes.Add(r);
            settings["autoSystemRoutingTable"] = routes;

            var inbound = new JsonObject
            {
                ["tag"] = TunTag,
                ["protocol"] = "tun",
                ["port"] = 0,
                ["settings"] = settings
            };
            if (sniffing is not null) inbound["sniffing"] = sniffing;
            return inbound;
        }

        /// <summary>
        /// Answers Windows' DNS from the core's own resolver, through the tunnel.
        ///
        /// <para>A and AAAA are answered by the core (cached, and resolved through the
        /// proxy). Everything else — SRV, TXT, MX, which launchers and game services do use
        /// — is passed on to a public resolver, also through the proxy. Left to its default
        /// the dns outbound would answer those with an empty success, and a game looking up
        /// its server's SRV record would be told there is none.</para>
        /// </summary>
        private static JsonObject DnsOutbound() => new()
        {
            ["tag"] = DnsOutTag,
            ["protocol"] = "dns",
            ["settings"] = new JsonObject
            {
                ["address"] = "1.1.1.1",
                ["port"] = 53,
                ["rules"] = new JsonArray(
                    new JsonObject { ["action"] = "hijack", ["qType"] = "1,28" },
                    new JsonObject { ["action"] = "direct" })
            },
            ["streamSettings"] = new JsonObject
            {
                ["sockopt"] = new JsonObject { ["dialerProxy"] = ProxyTag }
            }
        };

        /// <summary>
        /// The shared rules with the Windows ones placed around them.
        ///
        /// <list type="number">
        /// <item>The core's own DNS goes through the proxy — first, so no split rule can send
        /// lookups out in clear.</item>
        /// <item>Port 53 from the TUN goes to the DNS outbound; anything else to the TUN's own
        /// subnet is dropped.</item>
        /// <item>The SOCKS inbound — the probe channel — always goes to the proxy.</item>
        /// <item>The shared rules: multicast dropped, private ranges direct, the user's site
        /// rules, the geo rules.</item>
        /// <item>Split tunneling. Blacklist goes before the site and geo rules — "this game
        /// goes direct" is about the application, whatever it connects to. Whitelist goes
        /// after them, and the final catch-all becomes direct: only what the chosen apps
        /// send, and what a site rule explicitly sends to the proxy, uses the VPN.</item>
        /// </list>
        /// </summary>
        private static JsonArray Rules(JsonArray shared, WindowsSplitRules split)
        {
            var rules = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "field",
                    ["inboundTag"] = new JsonArray(DnsModuleTag),
                    ["outboundTag"] = ProxyTag
                },
                new JsonObject
                {
                    ["type"] = "field",
                    ["inboundTag"] = new JsonArray(TunTag),
                    ["port"] = "53",
                    ["network"] = "tcp,udp",
                    ["outboundTag"] = DnsOutTag
                },
                // The TUN's own subnet has nothing behind it but the resolver above. Windows
                // still talks to it — NetBIOS broadcasts to the /30's broadcast address, seen
                // on a real run — and the shared rules would send that out "direct".
                new JsonObject
                {
                    ["type"] = "field",
                    ["ip"] = new JsonArray(Address[..^1] + "0/30", Address6[..^1] + "0/126"),
                    ["outboundTag"] = "block"
                },
                // The SOCKS inbound is the probe channel: health checks dial through it to
                // learn whether the proxy works. Under a whitelist the catch-all is direct,
                // and a probe that went direct would report a dead proxy as healthy.
                new JsonObject
                {
                    ["type"] = "field",
                    ["inboundTag"] = new JsonArray(SocksTag),
                    ["outboundTag"] = ProxyTag
                }
            };

            var items = shared.Select(n => n?.DeepClone()).OfType<JsonNode>().ToList();

            // The shared list ends with the catch-all; it is the one rule this changes.
            JsonNode? catchAll = null;
            if (items.Count > 0 && IsCatchAll(items[^1]))
            {
                catchAll = items[^1];
                items.RemoveAt(items.Count - 1);
            }

            // Leading rules that must stay ahead of anything per-app: multicast and private ranges.
            var lead = 0;
            while (lead < items.Count && IsRangeRule(items[lead])) lead++;

            var matchers = split.IsActive ? ProcessMatchers(split.Processes) : [];
            var useSplit = matchers.Count > 0;

            for (var i = 0; i < lead; i++) rules.Add(items[i]);

            if (useSplit && split.Mode == SplitTunnelingMode.Blacklist)
                rules.Add(ProcessRule(matchers, DirectTag));

            for (var i = lead; i < items.Count; i++) rules.Add(items[i]);

            if (useSplit && split.Mode == SplitTunnelingMode.Whitelist)
            {
                rules.Add(ProcessRule(matchers, ProxyTag));
                rules.Add(new JsonObject
                {
                    ["type"] = "field",
                    ["network"] = "tcp,udp",
                    ["outboundTag"] = DirectTag
                });
            }
            else
            {
                rules.Add(catchAll ?? new JsonObject
                {
                    ["type"] = "field",
                    ["network"] = "tcp,udp",
                    ["outboundTag"] = ProxyTag
                });
            }

            return rules;
        }

        private static JsonObject ProcessRule(IReadOnlyList<string> matchers, string tag)
        {
            var list = new JsonArray();
            foreach (var m in matchers) list.Add(m);
            return new JsonObject
            {
                ["type"] = "field",
                ["ruleTag"] = "split-" + tag,
                ["process"] = list,
                ["outboundTag"] = tag
            };
        }

        private static bool IsCatchAll(JsonNode rule) =>
            rule is JsonObject o
            && o.Count <= 3
            && o["network"] is not null
            && o["ip"] is null && o["domain"] is null && o["process"] is null
            && o["inboundTag"] is null && o["port"] is null;

        private static bool IsRangeRule(JsonNode rule) =>
            rule is JsonObject o && o["ip"] is not null && o["domain"] is null
            && o["outboundTag"]?.GetValue<string>() is "block" or DirectTag
            && o["ip"] is JsonArray ips && ips.Count > 0
            && ips.All(ip => ip?.GetValue<string>() is { } s && !s.StartsWith("geoip:", StringComparison.Ordinal));
    }
}
