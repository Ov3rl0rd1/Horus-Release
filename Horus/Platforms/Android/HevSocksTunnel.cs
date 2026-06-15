using Android.Runtime;
using Android.Util;
using Java.Interop;
using System.Runtime.InteropServices;

namespace Horus.Platforms.Android
{
    [Register("com/horus/vpn/VPNService")]
    internal class HevSocksTunnel : Java.Lang.Object, IJavaObject
    {
        private const string HEV_LIB_NAME = "libhev_socks";
        private const string HEV_TAG = "HEV-SOCKS5";
        private const string HEV_SOCKS5_TUNNEL_CONFIG = """
misc:
  task-stack-size: 81920
  log-file: stderr
  log-level: debug
tunnel:
  name: tun_horus_0
  multi-queue: false
  ipv4: 198.18.0.1
  ipv6: 'fc00::1'
  mtu: 8500
socks5:
  port: 1080
  address: 127.0.0.1
  udp: 'udp'
""";

        private static Java.Lang.Thread? _hevThread = null;

        [DllImport(HEV_LIB_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "hev_socks5_tunnel_main_from_str")]
        private static extern int hev_socks5_tunnel_main_from_str([MarshalAs(UnmanagedType.LPStr)] string config_data, uint config_len, int tun_fd);

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

            _hevThread = new Java.Lang.Thread(() =>
            {
                Log.Verbose(HEV_TAG, "Start hev-socks5-tunnel");

                int result = 0;
                try
                {
                    result = hev_socks5_tunnel_main_from_str(HEV_SOCKS5_TUNNEL_CONFIG, (uint)HEV_SOCKS5_TUNNEL_CONFIG.Length, tun_fd);
                }
                catch (Exception e)
                {
                    Log.Error(HEV_TAG, e.Message);
                }

                if (result == -1)
                    throw new ExternalException();
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

        [Export("TProxyStartService")]
        private static extern void TProxyStartService(Java.Lang.String config_path, int fd);
        [Export("TProxyStartService")]
        private static extern void TProxyStopService();
        [Export("TProxyGetStats")]
        private static extern long[] TProxyGetStats();
    }
}
