using Horus.Domain.Models;
using Horus.Presentation.ViewModels;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// What a press of the connect button does, decided from the controller's state at the
/// moment of the press. Anything but a settled "off" is stopped: a connect in progress is
/// cancelled rather than waited for, and a tunnel that came up while the screen was not
/// looking (the Quick Settings tile, a restore after the process died) is switched off
/// rather than "connected" again to no effect.
/// </summary>
public class ConnectButtonTests
{
    [Theory]
    [InlineData(VpnState.Disconnected, ConnectButtonAction.Connect)]
    [InlineData(VpnState.Error, ConnectButtonAction.Connect)]
    [InlineData(VpnState.Connecting, ConnectButtonAction.Disconnect)]
    [InlineData(VpnState.Reconnecting, ConnectButtonAction.Disconnect)]
    [InlineData(VpnState.Connected, ConnectButtonAction.Disconnect)]
    [InlineData(VpnState.Disconnecting, ConnectButtonAction.Disconnect)]
    public void A_press_follows_the_controllers_state(VpnState state, ConnectButtonAction expected) =>
        Assert.Equal(expected, ConnectButton.ActionFor(state));
}
