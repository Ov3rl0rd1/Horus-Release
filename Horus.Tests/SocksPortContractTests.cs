using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Horus.Domain.Models;
using Horus.Protocols;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// xray's SOCKS5 inbound port and hev-socks5-tunnel's outbound port are two halves of one
/// contract, written in two languages, with nothing connecting them.
///
/// If they drift apart the tunnel still establishes, the UI still says ЗАЩИЩЕНО, the
/// notification still appears — and not one packet moves. That failure is invisible from
/// managed code and expensive to find on a device, which makes this the highest-value test
/// in the suite.
///
/// The YAML used to be inlined in the Android service, so these tests read it out of the
/// source with a regex. It now comes from <see cref="HevTunnelConfig"/>, which is shared by
/// the Android host (in-process, given a TUN fd) and the Windows host (child process,
/// creating its own wintun adapter) — so the generated config is asserted directly, and the
/// source checks that remain only guard against someone re-inlining a second copy.
/// </summary>
public class SocksPortContractTests
{
    private static string ConfigFor(int port) =>
        HevTunnelConfig.Build("/tmp/hev.log", HevTunnelConfig.DefaultLogLevel, port);

    private static string Config => ConfigFor(XrayConfig.DefaultSocksPort);

    private static XrayConfig XrayFor(int port) => new()
    {
        // Any outbound will do: the port under test is the app's SOCKS inbound, which the
        // node's outbound has nothing to do with.
        Outbound = System.Text.Json.Nodes.JsonNode.Parse(
            """{"tag":"proxy","protocol":"vless","settings":{}}""")!,
        Offer = "vless-reality",
        SocksPort = port
    };

    // ── The generated config ─────────────────────────────────────────────────

    /// <summary>
    /// The port is no longer a constant on either side — it is chosen per connect, because
    /// 1080 is frequently taken on desktop. So the contract to protect is not "both equal
    /// 1080" but "both equal whatever was chosen", checked across the range the allocator
    /// can actually hand out.
    /// </summary>
    [Theory]
    [InlineData(1080)]
    [InlineData(1081)]
    [InlineData(1099)]
    public void Hev_dials_the_same_port_xray_listens_on(int port)
    {
        var match = Regex.Match(ConfigFor(port), @"socks5:\s*\r?\n\s*port:\s*(?<port>\d+)");
        Assert.True(match.Success, "Could not find 'socks5: port:' in the generated hev YAML.");

        var hevPort = int.Parse(match.Groups["port"].Value);
        Assert.Equal(port, hevPort);
        Assert.Contains($"\"port\": {hevPort}", XrayFor(port).ToConfig());
    }

    [Fact]
    public void Default_port_is_still_the_conventional_one() =>
        Assert.Equal(1080, XrayConfig.DefaultSocksPort);

    [Fact]
    public void Allocator_takes_the_preferred_port_when_it_is_free()
    {
        var free = FreePort();
        Assert.Equal(free, SocksPortAllocator.Allocate(free));
    }

    [Fact]
    public void Allocator_moves_off_a_port_someone_else_holds()
    {
        // Deliberately not asserting anything about 1080 itself: whether it is free is a
        // property of the machine running the tests, and on a developer box it usually is
        // not — that is the entire reason this allocator exists.
        var taken = FreePort();
        var squatter = new TcpListener(IPAddress.Loopback, taken);
        squatter.Start();
        try
        {
            var moved = SocksPortAllocator.Allocate(taken);
            Assert.NotEqual(taken, moved);
            Assert.InRange(moved, taken + 1, taken + 19);
        }
        finally { squatter.Stop(); }
    }

    [Fact]
    public void Allocator_reuses_the_last_port_when_no_preference_is_given()
    {
        // Stability here is what lets a reconnect leave the bridge and the TUN untouched:
        // the port is baked into hev's config at start-up, so a port that moves forces a
        // restart of the bridge that a stable one does not.
        var first = SocksPortAllocator.Allocate();
        var second = SocksPortAllocator.Allocate();

        Assert.Equal(first, second);
    }

    [Fact]
    public void An_explicit_preference_beats_the_remembered_port()
    {
        // The remembered port must not answer a question that was not asked. Without this
        // the allocator ignored its own argument once anything had allocated before it.
        SocksPortAllocator.Allocate();

        var wanted = FreePort();
        Assert.Equal(wanted, SocksPortAllocator.Allocate(wanted));
    }

    /// <summary>A port that is free right now, chosen by the OS.</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public void Hev_tunnel_addresses_match_what_the_manager_configures()
    {
        // VpnManager.BuildTunnelOptions hands these to VpnService.Builder on Android and to
        // the route table on Windows. A mismatch also produces a live-looking tunnel that
        // drops every packet.
        Assert.Matches(@"ipv4:\s*198\.18\.0\.1", Config);
        Assert.Matches(@"ipv6:\s*'fc00::1'", Config);
        Assert.Equal("198.18.0.1", HevTunnelConfig.Ipv4Address);
    }

    [Fact]
    public void Hev_mtu_is_emitted_and_matches_the_constant()
    {
        var match = Regex.Match(Config, @"mtu:\s*(?<mtu>\d+)");
        Assert.True(match.Success, "hev YAML carries no mtu.");
        Assert.Equal(HevTunnelConfig.Mtu, int.Parse(match.Groups["mtu"].Value));
    }

    [Fact]
    public void Log_path_backslashes_are_normalised()
    {
        // Windows hands in a native path. hev parses the config as YAML, where a backslash
        // inside an unquoted scalar is fine — but its own path handling is POSIX, so the
        // forward-slash form is the one that reliably opens.
        Assert.Contains("log-file: C:/Users/x/hev.log",
            HevTunnelConfig.Build(@"C:\Users\x\hev.log", "warn", XrayConfig.DefaultSocksPort));
    }

    // ── One generator, not three ─────────────────────────────────────────────

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), "Horus", .. parts]));

    [Theory]
    [InlineData("Platforms", "Android", "HevSocksTunnel.cs")]
    [InlineData("Platforms", "Windows", "WindowsVpnService.cs")]
    public void Tunnel_hosts_use_the_shared_config_rather_than_their_own_copy(params string[] path)
    {
        var source = Source(path);

        Assert.Contains(nameof(HevTunnelConfig), source);
        Assert.DoesNotContain("socks5:", source);
        Assert.DoesNotContain("198.18.0.1", source);
    }
}
