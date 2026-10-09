using System.Text.Json.Nodes;
using Horus.Application.Routing;
using Horus.Domain.Models;
using Horus.Platforms.Windows.Tunnel;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// The config the Windows client gives its core: the shared envelope plus the TUN the
/// core owns, the DNS it answers, and per-application routing.
///
/// <para>Every one of these properties fails silently when wrong. A TUN without the
/// outbound binding loops the core's own connection into itself; DNS that is not
/// hijacked leaks every lookup; a process rule in the wrong place either does nothing or
/// overrides the user's site rules. Nothing on screen says which.</para>
/// </summary>
public class WindowsTunnelConfigTests
{
    private const string NodeOutbound = """
        { "tag": "proxy", "protocol": "vless",
          "settings": { "vnext": [ { "address": "1.2.3.4", "port": 443,
                        "users": [ { "id": "u", "encryption": "none", "flow": "xtls-rprx-vision" } ] } ] },
          "streamSettings": { "network": "tcp", "security": "reality",
                              "realitySettings": { "serverName": "s", "publicKey": "K", "shortId": "S" } } }
        """;

    private static string Base(Action<XrayConfig>? configure = null)
    {
        var cfg = new XrayConfig
        {
            Outbound = JsonNode.Parse(NodeOutbound)!,
            Offer = "vless-reality",
            ProtocolName = "vless",
            NodeAddress = "1.2.3.4",
            DirectInterface = "Wi-Fi"
        };
        configure?.Invoke(cfg);
        return cfg.ToConfig();
    }

    private static JsonObject Build(WindowsSplitRules? split = null, WindowsTunSettings? tun = null, Action<XrayConfig>? configure = null) =>
        JsonNode.Parse(WindowsTunnelConfig.Build(Base(configure), tun ?? new WindowsTunSettings(), split ?? WindowsSplitRules.None))!.AsObject();

    private static JsonArray Rules(JsonObject root) => root["routing"]!["rules"]!.AsArray();

    private static string? Tag(JsonNode? rule) => rule?["outboundTag"]?.GetValue<string>();

    /// <summary>Rules the Windows config puts ahead of the shared ones: DNS module, port 53, SOCKS probe.</summary>
    private const int Lead = 3;

