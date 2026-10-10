using System.Net.Sockets;
using System.Text;

namespace Horus.Platforms.Windows.Tunnel
{
    /// <summary>One thing to ask for through the proxy: plain HTTP, any answer will do.</summary>
    public readonly record struct ProbeTarget(string Host, int Port = 80, string Path = "/");

    /// <summary>
    /// Whether the proxy carries traffic, asked through the core's SOCKS inbound with a
    /// request only the far end can answer.
    ///
    /// <para><b>Why not the SOCKS reply.</b> xray's SOCKS server answers "succeeded" as
    /// soon as it has parsed the request, before the outbound dials anything
    /// (<c>proxy/socks/protocol.go</c>, <c>handshake5</c>). A probe that stopped at the
    /// reply — <c>SocksProbe</c> — therefore passed every outbound, dead or alive: a
    /// Hysteria2 that carried nothing was accepted at connect, shown as connected, and
    /// every later health check agreed with it.</para>
    ///
    /// <para>So the probe sends an HTTP request through the tunnel and wants the first
    /// line of a response back. Plain HTTP on purpose: it needs no certificate and no
    /// TLS stack, and any status line — 204, 301, 403 — is proof of a round trip through
    /// the node, which is the only question asked.</para>
    /// </summary>
    public static class SocksRoundTrip
    {
        private const byte Version = 0x05;
        private const byte NoAuth = 0x00;
        private const byte CmdConnect = 0x01;
        private const byte AddrIPv4 = 0x01;
        private const byte AddrDomain = 0x03;
        private const byte AddrIPv6 = 0x04;
        private const byte ReplySucceeded = 0x00;

        /// <summary>
        /// Small, cheap, long-lived answers on two operators, asked at once. The IP literal
        /// keeps the answer meaningful when the far end cannot resolve names.
        /// </summary>
        public static readonly ProbeTarget[] DefaultTargets =
        [
            new("cp.cloudflare.com", 80, "/generate_204"),
            new("www.gstatic.com", 80, "/generate_204"),
            new("1.1.1.1", 80, "/"),
        ];

        /// <summary>
        /// True as soon as any target answers; false once all have failed. The targets are
        /// asked at once, so a dead proxy costs one timeout rather than one per target.
        /// </summary>
        public static async Task<bool> AnyCarriesAsync(
            int socksPort, IReadOnlyList<ProbeTarget> targets, TimeSpan timeout, CancellationToken ct)
        {
            using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pending = targets.Select(t => CarriesAsync(socksPort, t, timeout, done.Token)).ToList();
            try
            {
                while (pending.Count > 0)
                {
                    var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(finished);
                    if (await finished.ConfigureAwait(false)) return true;
                }
                return false;
            }
            finally
            {
                done.Cancel();   // the rest are no longer needed
            }
        }

        /// <summary>
        /// True when an HTTP status line came back from <paramref name="target"/> through
        /// the proxy. Never throws: refused, timed out, closed, malformed — all "no".
        /// </summary>
        public static async Task<bool> CarriesAsync(
            int socksPort, ProbeTarget target, TimeSpan timeout, CancellationToken ct)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);

                using var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync("127.0.0.1", socksPort, cts.Token).ConfigureAwait(false);
                var stream = client.GetStream();

                await stream.WriteAsync(new byte[] { Version, 0x01, NoAuth }, cts.Token).ConfigureAwait(false);
                var greeting = new byte[2];
                await ReadExactlyAsync(stream, greeting, cts.Token).ConfigureAwait(false);
                if (greeting[0] != Version || greeting[1] != NoAuth) return false;

                await stream.WriteAsync(ConnectRequest(target.Host, target.Port), cts.Token).ConfigureAwait(false);

                // The reply is read whole — header and bound address — so the bytes that
                // follow are the far end's and nothing of the SOCKS exchange is mistaken
                // for an answer.
                var head = new byte[4];
                await ReadExactlyAsync(stream, head, cts.Token).ConfigureAwait(false);
                if (head[0] != Version || head[1] != ReplySucceeded) return false;
                var rest = head[3] switch
                {
                    AddrIPv4 => 4 + 2,
                    AddrIPv6 => 16 + 2,
                    AddrDomain => await ReadByteAsync(stream, cts.Token).ConfigureAwait(false) + 2,
                    _ => -1
                };
                if (rest < 0) return false;
                await ReadExactlyAsync(stream, new byte[rest], cts.Token).ConfigureAwait(false);

                var request = Encoding.ASCII.GetBytes(
                    $"GET {target.Path} HTTP/1.1\r\nHost: {target.Host}\r\nUser-Agent: Horus\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(request, cts.Token).ConfigureAwait(false);

                var status = new byte[7];
                await ReadExactlyAsync(stream, status, cts.Token).ConfigureAwait(false);
                return Encoding.ASCII.GetString(status) == "HTTP/1.";
            }
            catch
            {
                return false;
            }
        }

        /// <summary>CONNECT to a domain (an IP literal goes as one too; the core accepts both).</summary>
        internal static byte[] ConnectRequest(string host, int port)
        {
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
            return request;
        }

        private static async Task<int> ReadByteAsync(NetworkStream stream, CancellationToken ct)
        {
            var one = new byte[1];
            await ReadExactlyAsync(stream, one, ct).ConfigureAwait(false);
            return one[0];
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
