using System.Text.Json.Nodes;
using Horus.Application;
using Horus.Domain.Models;
using Horus.Protocols;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// Corrections applied to a node's outbound before the core sees it, and the second chance a
/// port-hopping Hysteria2 gets. Each case is a way the app failed with a node that worked from
/// other clients.
/// </summary>
public class OutboundFixupsTests
{
    // The API's Hysteria2 profile (HorusAPI node template), with hopping on.
    private const string Hysteria = """
        { "tag": "proxy", "protocol": "hysteria",
          "settings": { "version": 2, "address": "ge1.horusping.com", "port": 8443 },
          "streamSettings": { "network": "hysteria", "security": "tls",
            "tlsSettings": { "serverName": "ge1.horusping.com", "alpn": ["h3"] },
            "hysteriaSettings": { "version": 2, "auth": "u" },
            "finalmask": {
              "udp": [ { "type": "salamander", "settings": { "password": "p" } },
                       { "type": "udpHop", "settings": { "mode": "intervalLocal,intervalRemote", "interval": "30-60", "remotePorts": "31111-31999" } } ],
              "quicParams": { "congestion": "bbr", "bbrProfile": "standard" } } } }
        """;

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void A_correct_profile_is_left_alone()
    {
        var outbound = Parse(Hysteria);
        var before = outbound.ToJsonString();
        Assert.Empty(OutboundFixups.Apply(outbound));
        Assert.Equal(before, outbound.ToJsonString());
    }

    [Fact]
    public void A_port_written_as_a_string_becomes_a_number()
    {
        // The core's outbound port is a number; a string rejects the whole config.
        var outbound = Parse(Hysteria.Replace("\"port\": 8443", "\"port\": \"8443\""));
        var notes = OutboundFixups.Apply(outbound);
        Assert.Single(notes);
        Assert.Equal(8443, outbound["settings"]!["port"]!.GetValue<int>());
    }

    [Fact]
    public void Port_ranges_and_other_strings_are_not_touched()
    {
        var outbound = Parse(Hysteria);
        OutboundFixups.Apply(outbound);
        Assert.Equal("31111-31999", outbound["streamSettings"]!["finalmask"]!["udp"]![1]!["settings"]!["remotePorts"]!.GetValue<string>());
        var vless = Parse("""{ "protocol": "vless", "settings": { "vnext": [ { "address": "a", "port": "nope" } ] } }""");
        Assert.Empty(OutboundFixups.Apply(vless));
    }

    [Fact]
    public void Ports_inside_vnext_are_coerced_too()
    {
        var vless = Parse("""{ "protocol": "vless", "settings": { "vnext": [ { "address": "a", "port": "2053" } ] } }""");
        OutboundFixups.Apply(vless);
        Assert.Equal(2053, vless["settings"]!["vnext"]![0]!["port"]!.GetValue<int>());
    }

    [Fact]
    public void Hysteria_without_alpn_gets_h3()
    {
        var outbound = Parse(Hysteria.Replace(", \"alpn\": [\"h3\"]", ""));
        Assert.Single(OutboundFixups.Apply(outbound));
        Assert.Equal(["h3"], outbound["streamSettings"]!["tlsSettings"]!["alpn"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public void Hysteria_alpn_without_h3_gets_it_first()
    {
        var outbound = Parse(Hysteria.Replace("\"alpn\": [\"h3\"]", "\"alpn\": [\"h2\"]"));
        OutboundFixups.Apply(outbound);
        Assert.Equal(["h3", "h2"], outbound["streamSettings"]!["tlsSettings"]!["alpn"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public void Alpn_of_other_protocols_is_not_touched()
    {
        var vless = Parse("""{ "protocol": "vless", "streamSettings": { "security": "tls", "tlsSettings": { "serverName": "x" } } }""");
        Assert.Empty(OutboundFixups.Apply(vless));
        Assert.Null(vless["streamSettings"]!["tlsSettings"]!["alpn"]);
    }

    [Fact]
    public void Hopping_is_detected_and_removed_on_a_copy()
    {
        var outbound = Parse(Hysteria);
        Assert.True(OutboundFixups.HopsPorts(outbound));
        var plain = OutboundFixups.WithoutPortHopping(outbound);
        Assert.False(OutboundFixups.HopsPorts(plain));
        Assert.True(OutboundFixups.HopsPorts(outbound)); // the original is unchanged
        var masks = plain["streamSettings"]!["finalmask"]!["udp"]!.AsArray();
        Assert.Single(masks);
        Assert.Equal("salamander", masks[0]!["type"]!.GetValue<string>());
        Assert.NotNull(plain["streamSettings"]!["finalmask"]!["quicParams"]);
    }

    private static ConnectionCandidate C(string id, string json) => new()
    {
        Id = id, Label = id, ProtocolName = JsonNode.Parse(json)!["protocol"]!.GetValue<string>(), Outbound = Parse(json), NodeHost = "n"
    };

    [Fact]
    public void A_hopping_candidate_is_followed_by_the_same_one_without_hopping()
    {
        var vless = """{ "protocol": "vless", "settings": { "vnext": [ { "address": "a", "port": 2053 } ] } }""";
        var list = OutboundFixups.WithHopFallbacks([C("hysteria2", Hysteria), C("vless", vless)]);
        Assert.Equal(["hysteria2", "hysteria2" + OutboundFixups.NoHopSuffix, "vless"], list.Select(c => c.Id));
        Assert.False(OutboundFixups.HopsPorts(list[1].Outbound));
        Assert.Equal("hysteria", list[1].ProtocolName);
    }

    [Fact]
    public void Without_hopping_nothing_is_added()
    {
        var plain = OutboundFixups.WithoutPortHopping(Parse(Hysteria)).ToJsonString();
        var list = OutboundFixups.WithHopFallbacks([C("hysteria2", plain)]);
        Assert.Single(list);
    }

    [Theory]
    [InlineData(true, true, 5, true)]     // stored, one failed on the way: refresh
    [InlineData(true, false, 5, false)]   // stored, fresh, nothing failed: leave it
    [InlineData(true, false, 90, true)]   // stored for over an hour: refresh
    [InlineData(false, true, 5, false)]   // just fetched from the API: nothing to refresh
    public void The_cache_is_refreshed_after_a_connect_that_needed_it(bool fromCache, bool failed, int ageMinutes, bool expected) =>
        Assert.Equal(expected, CacheRefreshPolicy.AfterConnect(fromCache, failed, TimeSpan.FromMinutes(ageMinutes)));
}
