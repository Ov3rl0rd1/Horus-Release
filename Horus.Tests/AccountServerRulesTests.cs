using Horus.Application;
using Horus.Domain.Models;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// The server is the account's, not the device's: one binding (<c>users.current_server_id</c>)
/// shared by every device signed in to it. The app shows that server, moves it only when the
/// user picks another one, and follows it when it moves from somewhere else.
/// </summary>
public class AccountServerRulesTests
{
    [Theory]
    [InlineData(7, null, true)]
    [InlineData(7, 3, true)]
    [InlineData(7, 7, false)]
    [InlineData(null, null, false)]
    [InlineData(null, 3, true)]
    public void The_accounts_server_is_looked_up_only_when_it_is_not_the_one_known(int? account, int? known, bool lookUp) =>
        Assert.Equal(lookUp, AccountServerRules.NeedsLookup(account, known));

    [Theory]
    [InlineData(VpnState.Connected)]
    [InlineData(VpnState.Reconnecting)]
    public void A_running_tunnel_follows_the_account_to_its_new_server(VpnState state) =>
        Assert.True(AccountServerRules.TunnelMustFollow(accountServerId: 7, tunnelServerId: 3, state));

    [Theory]
    [InlineData(VpnState.Disconnected)]
    [InlineData(VpnState.Connecting)]
    [InlineData(VpnState.Disconnecting)]
    public void A_tunnel_that_is_not_running_takes_the_new_server_at_its_next_connect(VpnState state) =>
        Assert.False(AccountServerRules.TunnelMustFollow(accountServerId: 7, tunnelServerId: 3, state));

    [Theory]
    [InlineData(7, 7)]
    [InlineData(null, 3)]
    [InlineData(7, null)]
    public void Nothing_moves_without_a_known_difference(int? account, int? tunnel) =>
        Assert.False(AccountServerRules.TunnelMustFollow(account, tunnel, VpnState.Connected));

    [Theory]
    [InlineData(7, 3, true)]
    [InlineData(7, null, true)]
    [InlineData(7, 7, false)]
    public void Picking_the_server_the_account_is_already_on_changes_nothing(int chosen, int? account, bool change) =>
        Assert.Equal(change, AccountServerRules.IsChange(chosen, account));
}
