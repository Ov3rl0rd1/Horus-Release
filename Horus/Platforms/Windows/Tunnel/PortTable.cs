using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Horus.Platforms.Windows.Tunnel
{
    /// <summary>
    /// The local ports of every socket on the machine, from Windows' TCP and UDP tables —
    /// what <see cref="EphemeralPorts.Judge"/> counts. Both address families are read: a
    /// dual-stack socket (the core's, for one) is listed in the IPv6 table only.
    ///
    /// <para>Row layouts as in <see cref="WindowsConnectionMonitor"/>: MIB_UDPROW_OWNER_PID (12
    /// bytes, port at 4), MIB_UDP6ROW_OWNER_PID (28, at 20), MIB_TCPROW_OWNER_PID (24, at 8),
    /// MIB_TCP6ROW_OWNER_PID (56, at 20). TCP includes connections in TIME_WAIT, which hold
    /// their port for two minutes after they close.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class PortTable
    {
        private const int AfInet = 2, AfInet6 = 23;
        private const int TcpOwnerPidAll = 5, UdpOwnerPid = 1;
        private const int ErrorInsufficientBuffer = 122;

        public static List<int> LocalPorts(bool tcp)
        {
            var ports = new List<int>();
            if (tcp)
            {
                Read(ports, tcp: true, AfInet, rowSize: 24, portAt: 8);
                Read(ports, tcp: true, AfInet6, rowSize: 56, portAt: 20);
            }
            else
            {
                Read(ports, tcp: false, AfInet, rowSize: 12, portAt: 4);
                Read(ports, tcp: false, AfInet6, rowSize: 28, portAt: 20);
            }
            return ports;
        }

        private static void Read(List<int> ports, bool tcp, int family, int rowSize, int portAt)
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
                    // A table that grows between the two calls (a loop is opening sockets as fast
                    // as it can) needs more than it asked for a moment ago.
                    size += size / 8;
                    table = Marshal.AllocHGlobal(size);
                }
                if (table == IntPtr.Zero) return;

                var count = Marshal.ReadInt32(table);
                for (var i = 0; i < count; i++)
                {
                    var raw = (uint)Marshal.ReadInt32(table + 4 + i * rowSize, portAt);
                    ports.Add((int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF)));
                }
            }
            finally
            {
                if (table != IntPtr.Zero) Marshal.FreeHGlobal(table);
            }
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    }
}
