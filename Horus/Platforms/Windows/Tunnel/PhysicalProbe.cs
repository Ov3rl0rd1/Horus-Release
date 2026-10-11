using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Horus.Platforms.Windows.Tunnel
{
    /// <summary>
    /// A TCP handshake timed from a socket pinned to the physical interface.
    ///
    /// <para><b>Why pinned.</b> While the tunnel is up the default route points into the
    /// TUN, and the core's TUN stack completes a TCP handshake itself before it has even
    /// dialled the destination. An unpinned probe therefore "answers" in about a
    /// millisecond for every server, whatever its distance — the tunnel measuring itself.
    /// <c>IP_UNICAST_IF</c> sends the socket out of the named interface regardless of the
    /// route table, which is what the core does for its own sockets.</para>
    /// </summary>
    public static class PhysicalProbe
    {
        private const int IpUnicastIf = 31;   // IP_UNICAST_IF and IPV6_UNICAST_IF share the value

        /// <summary>
        /// Milliseconds to complete the handshake, or null without an answer.
        /// </summary>
        /// <param name="ifIndex">Interface to leave by; 0 lets the route table decide.</param>
        /// <param name="refusedIsAnswer">
        /// Count a reset as a sample. It is one round trip just the same, which matters when
        /// the probed port only serves UDP; a server-list probe wants an open port instead.
        /// </param>
        public static async Task<int?> ConnectAsync(
            string host, int port, int ifIndex, TimeSpan timeout, bool refusedIsAnswer, CancellationToken ct)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(timeout);

            IPAddress ip;
            try
            {
                ip = await ResolveAsync(host, attempt.Token).ConfigureAwait(false) ?? throw new SocketException((int)SocketError.HostNotFound);
            }
            catch (Exception) { return null; }

            using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            if (ifIndex > 0) Pin(socket, ip.AddressFamily, ifIndex);

            var clock = Stopwatch.StartNew();
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, port), attempt.Token).ConfigureAwait(false);
            }
            catch (SocketException ex) when (refusedIsAnswer && ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                // A reset came back: one round trip.
            }
            catch (Exception)
            {
                return null;   // timed out, unreachable, refused, or the caller gave up
            }
            return Math.Max(1, (int)clock.ElapsedMilliseconds);
        }

        private static void Pin(Socket socket, AddressFamily family, int ifIndex)
        {
            try
            {
                if (family == AddressFamily.InterNetwork)
                    // IPv4 takes the index in network byte order, IPv6 in host order.
                    socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IpUnicastIf, IPAddress.HostToNetworkOrder(ifIndex));
                else
                    socket.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)IpUnicastIf, ifIndex);
            }
            catch (SocketException ex)
            {
                Debug.WriteLine($"[Horus] probe pin {ifIndex}: {ex.SocketErrorCode}");
            }
        }

        /// <summary>The first IPv4 address, else the first IPv6 one; a literal is returned as is.</summary>
        private static async Task<IPAddress?> ResolveAsync(string host, CancellationToken ct)
        {
            if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) return literal;
            var all = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
        }
    }
}
