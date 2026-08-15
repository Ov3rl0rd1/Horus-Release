using System.Net.Sockets;
using System.Text;

namespace Horus.Protocols
{
    /// <summary>
    /// Asks the core's own SOCKS5 inbound to dial something, and reports whether it could.
    ///
    /// <para>This exists because the previous liveness proof — fetching our egress IP
    /// through the proxy — depends on our own API being reachable, and for these users it
    /// frequently is not. When both the direct and the proxied fetch failed, the connect
    /// path concluded "inconclusive" and accepted the protocol, which is how a provably
    /// dead tunnel reached the user with ЗАЩИЩЕНО on screen.</para>
    ///
    /// <para>A SOCKS5 CONNECT needs nothing of ours. The core answers with a reply code
    /// that says exactly whether it managed to reach the target: <c>0x00</c> succeeded,
    /// anything else is the core telling us it could not — host unreachable, network
    /// unreachable, or, in the failure this was written for, a name it cannot resolve.
    /// The request is sent as a <i>domain</i> deliberately, so the answer also covers the
    /// core's ability to resolve, which is the half that silently breaks.</para>
    /// </summary>
    public static class SocksProbe
    {
        private const byte Version = 0x05;
        private const byte NoAuth = 0x00;
        private const byte CmdConnect = 0x01;
        private const byte AddrDomain = 0x03;
        private const byte ReplySucceeded = 0x00;

        /// <summary>
        /// True when the core reported a successful CONNECT. Never throws: every failure
        /// mode — refused, timed out, malformed — is the same answer, "it could not".
        /// </summary>
        public static async Task<bool> CanDialAsync(
            int socksPort, string host, int port, TimeSpan timeout, CancellationToken ct)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);

                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", socksPort, cts.Token).ConfigureAwait(false);

                var stream = client.GetStream();

                // Greeting: one method, no authentication.
                await stream.WriteAsync(new byte[] { Version, 0x01, NoAuth }, cts.Token).ConfigureAwait(false);

                var greeting = new byte[2];
                await ReadExactlyAsync(stream, greeting, cts.Token).ConfigureAwait(false);
                if (greeting[0] != Version || greeting[1] != NoAuth) return false;

                // CONNECT to a domain, so the reply also reflects whether the core can resolve.
                var name = Encoding.ASCII.GetBytes(host);
                var request = new byte[7 + name.Length];
                request[0] = Version;
                request[1] = CmdConnect;
                request[2] = 0x00;
                request[3] = AddrDomain;
                request[4] = (byte)name.Length;
                name.CopyTo(request, 5);
                request[5 + name.Length] = (byte)(port >> 8);
                request[6 + name.Length] = (byte)(port & 0xFF);

                await stream.WriteAsync(request, cts.Token).ConfigureAwait(false);

                // Only the reply code matters; the bound address that follows is not read.
                var reply = new byte[4];
                await ReadExactlyAsync(stream, reply, cts.Token).ConfigureAwait(false);

                return reply[0] == Version && reply[1] == ReplySucceeded;
            }
            catch
            {
                return false;
            }
        }

        private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
                if (n == 0) throw new IOException("The proxy closed the connection.");
                read += n;
            }
        }
    }
}
