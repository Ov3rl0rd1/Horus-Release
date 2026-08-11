using Android.Util;
using Horus.Domain.Models;
using Horus.Protocols;
using System.Runtime.InteropServices;

namespace Horus.Platforms.Android
{
    /// <summary>
    /// hev-socks5-tunnel: reads packets off the VpnService TUN fd and speaks SOCKS5 to
    /// xray's inbound. Driven entirely through the library's plain C API.
    ///
    /// It used to also carry <c>[Register("com/horus/vpn/VPNService")]</c> plus
    /// <c>[Export]</c>ed stubs, to satisfy the <c>JNI_OnLoad</c> in hev's <c>hev-jni.c</c>.
    /// Those are gone, and so is that <c>JNI_OnLoad</c>: the library is now built without
    /// its JNI layer, because .NET Android loads it through <c>System.loadLibrary</c> at
    /// runtime startup rather than lazily on the first <c>[DllImport]</c>, and a
    /// <c>JNI_OnLoad</c> that cannot find its Java class aborts the process on launch.
    /// The full reasoning is in <c>Platforms/Android/lib/README.md</c>; the consequence
    /// here is that this class is plain P/Invoke with nothing Java about it, and the app id
    /// is no longer constrained by the bridge.
    /// </summary>
    internal static class HevSocksTunnel
    {
        private const string HEV_LIB_NAME = "libhev_socks";
        private const string HEV_TAG = "HEV-SOCKS5";

        private static Thread? _hevThread;

        [DllImport(HEV_LIB_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "hev_socks5_tunnel_main_from_str")]
        private static extern int hev_socks5_tunnel_main_from_str(byte[] config_data, uint config_len, int tun_fd);

        [DllImport(HEV_LIB_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "hev_socks5_tunnel_quit")]
        private static extern void hev_socks5_tunnel_quit();

        [DllImport(HEV_LIB_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "hev_socks5_tunnel_stats")]
        private static extern void hev_socks5_tunnel_stats(ref UIntPtr tx_packets, ref UIntPtr tx_bytes, ref UIntPtr rx_packets, ref UIntPtr rx_bytes);

        public static void StartTunnel(int tun_fd, int socksPort)
        {
            if (_hevThread != null)
            {
                Log.Warn(HEV_TAG, "Trying to start tunnel again!");
                return;
            }

            var logFile = DiagnosticPaths.HevLog;
            DiagnosticPaths.Truncate(logFile);

            // The native side takes a byte count, not a character count. Marshalling the
            // string ourselves keeps the two in agreement once the config carries a path
            // that is not pure ASCII.
            var config = System.Text.Encoding.UTF8.GetBytes(
                HevTunnelConfig.Build(logFile, HevTunnelConfig.DefaultLogLevel, socksPort));

            _hevThread = new Thread(() =>
            {
                Log.Verbose(HEV_TAG, "Start hev-socks5-tunnel");

                try
                {
                    int result = hev_socks5_tunnel_main_from_str(config, (uint)config.Length, tun_fd);
                    if (result != 0)
                        Log.Error(HEV_TAG, $"hev-socks5-tunnel exited with {result}");
                }
                catch (Exception e)
                {
                    // An escaping exception on a background thread would take the process
                    // down instead of surfacing anywhere useful.
                    Log.Error(HEV_TAG, e.Message);
                }
            })
            { IsBackground = true, Name = "hev-socks5-tunnel" };

            _hevThread.Start();
        }

        public static void StopTunnel()
        {
            if (_hevThread == null)
            {
                Log.Warn(HEV_TAG, "Trying to stop non existing tunnel!");
                return;
            }

            hev_socks5_tunnel_quit();

            // quit() unblocks main_from_str; give the loop a moment to unwind so a
            // reconnect does not race a still-running instance onto the same fd.
            _hevThread.Join(TimeSpan.FromSeconds(3));
            _hevThread = null;
        }

        public static long[]? GetTunnelStats()
        {
            if (_hevThread == null)
                return null;

            UIntPtr tx_packets = 0;
            UIntPtr tx_bytes = 0;
            UIntPtr rx_packets = 0;
            UIntPtr rx_bytes = 0;

            hev_socks5_tunnel_stats(ref tx_packets, ref tx_bytes, ref rx_packets, ref rx_bytes);

            return [(long)tx_packets, (long)tx_bytes, (long)rx_packets, (long)rx_bytes];
        }
    }
}
