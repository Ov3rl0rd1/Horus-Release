using System.Text.Json.Nodes;
using Horus.Protocols;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// Rewriting the node hostname to a resolved IP inside an outbound the app did not build.
///
/// <para>This is the most dangerous small function in the connect path. Too little and the
/// core is handed a hostname it cannot resolve — its Go resolver has no nameservers on
/// Android, so it accepts SOCKS sessions and never dials, and the app reports ЗАЩИЩЕНО over
/// a tunnel carrying nothing. Too much and it overwrites a certificate name, and every TLS
/// handshake fails validation instead.</para>
/// </summary>
public class OutboundAddressTests
{
    private const string Host = "ch1.horusping.com";
    private const string Ip = "149.33.15.57";

    private static JsonNode Vless() => JsonNode.Parse($$"""
    {
      "tag": "proxy",
      "protocol": "vless",
      "settings": { "vnext": [ { "address": "{{Host}}", "port": 443, "users": [ { "id": "u" } ] } ] },
      "streamSettings": {
        "security": "reality",
        "realitySettings": { "serverName": "www.microsoft.com", "publicKey": "K" }
      }
    }
    """)!;

    private static JsonNode Hysteria() => JsonNode.Parse($$"""
    {
      "tag": "proxy",
      "protocol": "hysteria",
      "settings": { "version": 2, "address": "{{Host}}", "port": 8443 },
      "streamSettings": {
        "security": "tls",
        "tlsSettings": { "serverName": "{{Host}}" },
        "hysteriaSettings": { "version": 2, "auth": "u" }
      }
    }
    """)!;

    private static JsonNode OlcRtc() => JsonNode.Parse("""
    {
      "tag": "proxy",
      "protocol": "olcrtc",
      "settings": { "provider": "jitsi", "transport": "datachannel", "roomId": "R", "key": "K" }
    }
    """)!;

    [Fact]
    public void Finds_the_address_in_a_vnext_array()
    {
        Assert.Equal(Host, OutboundAddress.FindHost(Vless()));
    }

    [Fact]
    public void Finds_the_address_at_the_top_of_settings()
    {
        Assert.Equal(Host, OutboundAddress.FindHost(Hysteria()));
    }

    [Fact]
    public void An_outbound_that_dials_nothing_has_no_host()
    {
        // Not an error. An olcRTC offer identifies a signalling room, so there is nothing to
        // resolve and nothing to route around the tunnel.
        Assert.Null(OutboundAddress.FindHost(OlcRtc()));
    }

    [Fact]
    public void Rewrites_the_dialled_address()
    {
        var outbound = Vless();
        var count = OutboundAddress.Rewrite(outbound, Host, Ip);

        Assert.Equal(1, count);
        Assert.Equal(Ip, outbound["settings"]!["vnext"]![0]!["address"]!.GetValue<string>());
    }

    [Fact]
    public void Never_rewrites_a_certificate_name()
    {
        // The Hysteria2 profile sets tlsSettings.serverName to the node host, so a blind
        // search-and-replace would put an IP where the certificate name belongs and break
        // every handshake. This is the single reason the walk is restricted to "address".
        var outbound = Hysteria();
        OutboundAddress.Rewrite(outbound, Host, Ip);

        Assert.Equal(Ip, outbound["settings"]!["address"]!.GetValue<string>());
        Assert.Equal(Host,
            outbound["streamSettings"]!["tlsSettings"]!["serverName"]!.GetValue<string>());
    }

    [Fact]
    public void Leaves_a_reality_server_name_alone()
    {
        // REALITY's serverName is the camouflage domain and has nothing to do with the node,
        // but it is a hostname sitting next to one — worth pinning that it is untouched.
        var outbound = Vless();
        OutboundAddress.Rewrite(outbound, Host, Ip);

        Assert.Equal("www.microsoft.com",
            outbound["streamSettings"]!["realitySettings"]!["serverName"]!.GetValue<string>());
    }

    [Fact]
    public void Leaves_an_address_that_is_not_the_node_alone()
    {
        // A profile may point an offer at something else — a chained proxy, a signalling
        // service. We have not resolved that name and must not pretend we have.
        var outbound = JsonNode.Parse("""
        {"protocol":"vless","settings":{"vnext":[{"address":"other.example","port":1}]}}
        """)!;

        Assert.Equal(0, OutboundAddress.Rewrite(outbound, Host, Ip));
        Assert.Equal("other.example",
            outbound["settings"]!["vnext"]![0]!["address"]!.GetValue<string>());
    }

    [Fact]
    public void Rewrites_every_occurrence()
    {
        // A profile could list several servers on the same node.
        var outbound = JsonNode.Parse(
            $$"""{"protocol":"x","settings":{"servers":[{"address":"{{Host}}"},{"address":"{{Host}}"}]} }""")!;

        Assert.Equal(2, OutboundAddress.Rewrite(outbound, Host, Ip));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var outbound = JsonNode.Parse("""
        {"protocol":"x","settings":{"address":"CH1.HorusPing.com"}}
        """)!;

        Assert.Equal(1, OutboundAddress.Rewrite(outbound, Host, Ip));
    }

    [Fact]
    public void A_non_string_address_is_left_alone()
    {
        // Some transports take a structured address. Nothing to substitute, and coercing it
        // would corrupt the config.
        var outbound = JsonNode.Parse("""{"protocol":"x","settings":{"address":{"host":"a"}}}""")!;

        Assert.Equal(0, OutboundAddress.Rewrite(outbound, Host, Ip));
        Assert.Null(OutboundAddress.FindHost(outbound));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void An_empty_host_rewrites_nothing(string? host)
    {
        Assert.Equal(0, OutboundAddress.Rewrite(Vless(), host!, Ip));
    }
}
