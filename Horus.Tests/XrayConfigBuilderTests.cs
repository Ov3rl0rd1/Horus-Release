using System.Text.Json;
using Horus.Domain.Models;
using Horus.Protocols;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// Golden assertions on the generated xray config.
///
/// <c>BuildHysteria2</c> in particular targets the custom core's own schema and will be
/// edited again — probably on a device, under time pressure. These tests are what will
/// say so on the day that edit breaks the VLESS path instead.
/// </summary>
public class XrayConfigBuilderTests
{
    private static JsonElement Build(string shareLink, Action<XrayConfig>? configure = null)
    {
        var cfg = new XrayConfig { Link = ShareLinkParser.Parse(shareLink) };
        configure?.Invoke(cfg);
        return JsonDocument.Parse(cfg.ToConfig()).RootElement.Clone();
    }

    private static JsonElement ProxyOutbound(JsonElement root)
    {
        foreach (var outbound in root.GetProperty("outbounds").EnumerateArray())
            if (outbound.GetProperty("tag").GetString() == XrayConfigBuilder.ProxyTag)
                return outbound;

        throw new Xunit.Sdk.XunitException("No outbound tagged 'proxy'.");
    }

    private const string VlessLink =
        "vless://uid-1@fi1.horus.dev:443?encryption=none&flow=xtls-rprx-vision&security=reality" +
        "&sni=www.microsoft.com&fp=chrome&pbk=PUBKEY&sid=SHORTID&type=tcp#MainVLESS";

    [Fact]
    public void Vless_outbound_carries_the_reality_parameters()
    {
        var proxy = ProxyOutbound(Build(VlessLink));

        Assert.Equal("vless", proxy.GetProperty("protocol").GetString());

        var user = proxy.GetProperty("settings").GetProperty("vnext")[0]
                        .GetProperty("users")[0];
        Assert.Equal("uid-1", user.GetProperty("id").GetString());
        Assert.Equal("none", user.GetProperty("encryption").GetString());
        Assert.Equal("xtls-rprx-vision", user.GetProperty("flow").GetString());

        var stream = proxy.GetProperty("streamSettings");
        Assert.Equal("reality", stream.GetProperty("security").GetString());

        var reality = stream.GetProperty("realitySettings");
        Assert.Equal("www.microsoft.com", reality.GetProperty("serverName").GetString());
        Assert.Equal("PUBKEY", reality.GetProperty("publicKey").GetString());
        Assert.Equal("SHORTID", reality.GetProperty("shortId").GetString());
        Assert.Equal("chrome", reality.GetProperty("fingerprint").GetString());
    }

    [Fact]
    public void Flow_is_omitted_when_the_link_does_not_specify_one()
    {
        var proxy = ProxyOutbound(Build(
            "vless://uid@h.example:443?security=reality&pbk=K&sid=S&type=tcp#t"));

        var user = proxy.GetProperty("settings").GetProperty("vnext")[0].GetProperty("users")[0];
        Assert.False(user.TryGetProperty("flow", out _));
    }

    [Fact]
    public void Hysteria2_outbound_includes_obfs_and_hop_ports()
    {
        var proxy = ProxyOutbound(Build(
            "hysteria2://pw@h.example:8443,20000-30000/?sni=s.example&obfs=salamander&obfs-password=OP#t"));

        Assert.Equal("hysteria2", proxy.GetProperty("protocol").GetString());

        var server = proxy.GetProperty("settings").GetProperty("servers")[0];
        Assert.Equal("h.example", server.GetProperty("address").GetString());
        Assert.Equal(8443, server.GetProperty("port").GetInt32());
        Assert.Equal("pw", server.GetProperty("password").GetString());

        var settings = proxy.GetProperty("streamSettings").GetProperty("hysteria2Settings");
        Assert.Equal("salamander", settings.GetProperty("obfs").GetProperty("type").GetString());
        Assert.Equal("OP", settings.GetProperty("obfs").GetProperty("password").GetString());
        Assert.Equal("20000-30000", settings.GetProperty("hopPorts").GetString());
    }

    [Fact]
    public void Hop_ports_are_omitted_when_the_link_has_no_range()
    {
        var proxy = ProxyOutbound(Build("hysteria2://pw@h.example:8443/?sni=s.example#t"));
        var settings = proxy.GetProperty("streamSettings").GetProperty("hysteria2Settings");

        Assert.False(settings.TryGetProperty("hopPorts", out _));
    }

    [Fact]
    public void Socks_inbound_matches_the_configured_port()
    {
        var root = Build(VlessLink);
        var inbound = root.GetProperty("inbounds")[0];

        Assert.Equal("socks", inbound.GetProperty("protocol").GetString());
        Assert.Equal(XrayConfig.DefaultSocksPort, inbound.GetProperty("port").GetInt32());
        Assert.Equal("127.0.0.1", inbound.GetProperty("listen").GetString());
        Assert.True(inbound.GetProperty("settings").GetProperty("udp").GetBoolean());
    }

    [Fact]
    public void Private_ranges_route_direct_and_everything_else_proxies()
    {
        var rules = Build(VlessLink).GetProperty("routing").GetProperty("rules");

        var direct = rules[0];
        Assert.Equal(XrayConfigBuilder.DirectTag, direct.GetProperty("outboundTag").GetString());

        var ranges = direct.GetProperty("ip").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("127.0.0.0/8", ranges);
        Assert.Contains("10.0.0.0/8", ranges);
        Assert.Contains("192.168.0.0/16", ranges);
        Assert.Contains("::1/128", ranges);
        Assert.Contains("fc00::/7", ranges);

        // Catch-all must be last, or it would shadow the direct rule.
        var last = rules[rules.GetArrayLength() - 1];
        Assert.Equal(XrayConfigBuilder.ProxyTag, last.GetProperty("outboundTag").GetString());
    }

    [Fact]
    public void Routing_never_uses_geo_predicates()
    {
        // geoip:/geosite: would require geoip.dat + geosite.dat next to the core and a
        // XraySetAssetPath call; the builder deliberately avoids that dependency.
        var json = new XrayConfig { Link = ShareLinkParser.Parse(VlessLink) }.ToConfig();

        Assert.DoesNotContain("geoip:", json);
        Assert.DoesNotContain("geosite:", json);
    }

    [Fact]
    public void Log_file_is_emitted_only_when_a_path_is_set()
    {
        var without = Build(VlessLink).GetProperty("log");
        Assert.False(without.TryGetProperty("error", out _));

        var with = Build(VlessLink, c => c.LogFilePath = "/tmp/xray.log").GetProperty("log");
        Assert.Equal("/tmp/xray.log", with.GetProperty("error").GetString());
        // Access logging records every destination the user visits — keep it off.
        Assert.Equal("none", with.GetProperty("access").GetString());
    }

    [Fact]
    public void Always_emits_direct_and_block_outbounds()
    {
        var tags = Build(VlessLink).GetProperty("outbounds").EnumerateArray()
            .Select(o => o.GetProperty("tag").GetString()).ToList();

        Assert.Contains(XrayConfigBuilder.ProxyTag, tags);
        Assert.Contains(XrayConfigBuilder.DirectTag, tags);
        Assert.Contains(XrayConfigBuilder.BlockTag, tags);
    }
}
