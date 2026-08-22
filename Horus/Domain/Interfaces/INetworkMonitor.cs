using Horus.Domain.Events;
using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    /// <summary>
    /// Watches the physical network underneath the tunnel.
    ///
    /// The distinction that matters: once the VPN is up it <i>is</i> the default network, so
    /// anything that asks the system "what am I connected to?" gets back the tunnel and
    /// learns nothing. Every implementation here deliberately looks past the VPN transport
    /// at the real link, because that is the one whose disappearance breaks things.
    ///
    /// This exists to replace polling. A Wi-Fi to mobile handover is an event the platform
    /// will tell us about in milliseconds; discovering it by noticing that traffic stopped
    /// takes tens of seconds and costs a timer running the whole time in between.
    /// </summary>
    public interface INetworkMonitor
    {
        /// <summary>The physical link changed identity — a handover, or the last one going away.</summary>
        event EventHandler<NetworkChangedEventArgs>? NetworkChanged;

        /// <summary>
        /// The operating system believes the tunnel is not carrying traffic. The string is
        /// the reason, and it ends up in the log.
        ///
        /// <para>Two sources feed this, and both are the platform's own verdict rather than
        /// a guess of ours. Android validates every network it manages — the VPN included —
        /// by fetching a known URL through it; when that stops working the network loses
        /// <c>NET_CAPABILITY_VALIDATED</c>. Separately, <c>ConnectivityDiagnosticsManager</c>
        /// reports suspected data stalls from TCP and DNS telemetry the system is already
        /// collecting. Neither costs a timer or a packet of ours.</para>
        /// </summary>
        event EventHandler<string>? TunnelSuspect;

        /// <summary>
        /// The user came back to the device — screen on, unlocked, or idle mode ending.
        ///
        /// The moment that matters for responsiveness. A tunnel dies unobserved during
        /// sleep and nobody is harmed until someone picks the phone up; checking then, and
        /// not on a timer through the night, is both faster where it counts and cheaper
        /// everywhere else.
        /// </summary>
        event EventHandler? DeviceWoke;

        /// <summary>
        /// The platform entered or left its idle state — Doze on Android. True on entering.
        ///
        /// <para>This is the only warning the app gets that nothing will be watching for a
        /// while, and the core has background loops that do not otherwise know. Distinct
        /// from <see cref="DeviceWoke"/>, which fires on the screen coming on: the device
        /// leaves Doze without anyone looking at it, and the screen comes on without the
        /// device ever having been in Doze.</para>
        /// </summary>
        event EventHandler<bool>? DeviceIdleChanged;

        /// <summary>Any usable non-VPN network right now.</summary>
        bool IsOnline { get; }

        /// <summary>What the current link is, for logs and for the reconnect decision.</summary>
        NetworkTransport Transport { get; }

        /// <summary>
        /// Tells the platform we suspect the tunnel is not carrying, which makes it
        /// re-validate immediately instead of on its own schedule. The answer arrives as
        /// <see cref="TunnelSuspect"/> — this turns a weak local hunch into an
        /// authoritative verdict within a second or two, without us probing anything.
        /// </summary>
        void ReportTunnelSuspect();

        void Start();
        void Stop();
    }
}
