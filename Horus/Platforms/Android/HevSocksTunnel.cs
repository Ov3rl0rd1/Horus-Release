using Android.Runtime;
using Android.Util;
using Horus.Domain.Models;
using Java.Interop;
using System.Runtime.InteropServices;

namespace Horus.Platforms.Android
{
    [Register("com/horus/vpn/VPNService")]
    internal class HevSocksTunnel : Java.Lang.Object, IJavaObject
    {
        private const string HEV_LIB_NAME = "libhev_socks";
        private const string HEV_TAG = "HEV-SOCKS5";

        /// <summary>
        /// At <c>warn</c> a healthy-looking but dead tunnel writes an empty log, which is
        /// indistinguishable from "hev never started". Debug builds log every connection so
        /// the TUN half is actually diagnosable; release stays quiet.
        /// </summary>
#if DEBUG
        private const string HevLogLevel = "info";
#else
        private const string HevLogLevel = "warn";
#endif

        /// <summary>
        /// hev-socks5-tunnel's YAML. The <c>socks5.port</c> here and
        /// <see cref="Horus.Domain.Models.XrayConfig.DefaultSocksPort"/> are two halves of
        /// one contract across a language boundary — change them together or the tunnel
        /// establishes and silently carries nothing.
        ///
        /// <paramref name="logFile"/> matters because <c>stderr</c> inside an Android app
        /// process goes to /dev/null: without a real path this half of the pipeline is
        /// completely undiagnosable.
        /// </summary>
        private static string BuildConfig(string logFile) => $"""
misc:
  task-stack-size: 81920
  log-file: {logFile}
  log-level: {HevLogLevel}
tunnel:
  name: tun_horus_0
  multi-queue: false
  ipv4: 198.18.0.1
  ipv6: 'fc00::1'
  mtu: 8500
socks5:
  port: {Horus.Domain.Models.XrayConfig.DefaultSocksPort}
  address: 127.0.0.1
  udp: 'udp'
""";

        private static Java.Lang.Thread? _hevThread = null;

        [DllImport(HEV_LIB_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "hev_socks5_tunnel_main_from_str")]
        private static extern int hev_socks5_tunnel_main_from_str(byte[] config_data, uint config_len, int tun_fd);

        [DllImport(HEV_LIB_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "hev_socks5_tunnel_quit")]
        private static extern void hev_socks5_tunnel_quit();

        [DllImport(HEV_LIB_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "hev_socks5_tunnel_stats")]
        private static extern void hev_socks5_tunnel_stats(ref UIntPtr tx_packets, ref UIntPtr tx_bytes, ref UIntPtr rx_packets, ref UIntPtr rx_bytes);

        public static void StartTunnel(int tun_fd)
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
            var config = System.Text.Encoding.UTF8.GetBytes(BuildConfig(logFile));

            _hevThread = new Java.Lang.Thread(() =>
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
                    // This runs on a bare Java thread — an escaping exception would take
                    // the process down instead of surfacing anywhere useful.
                    Log.Error(HEV_TAG, e.Message);
                }
            });
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

            _hevThread?.Interrupt();
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

            return new long[4] { (long)tx_packets, (long)tx_bytes, (long)rx_packets, (long)rx_bytes };
        }

        // The native library resolves its JNI entry points against the class name in
        // [Register] above ("com/horus/vpn/VPNService"), which is why the app id must stay
        // com.horus.vpn. These declarations exist to shape that Android Callable Wrapper;
        // the code above reaches the library through its plain C API instead.
        [Export("TProxyStartService")]
        private static extern void TProxyStartService(Java.Lang.String config_path, int fd);
        [Export("TProxyStopService")]
        private static extern void TProxyStopService();
        [Export("TProxyGetStats")]
        private static extern long[] TProxyGetStats();
    }
}
