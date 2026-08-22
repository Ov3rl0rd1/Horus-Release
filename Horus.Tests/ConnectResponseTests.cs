using System.Text.Json;
using Horus.Domain.Models;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// The shape of <c>GET /servers/connect</c>, pinned against the JSON the API actually
/// sends.
///
/// <para>This is a contract across a network boundary with no compiler between the two
/// sides, and it broke silently once already: the endpoint used to answer with a flat
/// <c>{ key: link }</c> map and now answers with a structured object where <c>vless</c> is
/// an array and <c>olcrtc</c> is not a link at all. A deserialiser that quietly produces
/// an empty object is indistinguishable from a node with nothing to offer, so the failure
/// would present as "no protocols available" rather than as a parsing problem.</para>
///
/// <para>The samples below are copied from the API documentation rather than written to
/// fit the models, which is the only way this test can catch the models drifting.</para>
/// </summary>
public class ConnectResponseTests
{
    private static ServerConnection Parse(string json) =>
        JsonSerializer.Deserialize<ServerConnection>(json)
        ?? throw new InvalidOperationException("deserialised to null");

    private const string FullResponse = """
    {
      "server": { "id": 12, "name": "Germany 1", "country": "DE", "city": "Frankfurt", "host": "de1.example.com" },
      "vless": [ "vless://uid@de1.example.com:443?encryption=none&flow=xtls-rprx-vision&security=reality&sni=x.com&fp=randomized&pbk=KEY&sid=ab&type=tcp#Horus-DE" ],
      "hysteria2": "hysteria2://pass@de1.example.com:8443,20000-30000/?sni=de1.example.com&obfs=salamander&obfs-password=p#Horus-DE",
      "olcrtc": { "provider": "wbstream", "transport": "vp8channel", "room_id": "R1", "room_key": "K1", "uuid": "U1", "host": "de1.example.com" }
    }
    """;

    [Fact]
    public void Full_response_yields_one_candidate_per_endpoint()
    {
        var connection = Parse(FullResponse);
        var candidates = connection.Candidates();

        Assert.Equal(3, candidates.Count);
        Assert.True(connection.Offers(ProtocolType.Hysteria2));
        Assert.True(connection.Offers(ProtocolType.Vless));
        Assert.True(connection.Offers(ProtocolType.OlcRtc));
        Assert.Equal("Germany 1", connection.Server?.Name);
    }

    [Fact]
    public void Vless_is_an_array_and_every_entry_becomes_a_candidate()
    {
        // The contract says a node may publish several VLESS endpoints. Taking only the
        // first would silently discard the alternatives a node adds later.
        var connection = Parse("""
        { "server": null,
          "vless": [ "vless://a@h:443?security=reality&pbk=K&sid=ab#one",
                     "vless://b@h:8443?security=reality&pbk=K&sid=cd#two" ],
          "hysteria2": null, "olcrtc": null }
        """);

        var vless = connection.Candidates().Where(c => c.Protocol == ProtocolType.Vless).ToList();

        Assert.Equal(2, vless.Count);
        Assert.All(vless, c => Assert.NotNull(c.Link));
    }

    [Fact]
    public void Olcrtc_arrives_as_an_object_not_a_link()
    {
        var connection = Parse(FullResponse);
        var rtc = connection.Candidates().Single(c => c.Protocol == ProtocolType.OlcRtc);

        // No URI: there is nowhere in one to put a signalling room, which is exactly why
        // the API stopped pretending there was.
        Assert.Null(rtc.Link);
        Assert.NotNull(rtc.OlcRtc);
        Assert.Equal("wbstream", rtc.OlcRtc!.Provider);
        Assert.Equal("R1", rtc.OlcRtc.RoomId);
        Assert.Equal("U1", rtc.OlcRtc.Uuid);
    }

    [Fact]
    public void Null_and_empty_endpoints_are_dropped_rather_than_offered()
    {
        // A node that publishes nothing must read as "no candidates", not as three broken
        // ones — the connect path would otherwise spend a full fallback cycle failing.
        var connection = Parse("""
        { "server": null, "vless": [ "", "   " ], "hysteria2": "", "olcrtc": null }
        """);

        Assert.Empty(connection.Candidates());
        Assert.False(connection.HasAny);
    }

    [Fact]
    public void Half_filled_olcrtc_is_not_offered()
    {
        // A room id with no key cannot be dialled. Offering it would burn an attempt and
        // report a protocol failure for what is really a provisioning gap.
        var connection = Parse("""
        { "server": null, "vless": [], "hysteria2": null,
          "olcrtc": { "provider": "wbstream", "transport": "vp8channel",
                      "room_id": "R1", "room_key": "", "uuid": "U1", "host": "h" } }
        """);

        Assert.Empty(connection.Candidates());
    }

    [Fact]
    public void Missing_fields_deserialise_to_an_empty_offer_rather_than_throwing()
    {
        // An older or partially deployed API must degrade to "nothing on offer", which the
        // connect path reports cleanly, rather than to an exception mid-connect.
        var connection = Parse("{}");

        Assert.Empty(connection.Candidates());
        Assert.Null(connection.Server);
    }

    [Fact]
    public void Ping_candidate_maps_the_documented_fields()
    {
        // PingCandidate carries no name — only a bound server has one — so ServerInfo has
        // to derive a label rather than show an empty string.
        var server = JsonSerializer.Deserialize<ServerInfo>("""
        { "id": 12, "country": "DE", "city": "Frankfurt",
          "current_load": 3, "reserved_count": 4, "max_clients": 20, "host": "de1.example.com" }
        """)!;

        Assert.Equal(12, server.Id);
        Assert.Equal(4, server.ReservedCount);
        Assert.Equal(16, server.FreeSlots);
        Assert.True(server.HasCapacity);
        Assert.Equal("Frankfurt", server.Name);
        Assert.Equal("Frankfurt, DE", server.Location);
    }

    [Fact]
    public void A_full_node_reports_no_capacity()
    {
        // Reserved, not current_load, is what the API checks — a node can be full with
        // nobody online, and showing it as selectable earns a 409 at connect time.
        var server = JsonSerializer.Deserialize<ServerInfo>("""
        { "id": 1, "country": "DE", "city": "F", "host": "h",
          "current_load": 0, "reserved_count": 20, "max_clients": 20 }
        """)!;

        Assert.Equal(0, server.FreeSlots);
        Assert.False(server.HasCapacity);
    }

    [Fact]
    public void Bound_server_projects_onto_the_shape_the_ui_binds_to()
    {
        var bound = JsonSerializer.Deserialize<BoundServer>("""
        { "id": 12, "name": "Germany 1", "country": "DE", "city": "Frankfurt", "host": "de1.example.com" }
        """)!;

        var info = bound.ToServerInfo();

        Assert.Equal(12, info.Id);
        Assert.Equal("de1.example.com", info.Host);
        Assert.Equal("Frankfurt, DE", info.Location);
    }
}
