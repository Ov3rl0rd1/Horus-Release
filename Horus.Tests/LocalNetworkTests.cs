using System.Net;
using Horus.Protocols;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// The two lists that decide what never leaves through the node. Both are consumed by more
/// than one layer — the core's routing table and the TUN's own routes — and a mistake in
/// either is silent: a missing entry proxies LAN traffic, an extra one sends real traffic
/// outside the tunnel.
/// </summary>
public class LocalNetworkTests
{
    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("172.16.0.0/12")]
    [InlineData("192.168.0.0/16")]
    [InlineData("127.0.0.0/8")]
    [InlineData("169.254.0.0/16")]
    [InlineData("::1/128")]
    [InlineData("fc00::/7")]
    [InlineData("fe80::/10")]
    public void The_obvious_private_ranges_are_direct(string cidr) =>
        Assert.Contains(cidr, LocalNetworks.Direct);

    [Fact]
    public void The_tunnels_own_subnet_is_direct()
    {
        // 198.18.0.1 is the TUN address. Without 198.18.0.0/15 a packet addressed to our
        // own interface would be handed to the proxy and sent to the node — wrong, and a
        // small hint to the node about how the client is built.
        Assert.Contains("198.18.0.0/15", LocalNetworks.Direct);

        var tun = IPAddress.Parse(HevTunnelConfig.Ipv4Address);
        Assert.True(InRange(tun, "198.18.0.0/15"));
    }

    [Fact]
    public void Carrier_grade_nat_is_direct_but_not_excluded_from_the_tunnel()
    {
        // 100.64/10 is a destination worth sending direct (a Tailscale peer, a carrier
        // service) but it is also the space an operator hands the phone itself. Excluding
        // it from the tunnel's routes could take real traffic outside the VPN, so the two
        // lists deliberately disagree here.
        Assert.Contains("100.64.0.0/10", LocalNetworks.Direct);
        Assert.DoesNotContain("100.64.0.0/10", LocalNetworks.ExcludedFromTunnel);
    }

    [Fact]
    public void Everything_excluded_from_the_tunnel_is_also_direct()
    {
        // A range kept out of the TUN routes but still proxied by the core would be routed
        // to the node by one layer and around it by the other.
        foreach (var cidr in LocalNetworks.ExcludedFromTunnel.Concat(LocalNetworks.ExcludedFromTunnelV6))
            Assert.Contains(cidr, LocalNetworks.Direct);
    }

    [Fact]
    public void Nothing_routable_is_excluded_from_the_tunnel()
    {
        // The guard against the dangerous mistake: an over-broad entry here is a silent
        // leak. Every excluded range must be private, loopback or link-local.
        foreach (var cidr in LocalNetworks.ExcludedFromTunnel)
        {
            var (address, _) = LocalNetworks.Split(cidr);
            var ip = IPAddress.Parse(address).GetAddressBytes();

            var isPrivate =
                ip[0] == 10 ||
                (ip[0] == 172 && ip[1] >= 16 && ip[1] <= 31) ||
                (ip[0] == 192 && ip[1] == 168) ||
                (ip[0] == 169 && ip[1] == 254) ||
                ip[0] == 127;

            Assert.True(isPrivate, $"{cidr} is not a private range and must not bypass the tunnel");
        }
    }

    [Theory]
    [InlineData("10.0.0.0/8", "10.0.0.0", 8)]
    [InlineData("fc00::/7", "fc00::", 7)]
    [InlineData("1.2.3.4", "1.2.3.4", 32)]
    [InlineData("::1", "::1", 128)]
    public void Split_reads_the_prefix(string cidr, string address, int prefix)
    {
        var parsed = LocalNetworks.Split(cidr);
        Assert.Equal(address, parsed.Address);
        Assert.Equal(prefix, parsed.Prefix);
    }

    [Fact]
    public void Every_entry_parses_as_an_address()
    {
        // These strings are handed to the core and to Android's IpPrefix. A typo is not
        // caught at compile time and produces a rejected route or a rejected config.
        foreach (var cidr in LocalNetworks.Direct
                     .Concat(LocalNetworks.ExcludedFromTunnel)
                     .Concat(LocalNetworks.ExcludedFromTunnelV6))
        {
            var (address, prefix) = LocalNetworks.Split(cidr);
            Assert.True(IPAddress.TryParse(address, out var ip), $"{cidr} has an unparsable address");
            Assert.InRange(prefix, 0, ip!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        }
    }

    private static bool InRange(IPAddress address, string cidr)
    {
        var (network, prefix) = LocalNetworks.Split(cidr);
        var a = address.GetAddressBytes();
        var n = IPAddress.Parse(network).GetAddressBytes();

        for (var bit = 0; bit < prefix; bit++)
        {
            var mask = (byte)(1 << (7 - bit % 8));
            if ((a[bit / 8] & mask) != (n[bit / 8] & mask)) return false;
        }
        return true;
    }
}
