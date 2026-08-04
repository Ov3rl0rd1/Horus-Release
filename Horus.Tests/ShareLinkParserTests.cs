using Horus.Domain.Models;
using Horus.Protocols;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// The link formats here are exactly what HorusAPI's ClientConfigBuilder emits. A parsing
/// slip produces a config that looks plausible and silently fails to carry traffic, which
/// is expensive to diagnose on a device — hence the coverage.
/// </summary>
public class ShareLinkParserTests
{
    private const string Uuid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    private const string VlessLink =
        $"vless://{Uuid}@fi1.horus.dev:443" +
        "?encryption=none&flow=xtls-rprx-vision&security=reality&sni=www.microsoft.com" +
        "&fp=chrome&pbk=xR8pQb2wZmT9kL0vNhY7cJfE1sA4dG6uI3oP5nWqXyM&sid=a1b2c3d4&type=tcp#MainVLESS";

    private const string HysteriaLink =
        $"hysteria2://{Uuid}@fi1.horus.dev:8443,20000-30000/" +
        "?sni=www.microsoft.com&obfs=salamander&obfs-password=s3cr3t-obfs#MainHystria";

    [Fact]
    public void Parses_vless_reality_link()
    {
        var link = ShareLinkParser.Parse(VlessLink);

        Assert.Equal(ProtocolType.Vless, link.Protocol);
        Assert.Equal(Uuid, link.Credential);
        Assert.Equal("fi1.horus.dev", link.Host);
        Assert.Equal(443, link.Port);
        Assert.Equal("xtls-rprx-vision", link.Flow);
        Assert.Equal("none", link.Encryption);
        Assert.Equal("www.microsoft.com", link.Sni);
        Assert.Equal("chrome", link.Fingerprint);
        Assert.Equal("a1b2c3d4", link.ShortId);
        Assert.Equal("MainVLESS", link.Tag);
        Assert.True(link.IsReality);
        Assert.Null(link.PortRange);
    }

    [Fact]
    public void Parses_hysteria2_link_with_port_hopping_range()
    {
        var link = ShareLinkParser.Parse(HysteriaLink);

        Assert.Equal(ProtocolType.Hysteria2, link.Protocol);
        Assert.Equal(Uuid, link.Credential);
        Assert.Equal("fi1.horus.dev", link.Host);
        Assert.Equal(8443, link.Port);
        // The comma-separated range is why this parser cannot be built on System.Uri.
        Assert.Equal("20000-30000", link.PortRange);
        Assert.Equal("salamander", link.Obfs);
        Assert.Equal("s3cr3t-obfs", link.ObfsPassword);
        Assert.Equal("MainHystria", link.Tag);
    }

    [Theory]
    [InlineData("hysteria2://pw@h.example:443#t")]
    [InlineData("hy2://pw@h.example:443#t")]
    public void Accepts_both_hysteria_scheme_spellings(string raw)
    {
        Assert.Equal(ProtocolType.Hysteria2, ShareLinkParser.Parse(raw).Protocol);
    }

    [Fact]
    public void Parses_bracketed_ipv6_authority()
    {
        var link = ShareLinkParser.Parse("vless://uid@[2001:db8::1]:8443?security=reality&pbk=k&sid=s#v6");

        Assert.Equal("2001:db8::1", link.Host);
        Assert.Equal(8443, link.Port);
    }

    [Fact]
    public void Credential_may_contain_an_at_sign()
    {
        // Split must happen on the LAST '@', not the first.
        var link = ShareLinkParser.Parse("hysteria2://user@name@h.example:443#t");

        Assert.Equal("user@name", link.Credential);
        Assert.Equal("h.example", link.Host);
    }

    [Fact]
    public void Decodes_percent_and_plus_in_query_values()
    {
        var link = ShareLinkParser.Parse(
            "hysteria2://pw@h.example:443/?obfs=salamander&obfs-password=a%2Bb+c#t");

        Assert.Equal("a+b c", link.ObfsPassword);
    }

    [Fact]
    public void Link_without_query_or_path_still_parses()
    {
        Assert.Equal(443, ShareLinkParser.Parse("hysteria2://pw@h.example:443#t").Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ss://x@h:1")]          // unsupported scheme
    [InlineData("vless://x@host")]      // no port
    [InlineData("vless://host:443")]    // no credential
    [InlineData("vless://x@host:0")]    // port out of range
    [InlineData("vless://x@host:70000")]
    [InlineData("not a link")]
    public void Rejects_malformed_links(string raw)
    {
        Assert.False(ShareLinkParser.TryParse(raw, out _));
    }

    [Fact]
    public void Redacts_the_credential_from_error_text()
    {
        // Malformed links get logged; the auth secret must not ride along.
        var ex = Assert.ThrowsAny<Exception>(() => ShareLinkParser.Parse("vless://sup3rs3cret@host"));

        Assert.DoesNotContain("sup3rs3cret", ex.Message);
    }
}
