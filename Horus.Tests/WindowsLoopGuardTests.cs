using System.Net.Sockets;
using System.Text.Json.Nodes;
using Horus.Domain.Models;
using Horus.Platforms.Windows.Tunnel;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// What the Windows client does so that the core's own sockets cannot come back into the
/// tunnel, and what it says when something eats the machine's ports anyway.
///
/// <para>The field failure (0.12.0, "the connection drops every 10–15 minutes"): IPv6 was
/// unticked on the physical adapter, so the core could not pin the IPv6 half of its sockets
/// there; the only IPv6 route on the machine was the tunnel's, and everything sent direct to
/// an IPv6 address came straight back in, a new socket each turn — 16 000 from one datagram
/// in 80 seconds, the whole ephemeral port range. Nothing on the machine could connect for
/// five minutes, and the client reported a healthy tunnel throughout.</para>
/// </summary>
public class WindowsLoopGuardTests
{
    // ── IPv6 is only captured when the core can send it back out ──────────────

    [Fact]
    public void Ipv6_that_cannot_be_pinned_is_not_captured_and_is_blocked_outside_the_tunnel()
    {
        var decided = Ipv6Capture.Decide(new WindowsTunSettings(), Ipv6Pin.Refused);

        Assert.False(decided.CaptureIpv6);
        // Not routed into the tunnel must not mean routed past it: should the machine move to a
        // network that has IPv6, it would leave beside the VPN until the next connect.
        Assert.True(decided.BlockIpv6Outside);
    }

    [Theory]
    [InlineData(Ipv6Pin.Works)]
    [InlineData(Ipv6Pin.Unknown)]
    public void Ipv6_stays_captured_when_the_pin_works_or_nothing_is_known(Ipv6Pin pin)
    {
        var wanted = new WindowsTunSettings(Mtu: 9000);
        Assert.Equal(wanted, Ipv6Capture.Decide(wanted, pin));
    }

    [Fact]
    public void A_user_who_turned_ipv6_capture_off_keeps_exactly_that()
    {
        // "IPv6 through the VPN: off" means it goes past the VPN. The guard does not turn that
        // into a block.
        var wanted = new WindowsTunSettings(CaptureIpv6: false);
        Assert.Equal(wanted, Ipv6Capture.Decide(wanted, Ipv6Pin.Refused));
    }

    private const string NodeOutbound = """
        { "tag": "proxy", "protocol": "vless",
          "settings": { "vnext": [ { "address": "1.2.3.4", "port": 443,
                        "users": [ { "id": "u", "encryption": "none" } ] } ] },
          "streamSettings": { "network": "tcp" } }
        """;

    private static JsonObject Build(
        WindowsTunSettings? tun = null, TunnelRouting routing = TunnelRouting.Vpn, IReadOnlyList<BlockedEndpoint>? blocked = null)
    {
        var cfg = new XrayConfig
        {
            Outbound = JsonNode.Parse(NodeOutbound)!,
            Offer = "vless",
            ProtocolName = "vless",
            NodeAddress = "1.2.3.4"
        };
        return JsonNode.Parse(WindowsTunnelConfig.Build(
            cfg.ToConfig(), tun ?? new WindowsTunSettings(), WindowsSplitRules.None, routing, blocked))!.AsObject();
    }

    private static List<string> Strings(JsonNode? array) =>
        array is null ? [] : [.. array.AsArray().Select(n => n!.GetValue<string>())];

    private static JsonNode TunSettings(JsonObject root) => root["inbounds"]![0]!["settings"]!;

    [Fact]
    public void A_session_without_ipv6_asks_the_core_to_block_it_outside_the_tunnel()
    {
        var settings = TunSettings(Build(Ipv6Capture.Decide(new WindowsTunSettings(), Ipv6Pin.Refused)));

        Assert.Equal(["0.0.0.0/1", "128.0.0.0/1"], Strings(settings["autoSystemRoutingTable"]));
        Assert.Single(settings["gateway"]!.AsArray());
        // "misconfigtun" is the core's name for "no route of this IP version leads to the TUN,
        // so keep it off every other interface". Dynamic WFP session: gone with the process.
        Assert.Equal(["dns", "misconfigtun"], Strings(settings["autoSystemWfpBlockLeak"]));
    }

