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

        /// <summary>Any usable non-VPN network right now.</summary>
        bool IsOnline { get; }

        /// <summary>What the current link is, for logs and for the reconnect decision.</summary>
        NetworkTransport Transport { get; }

        void Start();
        void Stop();
    }
}
