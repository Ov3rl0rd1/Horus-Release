namespace Horus.Platforms.Windows.Tunnel
{
    /// <summary>What a trial pin of a socket's IPv6 to the physical interface said.</summary>
    public enum Ipv6Pin
    {
        /// <summary>The interface took it: the core can send IPv6 out past the tunnel.</summary>
        Works,

        /// <summary>
        /// The interface would not take it — <c>IPV6_UNICAST_IF</c> answered <c>WSAEINVAL</c>,
        /// which is what Windows says when IPv6 is unticked on the adapter (measured on a
        /// hosted runner: fine with the binding on, 10022 the moment it is taken off).
        /// </summary>
        Refused,

        /// <summary>No physical path was known to try it on.</summary>
        Unknown
    }

    /// <summary>
    /// Whether this session routes IPv6 into the tunnel.
    ///
    /// <para><b>Why it is not simply the user's setting.</b> Anything the rules send direct
    /// leaves by a socket the core pins to the physical interface. Where the IPv6 half of
    /// that socket cannot be pinned, a captured IPv6 destination has no way out: on 0.12.0
    /// the socket went unpinned, followed the only IPv6 route there was — the tunnel's — and
    /// came back in, a new socket each turn, until the machine had no ephemeral ports left
    /// (16 000 from one datagram in 80 seconds). The core now refuses such a socket instead,
    /// but a refused connection is still worse than none: the TUN has already completed the
    /// application's TCP handshake, so the application sees a reset rather than "no IPv6" and
    /// falls back to IPv4 late. Not capturing IPv6 at all gives it the answer it would get
    /// without the VPN — unreachable, at once.</para>
    ///
    /// <para><b>And blocked outside.</b> Not routed into the tunnel must not come to mean
    /// routed past it. Today the adapter has no IPv6, so nothing leaves; if the machine moves
    /// to a network that has it, IPv6 would go beside the VPN until the next connect. The
    /// core's own filter for exactly this ("misconfigtun": an IP version no route of which
    /// leads to the TUN) keeps it off every other interface for as long as the process lives.</para>
    ///
    /// <para>Pure: <c>Horus.Tests</c> pins it.</para>
    /// </summary>
    public static class Ipv6Capture
    {
        /// <summary>
        /// The session's settings: <paramref name="wanted"/>, unless it captures IPv6 and the
        /// physical interface will not take a pin. A user who turned capture off keeps exactly
        /// that — "past the VPN" is what the setting says, not "blocked".
        /// </summary>
        public static WindowsTunSettings Decide(WindowsTunSettings wanted, Ipv6Pin pin) =>
            wanted.CaptureIpv6 && pin == Ipv6Pin.Refused
                ? wanted with { CaptureIpv6 = false, BlockIpv6Outside = true }
                : wanted;

        /// <summary>One line for the log and the state screen.</summary>
        public static string Describe(WindowsTunSettings wanted, WindowsTunSettings decided, Ipv6Pin pin) =>
            (wanted.CaptureIpv6, decided.CaptureIpv6) switch
            {
                (true, true) => pin == Ipv6Pin.Unknown ? "captured (pin not tried: no physical path known)" : "captured",
                (true, false) => "not captured: it cannot be pinned to the physical interface (IPv6 is off on the adapter); blocked outside the tunnel",
                _ => "not captured (turned off in settings); it goes past the VPN"
            };
    }
}
