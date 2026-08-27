namespace Horus.Protocols
{
    /// <summary>
    /// The address ranges that must not be carried by the tunnel, in one place because two
    /// different layers need the same answer and they used to disagree.
    ///
    /// <para><b>Two layers, two jobs.</b> <see cref="Direct"/> is what the core is told to
    /// send out through <c>freedom</c> instead of the proxy. <see cref="ExcludedFromTunnel"/>
    /// is the narrower set that is kept out of the TUN's routes entirely, so those packets
    /// never enter the tunnel in the first place.</para>
    ///
    /// <para><b>Why the second one matters even though the first exists.</b> A LAN packet
    /// that enters the TUN and is then sent <c>direct</c> does eventually reach the network —
    /// but it leaves from the app's own socket, so its source address is ours and not the
    /// original one. Anything that answers to the sender (a printer, a NAS, a router's admin
    /// page behind a same-subnet check) sees a connection from the wrong place. Keeping the
    /// range out of the routes avoids the round trip and preserves the source.</para>
    ///
    /// <para><b>Why the two sets are not identical.</b> Carrier-grade NAT
    /// (<c>100.64.0.0/10</c>) is a destination worth sending direct — it is where a
    /// Tailscale peer or a carrier service lives — but it is also the space a mobile
    /// operator hands out to the phone itself, so excluding it from the tunnel's routes
    /// could take real traffic outside the VPN. It is direct, not excluded. The same
    /// caution applies to the reserved ranges below.</para>
    /// </summary>
    public static class LocalNetworks
    {
        /// <summary>
        /// Everything the core routes through <c>freedom</c> rather than the node.
        ///
        /// Beyond the obvious RFC 1918 blocks this includes the tunnel's own subnet
        /// (<c>198.18.0.0/15</c>, which is where <c>198.18.0.1</c> lives): without it a
        /// packet addressed to our own TUN address would be handed to the proxy and sent to
        /// the node, which is both wrong and a small information leak about the client.
        /// </summary>
        public static readonly string[] Direct =
        [
            // Loopback and this-network.
            "0.0.0.0/8", "127.0.0.0/8",

            // RFC 1918 private space.
            "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",

            // Link-local, including the APIPA range and IPv4 multicast's neighbours.
            "169.254.0.0/16",

            // Carrier-grade NAT. Also Tailscale's default range.
            "100.64.0.0/10",

            // IETF protocol assignments, and the benchmarking block the TUN itself sits in.
            "192.0.0.0/24", "198.18.0.0/15",

            // Reserved. Nothing routable is here, and proxying it only wastes a session.
            "240.0.0.0/4",

            "::1/128", "fc00::/7", "fe80::/10"
        ];

        /// <summary>
        /// Ranges kept out of the tunnel's routes, so LAN traffic never enters it.
        ///
        /// Deliberately conservative: only scopes that are unambiguously local to the
        /// device's own network. Anything a user might actually browse to must stay inside
        /// the tunnel, and an over-broad entry here is a silent leak rather than a visible
        /// failure — which is why the reserved and carrier ranges above are not repeated.
        /// </summary>
        public static readonly string[] ExcludedFromTunnel =
        [
            "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16"
        ];

        /// <summary>
        /// IPv6 counterpart of <see cref="ExcludedFromTunnel"/>, kept separate because the
        /// platform APIs that consume it take a family-specific prefix.
        /// </summary>
        public static readonly string[] ExcludedFromTunnelV6 =
        [
            "fc00::/7", "fe80::/10"
        ];

        /// <summary>Splits "10.0.0.0/8" into its address and prefix length.</summary>
        public static (string Address, int Prefix) Split(string cidr)
        {
            var slash = cidr.IndexOf('/');
            return slash < 0
                ? (cidr, cidr.Contains(':') ? 128 : 32)
                : (cidr[..slash], int.Parse(cidr[(slash + 1)..]));
        }
    }
}