    [Fact]
    public void The_tun_is_the_first_inbound_and_binds_the_cores_own_sockets()
    {
        var tun = Build()["inbounds"]![0]!;

        Assert.Equal("tun", tun["protocol"]!.GetValue<string>());
        Assert.Equal(WindowsTunnelConfig.TunTag, tun["tag"]!.GetValue<string>());

        var s = tun["settings"]!;
        Assert.Equal("Horus", s["name"]!.GetValue<string>());
        Assert.Equal(1500, s["mtu"]!.GetValue<int>());
        // Without the binding the connection to the node is routed into the tunnel it carries.
        Assert.Equal("auto", s["autoOutboundsInterface"]!.GetValue<string>());
        Assert.Equal(["198.18.0.2"], s["dns"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void Ipv6_is_captured_unless_turned_off()
    {
        var routes = Build()["inbounds"]![0]!["settings"]!["autoSystemRoutingTable"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1"], routes);

        var v4 = Build(tun: new WindowsTunSettings(CaptureIpv6: false))["inbounds"]![0]!["settings"]!;
        Assert.Equal(["0.0.0.0/1", "128.0.0.0/1"], v4["autoSystemRoutingTable"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Single(v4["gateway"]!.AsArray());
    }

    [Fact]
    public void The_socks_inbound_stays_for_the_probes()
    {
        var inbounds = Build()["inbounds"]!.AsArray();
        Assert.Equal(2, inbounds.Count);
        Assert.Equal("socks", inbounds[1]!["protocol"]!.GetValue<string>());
        // The TUN sniffs exactly as the SOCKS inbound did.
        Assert.Equal(inbounds[1]!["sniffing"]!.ToJsonString(), inbounds[0]!["sniffing"]!.ToJsonString());
    }

    [Fact]
    public void Dns_is_answered_by_the_core_and_never_leaves_in_clear()
    {
        var root = Build();
        var rules = Rules(root);

        // First: the core's own lookups go through the proxy whatever else is decided.
        Assert.Equal(WindowsTunnelConfig.DnsModuleTag, rules[0]!["inboundTag"]![0]!.GetValue<string>());
        Assert.Equal("proxy", Tag(rules[0]));
        Assert.Equal(WindowsTunnelConfig.DnsModuleTag, root["dns"]!["tag"]!.GetValue<string>());

        // Second: port 53 from the TUN is answered by the DNS outbound.
        Assert.Equal("53", rules[1]!["port"]!.GetValue<string>());
        Assert.Equal(WindowsTunnelConfig.DnsOutTag, Tag(rules[1]));

        var dnsOut = root["outbounds"]!.AsArray().Single(o => o!["tag"]!.GetValue<string>() == WindowsTunnelConfig.DnsOutTag)!;
        Assert.Equal("proxy", dnsOut["streamSettings"]!["sockopt"]!["dialerProxy"]!.GetValue<string>());

        // SRV/TXT/MX must be forwarded, not answered empty.
        var dnsRules = dnsOut["settings"]!["rules"]!.AsArray();
        Assert.Equal("hijack", dnsRules[0]!["action"]!.GetValue<string>());
        Assert.Equal("1,28", dnsRules[0]!["qType"]!.GetValue<string>());
        Assert.Equal("direct", dnsRules[^1]!["action"]!.GetValue<string>());
    }

    [Fact]
    public void The_direct_outbound_loses_its_stale_interface_pin()
    {
        var direct = Build()["outbounds"]!.AsArray().Single(o => o!["tag"]!.GetValue<string>() == "direct")!;
        Assert.Null(direct["streamSettings"]?["sockopt"]?["interface"]);
        Assert.Equal("freedom", direct["protocol"]!.GetValue<string>());
    }

    [Fact]
    public void Without_split_tunneling_the_shared_rules_are_kept_in_order()
    {
        var shared = JsonNode.Parse(Base())!["routing"]!["rules"]!.AsArray();
        var rules = Rules(Build());

        Assert.Equal(shared.Count + Lead, rules.Count);
        for (var i = 0; i < shared.Count; i++)
            Assert.Equal(shared[i]!.ToJsonString(), rules[i + Lead]!.ToJsonString());
        Assert.DoesNotContain(rules, r => r?["process"] is not null);
        Assert.Equal("proxy", Tag(rules[^1]));
    }

    [Fact]
    public void Blacklisted_apps_go_direct_before_any_site_rule()
    {
        var rules = Rules(Build(
            new WindowsSplitRules(SplitTunnelingMode.Blacklist, ["Game.exe"]),
            configure: c => c.SiteRules = [new SiteRule("example.com", RuleAction.Proxy)]));

        var process = rules.Select((r, i) => (r, i)).Single(x => x.r?["process"] is not null);
        var site = rules.Select((r, i) => (r, i)).Single(x => x.r?["domain"] is not null);

        Assert.Equal("direct", Tag(process.r));
        Assert.True(process.i < site.i, "the app rule must win over a site rule");

        // ...but never ahead of the multicast drop and the private ranges.
        Assert.Equal("block", Tag(rules[Lead]));
        Assert.Equal("direct", Tag(rules[Lead + 1]));
        Assert.True(process.i > Lead + 1);
        Assert.Equal("proxy", Tag(rules[^1]));
    }

    [Fact]
    public void Whitelist_sends_only_the_chosen_apps_through_the_proxy()
    {
        var rules = Rules(Build(
            new WindowsSplitRules(SplitTunnelingMode.Whitelist, ["game.exe"]),
            configure: c => c.SiteRules = [new SiteRule("example.com", RuleAction.Proxy)]));

        var process = rules.Select((r, i) => (r, i)).Single(x => x.r?["process"] is not null);
        var site = rules.Select((r, i) => (r, i)).Single(x => x.r?["domain"] is not null);

        Assert.Equal("proxy", Tag(process.r));
        Assert.True(site.i < process.i, "an explicit site rule still applies to every app");
        Assert.Equal("direct", Tag(rules[^1]));
        Assert.Single(rules, r => r?["network"] is not null && r?["ip"] is null && r?["port"] is null && r?["process"] is null && r?["domain"] is null);
    }

    [Theory]
    [InlineData(SplitTunnelingMode.Disabled)]
    [InlineData(SplitTunnelingMode.Whitelist)]
    [InlineData(SplitTunnelingMode.Blacklist)]
    public void Health_probes_always_reach_the_proxy(SplitTunnelingMode mode)
    {
        // A probe that went direct under a whitelist would call a dead proxy healthy.
        var rules = Rules(Build(new WindowsSplitRules(mode, ["game.exe"])));
        var socks = rules.Select((r, i) => (r, i)).First(x => x.r?["inboundTag"]?[0]?.GetValue<string>() == WindowsTunnelConfig.SocksTag);
        Assert.Equal("proxy", Tag(socks.r));
        Assert.True(socks.i < Lead);
    }

    [Fact]
    public void A_mode_with_no_apps_changes_nothing()
    {
        var plain = Rules(Build()).ToJsonString();
        Assert.Equal(plain, Rules(Build(new WindowsSplitRules(SplitTunnelingMode.Whitelist, []))).ToJsonString());
        Assert.Equal(plain, Rules(Build(new WindowsSplitRules(SplitTunnelingMode.Disabled, ["a.exe"]))).ToJsonString());
    }

    [Theory]
    [InlineData("chrome.exe", new[] { "chrome.exe", "chrome" })]
    [InlineData("GAME.EXE", new[] { "GAME.EXE", "GAME", "game" })]
    [InlineData(@"C:\Games\Big Game\Game.exe", new[] { "C:/Games/Big Game/Game.exe", "Game.exe", "Game", "game" })]
    [InlineData("  \"Discord.exe\" ", new[] { "Discord.exe", "Discord", "discord" })]
    public void Process_matchers_cover_how_xray_compares(string entry, string[] expected)
    {
        Assert.Equal(expected, WindowsTunnelConfig.ProcessMatchers([entry]));
    }

    [Fact]
    public void Process_matchers_drop_blanks_and_duplicates()
    {
        Assert.Equal(["a.exe", "a"], WindowsTunnelConfig.ProcessMatchers(["", "  ", "a.exe", "a.exe", "A"]).Take(2));
        Assert.Empty(WindowsTunnelConfig.ProcessMatchers(["", " "]));
    }

    [Fact]
    public void Routing_and_the_proxy_outbound_can_be_lifted_out_for_live_changes()
    {
        var config = WindowsTunnelConfig.Build(Base(), new WindowsTunSettings(), WindowsSplitRules.None);

        var routing = JsonNode.Parse(WindowsTunnelConfig.RoutingOf(config))!;
        Assert.Equal("AsIs", routing["domainStrategy"]!.GetValue<string>());
        Assert.NotEmpty(routing["rules"]!.AsArray());

        var proxy = JsonNode.Parse(WindowsTunnelConfig.OutboundOf(config)!)!;
        Assert.Equal("vless", proxy["protocol"]!.GetValue<string>());
        Assert.Null(WindowsTunnelConfig.OutboundOf(config, "no-such-tag"));
    }
}
