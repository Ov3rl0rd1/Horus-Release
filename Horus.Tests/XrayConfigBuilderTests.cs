using System.Text.Json;
using System.Text.Json.Nodes;
using Horus.Domain.Models;
using Horus.Protocols;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// Golden assertions on the generated xray config.
///
/// <para><b>What these cover changed with the API.</b> The app used to build the proxy
/// outbound itself from a share link, and most of this file checked that reconstruction —
/// the fork's <c>hysteria</c> transport name, the finalmask layout, REALITY fields. The
/// node ships the outbound now, so none of that is the app's to get wrong; what is left,
/// and what these test, is the envelope the app still owns: the SOCKS inbound the bridge
/// dials, the resolvers, and the routing that keeps local traffic off the tunnel.</para>
/// </summary>
public class XrayConfigBuilderTests
{
    /// <summary>A minimal but realistic node outbound, in the shape the profiles emit.</summary>
    private const string NodeOutbound = """
        {
          "tag": "proxy",
          "protocol": "vless",
          "settings": {
            "vnext": [ { "address": "1.2.3.4", "port": 443,
                         "users": [ { "id": "uid-1", "encryption": "none", "flow": "xtls-rprx-vision" } ] } ]
          },
          "streamSettings": {
            "network": "tcp",
            "security": "reality",
            "realitySettings": { "serverName": "www.microsoft.com", "publicKey": "K", "shortId": "S" }
          }
        }
        """;

    private static XrayConfig Config(string outboundJson = NodeOutbound, Action<XrayConfig>? configure = null)
    {
        var cfg = new XrayConfig
        {
            Outbound = JsonNode.Parse(outboundJson)!,
            Offer = "vless-reality",
            Label = "VLESS REALITY",
            ProtocolName = "vless",
            NodeAddress = "1.2.3.4"
        };
        configure?.Invoke(cfg);
        return cfg;
    }

    private static JsonElement Build(Action<XrayConfig>? configure = null) =>
        JsonDocument.Parse(Config(configure: configure).ToConfig()).RootElement.Clone();

    private static JsonElement ProxyOutbound(JsonElement root)
    {
        foreach (var outbound in root.GetProperty("outbounds").EnumerateArray())
            if (outbound.GetProperty("tag").GetString() == XrayConfigBuilder.ProxyTag)
                return outbound;

        throw new Xunit.Sdk.XunitException("No outbound tagged 'proxy'.");
    }

    // ── The node's outbound is passed through ───────────────────────────────

    [Fact]
    public void The_nodes_outbound_is_used_verbatim()
    {
        // The whole point of the new contract: whatever the node sent is what runs. If the
        // app ever starts rewriting fields here, a node can no longer offer a protocol this
        // build has not heard of — which is the property the API was restructured to get.
        var proxy = ProxyOutbound(Build());

        Assert.Equal("vless", proxy.GetProperty("protocol").GetString());

        var vnext = proxy.GetProperty("settings").GetProperty("vnext")[0];
        Assert.Equal("1.2.3.4", vnext.GetProperty("address").GetString());
        Assert.Equal(443, vnext.GetProperty("port").GetInt32());

        var reality = proxy.GetProperty("streamSettings").GetProperty("realitySettings");
        Assert.Equal("www.microsoft.com", reality.GetProperty("serverName").GetString());
    }

    [Fact]
    public void An_unknown_protocol_still_produces_a_config()
    {
        // A node offering something released after this build must still work.
        var proxy = ProxyOutbound(Build(cfg => cfg.Outbound =
            JsonNode.Parse("""{"protocol":"something-new","settings":{"address":"1.2.3.4"}}""")!));

        Assert.Equal("something-new", proxy.GetProperty("protocol").GetString());
    }

    [Fact]
    public void The_proxy_tag_is_forced()
    {
        // Every routing rule points at "proxy". A node that omits or misspells the tag would
        // otherwise produce a config whose rules reference an outbound that does not exist,
        // and xray answers that with a startup failure rather than anything diagnosable.
        var proxy = ProxyOutbound(Build(cfg => cfg.Outbound =
            JsonNode.Parse("""{"tag":"whatever","protocol":"vless","settings":{}}""")!));

        Assert.Equal(XrayConfigBuilder.ProxyTag, proxy.GetProperty("tag").GetString());
    }

    private static JsonElement Rules(JsonElement root) =>
        root.GetProperty("routing").GetProperty("rules");

