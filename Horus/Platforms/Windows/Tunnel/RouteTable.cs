using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Horus.Platforms.Windows.Tunnel
{
    /// <summary>
    /// The machine's IPv4 default routes, read from the live stack through IP Helper.
    ///
    /// <para>Not <c>System.Net.NetworkInformation</c>: it exposes neither route metrics nor
    /// interface metrics, and those are exactly what decides which path Windows — and the
    /// core, which mirrors it — uses. Struct offsets below were verified against
    /// wireguard-go's <c>winipcfg</c> definitions with compile-time assertions
    /// (<c>MIB_IPFORWARD_ROW2</c> 104 bytes, <c>MIB_IPINTERFACE_ROW</c> 168, <c>MIB_IF_ROW2</c>
    /// 1352).</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class RouteTable
    {
        private const ushort AfInet = 2;
        private const int RowSize = 104;
        private const int TableHeader = 8;
        private const uint IfTypeIeee80211 = 71;
        private const uint IfOperStatusUp = 1;

        public static IReadOnlyList<DefaultRoute> DefaultRoutes(string tunAlias)
        {
            var result = new List<DefaultRoute>();
            if (GetIpForwardTable2(AfInet, out var table) != 0 || table == IntPtr.Zero) return result;

            try
            {
                var count = Marshal.ReadInt32(table);
                for (var i = 0; i < count; i++)
                {
                    var row = table + TableHeader + i * RowSize;
                    if (Marshal.ReadByte(row, 40) != 0) continue; // DestinationPrefix.PrefixLength

                    var luid = (ulong)Marshal.ReadInt64(row, 0);
                    var index = Marshal.ReadInt32(row, 8);
                    var nextHop = new IPAddress((uint)Marshal.ReadInt32(row, 44 + 4)); // sockaddr_in.sin_addr
                    var routeMetric = (uint)Marshal.ReadInt32(row, 84);

                    var (alias, type, up) = Describe(luid);
                    var ifMetric = InterfaceMetric(luid);

                    result.Add(new DefaultRoute(
                        index,
                        routeMetric + ifMetric,
                        up,
                        string.Equals(alias, tunAlias, StringComparison.OrdinalIgnoreCase),
                        type == IfTypeIeee80211,
                        nextHop.ToString(),
                        alias));
                }
            }
            finally { FreeMibTable(table); }

            return result;
        }

        private static (string Alias, uint Type, bool Up) Describe(ulong luid)
        {
            var buffer = Marshal.AllocHGlobal(1352);
            try
            {
                for (var i = 0; i < 1352; i += 8) Marshal.WriteInt64(buffer, i, 0);
                Marshal.WriteInt64(buffer, 0, (long)luid);
                if (GetIfEntry2(buffer) != 0) return (string.Empty, 0, false);

                var alias = Marshal.PtrToStringUni(buffer + 28) ?? string.Empty;
                var type = (uint)Marshal.ReadInt32(buffer, 1128);
                var oper = (uint)Marshal.ReadInt32(buffer, 1156);
                return (alias, type, oper == IfOperStatusUp);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static uint InterfaceMetric(ulong luid)
        {
            var buffer = Marshal.AllocHGlobal(168);
            try
            {
                InitializeIpInterfaceEntry(buffer);
                Marshal.WriteInt16(buffer, 0, (short)AfInet);
                Marshal.WriteInt64(buffer, 8, (long)luid);
                return GetIpInterfaceEntry(buffer) == 0 ? (uint)Marshal.ReadInt32(buffer, 148) : 0;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        [DllImport("iphlpapi.dll")]
        private static extern int GetIpForwardTable2(ushort family, out IntPtr table);

        [DllImport("iphlpapi.dll")]
        private static extern void FreeMibTable(IntPtr memory);

        [DllImport("iphlpapi.dll")]
        private static extern int GetIfEntry2(IntPtr row);

        [DllImport("iphlpapi.dll")]
        private static extern void InitializeIpInterfaceEntry(IntPtr row);

        [DllImport("iphlpapi.dll")]
        private static extern int GetIpInterfaceEntry(IntPtr row);
    }
}
