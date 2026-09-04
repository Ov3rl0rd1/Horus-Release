using System.Text.Json;
using Horus.Domain.Models;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// The <c>GET /servers/connect</c> payload, as HorusAPI renders it for the app.
///
/// <para>The body below is the shape produced by <c>OfferRenderer.RenderOutbounds</c> from
/// the node's <c>default.json</c> profile: the account substituted in, and the node's own
/// placeholders (ports, keys, host) already resolved on the node side. Share links are no
/// longer part of this path — they survive only on the base64 subscription URL third-party
/// clients use.</para>
/// </summary>
public class ConnectResponseTests
{
    private const string Body = """
    {
      "server": { "id": 7, "name": "Horus-CH", "country": "CH", "city": "Zurich", "host": "ch1.horusping.com" },
      "outbounds": [
        {
          "id": "vless-reality",
          "label": "VLESS REALITY",
          "tag": "vless-in",
          "outbound": {
            "tag": "proxy",
            "protocol": "vless",
            "settings": {
              "vnext": [ { "address": "ch1.horusping.com", "port": 443,
                           "users": [ { "id": "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
                                        "encryption": "none", "flow": "xtls-rprx-vision", "level": 0 } ] } ]
            },
            "streamSettings": {
              "network": "tcp",
              "security": "reality",
              "realitySettings": { "serverName": "www.microsoft.com", "fingerprint": "randomized",
                                   "publicKey": "PUBKEY", "shortId": "a1b2", "spiderX": "" }
            }
          }
        },
        {
          "id": "hysteria2",
          "label": "Hysteria2",
          "tag": "hy-in",
          "outbound": {
            "tag": "proxy",
            "protocol": "hysteria",
            "settings": { "version": 2, "address": "ch1.horusping.com", "port": 8443 },
            "streamSettings": {
              "network": "hysteria",
              "security": "tls",
              "tlsSettings": { "serverName": "ch1.horusping.com" },
              "hysteriaSettings": { "version": 2, "auth": "3f2504e0-4f89-11d3-9a0c-0305e82c3301" },
              "finalmask": { "udp": [ { "type": "salamander", "settings": { "password": "OBFS" } } ] }
            }
          }
        },
        {
          "id": "olcrtc",
          "label": "olcRTC",
          "tag": "olcrtc-in",
          "outbound": {
            "tag": "proxy",
            "protocol": "olcrtc",
            "settings": { "provider": "jitsi", "transport": "datachannel", "roomId": "R",
                          "key": "K", "dnsServer": "8.8.8.8:53",
                          "deviceId": "3f2504e0-4f89-11d3-9a0c-0305e82c3301" }
          }
        }
      ]
    }
    """;

    private static ServerConnection Parse(string json) =>
        JsonSerializer.Deserialize<ServerConnection>(json)!;

    [Fact]
    public void Reads_the_bound_server()
    {
        var connection = Parse(Body);

        Assert.NotNull(connection.Server);
        Assert.Equal(7, connection.Server!.Id);
        Assert.Equal("ch1.horusping.com", connection.Server.Host);
        Assert.Equal("Zurich, CH", connection.Server.Location);
    }

    [Fact]
    public void Every_offer_becomes_a_candidate()
    {
        var candidates = Parse(Body).Candidates();

        Assert.Equal(3, candidates.Count);
        Assert.Equal(["vless-reality", "hysteria2", "olcrtc"], candidates.Select(c => c.Id));
        Assert.Equal(["vless", "hysteria", "olcrtc"], candidates.Select(c => c.ProtocolName));
    }

    [Fact]
    public void The_nodes_order_is_preserved()
    {
        // A profile lists its preferred offer first, and the node is the side that knows
        // what it is running. The app used to re-sort by its own protocol preference, which
        // is no longer expressible — and was the wrong place to decide anyway.
        Assert.Equal("vless-reality", Parse(Body).Candidates()[0].Id);
    }

    [Fact]
    public void The_outbound_survives_intact()
    {
        // It is passed to the core verbatim, so anything lost in deserialisation is lost
        // for good — and would surface as a config the core rejects at start.
        var vless = Parse(Body).Candidates()[0].Outbound;

        Assert.Equal("vless", vless["protocol"]!.GetValue<string>());
        Assert.Equal(443, vless["settings"]!["vnext"]![0]!["port"]!.GetValue<int>());
        Assert.Equal("xtls-rprx-vision",
            vless["settings"]!["vnext"]![0]!["users"]![0]!["flow"]!.GetValue<string>());
        Assert.Equal("www.microsoft.com",
            vless["streamSettings"]!["realitySettings"]!["serverName"]!.GetValue<string>());
    }

    [Fact]
    public void The_label_falls_back_to_the_id()
    {
        var connection = Parse("""
        {
          "server": { "id": 1, "host": "h.example" },
          "outbounds": [ { "id": "custom", "label": "", "outbound": { "protocol": "x" } } ]
        }
        """);

        Assert.Equal("custom", connection.Candidates()[0].Label);
    }

    [Fact]
    public void An_offer_with_no_outbound_is_dropped()
    {
        // The API skips these, but a malformed row must not reach the connect loop as a
        // candidate that throws the moment it is used.
        var connection = Parse("""
        {
          "server": { "id": 1, "host": "h.example" },
          "outbounds": [ { "id": "broken", "label": "Broken" },
                         { "id": "ok", "outbound": { "protocol": "vless" } } ]
        }
        """);

        Assert.Equal(["ok"], connection.Candidates().Select(c => c.Id));
    }

    [Fact]
    public void A_node_with_nothing_to_offer_reads_as_empty()
    {
        // 503 is what the API returns in this case, but a body with an empty list has to
        // degrade to "no candidates" rather than to a null reference.
        var connection = Parse("""{ "server": { "id": 1, "host": "h.example" }, "outbounds": [] }""");

        Assert.False(connection.HasAny);
        Assert.Empty(connection.Candidates());
    }

    [Fact]
    public void A_body_from_the_previous_contract_reads_as_empty()
    {
        // What a cache entry written by an older build looks like after the schema change.
        // It must produce no candidates rather than a half-populated one — the cache key was
        // versioned for the same reason.
        var connection = Parse("""
        {
          "server": { "id": 1, "host": "h.example" },
          "vless": [ "vless://uid@h.example:443?security=reality#t" ],
          "hysteria2": "hysteria2://pw@h.example:8443?sni=s#t"
        }
        """);

        Assert.False(connection.HasAny);
    }

    [Fact]
    public void Candidates_carry_the_node_host()
    {
        // Used for diagnostics; the address actually dialled is read out of the outbound,
        // because a profile is free to point an offer somewhere other than the node.
        Assert.All(Parse(Body).Candidates(), c => Assert.Equal("ch1.horusping.com", c.NodeHost));
    }
}
