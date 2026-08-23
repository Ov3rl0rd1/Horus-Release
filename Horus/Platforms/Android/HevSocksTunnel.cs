using Android.Util;
using Horus.Application;
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

        /// <summary>
        /// The SOCKS port the running bridge was configured with. A rebuild that changes it
        /// cannot be a descriptor swap: the port lives in the YAML the bridge parsed once at
        /// start-up, so the bridge has to be restarted to learn a new one.
        /// </summary>
        private static int _activePort;

        [DllImport(HEV_LIB_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "hev_socks5_tunnel_main_from_str")]
        private static extern int hev_socks5_tunnel_main_from_str(byte[] config_data, uint config_len, int tun_fd);

        [DllImport(HEV_LIB_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "hev_socks5_tunnel_quit")]
        private static extern void hev_socks5_tunnel_quit();

        /// <summary>
        /// Hands the bridge a new TUN descriptor without stopping it. Present only in the
        /// patched build — see packaging/android/hev-patches.
        /// </summary>
        [DllImport(HEV_LIB_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "hev_socks5_tunnel_set_fd")]
        private static extern int hev_socks5_tunnel_set_fd(int tun_fd);


        [DllImport(HEV_LIB_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "hev_socks5_tunnel_stats")]
        private static extern void hev_socks5_tunnel_stats(ref UIntPtr tx_packets, ref UIntPtr tx_bytes, ref UIntPtr rx_packets, ref UIntPtr rx_bytes);

        public static void StartTunnel(int tun_fd, int socksPort)
        {
            if (_hevThread != null)
            {
                // Either a live tunnel, or one StopTunnel could not join. Both mean a
                // second reader on the same fd, so this is a hard refusal rather than a
                // warning — the caller surfaces it as a failed connect, which is recoverable,
                // instead of a half-working tunnel, which is not diagnosable.
                throw new InvalidOperationException(
                    "\u041f\u0440\u0435\u0434\u044b\u0434\u0443\u0449\u0438\u0439 \u0442\u0443\u043d\u043d\u0435\u043b\u044c \u0435\u0449\u0451 \u043d\u0435 \u043e\u0441\u0442\u0430\u043d\u043e\u0432\u043b\u0435\u043d. \u041f\u043e\u043f\u0440\u043e\u0431\u0443\u0439\u0442\u0435 \u0447\u0435\u0440\u0435\u0437 \u043d\u0435\u0441\u043a\u043e\u043b\u044c\u043a\u043e \u0441\u0435\u043a\u0443\u043d\u0434.");
            }

            var logFile = DiagnosticPaths.HevLog;
            DiagnosticPaths.Rotate(logFile);

            // The native side takes a byte count, not a character count. Marshalling the
            // string ourselves keeps the two in agreement once the config carries a path
            // that is not pure ASCII.
            var config = System.Text.Encoding.UTF8.GetBytes(
                HevTunnelConfig.Build(logFile, UserPreferences.HevLogLevel, socksPort));

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
            _activePort = socksPort;
        }

        /// <summary>
        /// Points the running bridge at a new TUN descriptor, restarting it only if it has
        /// to.
        ///
        /// <para>The fast path is a descriptor swap inside the bridge, which keeps every
        /// established session alive and costs a wakeup. It applies when the SOCKS port is
        /// unchanged — the common case, because the port is deliberately kept stable across
        /// reconnects — and when the library is a build carrying the patch.</para>
        ///
        /// <para>Everything else falls back to stop-and-start, which is what this always
        /// used to do. An older library without the export is not an error: the app is
        /// expected to run against a stock build, just more slowly.</para>
        /// </summary>
        public static void Rebind(int tun_fd, int socksPort)
        {
            if (_hevThread is null)
            {
                StartTunnel(tun_fd, socksPort);
                return;
            }

            if (socksPort == _activePort && TrySetFd(tun_fd))
            {
                Log.Info(HEV_TAG, $"rebound to fd {tun_fd} without restarting");
                Diag.Info("tun", "bridge rebound in place");
                return;
            }

            Diag.Info("tun", socksPort == _activePort
                ? "bridge does not support rebinding; restarting"
                : $"socks port changed {_activePort} -> {socksPort}; restarting the bridge");

            if (!StopTunnel())
                throw new InvalidOperationException(
                    "Предыдущий туннель ещё не остановлен. Попробуйте через несколько секунд.");

            StartTunnel(tun_fd, socksPort);
        }

        /// <summary>
        /// Calls the patched entry point, reporting whether it took. Never throws: a
        /// library without it is the ordinary case on a stock build, and the caller has a
        /// working fallback.
        /// </summary>
        private static bool TrySetFd(int tun_fd)
        {
            try
            {
                var result = hev_socks5_tunnel_set_fd(tun_fd);
                if (result == 0) return true;

                Diag.Warn("tun", $"hev_socks5_tunnel_set_fd returned {result}");
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                // A stock upstream build. Said once at info, not warned about repeatedly:
                // it is a known configuration, not a fault.
                Diag.Info("tun", "this bridge build has no set_fd; falling back to restart");
                return false;
            }
            catch (Exception ex)
            {
                Diag.Warn("tun", $"set_fd failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stops the bridge and reports whether it actually went away.
        ///
        /// <para><b>False means the previous instance is still running.</b> The old version
        /// cleared the field regardless of whether the join succeeded, so a bridge that had
        /// not unwound within three seconds was forgotten about — and the next connect
        /// happily started a second one, with two readers on the same TUN fd. The symptom
        /// would be a tunnel that carries a fraction of its packets, which is far harder to
        /// diagnose than a connect that refuses to proceed.</para>
        ///
        /// <para>Callers must not start a new tunnel on a false. In practice the join
        /// returns in milliseconds; a timeout means something is genuinely wrong.</para>
        /// </summary>
        public static bool StopTunnel()
        {
            var thread = _hevThread;
            if (thread == null)
            {
                Log.Warn(HEV_TAG, "Trying to stop non existing tunnel!");
                return true;
            }

            hev_socks5_tunnel_quit();

            // quit() unblocks main_from_str; give the loop a moment to unwind so a
            // reconnect does not race a still-running instance onto the same fd.
            if (!thread.Join(TimeSpan.FromSeconds(3)))
            {
                Diag.Error("tun", "hev-socks5-tunnel did not exit within 3s; not starting another");
                return false;
            }

            _hevThread = null;
            _activePort = 0;
            return true;
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
