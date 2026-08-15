namespace Horus.Domain.Models
{
    /// <summary>The kind of physical link carrying the tunnel.</summary>
    public enum NetworkTransport
    {
        None = 0,
        Wifi,
        Cellular,
        Ethernet,
        Other
    }

    /// <summary>
    /// How healthy the tunnel is, and — when it is not — which part failed. The whole point
    /// of separating these is that they need opposite responses: a dead outbound should
    /// move to the next protocol, a dead core or bridge should be restarted in place, and
    /// no internet at all should do nothing whatsoever until the network comes back.
    /// Treating them alike is what produces a client that reconnects in a loop on a train.
    /// </summary>
    public enum TunnelHealth
    {
        /// <summary>Traffic is flowing, or the device is simply idle. Nothing to do.</summary>
        Healthy = 0,

        /// <summary>xray stopped. Restart the whole connection.</summary>
        CoreDead,

        /// <summary>The TUN or the SOCKS bridge is gone — usually the system reclaiming it.</summary>
        TunnelDead,

        /// <summary>
        /// The device has no working link at all. The tunnel is not at fault and
        /// reconnecting cannot help; wait for the network to come back.
        /// </summary>
        NoInternet,

        /// <summary>
        /// The link is fine and the core is running, but nothing comes back through the
        /// proxy: the node or this protocol is not working here. Try the next protocol.
        /// </summary>
        OutboundDead
    }
}