    [Fact]
    public void Ipv6_is_only_blocked_outside_when_asked_and_when_it_is_not_captured()
    {
        // The user's own "off": past the VPN, as before.
        Assert.Equal(["dns"], Strings(TunSettings(Build(new WindowsTunSettings(CaptureIpv6: false)))["autoSystemWfpBlockLeak"]));
        // Captured: the block would be a contradiction, and is not written.
        Assert.Equal(["dns"], Strings(TunSettings(Build(new WindowsTunSettings(BlockIpv6Outside: true)))["autoSystemWfpBlockLeak"]));
        // Without the DNS filters (a machine whose filtering engine refused them once) the
        // IPv6 block is asked for on its own.
        Assert.Equal(["misconfigtun"], Strings(TunSettings(Build(
            new WindowsTunSettings(CaptureIpv6: false, BlockDnsLeaks: false, BlockIpv6Outside: true)))["autoSystemWfpBlockLeak"]));
    }

    // ── A loop the core did not stop is seen from the connection list ─────────

    private static CoreConnection C(ulong id, string dst, string net = "udp", int srcPort = 0, string outbound = "direct") =>
        new()
        {
            Id = id, Network = net, Destination = dst, Outbound = outbound,
            Source = $"[fdfe:dcba:9876::1]:{(srcPort == 0 ? 50_000 + (int)id : srcPort)}"
        };

    private static List<CoreConnection> Many(int count, string dst, string net = "udp", ulong firstId = 1) =>
        [.. Enumerable.Range(0, count).Select(i => C(firstId + (ulong)i, dst, net))];

    [Fact]
    public void Hundreds_of_connections_to_one_destination_from_our_own_sockets_are_a_loop()
    {
        var list = Many(300, "[2600:1f18:4622:109::1]:41359");
        list.Add(C(1000, "142.250.1.1:443", "tcp"));

        var storm = Assert.Single(LoopStormDetector.Find(list, _ => true));

        Assert.Equal("udp", storm.Network);
        Assert.Equal("[2600:1f18:4622:109::1]:41359", storm.Destination);
        Assert.Equal(300, storm.ConnectionIds.Count);
        Assert.DoesNotContain(1000UL, storm.ConnectionIds);
    }

    [Fact]
    public void Another_programs_many_connections_are_its_own_business()
    {
        // A download manager, a port scanner, a load test: plenty of connections to one place,
        // none of them from a socket this process holds.
        Assert.Empty(LoopStormDetector.Find(Many(500, "203.0.113.5:443", "tcp"), _ => false));
    }

    [Fact]
    public void A_few_of_our_own_among_many_of_someone_elses_are_not_a_loop()
    {
        var list = Many(200, "203.0.113.5:443", "tcp");
        Assert.Empty(LoopStormDetector.Find(list, c => c.Id <= 10));
    }

    [Fact]
    public void Below_the_threshold_nobody_is_even_asked_who_owns_what()
    {
        // Owner lookup reads two system tables; an ordinary sample must not pay for it.
        var asked = 0;
        var list = Many(LoopStormDetector.Threshold - 1, "203.0.113.5:443", "tcp");
        list.AddRange(Many(LoopStormDetector.Threshold - 1, "203.0.113.5:443", "udp", firstId: 5000));
        list.AddRange(Many(LoopStormDetector.Threshold - 1, "203.0.113.5:8443", "tcp", firstId: 9000));

        Assert.Empty(LoopStormDetector.Find(list, _ => { asked++; return true; }));
        Assert.Equal(0, asked);
    }

    [Theory]
    [InlineData("udp", "[2600:1f18:4622:109::1]:41359", "2600:1f18:4622:109::1", 41359)]
    [InlineData("tcp", "3.237.141.246:36651", "3.237.141.246", 36651)]
    public void A_looping_destination_is_named_for_a_block_rule(string network, string destination, string address, int port)
    {
        var blocked = BlockedEndpoint.Parse(network, destination);
        Assert.Equal(new BlockedEndpoint(network, address, port), blocked);
    }

    [Theory]
    [InlineData("udp", "example.com:443")]   // the TUN only ever sees addresses; a name is not one to block by
    [InlineData("udp", "[2600::1]")]
    [InlineData("icmp", "1.2.3.4:0")]
    public void What_cannot_be_written_as_a_rule_is_not_blocked(string network, string destination) =>
        Assert.Null(BlockedEndpoint.Parse(network, destination));