    [Fact]
    public void Geo_rules_are_absent_unless_enabled()
    {
        // Naming a category the core cannot resolve is not a soft failure: the config is
        // rejected and the tunnel never comes up. Off by default is the safe state.
        foreach (var rule in Rules(Build()).EnumerateArray())
            Assert.False(rule.TryGetProperty("domain", out _));
    }

    [Fact]
    public void A_direct_geo_category_routes_to_freedom()
    {
        var root = Build(cfg => cfg.Geo = new GeoRoutingOptions
        {
            Enabled = true,
            DirectSites = ["geosite:category-ru"],
            DirectIps = ["geoip:ru"]
        });

        var direct = Rules(root).EnumerateArray()
            .Where(r => r.GetProperty("outboundTag").GetString() == XrayConfigBuilder.DirectTag)
            .ToList();

        Assert.Contains(direct, r => r.TryGetProperty("domain", out var d)
            && d.EnumerateArray().Any(v => v.GetString() == "geosite:category-ru"));
        Assert.Contains(direct, r => r.TryGetProperty("ip", out var ip)
            && ip.EnumerateArray().Any(v => v.GetString() == "geoip:ru"));
    }

    [Fact]
    public void An_exception_is_matched_before_the_category_that_contains_it()
    {
        // The whole design. A geo set is tens of thousands of entries and cannot be edited,
        // so a user exception can only win by being earlier in the list. If this ordering
        // ever inverts, the exception silently stops working and the domain goes direct.
        var root = Build(cfg => cfg.Geo = new GeoRoutingOptions
        {
            Enabled = true,
            DirectSites = ["geosite:category-ru"],
            ProxyDomainExceptions = ["example.ru"]
        });

        var rules = Rules(root).EnumerateArray().ToList();

        var exception = rules.FindIndex(r => r.TryGetProperty("domain", out var d)
            && d.EnumerateArray().Any(v => v.GetString() == "example.ru"));
        var category = rules.FindIndex(r => r.TryGetProperty("domain", out var d)
            && d.EnumerateArray().Any(v => v.GetString() == "geosite:category-ru"));

        Assert.True(exception >= 0 && category >= 0, "both rules must be emitted");
        Assert.True(exception < category, "the exception must come first or it can never win");
    }

    [Fact]
    public void Geo_rules_still_come_after_the_local_ranges()
    {
        // Local traffic must stay local whatever the geo configuration says: a LAN address
        // that fell into a geo category would otherwise be routed by it.
        var root = Build(cfg => cfg.Geo = new GeoRoutingOptions
        {
            Enabled = true,
            DirectSites = ["geosite:category-ru"]
        });

        var rules = Rules(root).EnumerateArray().ToList();

        var local = rules.FindIndex(r => r.TryGetProperty("ip", out var ip)
            && ip.EnumerateArray().Any(v => v.GetString() == "192.168.0.0/16"));
        var geo = rules.FindIndex(r => r.TryGetProperty("domain", out var d)
            && d.EnumerateArray().Any(v => v.GetString() == "geosite:category-ru"));

        Assert.True(local >= 0 && geo > local);
    }

    [Fact]
    public void The_catch_all_stays_last()
    {
        var root = Build(cfg => cfg.Geo = new GeoRoutingOptions
        {
            Enabled = true,
            DirectSites = ["geosite:category-ru"]
        });

        var rules = Rules(root).EnumerateArray().ToList();
        var last = rules[^1];

        Assert.Equal(XrayConfigBuilder.ProxyTag, last.GetProperty("outboundTag").GetString());
        Assert.Equal("tcp,udp", last.GetProperty("network").GetString());
    }

    public void Socks_inbound_matches_the_configured_port()
    {
        var root = Build();
        var inbound = root.GetProperty("inbounds")[0];

        Assert.Equal("socks", inbound.GetProperty("protocol").GetString());
        Assert.Equal(XrayConfig.DefaultSocksPort, inbound.GetProperty("port").GetInt32());
        Assert.Equal("127.0.0.1", inbound.GetProperty("listen").GetString());
        Assert.True(inbound.GetProperty("settings").GetProperty("udp").GetBoolean());
    }

