using Horus.Domain.Models;
using Horus.Platforms.Windows.Tunnel;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// The application list on Windows: how the core's connections fold into one row per
/// application, and when a row says its connections still follow an older rule. A wrong
/// fold shows a game as "direct" while it is on the VPN, which is exactly the question the
/// screen exists to answer.
/// </summary>
public class AppTrafficTests
{
    private static CoreConnection C(ulong id, int srcPort, string outbound, long up = 0, long down = 0,
        string dst = "203.0.113.9:443", string? target = null, long started = 0, string net = "tcp") => new()
        {
            Id = id,
            Network = net,
            Source = $"198.18.0.1:{srcPort}",
            Destination = dst,
            Target = target,
            Outbound = outbound,
            Up = up,
            Down = down,
            StartedMs = started,
        };

    private static readonly Dictionary<int, ProcessIdentity> Owners = new()
    {
        [5001] = new(100, "cs2.exe", @"C:\Games\cs2.exe"),
        [5002] = new(100, "cs2.exe", @"C:\Games\cs2.exe"),
        [5003] = new(200, "chrome.exe", @"C:\Chrome\chrome.exe"),
        [5004] = new(201, "chrome.exe", @"C:\Chrome\chrome.exe"),
    };

    private static IReadOnlyList<AppTraffic> Group(params CoreConnection[] cs) =>
        AppTrafficGrouping.Group(cs, c => Owners.GetValueOrDefault(c.SourcePort));

    [Fact]
    public void Connections_fold_into_one_row_per_executable_busiest_first_unknown_last()
    {
        var apps = Group(
            C(1, 5001, WindowsTunnelConfig.ProxyTag, up: 10, down: 100),
            C(2, 5002, WindowsTunnelConfig.ProxyTag, up: 5, down: 50, net: "udp"),
            C(3, 5003, WindowsTunnelConfig.DirectTag, up: 1000, down: 9000),
            C(4, 5004, WindowsTunnelConfig.ProxyTag, up: 1, down: 1),
            C(5, 6000, WindowsTunnelConfig.ProxyTag, up: 99_999, down: 99_999));

        Assert.Equal(["chrome.exe", "cs2.exe", AppTrafficGrouping.Unknown], apps.Select(a => a.Name));

        var chrome = apps[0];
        Assert.Equal([200, 201], chrome.Pids);
        Assert.Equal(RouteMix.Mixed, chrome.Mix);
        Assert.Equal([3ul, 4ul], chrome.ConnectionIds);

        var game = apps[1];
        Assert.Equal(RouteMix.Vpn, game.Mix);
        Assert.Equal(2, game.ViaVpn);
        Assert.Equal(15, game.Up);
        Assert.Equal(150, game.Down);
    }

    [Fact]
    public void Dns_and_blocked_connections_are_neither_vpn_nor_direct()
    {
        var app = Assert.Single(Group(C(1, 5001, WindowsTunnelConfig.DnsOutTag), C(2, 5002, "block")));
        Assert.Equal(RouteMix.Other, app.Mix);
        Assert.Equal(2, app.Other);
    }

    [Fact]
    public void The_oldest_start_is_kept_and_unknown_starts_are_ignored()
    {
        var app = Assert.Single(Group(
            C(1, 5001, WindowsTunnelConfig.ProxyTag, started: 0),
            C(2, 5002, WindowsTunnelConfig.ProxyTag, started: 2_000),
            C(3, 5001, WindowsTunnelConfig.ProxyTag, started: 1_500)));
        Assert.Equal(1_500, app.OldestStartedMs);
    }

    [Fact]
    public void Targets_prefer_the_sniffed_name_and_are_capped()
    {
        var cs = Enumerable.Range(0, 8)
            .Select(i => C((ulong)i, 5003, WindowsTunnelConfig.ProxyTag, dst: $"203.0.113.{i}:443", target: i < 2 ? $"site{i}.example:443" : null))
            .ToArray();
        var app = Assert.Single(Group(cs));
        Assert.Equal(5, app.Targets.Count);
        Assert.Equal("site0.example", app.Targets[0]);
        Assert.Equal("203.0.113.2", app.Targets[2]);
    }

    [Theory]
    [InlineData("1.2.3.4:443", "1.2.3.4")]
    [InlineData("[2001:db8::1]:443", "2001:db8::1")]
    [InlineData("example.com:80", "example.com")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    public void Host_of_an_endpoint(string endpoint, string host) =>
        Assert.Equal(host, AppTrafficGrouping.HostOf(endpoint));

    [Theory]
    [InlineData(SplitTunnelingMode.Disabled, false, AppRoute.Vpn)]
    [InlineData(SplitTunnelingMode.Disabled, true, AppRoute.Vpn)]
    [InlineData(SplitTunnelingMode.Blacklist, true, AppRoute.Direct)]
    [InlineData(SplitTunnelingMode.Blacklist, false, AppRoute.Vpn)]
    [InlineData(SplitTunnelingMode.Whitelist, true, AppRoute.Vpn)]
    [InlineData(SplitTunnelingMode.Whitelist, false, AppRoute.Direct)]
    public void The_split_choice_maps_to_a_route(SplitTunnelingMode mode, bool chosen, AppRoute route) =>
        Assert.Equal(route, AppRoutePolicy.For(mode, chosen));

    [Fact]
    public void Only_connections_older_than_the_rule_need_a_restart()
    {
        var app = Assert.Single(Group(C(1, 5001, WindowsTunnelConfig.ProxyTag, started: 1_000)));

        Assert.True(AppRoutePolicy.NeedsRestart(app, ruleChangedAtMs: 2_000));
        Assert.False(AppRoutePolicy.NeedsRestart(app, ruleChangedAtMs: 500));   // reconnected since
        Assert.False(AppRoutePolicy.NeedsRestart(app, ruleChangedAtMs: 0));     // never changed
        Assert.False(AppRoutePolicy.NeedsRestart(null, ruleChangedAtMs: 2_000)); // nothing open

        var unknownStart = Assert.Single(Group(C(1, 5001, WindowsTunnelConfig.ProxyTag, started: 0)));
        Assert.False(AppRoutePolicy.NeedsRestart(unknownStart, ruleChangedAtMs: 2_000));
    }
}
