using Horus.Platforms.Windows.Tunnel;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// What the Windows tunnel does once nothing brings a dead proxy back. It used to take the
/// adapter down after a few failed recoveries, and every connection on the machine went with
/// it — the game routed direct included, whose packets the TUN was carrying perfectly well.
/// The tunnel now stays; only where its traffic goes changes.
/// </summary>
public class DeadProxyPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(DeadProxyPolicy.ClosedRecoveries - 1)]
    public void A_fresh_failure_holds_the_tunnel_closed(int failedRecoveries) =>
        Assert.Equal(DeadProxyResponse.HoldClosed, DeadProxyPolicy.After(failedRecoveries, killSwitch: false));

    [Theory]
    [InlineData(DeadProxyPolicy.ClosedRecoveries)]
    [InlineData(DeadProxyPolicy.ClosedRecoveries + 10)]
    public void A_lasting_failure_lets_traffic_around_the_vpn_without_dropping_the_tunnel(int failedRecoveries) =>
        Assert.Equal(DeadProxyResponse.FailOpen, DeadProxyPolicy.After(failedRecoveries, killSwitch: false));

    [Theory]
    [InlineData(1)]
    [InlineData(DeadProxyPolicy.ClosedRecoveries + 10)]
    public void The_kill_switch_keeps_it_closed_for_good(int failedRecoveries) =>
        Assert.Equal(DeadProxyResponse.HoldClosed, DeadProxyPolicy.After(failedRecoveries, killSwitch: true));
}