    [Fact]
    public void Private_ranges_route_direct_and_everything_else_proxies()
    {
        var rules = Build().GetProperty("routing").GetProperty("rules");

        // Multicast and broadcast are dropped first. Sending them anywhere else costs a
        // SOCKS5 session per packet, and on Windows they come straight back through the
        // tunnel and amplify — see XrayConfigBuilder.DropRanges.
        var drop = rules[0];
        Assert.Equal(XrayConfigBuilder.BlockTag, drop.GetProperty("outboundTag").GetString());

        var dropped = drop.GetProperty("ip").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("224.0.0.0/4", dropped);
        Assert.Contains("255.255.255.255/32", dropped);
        Assert.Contains("ff00::/8", dropped);

        var direct = rules[1];
        Assert.Equal(XrayConfigBuilder.DirectTag, direct.GetProperty("outboundTag").GetString());

        var ranges = direct.GetProperty("ip").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("127.0.0.0/8", ranges);
        Assert.Contains("10.0.0.0/8", ranges);
        Assert.Contains("192.168.0.0/16", ranges);
        Assert.Contains("::1/128", ranges);
        Assert.Contains("fc00::/7", ranges);

        // Unicast LAN ranges stay direct; nothing droppable leaks into that rule.
        Assert.DoesNotContain("224.0.0.0/4", ranges);
        Assert.DoesNotContain("255.255.255.255/32", ranges);

        // Catch-all must be last, or it would shadow the direct rule.
        var last = rules[rules.GetArrayLength() - 1];
        Assert.Equal(XrayConfigBuilder.ProxyTag, last.GetProperty("outboundTag").GetString());
    }

    /// <summary>
    /// No rule may send resolver traffic anywhere but the proxy. An exemption here does not
    /// look like a leak in the config — it looks like a sensible bootstrap shortcut — but it
    /// puts every name the device looks up on the wire in clear while the UI says the
    /// connection is protected. The node is pre-resolved before the core starts, so nothing
    /// needs the shortcut.
    /// </summary>
    [Fact]
    public void No_rule_exempts_the_resolvers_from_the_tunnel()
    {
        var resolvers = XrayConfigBuilder.ResolverIps(Config().DnsServers);

        Assert.NotEmpty(resolvers);   // otherwise this test proves nothing

        var rules = Build().GetProperty("routing").GetProperty("rules");

        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.GetProperty("outboundTag").GetString() == XrayConfigBuilder.ProxyTag)
                continue;
            if (!rule.TryGetProperty("ip", out var ips))
                continue;

            var listed = ips.EnumerateArray().Select(e => e.GetString()!).ToList();

            foreach (var resolver in resolvers)
                Assert.DoesNotContain(listed, entry => entry.StartsWith(resolver + "/")
                                                       || entry == resolver);
        }
    }

    [Fact]
    public void Config_carries_explicit_dns_servers()
    {
        // Android has no /etc/resolv.conf, so the core's Go resolver has no nameservers and
        // fails every lookup without sending a packet — the outbound then never dials at
        // all. Explicit servers are what make name resolution work inside the core.
        var dns = Build().GetProperty("dns");

        var servers = dns.GetProperty("servers").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.NotEmpty(servers);
        Assert.Contains(servers, s => s!.StartsWith("https://"));   // DoH resists UDP poisoning
    }

    [Fact]
    public void Routing_resolves_nothing_on_the_client()
    {
        // AsIs keeps proxied domains unresolved locally — the node resolves them. IPIfNonMatch
        // would force a client-side lookup for every destination, which on Android stalls
        // every connection on the dead Go resolver.
        Assert.Equal("AsIs",
            Build().GetProperty("routing").GetProperty("domainStrategy").GetString());
    }

    [Fact]
    public void Routing_never_uses_geo_predicates()
    {
        // geoip:/geosite: would require geoip.dat + geosite.dat next to the core and a
        // XraySetAssetPath call; the builder deliberately avoids that dependency.
        var json = Config().ToConfig();

        Assert.DoesNotContain("geoip:", json);
        Assert.DoesNotContain("geosite:", json);
    }

    [Fact]
    public void Log_file_is_emitted_only_when_a_path_is_set()
    {
        var without = Build().GetProperty("log");
        Assert.False(without.TryGetProperty("error", out _));

        var with = Build(c => c.LogFilePath = "/tmp/xray.log").GetProperty("log");
        Assert.Equal("/tmp/xray.log", with.GetProperty("error").GetString());
        // Access logging records every destination the user visits — keep it off.
        Assert.Equal("none", with.GetProperty("access").GetString());
    }

    [Fact]
    public void Always_emits_direct_and_block_outbounds()
    {
        var tags = Build().GetProperty("outbounds").EnumerateArray()
            .Select(o => o.GetProperty("tag").GetString()).ToList();

        Assert.Contains(XrayConfigBuilder.ProxyTag, tags);
        Assert.Contains(XrayConfigBuilder.DirectTag, tags);
        Assert.Contains(XrayConfigBuilder.BlockTag, tags);
    }
}
