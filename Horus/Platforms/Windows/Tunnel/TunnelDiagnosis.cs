using System.Net;
using System.Net.Sockets;

namespace Horus.Platforms.Windows.Tunnel
{
    /// <summary>
    /// Whether the system can still give a socket a port.
    ///
    /// <para>Windows hands out local ports from 49152–65535 — 16 384 of them — and a socket
    /// keeps its port until it is closed (a closed TCP one for two more minutes). When they
    /// are gone, <c>bind</c> answers <c>WSAENOBUFS</c> and no program on the machine can open
    /// a new connection: not the browser, not the DNS client, not this app's own probes.
    /// Connections that are already open carry on, which is why the tunnel's own health check
    /// saw nothing — the proxy's connections to the node were open — while the user saw the
    /// internet stop. Asking for a port is the only way to know.</para>
    /// </summary>
    public static class EphemeralPorts
    {
        /// <summary>Windows' default dynamic range, 49152–65535 (<c>netsh int ipv4 show dynamicport</c>).</summary>
        public const int DefaultRangeStart = 49_152;
        public const int DefaultRangeEnd = 65_535;
        public const int DefaultRangeSize = DefaultRangeEnd - DefaultRangeStart + 1;

        /// <summary>
        /// Share of the range in use from which it counts as used up. Windows refuses before the
        /// last port: on a hosted runner bind failed with 16 365 of 16 384 in use on one run and
        /// with 16 083 (98 %) on the next — part of the range is excluded on many machines
        /// (Hyper-V, WSL and Docker reserve blocks of it; <c>netsh int ipv4 show excludedportrange</c>),
        /// which is the likely reason, not a measured one.
        /// </summary>
        public const double ExhaustedShare = 0.97;

        /// <summary>Share of the range in use from which it is worth saying so.</summary>
        public const double LowShare = 0.85;

        public static bool IsExhaustion(SocketError error) => error == SocketError.NoBufferSpaceAvailable;

        /// <summary>
        /// How close a pool is to running out: by the ports in use, or by a bind that was refused.
        ///
        /// <para>The count is what can be relied on. A bind that succeeds proves little where
        /// ports are being eaten — some system socket frees one now and then and the probe can
        /// win it; on a hosted runner one did right after the 16 365th socket was refused. The
        /// refused bind still counts on its own: the count assumes the default range, and a
        /// machine set to a narrower one runs out sooner.</para>
        /// </summary>
        public static PortPressure Assess(int inUse, int rangeSize, bool bindRefused)
        {
            if (bindRefused || inUse >= rangeSize * ExhaustedShare) return PortPressure.Exhausted;
            return inUse >= rangeSize * LowShare ? PortPressure.Low : PortPressure.Ok;
        }

        /// <summary>Distinct ports within the default dynamic range — each counted once, whatever table listed it.</summary>
        public static int CountInRange(IEnumerable<int> ports) =>
            ports.Where(p => p is >= DefaultRangeStart and <= DefaultRangeEnd).Distinct().Count();

        /// <summary>Both pools, judged from the ports in use (from the system's socket tables) and a bind each.</summary>
        public static PortReport Judge(IEnumerable<int> udpPorts, IEnumerable<int> tcpPorts)
        {
            var refused = Probe();
            int udp = CountInRange(udpPorts), tcp = CountInRange(tcpPorts);
            return new PortReport(
                Assess(udp, DefaultRangeSize, refused.HasFlag(PortShortage.Udp)), udp,
                Assess(tcp, DefaultRangeSize, refused.HasFlag(PortShortage.Tcp)), tcp);
        }

        /// <summary>Whether either pool is used up. UDP and TCP ports are handed out separately.</summary>
        public static bool Exhausted() => Probe() != PortShortage.None;

        /// <summary>Binds a UDP and a TCP socket to any port and lets them go.</summary>
        public static PortShortage Probe() =>
            (CanBind(SocketType.Dgram, ProtocolType.Udp) ? PortShortage.None : PortShortage.Udp)
            | (CanBind(SocketType.Stream, ProtocolType.Tcp) ? PortShortage.None : PortShortage.Tcp);

        private static bool CanBind(SocketType type, ProtocolType protocol)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, type, protocol);
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                return true;
            }
            catch (SocketException ex) { return !IsExhaustion(ex.SocketErrorCode); }
            catch (Exception) { return true; }   // anything else is not this
        }
    }

    /// <summary>Both pools of local ports: how close each is to running out, and how many of the default range are in use.</summary>
    public sealed record PortReport(PortPressure Udp, int UdpInUse, PortPressure Tcp, int TcpInUse)
    {
        public PortPressure Worst => Udp > Tcp ? Udp : Tcp;

        public string Describe() =>
            $"UDP {UdpInUse} of {EphemeralPorts.DefaultRangeSize} in use ({Udp}), TCP {TcpInUse} ({Tcp})";
    }

    /// <summary>How close a pool of local ports is to running out.</summary>
    public enum PortPressure
    {
        Ok,

        /// <summary>Most of the range is in use. Nothing fails yet; worth a line in the log.</summary>
        Low,

        /// <summary>No port to give: no new connection of that kind can be opened on the machine.</summary>
        Exhausted
    }

    /// <summary>Which of the two port pools refused a bind.</summary>
    [Flags]
    public enum PortShortage
    {
        None = 0,
        Udp = 1,
        Tcp = 2
    }

    /// <summary>The tunnel's connections in one line, for a log entry written when something is wrong.</summary>
    public static class ConnectionSummary
    {
        public static string Busiest(IReadOnlyList<CoreConnection> connections, int top = 3)
        {
            if (connections.Count == 0) return "none open";

            var groups = connections
                .GroupBy(c => (c.Network, c.Destination, Outbound: c.Outbound ?? "?"))
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key.Destination, StringComparer.Ordinal)
                .Take(top)
                .Select(g => $"{g.Key.Network} {g.Key.Destination} x{g.Count()} ({g.Key.Outbound})");

            return $"{connections.Count} open; {string.Join(", ", groups)}";
        }
    }

    /// <summary>
    /// What it means when no node answers a ping sent from the physical interface.
    ///
    /// <para>The server list pings from sockets pinned to the physical interface, past the
    /// tunnel. "0 of N answered" used to read as "the nodes are down" — but with the tunnel up
    /// and the proxy carrying traffic, the node the proxy runs on is plainly answering. Then
    /// the silence is this machine's: the probe could not get a socket out.</para>
    /// </summary>
    public static class PingDiagnosis
    {
        /// <summary>What to log, or null when the sweep says nothing unusual.</summary>
        public static string? Explain(int probed, int answered, bool tunnelUp, bool proxyHealthy, bool portsExhausted)
        {
            if (probed <= 0 || answered > 0 || !tunnelUp || !proxyHealthy) return null;

            return portsExhausted
                ? $"none of {probed} node(s) answered, and the proxy is working: this machine has (nearly) no free ports left " +
                  "(WSAENOBUFS), so the probe could not open a socket. Nothing on the machine can open a new connection until ports free up"
                : $"none of {probed} node(s) answered from the physical interface, and the proxy is working: " +
                  "the nodes are not the problem — sockets pinned to the physical interface are not getting out (a firewall, or the interface has changed)";
        }
    }
}
