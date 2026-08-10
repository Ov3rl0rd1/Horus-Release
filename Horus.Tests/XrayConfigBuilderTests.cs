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
    public void Xhttp_vless_link_emits_xhttpSettings()
    {
        // The node also publishes an xhttp/REALITY endpoint. "xhttp" is only an alias for
        // splithttp — the settings object is still required, and without it the transport
        // has no path to request.
        var stream = ProxyOutbound(Build(
                "vless://uid@h.example:8443?encryption=none&security=reality&sni=s.example" +
                "&fp=randomized&pbk=K&sid=S&type=xhttp&path=%2Fapi%2Fv1%2Fupdates&mode=stream-one#x"))
            .GetProperty("streamSettings");

        Assert.Equal("xhttp", stream.GetProperty("network").GetString());

        var xhttp = stream.GetProperty("xhttpSettings");
        Assert.Equal("/api/v1/updates", xhttp.GetProperty("path").GetString());
        Assert.Equal("stream-one", xhttp.GetProperty("mode").GetString());
    }

    [Fact]
    public void Tcp_vless_link_emits_no_xhttpSettings()
    {
        var stream = ProxyOutbound(Build(VlessLink)).GetProperty("streamSettings");

        Assert.Equal("tcp", stream.GetProperty("network").GetString());
        Assert.False(stream.TryGetProperty("xhttpSettings", out _));
    }

    [Fact]
    public void Flow_is_omitted_when_the_link_does_not_specify_one()
    {
        var proxy = ProxyOutbound(Build(
            "vless://uid@h.example:443?security=reality&pbk=K&sid=S&type=tcp#t"));

        var user = proxy.GetProperty("settings").GetProperty("vnext")[0].GetProperty("users")[0];
        Assert.False(user.TryGetProperty("flow", out _));
    }

    // The fork registers this as "hysteria", not "hysteria2" — see proxy/hysteria and
    // infra/conf.TransportProtocol. Emitting "hysteria2" is what produced the
    // "no transport for hysteria protocol" config error.
    private const string HysteriaLink =
        "hysteria2://pw@h.example:8443,20000-30000/?sni=s.example&alpn=h3" +
        "&obfs=salamander&obfs-password=OP&hopInterval=30#t";

    [Fact]
    public void Hysteria_outbound_uses_the_forks_protocol_and_transport_names()
    {
        var proxy = ProxyOutbound(Build(HysteriaLink));

        Assert.Equal("hysteria", proxy.GetProperty("protocol").GetString());
        Assert.Equal("hysteria", proxy.GetProperty("streamSettings").GetProperty("network").GetString());
    }

    [Fact]
    public void Hysteria_settings_are_flat_with_a_version_and_carry_auth_on_the_transport()
    {
        var proxy = ProxyOutbound(Build(HysteriaLink));

        // settings is {version,address,port} — not a servers[] array.
        var settings = proxy.GetProperty("settings");
        Assert.Equal(2, settings.GetProperty("version").GetInt32());
        Assert.Equal("h.example", settings.GetProperty("address").GetString());
        Assert.Equal(8443, settings.GetProperty("port").GetInt32());
        Assert.False(settings.TryGetProperty("servers", out _));

        // The auth password lives on the transport (HysteriaConfig.Auth), not the outbound.
        var hy = proxy.GetProperty("streamSettings").GetProperty("hysteriaSettings");
        Assert.Equal(2, hy.GetProperty("version").GetInt32());
        Assert.Equal("pw", hy.GetProperty("auth").GetString());
    }

    [Fact]
    public void Hysteria_tls_uses_the_node_domain_and_offers_h3()
    {
        var tls = ProxyOutbound(Build(HysteriaLink))
            .GetProperty("streamSettings").GetProperty("tlsSettings");

        // The node's HY2 certificate is issued for its own hostname, so the SNI must be
        // the host — not the link's sni, which carries the REALITY camouflage domain.
        Assert.Equal("h.example", tls.GetProperty("serverName").GetString());
        Assert.Contains("h3", tls.GetProperty("alpn").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public void Hysteria_defaults_alpn_to_h3_when_the_link_omits_it()
    {
        // The API issues hysteria2 links without alpn; the listener negotiates h3 only.
        var tls = ProxyOutbound(Build("hysteria2://pw@h.example:9443/?sni=s.example#t"))
            .GetProperty("streamSettings").GetProperty("tlsSettings");

        Assert.Equal(["h3"], tls.GetProperty("alpn").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public void Hysteria_obfs_and_port_hopping_live_under_finalmask()
    {
        // HysteriaConfig.Build warns that congestion/up/down/udphop moved to
        // finalmask/quicParams, and salamander is a finalmask udp mask.
        var finalmask = ProxyOutbound(Build(HysteriaLink))
            .GetProperty("streamSettings").GetProperty("finalmask");

        var mask = finalmask.GetProperty("udp")[0];
        Assert.Equal("salamander", mask.GetProperty("type").GetString());
        Assert.Equal("OP", mask.GetProperty("settings").GetProperty("password").GetString());

        var hop = finalmask.GetProperty("quicParams").GetProperty("udpHop");
        Assert.Equal("20000-30000", hop.GetProperty("ports").GetString());
        Assert.Equal(30, hop.GetProperty("interval").GetInt32());
    }

    [Fact]
    public void Hysteria_omits_finalmask_when_there_is_nothing_to_mask()
    {
        var stream = ProxyOutbound(Build("hysteria2://pw@h.example:8443/?sni=s.example#t"))
            .GetProperty("streamSettings");

        Assert.False(stream.TryGetProperty("finalmask", out _));
    }

    [Fact]
    public void Hop_interval_below_the_cores_minimum_is_dropped()
    {
        // A non-zero interval under 5s is rejected by the core; omitting it lets the
        // core apply its own default instead of failing the whole config.
        var hop = ProxyOutbound(Build(
                "hysteria2://pw@h.example:8443,20000-30000/?obfs=salamander&hopInterval=2#t"))
            .GetProperty("streamSettings").GetProperty("finalmask")
            .GetProperty("quicParams").GetProperty("udpHop");

        Assert.False(hop.TryGetProperty("interval", out _));
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
    public void Config_carries_explicit_dns_servers()
    {
        // Android has no /etc/resolv.conf, so the core's Go resolver has no nameservers and
        // fails every lookup without sending a packet — the outbound then never dials at
        // all. Explicit servers are what make name resolution work inside the core.
        var dns = Build(VlessLink).GetProperty("dns");

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
            Build(VlessLink).GetProperty("routing").GetProperty("domainStrategy").GetString());
    }

    [Fact]
    public void Outbound_dials_the_resolved_ip_but_keeps_the_hostname_as_sni()
    {
        var cfg = new XrayConfig { Link = ShareLinkParser.Parse(VlessLink) };
        cfg.Link.ResolvedHost = "203.0.113.7";

        var proxy = ProxyOutbound(JsonDocument.Parse(cfg.ToConfig()).RootElement);

        Assert.Equal("203.0.113.7",
            proxy.GetProperty("settings").GetProperty("vnext")[0].GetProperty("address").GetString());
        Assert.Equal("www.microsoft.com",
            proxy.GetProperty("streamSettings").GetProperty("realitySettings")
                 .GetProperty("serverName").GetString());
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
