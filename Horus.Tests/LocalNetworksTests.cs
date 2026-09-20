using System.Net;
using Horus.Protocols;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// <see cref="LocalNetworks.IsDirectRange"/> decides whether an address may be given a host
/// route by the Windows split tunneling.
///
/// <para>Both ways of being wrong are silent. Too narrow, and a host route is installed for a
/// LAN address, overriding the on-link route the machine already has — the printer stops
/// answering, and it keeps not answering after the VPN is switched off, because the route
/// outlives it. Too broad, and a real destination is never steered, so traffic that was meant
/// to bypass the tunnel stays inside it with nothing to say so.</para>
/// </summary>
public class LocalNetworksTests
{
    [Theory]
    [InlineData("127.0.0.1")]        // loopback
    [InlineData("10.1.2.3")]         // RFC 1918
    [InlineData("172.16.0.1")]       // RFC 1918, low edge
    [InlineData("172.31.255.254")]   // RFC 1918, high edge — /12, not /16
    [InlineData("192.168.1.1")]
    [InlineData("169.254.10.10")]    // link-local
    [InlineData("100.64.0.1")]       // carrier-grade NAT
    [InlineData("198.18.0.1")]       // the TUN's own block
    [InlineData("0.0.0.0")]
    public void Local_addresses_are_direct(string address) =>
        Assert.True(LocalNetworks.IsDirectRange(IPAddress.Parse(address)), address);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]       // just past the /12 — a public address that looks private
    [InlineData("172.15.255.255")]   // just before it
    [InlineData("192.169.0.1")]      // one octet off 192.168/16
    [InlineData("100.128.0.1")]      // past the /10
    [InlineData("11.0.0.1")]
    public void Public_addresses_are_not(string address) =>
        Assert.False(LocalNetworks.IsDirectRange(IPAddress.Parse(address)), address);

    [Fact]
    public void Prefix_boundaries_are_respected_bit_by_bit()
    {
        // 100.64.0.0/10 spans 100.64 through 100.127. The bytes differ inside the prefix's
        // last byte, which is the case a whole-byte comparison gets wrong.
        Assert.True(LocalNetworks.IsDirectRange(IPAddress.Parse("100.127.255.255")));
        Assert.False(LocalNetworks.IsDirectRange(IPAddress.Parse("100.63.255.255")));
    }

    [Fact]
    public void Families_do_not_cross()
    {
        // ::1 must match the v6 entry, not fall through a v4 comparison.
        Assert.True(LocalNetworks.IsDirectRange(IPAddress.Parse("::1")));
        Assert.True(LocalNetworks.IsDirectRange(IPAddress.Parse("fe80::1")));
        Assert.False(LocalNetworks.IsDirectRange(IPAddress.Parse("2606:4700:4700::1111")));
    }
}
