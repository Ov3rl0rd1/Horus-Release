using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Horus.Domain.Models;

namespace Horus.Platforms.Windows.Tunnel
{
    /// <summary>
    /// Which application each tunnelled connection belongs to, and a way to make it
    /// reconnect.
    ///
    /// <para>The core reports each connection with the source address of the application's
    /// own socket — the TUN address and a local port. Windows' TCP and UDP owner tables map
    /// that port to the process holding it. Both are read once per refresh, so a screen
    /// listing hundreds of connections costs two table reads, not hundreds.</para>
    ///
    /// <para>"Restart" closes the connections in the core; the application sees them end and
    /// reconnects, and the new connections are routed under whatever the rules are now. That
    /// is what makes a changed split-tunnel rule visible without restarting anything.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsConnectionMonitor
    {
        private static readonly TimeSpan IdentityTtl = TimeSpan.FromMinutes(2);

        private readonly Dictionary<int, (ProcessIdentity Who, DateTime At)> _identities = [];
        private readonly object _gate = new();

        /// <summary>False with a core built before connection listing existed.</summary>
        public bool IsSupported => XrayLive.IsSupported;

        /// <summary>The live connections, unparsed into applications. Empty when unsupported.</summary>
        public IReadOnlyList<CoreConnection> Connections() => CoreConnection.Parse(XrayLive.Connections());

        public IReadOnlyList<AppTraffic> Snapshot()
        {
            var connections = Connections();
            if (connections.Count == 0) return [];

            var owners = OwnerTable.Read();
            return AppTrafficGrouping.Group(connections, c =>
            {
                var key = (c.Network == "tcp", c.Source.StartsWith('['), c.SourcePort);
                return owners.TryGetValue(key, out var pid) ? Identify(pid) : null;
            });
        }

        /// <summary>Closes an application's connections so it reconnects under the current rules.</summary>
        public int Restart(AppTraffic app)
        {
            var closed = XrayLive.CloseConnections(app.ConnectionIds);
            Diag.Info("connections", $"restarted {app.Name}: {closed} connection(s) closed", userAction: true);
            return closed;
        }

        private ProcessIdentity? Identify(int pid)
        {
            var now = DateTime.UtcNow;
            lock (_gate)
            {
                if (_identities.TryGetValue(pid, out var cached) && now - cached.At < IdentityTtl)
                    return cached.Who;
            }

            var path = ImagePath(pid);
            var who = path is null
                ? (pid == 4 ? new ProcessIdentity(pid, "System", null) : null)
                : new ProcessIdentity(pid, System.IO.Path.GetFileName(path), path);

            if (who is not null)
                lock (_gate) _identities[pid] = (who, now);
            return who;
        }

        private static string? ImagePath(int pid)
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(1024);
                var size = buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
            }
            finally { CloseHandle(handle); }
        }

        /// <summary>
        /// (is TCP, is IPv6, local port) → owning pid, for sockets bound to the TUN address or to any
        /// address. Row layouts are MIB_TCPROW_OWNER_PID (24 bytes), MIB_UDPROW_OWNER_PID
        /// (12), and their IPv6 forms (56, 28) — the same offsets the core's own process
        /// lookup uses (common/net/find_process_windows.go).
        /// </summary>
        private static class OwnerTable
        {
            private const int AfInet = 2, AfInet6 = 23;
            private const int TcpOwnerPidAll = 5, UdpOwnerPid = 1;

            private static readonly uint TunAddress =
                BitConverter.ToUInt32(IPAddress.Parse(WindowsTunnelConfig.Address).GetAddressBytes());

            public static Dictionary<(bool Tcp, bool V6, int Port), int> Read()
            {
                var map = new Dictionary<(bool, bool, int), int>();
                Fill(map, tcp: true, AfInet, 24, (row) => (Read32(row, 4), Port(row, 8), Read32(row, 20)));
                Fill(map, tcp: false, AfInet, 12, (row) => (Read32(row, 0), Port(row, 4), Read32(row, 8)));
                // IPv6 rows: the address is 16 bytes and the TUN's is the only v6 source the
                // core reports, so any v6 socket on the port is taken as it.
                Fill(map, tcp: true, AfInet6, 56, (row) => (TunAddress, Port(row, 20), Read32(row, 52)));
                Fill(map, tcp: false, AfInet6, 28, (row) => (TunAddress, Port(row, 20), Read32(row, 24)));
                return map;
            }

            private static void Fill(
                Dictionary<(bool, bool, int), int> map, bool tcp, int family, int rowSize,
                Func<IntPtr, (uint Addr, int Port, uint Pid)> parse)
            {
                var size = 0;
                var table = IntPtr.Zero;
                try
                {
                    for (var attempt = 0; attempt < 4; attempt++)
                    {
                        var rc = tcp
                            ? GetExtendedTcpTable(table, ref size, false, family, TcpOwnerPidAll, 0)
                            : GetExtendedUdpTable(table, ref size, false, family, UdpOwnerPid, 0);
                        if (rc == 0) break;
                        if (rc != ErrorInsufficientBuffer) return;
                        if (table != IntPtr.Zero) Marshal.FreeHGlobal(table);
                        table = Marshal.AllocHGlobal(size);
                    }
                    if (table == IntPtr.Zero) return;

                    var count = Marshal.ReadInt32(table);
                    for (var i = 0; i < count; i++)
                    {
                        var row = table + 4 + i * rowSize;
                        var (addr, port, pid) = parse(row);
                        var key = (tcp, family == AfInet6, port);
                        // Prefer the socket on the TUN address; an unbound one (0.0.0.0) is the
                        // UDP case, where Windows binds lazily on first send.
                        if (addr == TunAddress) map[key] = (int)pid;
                        else if (addr == 0) map.TryAdd(key, (int)pid);
                    }
                }
                finally
                {
                    if (table != IntPtr.Zero) Marshal.FreeHGlobal(table);
                }
            }

            private static uint Read32(IntPtr row, int offset) => (uint)Marshal.ReadInt32(row, offset);

            /// <summary>Only the low 16 bits are used, in network order.</summary>
            private static int Port(IntPtr row, int offset)
            {
                var raw = (uint)Marshal.ReadInt32(row, offset);
                return (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
            }
        }

        private const int ErrorInsufficientBuffer = 122;
        private const uint ProcessQueryLimitedInformation = 0x1000;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref int size);
    }
}
