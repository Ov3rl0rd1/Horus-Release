namespace Horus.Domain.Models
{
    public class TunnelOptions
    {
        public string TunAddress { get; set; }   // 172.19.0.1
        public int TunPrefix { get; set; }   // 30
        public int Mtu { get; set; }   // 1500
        public string[] DnsServers { get; set; }
        public string[] BypassApps { get; set; }   // Android per-app VPN
        public bool AllTraffic { get; set; }

        /// <summary>
        /// Literal IPs that must keep reaching the internet off-tunnel — in practice the
        /// VPN node the core is dialling.
        ///
        /// This is the Windows counterpart of excluding the app's own UID on Android. The
        /// core runs inside this process, so once a default route points at the TUN its
        /// socket to the node is routed back into the tunnel it is carrying, and the whole
        /// thing deadlocks. Android solves it per-app; Windows has no such notion, so the
        /// escape hatch is a host route per address, installed before the default route.
        ///
        /// Ignored on Android, where the UID exclusion already covers it.
        /// </summary>
        public string[] BypassIps { get; set; } = [];

        /// <summary>
        /// The node's literal IP, or null when it could not be resolved before connecting.
        /// Called out separately from <see cref="BypassIps"/> because its absence is fatal
        /// on such a platform while a missing resolver entry merely degrades DNS: without a
        /// host route to the node there is no working tunnel to build at all.
        /// </summary>
        public string? NodeAddress { get; set; }

        /// <summary>
        /// Loopback port the TUN bridge dials — the same one the core's SOCKS5 inbound was
        /// rendered with. Carried here rather than read from a constant so that both ends
        /// move together when the conventional port is already taken.
        /// </summary>
        public int SocksPort { get; set; } = XrayConfig.DefaultSocksPort;
    }
}
