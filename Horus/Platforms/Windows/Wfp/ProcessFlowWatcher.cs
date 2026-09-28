using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Horus.Domain.Models;

namespace Horus.Platforms.Windows.Wfp
{
    /// <summary>One outbound connection, as the kernel saw it being opened.</summary>
    /// <param name="ProcessId">The process opening it.</param>
    /// <param name="Remote">Where it is going.</param>
    public readonly record struct FlowEvent(uint ProcessId, IPAddress Remote);

    /// <summary>
    /// Watches every outbound connection on the machine and reports who opened it and where
    /// it is going, using WinDivert's SOCKET layer — the one place that pairs a connection
    /// with a process id before any packet is on the wire.
    ///
    /// <para><b>Sniffing, not intercepting, and that is a decision rather than a shortcut.</b>
    /// WinDivert can hold a socket event until we release it, which would let a route be in
    /// place before the first packet moves. It would also put this process in the path of
    /// every connect() on the machine: a stall here would stall all of Windows' networking,
    /// and a crash would do worse. Sniffing gives up exactness on the first connection to a
    /// destination and keeps that failure impossible.</para>
    ///
    /// <para><b>The direction that first connection fails in is the safe one.</b> Until the
    /// route exists, traffic follows the default route, which is the tunnel. So a flow that
    /// should have bypassed the VPN is briefly carried by it — over-protected, not leaked —
    /// and the route that lands a moment later fixes every connection after it. The one case
    /// where that is not good enough is a whitelisted app riding a bypass route installed for
    /// some other process, and that is what the WFP block in
    /// <see cref="WindowsSplitTunnelingService"/> exists to catch.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class ProcessFlowWatcher : IDisposable
    {
        private const string Dll = "WinDivert.dll";

        private const short LayerSocket = 3;
        private const ulong FlagSniff = 0x0001;
        private const ulong FlagRecvOnly = 0x0004;

        /// <summary>WINDIVERT_EVENT_SOCKET_CONNECT.</summary>
        private const byte EventSocketConnect = 4;

        private IntPtr _handle = new(-1);
        private Thread? _thread;
        private volatile bool _stopping;

        private readonly Action<FlowEvent> _onFlow;

        public ProcessFlowWatcher(Action<FlowEvent> onFlow) => _onFlow = onFlow;

        public bool IsRunning => _thread is { IsAlive: true };

        /// <summary>
        /// Starts watching. Throws when WinDivert will not open, which is a real and ordinary
        /// outcome — the driver may be missing, blocked by a security product, or refused
        /// because the process is not elevated.
        /// </summary>
        public void Start()
        {
            if (IsRunning) return;

            // Outbound TCP and UDP only. Filtering in the driver rather than in managed code
            // is what keeps this off the hot path: loopback and inbound never reach us.
            const string filter = "outbound and (tcp or udp)";

            _handle = WinDivertOpen(filter, LayerSocket, 0, FlagSniff | FlagRecvOnly);
            if (_handle == new IntPtr(-1))
                throw new InvalidOperationException(
                    $"WinDivertOpen failed: {Marshal.GetLastWin32Error()}. " +
                    "The driver is missing, blocked, or the process is not elevated.");

            _stopping = false;
            _thread = new Thread(Pump)
            {
                IsBackground = true,
                Name = "horus-flow-watcher"
            };
            _thread.Start();
        }

        private void Pump()
        {
            var addr = new WinDivertAddress();

            while (!_stopping)
            {
                // Socket events carry no packet, so the buffer is null and the length zero.
                if (!WinDivertRecv(_handle, IntPtr.Zero, 0, IntPtr.Zero, ref addr))
                {
                    // Shutting down closes the handle under us, which is the ordinary way
                    // out of this loop rather than an error.
                    if (_stopping) return;

                    // ERROR_INSUFFICIENT_BUFFER on a layer that carries no payload is normal;
                    // anything else and the driver is gone, so stop rather than spin.
                    if (Marshal.GetLastWin32Error() is not (122 or 0)) return;
                    continue;
                }

                if (addr.Event != EventSocketConnect) continue;

                try
                {
                    var remote = ReadRemote(addr);
                    if (remote is not null) _onFlow(new FlowEvent(addr.Socket.ProcessId, remote));
                }
                catch
                {
                    // A handler that throws must not take the watcher down with it: losing
                    // the thread means losing every later flow, silently.
                }
            }
        }

        /// <summary>
        /// The remote address of a socket event, or null when it is not one we steer.
        ///
        /// <para>Byte order is deliberately not handled here. WinDivert's own helpers do the
        /// conversion and the formatting, and the result is parsed back — a detour that
        /// costs a string per connection and removes the one class of bug that would be
        /// invisible from this side. An address assembled by hand with the words in the
        /// wrong order is still a valid address: it installs a route to the wrong host and
        /// nothing anywhere reports a problem.</para>
        ///
        /// <para>IPv6 is recognised and then declined, because the steering below is host
        /// routes added with <c>route add</c> over IPv4. Returning an address we cannot act
        /// on would be worse than returning none.</para>
        /// </summary>
        private static IPAddress? ReadRemote(in WinDivertAddress addr)
        {
            // Bit 4 of the flag byte that follows Layer and Event is WINDIVERT_ADDRESS.IPv6.
            if ((addr.Flags & 0x10) != 0) return null;

            var network = addr.Socket.RemoteAddr0;
            WinDivertHelperNtohl(ref network, out var host);

            var buffer = new byte[32];
            if (!WinDivertHelperFormatIPv4Address(host, buffer, (uint)buffer.Length)) return null;

            var text = System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0');
            return IPAddress.TryParse(text, out var ip) ? ip : null;
        }

        public void Dispose()
        {
            _stopping = true;

            if (_handle != new IntPtr(-1))
            {
                WinDivertClose(_handle);
                _handle = new IntPtr(-1);
            }

            // Bounded: the pump is a background thread and the process must not be held up
            // by a driver that is refusing to return.
            _thread?.Join(TimeSpan.FromSeconds(2));
            _thread = null;
        }

        // ── Interop ──────────────────────────────────────────────────────────

        /// <summary>
        /// WINDIVERT_ADDRESS. The tail is a union of per-layer payloads inside sixty-four
        /// bytes; only the SOCKET arm is described here, and the rest is padding so the
        /// overall size still matches.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct WinDivertAddress
        {
            public long Timestamp;

            /// <summary>Layer (8 bits), Event (8 bits), then flag bits — read as bytes.</summary>
            public byte Layer;
            public byte Event;
            public byte Flags;
            public byte Reserved0;

            public uint Reserved1;
            public ulong Reserved2;

            public WinDivertDataSocket Socket;
        }

        /// <summary>WINDIVERT_DATA_SOCKET, padded out to the union's sixty-four bytes.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct WinDivertDataSocket
        {
            public ulong Endpoint;
            public ulong ParentEndpoint;
            public uint ProcessId;

            public uint LocalAddr0, LocalAddr1, LocalAddr2, LocalAddr3;
            public uint RemoteAddr0, RemoteAddr1, RemoteAddr2, RemoteAddr3;

            public ushort LocalPort;
            public ushort RemotePort;
            public byte Protocol;
            public byte Pad0, Pad1, Pad2;
        }

        [DllImport(Dll, SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr WinDivertOpen(string filter, short layer, short priority, ulong flags);

        [DllImport(Dll, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinDivertRecv(
            IntPtr handle, IntPtr packet, uint packetLen, IntPtr recvLen, ref WinDivertAddress addr);

        [DllImport(Dll, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinDivertClose(IntPtr handle);

        /// <summary>Network to host order, done by the library rather than by us.</summary>
        [DllImport(Dll, EntryPoint = "WinDivertHelperNtohIpv4Address")]
        private static extern void WinDivertHelperNtohl(ref uint inAddr, out uint outAddr);

        [DllImport(Dll)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinDivertHelperFormatIPv4Address(
            uint addr, [Out] byte[] buffer, uint bufLen);
    }
}
