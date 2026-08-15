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
        /// The operating system has decided the tunnel itself no longer reaches the
        /// internet.
        ///
        /// This is the one genuine push signal for a dead tunnel. Android validates every
        /// network it manages, the VPN included, by fetching a known URL through it; when
        /// that stops working the network loses <c>NET_CAPABILITY_VALIDATED</c> and this
        /// fires. No timer, no probe of ours, and the verdict is the platform's own.
        /// </summary>
        event EventHandler? TunnelValidationLost;

        /// <summary>
        /// The user came back to the device — screen on, unlocked, or idle mode ending.
        ///
        /// The moment that matters for responsiveness. A tunnel dies unobserved during
        /// sleep and nobody is harmed until someone picks the phone up; checking then, and
        /// not on a timer through the night, is both faster where it counts and cheaper
        /// everywhere else.
        /// </summary>
        event EventHandler? DeviceWoke;

        /// <summary>Any usable non-VPN network right now.</summary>
        bool IsOnline { get; }

        /// <summary>What the current link is, for logs and for the reconnect decision.</summary>
        NetworkTransport Transport { get; }

        /// <summary>
        /// Tells the platform we suspect the tunnel is not carrying, which makes it
        /// re-validate immediately instead of on its own schedule. The answer arrives as
        /// <see cref="TunnelValidationLost"/> — this turns a weak local hunch into an
        /// authoritative verdict within a second or two, without us probing anything.
        /// </summary>
        void ReportTunnelSuspect();

        void Start();
        void Stop();
    }
}
