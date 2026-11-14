using System.Net;

namespace Horus.Platforms.Windows.Tunnel
{
    /// <summary>Many live TUN connections to one destination, all from this process's own sockets.</summary>
    public sealed record LoopStorm(string Network, string Destination, IReadOnlyList<ulong> ConnectionIds)
    {
        public int Count => ConnectionIds.Count;
    }

    /// <summary>A destination the tunnel drops for the rest of the session.</summary>
    /// <param name="Network"><c>tcp</c> or <c>udp</c>.</param>
    /// <param name="Address">An IP address, without brackets.</param>
    public sealed record BlockedEndpoint(string Network, string Address, int Port)
    {
        /// <summary>
        /// From a connection as the core lists it (<c>1.2.3.4:443</c>, <c>[2600::1]:443</c>).
        /// Null for anything a routing rule could not name — the TUN only ever sees addresses.
        /// </summary>
        public static BlockedEndpoint? Parse(string network, string destination)
        {
            if (network is not ("tcp" or "udp")) return null;
            var host = AppTrafficGrouping.HostOf(destination);
            var port = CoreConnection.PortOf(destination);
            if (host == destination || port is <= 0 or > 65535) return null;
            return IPAddress.TryParse(host, out _) ? new BlockedEndpoint(network, host, port) : null;
        }
    }

    /// <summary>
    /// Sees, from the core's connection list, a loop the core itself did not stop.
    ///
    /// <para><b>What a loop looks like from here.</b> A socket of the core's that is not held
    /// to the physical interface follows the routes into the TUN; the TUN takes it for an
    /// application's connection and sends it out again on a new socket, which comes back the
    /// same way. Every turn is one more live connection to the same destination, and its
    /// source is a socket of this very process — the core runs inside it. On a user's machine
    /// that was 16 000 of them from one datagram.</para>
    ///
    /// <para><b>Why both conditions.</b> Many connections to one place is what a download
    /// manager does; a connection from our own process is what the app's own API call is.
    /// Many from our own process to one place is neither.</para>
    ///
    /// <para>The core drops such connections on arrival now (fork <c>proxy/tun/fork_loop.go</c>),
    /// so they never reach this list. This is for the loop that gets past it — a socket opened
    /// outside the core's dialer, or a core built before the check — and it costs a dictionary
    /// per health sample.</para>
    ///
    /// <para>Pure: <c>Horus.Tests</c> pins it.</para>
    /// </summary>
    public static class LoopStormDetector
    {
        /// <summary>
        /// Live connections to one endpoint before the owners are looked at. Browsers hold
        /// six per host and download managers a few dozen; a loop passes this in a second.
        /// </summary>
        public const int Threshold = 64;

        /// <param name="fromOurOwnSocket">
        /// Whether a connection's source socket belongs to this process. Only called for
        /// destinations already over the threshold: answering reads two system tables.
        /// </param>
        public static IReadOnlyList<LoopStorm> Find(
            IReadOnlyList<CoreConnection> connections, Func<CoreConnection, bool> fromOurOwnSocket, int threshold = Threshold)
        {
            if (connections.Count < threshold) return [];

            var byEndpoint = new Dictionary<(string Network, string Destination), List<CoreConnection>>();
            foreach (var c in connections)
            {
                var key = (c.Network, c.Destination);
                if (!byEndpoint.TryGetValue(key, out var list)) byEndpoint[key] = list = [];
                list.Add(c);
            }

            var storms = new List<LoopStorm>();
            foreach (var ((network, destination), list) in byEndpoint)
            {
                if (list.Count < threshold) continue;
                var ours = list.Where(fromOurOwnSocket).Select(c => c.Id).ToList();
                if (ours.Count >= threshold) storms.Add(new LoopStorm(network, destination, ours));
            }
            return storms;
        }
    }
}
