namespace Horus.Domain.Interfaces
{
    /// <summary>
    /// Supplies the network interface a <c>direct</c> socket has to be pinned to for it to
    /// actually leave the machine.
    ///
    /// <para><b>Registered only where the answer is not "obviously nothing".</b> On Android
    /// the core's sockets already escape the tunnel because the app's whole UID is excluded,
    /// so there is nothing to pin and no implementation. On Windows there is no such notion:
    /// <c>direct</c> is the OS route table, and once <c>0.0.0.0/1</c> and <c>128.0.0.0/1</c>
    /// point at the TUN a "direct" packet is handed straight back to the tunnel, where hev
    /// returns it to the SOCKS5 inbound and routing sends it out <c>direct</c> again. That
    /// loop is unbounded and costs a session per turn.</para>
    ///
    /// <para>The escape hatch that scales is a socket option rather than a route: xray's
    /// <c>sockopt.interface</c> becomes <c>IP_UNICAST_IF</c> on Windows, which overrides the
    /// route table for that socket alone. One string in the config replaces what would
    /// otherwise be a host route per destination — and <c>geoip:ru</c> is 23 000 prefixes,
    /// so the host-route answer that serves the node does not generalise.</para>
    ///
    /// <para>Callers must treat a null answer as "direct cannot be made direct here" and
    /// leave geo rules out of the config entirely, rather than emitting them and hoping.</para>
    /// </summary>
    public interface IDirectPathProvider
    {
        /// <summary>
        /// The interface name to pin <c>direct</c> to, or null when it cannot be determined.
        ///
        /// <para><b>Must be called before the tunnel exists.</b> It answers by asking the
        /// OS how it would currently reach the internet, so once the TUN holds the default
        /// route the honest answer becomes the tunnel itself. The connect path calls it
        /// while building the config, which is before <c>XrayStart</c> and well before the
        /// TUN — the same timing the bypass routes depend on.</para>
        /// </summary>
        string? ResolveDirectInterface();
    }
}
