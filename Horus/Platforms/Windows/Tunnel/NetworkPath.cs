namespace Horus.Platforms.Windows.Tunnel
{
    /// <summary>One default route as the route table reports it.</summary>
    /// <param name="InterfaceIndex">The interface the route leaves by.</param>
    /// <param name="Metric">Route metric plus interface metric — Windows' own cost.</param>
    /// <param name="IsUp">The interface is operational.</param>
    /// <param name="IsTunnel">Our own adapter. Never a valid answer: binding the core to it is the loop.</param>
    /// <param name="IsWifi">802.11. Only used to label the path for the user.</param>
    /// <param name="Gateway">Next hop, as text.</param>
    /// <param name="Name">Adapter alias, for logs and the UI.</param>
    public sealed record DefaultRoute(
        int InterfaceIndex, uint Metric, bool IsUp, bool IsTunnel, bool IsWifi, string Gateway, string Name);

    /// <summary>The path the machine's traffic takes to the internet when the VPN is not in the way.</summary>
    public sealed record NetworkPath(int InterfaceIndex, string Gateway, string Name, bool IsWifi)
    {
        public string Describe() => $"{Name} (#{InterfaceIndex}, gw {Gateway}{(IsWifi ? ", Wi-Fi" : string.Empty)})";
    }

    /// <summary>
    /// What counts as the physical path, and what counts as it changing.
    ///
    /// <para><b>The same answer the core gives.</b> The core pins its sockets to the
    /// interface with the lowest combined metric among the default routes that are not
    /// its own TUN (proxy/tun/tun_windows.go, as patched in the fork). Detecting a
    /// "handover" any other way means acting on changes the core does not care about —
    /// and the previous detector did exactly that: any adapter appearing or disappearing
    /// with a gateway (a phone tethered, a virtual switch, Wi-Fi flapping while Ethernet
    /// carried everything) was a "handover", and every handover reset the core's
    /// sessions. On Hysteria2 that drops every TCP connection — measured on the bench,
    /// all of them, every time. That is a player kicked from a match for a Wi-Fi blip on a
    /// cable-connected PC.</para>
    ///
    /// <para>Pure, so <c>Horus.Tests</c> pins it.</para>
    /// </summary>
    public static class NetworkPathSelector
    {
        public static NetworkPath? Select(IEnumerable<DefaultRoute> routes)
        {
            DefaultRoute? best = null;
            foreach (var r in routes)
            {
                if (!r.IsUp || r.IsTunnel) continue;
                if (best is null || r.Metric < best.Metric) best = r;
            }
            return best is null ? null : new NetworkPath(best.InterfaceIndex, best.Gateway, best.Name, best.IsWifi);
        }

        /// <summary>What a change of path means for the connections already open.</summary>
        public enum Change
        {
            /// <summary>Same interface, same next hop. Nothing to do.</summary>
            None,

            /// <summary>Offline now. Nothing can be done until a path exists.</summary>
            Lost,

            /// <summary>Back from offline. Sessions from before are dead; rebuild.</summary>
            Restored,

            /// <summary>
            /// A different path is preferred, but the old one is still up. Open connections
            /// keep working over it; only new ones take the new path. Dropping them would be
            /// pure damage.
            /// </summary>
            Preferred,

            /// <summary>The old path is gone. Everything bound to it is dead; reset and rebuild.</summary>
            Replaced
        }

        /// <param name="before">The path the core was bound to.</param>
        /// <param name="after">The path it would choose now.</param>
        /// <param name="stillUp">Whether the interface of <paramref name="before"/> is still up with a default route.</param>
        public static Change Classify(NetworkPath? before, NetworkPath? after, bool stillUp)
        {
            if (after is null) return before is null ? Change.None : Change.Lost;
            if (before is null) return Change.Restored;
            if (before.InterfaceIndex == after.InterfaceIndex && before.Gateway == after.Gateway) return Change.None;

            // Same interface, new next hop: a DHCP renewal onto another router, or a roam to
            // an access point on another subnet. The address the sessions were bound to is
            // usually gone with it, so treat it as replaced.
            if (before.InterfaceIndex == after.InterfaceIndex) return Change.Replaced;

            return stillUp ? Change.Preferred : Change.Replaced;
        }
    }
}
