using Horus.Domain.Events;
using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    /// <summary>
    /// What the UI and the update path need from whatever runs the VPN.
    ///
    /// <para>Two implementations, deliberately separate: <c>VpnManager</c> on Android, where
    /// the connection lives through Doze, revokes and a foreground service; and
    /// <c>WindowsVpnController</c> on Windows, where the core owns the TUN in-process and the
    /// concerns are a desktop's — a game that must not be dropped, network changes on a
    /// machine with several adapters, per-application routing. Sharing one class meant
    /// every fix for one platform was a risk for the other. Only this surface is shared.</para>
    /// </summary>
    public interface IVpnController
    {
        VpnState State { get; }
        ServerInfo? ActiveServer { get; }

        /// <summary>What the node calls the active offer. Shown to the user.</summary>
        string? ActiveOfferLabel { get; }

        event EventHandler<VpnStateChangedEventArgs>? StateChanged;

        Task ConnectAsync(ServerInfo? server = null, CancellationToken ct = default);
        Task DisconnectAsync();

        /// <summary>Brings the tunnel back at startup if the user left it on, or if they asked for auto-connect.</summary>
        Task TryRestoreOrAutoConnectAsync(CancellationToken ct = default);
    }
}
