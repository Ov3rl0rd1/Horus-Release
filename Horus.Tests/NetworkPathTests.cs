using Horus.Platforms.Windows.Tunnel;
using Xunit;
using Change = Horus.Platforms.Windows.Tunnel.NetworkPathSelector.Change;

namespace Horus.Tests;

/// <summary>
/// Which network events the Windows client may act on. Acting on the wrong one resets
/// sessions, which on Hysteria2 drops every TCP connection on the machine.
/// </summary>
public class NetworkPathTests
{
    private static DefaultRoute R(int index, uint metric, bool up = true, bool tun = false, bool wifi = false, string gw = "192.168.1.1") =>
        new(index, metric, up, tun, wifi, gw, $"if{index}");

    [Fact]
    public void The_lowest_metric_wins_and_the_tunnel_never_does()
    {
        var path = NetworkPathSelector.Select([R(30, 0, tun: true), R(7, 35, wifi: true), R(12, 25)]);
        Assert.Equal(12, path!.InterfaceIndex);
    }

    [Fact]
    public void Wifi_is_not_preferred_over_a_cheaper_cable()
    {
        // Upstream preferred any Wi-Fi outright; the fork follows the metric, and so must this.
        Assert.Equal(12, NetworkPathSelector.Select([R(7, 50, wifi: true), R(12, 25)])!.InterfaceIndex);
        Assert.Equal(7, NetworkPathSelector.Select([R(7, 20, wifi: true), R(12, 25)])!.InterfaceIndex);
    }

    [Fact]
    public void Down_interfaces_and_an_empty_table_give_no_path()
    {
        Assert.Null(NetworkPathSelector.Select([R(5, 1, up: false), R(30, 0, tun: true)]));
        Assert.Null(NetworkPathSelector.Select([]));
    }

    private static NetworkPath P(int index, string gw = "192.168.1.1") => new(index, gw, $"if{index}", false);

    [Fact]
    public void A_wifi_blip_on_a_cabled_pc_changes_nothing()
    {
        Assert.Equal(Change.None, NetworkPathSelector.Classify(P(12), P(12), stillUp: true));
    }

    [Fact]
    public void A_better_path_appearing_leaves_open_connections_alone()
    {
        // Cable plugged in while on Wi-Fi: Wi-Fi is still up, so what runs over it keeps running.
        Assert.Equal(Change.Preferred, NetworkPathSelector.Classify(P(7), P(12), stillUp: true));
    }

    [Fact]
    public void Losing_the_path_in_use_is_a_real_handover()
    {
        Assert.Equal(Change.Replaced, NetworkPathSelector.Classify(P(12), P(7), stillUp: false));
        Assert.Equal(Change.Replaced, NetworkPathSelector.Classify(P(7, "10.0.0.1"), P(7, "10.1.0.1"), stillUp: true));
    }

    [Fact]
    public void Offline_and_back()
    {
        Assert.Equal(Change.Lost, NetworkPathSelector.Classify(P(12), null, stillUp: false));
        Assert.Equal(Change.Restored, NetworkPathSelector.Classify(null, P(12), stillUp: false));
        Assert.Equal(Change.None, NetworkPathSelector.Classify(null, null, stillUp: false));
    }
}
