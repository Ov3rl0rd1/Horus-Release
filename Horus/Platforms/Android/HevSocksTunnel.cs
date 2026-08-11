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
    /// That was never load-bearing: <c>JNI_OnLoad</c> is invoked by ART from
    /// <c>System.loadLibrary</c>, while <c>[DllImport]</c> resolves through plain
    /// <c>dlopen</c>, which does not call it. The <c>[Export]</c> stubs would not have
    /// worked anyway — they generate ordinary Java methods in the callable wrapper, not
    /// <c>native</c> ones, so <c>RegisterNatives</c> would have failed on them.
    ///
    /// Dropping them means stock upstream builds work as-is, with no
    /// <c>-DPKGNAME</c>/<c>-DCLSNAME</c> override at build time. Note that upstream
    /// <c>64cc609</c> (2026-07-30) made <c>JNI_OnLoad</c> return <c>JNI_ERR</c> on a failed
    /// registration where it previously ignored the result — so if anything ever loads this
    /// library through <c>JavaSystem.LoadLibrary</c>, it will now fail loudly. Don't.
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