    [Theory]
    [InlineData(TunnelRouting.Vpn)]
    [InlineData(TunnelRouting.FailOpen)]
    public void A_blocked_endpoint_is_dropped_before_any_rule_could_send_it_direct(TunnelRouting routing)
    {
        var rules = Build(routing: routing, blocked: [new BlockedEndpoint("udp", "2600:1f18:4622:109::1", 41359)])["routing"]!["rules"]!.AsArray();

        var index = rules.ToList().FindIndex(r => r?["port"]?.GetValue<string>() == "41359");
        Assert.True(index >= 0, "no rule for the blocked endpoint");
        var rule = rules[index]!;
        Assert.Equal("block", rule["outboundTag"]!.GetValue<string>());
        Assert.Equal("udp", rule["network"]!.GetValue<string>());
        Assert.Equal(["2600:1f18:4622:109::1"], Strings(rule["ip"]));
        Assert.Equal([WindowsTunnelConfig.TunTag], Strings(rule["inboundTag"]));

        // Ahead of everything that could send the TUN's traffic out: by range, site, process
        // or default. (A rule for another inbound — the DNS module's own queries — is not one.)
        var firstWayOut = rules.ToList().FindIndex(r =>
            r?["outboundTag"]?.GetValue<string>() is WindowsTunnelConfig.DirectTag or WindowsTunnelConfig.ProxyTag
            && (r["inboundTag"] is null || Strings(r["inboundTag"]).Contains(WindowsTunnelConfig.TunTag)));
        Assert.True(firstWayOut < 0 || index < firstWayOut, $"blocked at {index}, first way out at {firstWayOut}");
    }

    [Fact]
    public void Nothing_is_blocked_unless_something_looped() =>
        Assert.Equal(
            Build()["routing"]!.ToJsonString(),
            Build(blocked: [])["routing"]!.ToJsonString());

    // ── Saying what is wrong ──────────────────────────────────────────────────

    [Fact]
    public void Running_out_of_ports_is_told_apart_from_every_other_socket_error()
    {
        // WSAENOBUFS: what bind answers when the ephemeral range is used up.
        Assert.True(EphemeralPorts.IsExhaustion(SocketError.NoBufferSpaceAvailable));
        Assert.False(EphemeralPorts.IsExhaustion(SocketError.AccessDenied));
        Assert.False(EphemeralPorts.IsExhaustion(SocketError.NetworkUnreachable));
    }

    [Fact]
    public void A_healthy_machine_has_ports_of_both_kinds()
    {
        // UDP and TCP ports are separate pools; the field failure emptied each in turn.
        Assert.Equal(PortShortage.None, EphemeralPorts.Probe());
        Assert.False(EphemeralPorts.Exhausted());
    }

    [Fact]
    public void The_busiest_destinations_lead_the_summary()
    {
        var list = Many(40, "[2600::1]:41359");
        list.AddRange(Many(7, "3.3.3.3:443", "tcp", firstId: 100));
        list.Add(C(500, "9.9.9.9:53", "udp", outbound: "dns-out"));

        var summary = ConnectionSummary.Busiest(list, top: 2);

        Assert.Equal("48 open; udp [2600::1]:41359 x40 (direct), tcp 3.3.3.3:443 x7 (direct)", summary);
        Assert.Equal("none open", ConnectionSummary.Busiest([]));
    }

    [Fact]
    public void No_node_answering_from_the_physical_interface_while_the_proxy_works_is_not_about_the_nodes()
    {
        // What the field log showed and nobody could read: "probed 2 node(s), 0 answered" while
        // the proxy carried traffic the whole time. The probe could not open a socket.
        var said = PingDiagnosis.Explain(probed: 2, answered: 0, tunnelUp: true, proxyHealthy: true, portsExhausted: true);
        Assert.NotNull(said);
        Assert.Contains("ports", said);

        var unexplained = PingDiagnosis.Explain(probed: 2, answered: 0, tunnelUp: true, proxyHealthy: true, portsExhausted: false);
        Assert.NotNull(unexplained);
        Assert.Contains("physical interface", unexplained);
    }

    [Theory]
    [InlineData(2, 1, true, true)]    // somebody answered
    [InlineData(0, 0, true, true)]    // nobody was asked
    [InlineData(2, 0, false, true)]   // no tunnel: silence is what it looks like
    [InlineData(2, 0, true, false)]   // the proxy is down too: the network is, and that is said elsewhere
    public void An_ordinary_sweep_is_not_diagnosed(int probed, int answered, bool tunnelUp, bool proxyHealthy) =>
        Assert.Null(PingDiagnosis.Explain(probed, answered, tunnelUp, proxyHealthy, portsExhausted: false));
}
