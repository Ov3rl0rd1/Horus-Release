using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Horus.Domain.Models;
using Horus.Protocols;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// xray's SOCKS5 inbound port and hev-socks5-tunnel's outbound port are two halves of one
/// contract, written in two languages, in two files, with nothing connecting them.
///
/// If they drift apart the tunnel still establishes, the UI still says ЗАЩИЩЕНО, the
/// notification still appears — and not one packet moves. That failure is invisible from
/// managed code and expensive to find on a device, which makes this the highest-value test
/// in the suite.
///
/// HevSocksTunnel.cs is Android-only so it cannot be linked into this project; the source
/// is read from disk instead, located relative to this file.
/// </summary>
public class SocksPortContractTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));

    private static string HevSourcePath =>
        Path.Combine(RepoRoot(), "Horus", "Platforms", "Android", "HevSocksTunnel.cs");

    [Fact]
    public void Hev_source_is_where_this_test_expects_it()
    {
        Assert.True(File.Exists(HevSourcePath),
            $"HevSocksTunnel.cs not found at {HevSourcePath}. If it moved, update this test — " +
            "do not delete it.");
    }

    [Fact]
    public void Hev_dials_the_same_port_xray_listens_on()
    {
        var source = File.ReadAllText(HevSourcePath);

        // socks5:
        //   port: <value>
        var match = Regex.Match(source, @"socks5:\s*\r?\n\s*port:\s*(?<port>[^\r\n]+)");
        Assert.True(match.Success, "Could not find 'socks5: port:' in the hev YAML config.");

        var portExpression = match.Groups["port"].Value.Trim();

        // The config is an interpolated string, so the expected form is a reference to the
        // shared constant rather than a literal. A hardcoded number here means the two
        // sides can drift silently — which is the whole point of this test.
        Assert.True(
            portExpression.Contains(nameof(XrayConfig.DefaultSocksPort), StringComparison.Ordinal),
            $"hev's socks5 port is '{portExpression}', not a reference to " +
            $"{nameof(XrayConfig)}.{nameof(XrayConfig.DefaultSocksPort)}. The TUN bridge and the " +
            "xray inbound must resolve to the same port or the tunnel carries nothing.");
    }

    [Fact]
    public void Generated_config_listens_on_the_shared_default_port()
    {
        var cfg = new XrayConfig
        {
            Link = ShareLinkParser.Parse("vless://uid@h.example:443?security=reality&pbk=K&sid=S#t")
        };

        Assert.Equal(XrayConfig.DefaultSocksPort, cfg.SocksPort);
        Assert.Contains($"\"port\": {XrayConfig.DefaultSocksPort}", cfg.ToConfig());
    }

    [Fact]
    public void Hev_tunnel_address_matches_the_tun_address_the_manager_configures()
    {
        // VpnManager.BuildTunnelOptions hands 198.18.0.1/30 to VpnService.Builder; hev is
        // configured with the same address. A mismatch here also produces a live-looking
        // tunnel that drops every packet.
        var source = File.ReadAllText(HevSourcePath);

        Assert.Matches(@"ipv4:\s*198\.18\.0\.1", source);
        Assert.Matches(@"ipv6:\s*'fc00::1'", source);
    }
}
