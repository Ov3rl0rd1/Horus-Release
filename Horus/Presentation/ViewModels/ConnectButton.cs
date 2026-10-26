using Horus.Domain.Models;

namespace Horus.Presentation.ViewModels
{
    public enum ConnectButtonAction { Connect, Disconnect }

    /// <summary>
    /// What a press of the connect button does. Decided from the controller's state at the
    /// moment of the press, never from what the screen last showed: a tunnel brought up from
    /// the Quick Settings tile, or restored after the process died, is running before any
    /// screen hears of it.
    /// </summary>
    public static class ConnectButton
    {
        /// <summary>Anything but a settled "off" is stopped; a connect in progress is cancelled, not waited for.</summary>
        public static ConnectButtonAction ActionFor(VpnState state) =>
            state is VpnState.Disconnected or VpnState.Error
                ? ConnectButtonAction.Connect
                : ConnectButtonAction.Disconnect;
    }
}
