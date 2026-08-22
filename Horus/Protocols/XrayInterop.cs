using System.Runtime.InteropServices;

namespace Horus.Protocols
{
    /// <summary>
    /// P/Invoke surface of the bundled xray-core shared library
    /// (<c>libxray.so</c> on Android, <c>xray.dll</c> on Windows — the probe name
    /// <c>"xray"</c> resolves both).
    ///
    /// xray runs <b>in-process</b>, not as a child process. That matters in two ways:
    /// the app's own UID must be excluded from the VPN tunnel or xray's outbound
    /// sockets loop back through it (see <c>HorusVpnTunnelService</c>), and a panic
    /// inside the core takes the whole app down rather than just a subprocess.
    ///
    /// Contract: every <c>int</c> returns 0 on success and -1 on failure, with the
    /// detail available from <see cref="LastError"/>. Every <c>char*</c> returned is
    /// owned by the caller and must be released with <c>XrayFree</c> — it comes from
    /// C <c>malloc</c>, so <c>Marshal.FreeHGlobal</c> is the wrong pairing.
    /// </summary>
    internal static class XrayInterop
    {
        private const string Lib = "xray";

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int XrayStart(byte[] configJson);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int XrayStop();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int XrayIsRunning();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int XrayTest(byte[] configJson);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int XraySetAssetPath(byte[] path);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int XrayResetConnections();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void XrayForceGc();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int XraySleep();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int XrayWake();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int XrayIsPaused();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr XrayVersion();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr XrayLastError();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void XrayFree(IntPtr s);

        // ── Managed API ──────────────────────────────────────────────────────

        /// <summary>Version string of the linked core, or the failure reason.</summary>
        public static string Version()
        {
            try { return Consume(XrayVersion()); }
            catch (DllNotFoundException) { return "not found"; }
            catch (EntryPointNotFoundException) { return "incompatible build"; }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Message from the last failed call; empty when the last call succeeded.</summary>
        public static string LastError() => Consume(XrayLastError());

        /// <summary>True while an instance is running.</summary>
        public static bool IsRunning()
        {
            try { return XrayIsRunning() == 1; }
            catch { return false; }
        }

        /// <summary>
        /// Parses the config and builds every handler without starting anything —
        /// the equivalent of <c>xray run -test</c>. Cheap way to surface a bad
        /// outbound schema as a precise parser message instead of a timeout.
        /// </summary>
        public static void Test(string configJson)
        {
            if (XrayTest(Utf8(configJson)) != 0)
                throw new InvalidOperationException(Describe("Конфигурация отклонена ядром"));
        }

        public static void Start(string configJson)
        {
            if (XrayStart(Utf8(configJson)) != 0)
                throw new InvalidOperationException(Describe("Не удалось запустить ядро"));
        }

        /// <summary>
        /// Stops and releases the running instance. Not an error when nothing is
        /// running, so teardown paths can call it unconditionally — and they must,
        /// because <see cref="Start"/> fails while an instance already exists.
        /// </summary>
        public static void Stop()
        {
            try { XrayStop(); }
            catch { /* teardown is best-effort */ }
        }

        /// <summary>Directory holding geoip.dat / geosite.dat. Only needed if a routing
        /// rule uses a <c>geoip:</c> or <c>geosite:</c> predicate — the generated config
        /// deliberately avoids them.</summary>
        public static void SetAssetPath(string path) => XraySetAssetPath(Utf8(path));

        /// <summary>
        /// Closes pooled transport sessions so the next dial builds fresh ones. Returns how
        /// many were closed, or -1 if the call failed.
        ///
        /// <para>For a network handover. Every session established over the old link died
        /// with it, but the transport is not told: QUIC sits on the dead path until an idle
        /// timeout measured in minutes, which is the "connected but nothing loads until I
        /// toggle it" the user reports. This costs one call and leaves the running instance
        /// and the TUN standing, where the alternative — a full reconnect — rebuilds
        /// everything and drops the user's traffic on the way.</para>
        ///
        /// <para>Never throws: a library too old to export it is a reason to fall back to
        /// the reconnect path, not to fail the caller.</para>
        /// </summary>
        public static int ResetConnections()
        {
            try { return XrayResetConnections(); }
            catch (EntryPointNotFoundException) { return -1; }
            catch { return -1; }
        }

        /// <summary>
        /// Asks the core to hand freed memory back to the OS. Asynchronous inside the
        /// library, so this returns immediately and is safe from a low-memory callback.
        ///
        /// <para>Go holds released pages rather than returning them, which for a process
        /// that stays resident for weeks means a resident set that only grows — and a large
        /// process is the first thing the OOM killer reaches for.</para>
        /// </summary>
        public static void ForceGc()
        {
            try { XrayForceGc(); }
            catch { /* a memory hint is best-effort by definition */ }
        }

        /// <summary>
        /// Pauses the core's background housekeeping. Returns false if the library does not
        /// support it, which is the signal to stop trying.
        ///
        /// <para>The hysteria transport runs two housekeeping loops at 1 Hz — reaping idle
        /// UDP sessions and dead QUIC clients — and neither knows the screen is off. On
        /// Android each tick is a timer the kernel services, and over a night that is tens
        /// of thousands of wakeups to inspect structures nothing has touched. Traffic is
        /// unaffected; only the tidying stops, and the first tick after
        /// <see cref="Wake"/> catches up on it.</para>
        /// </summary>
        public static bool Sleep() => TryVoid(XraySleep);

        /// <summary>Resumes background housekeeping. Idempotent.</summary>
        public static bool Wake() => TryVoid(XrayWake);

        /// <summary>
        /// Whether housekeeping is paused: true, false, or null when the library cannot say.
        /// Diagnostics only — it exists so a bug report can show whether the Doze wiring
        /// actually reached the core rather than leaving it to be assumed.
        /// </summary>
        public static bool? IsPaused()
        {
            try
            {
                var result = XrayIsPaused();
                return result < 0 ? null : result == 1;
            }
            catch { return null; }
        }

        /// <summary>
        /// Calls an entry point that may be absent from an older library.
        ///
        /// <para>The distinction matters: <see cref="EntryPointNotFoundException"/> means
        /// the app and the core have drifted apart, which is worth saying once and then
        /// living with, while any other failure is the core's own and already recorded in
        /// <see cref="LastError"/>.</para>
        /// </summary>
        private static bool TryVoid(Func<int> call)
        {
            try { return call() == 0; }
            catch (EntryPointNotFoundException) { return false; }
            catch { return false; }
        }

        // ── Private ──────────────────────────────────────────────────────────

        /// <summary>cgo takes a NUL-terminated C string; marshal UTF-8 explicitly so
        /// non-ASCII paths in the config survive the boundary.</summary>
        private static byte[] Utf8(string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            var buffer = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, buffer, 0, bytes.Length);
            return buffer; // trailing 0 from the zero-initialised array
        }

        /// <summary>Takes ownership of a char* the library returned and frees it.</summary>
        private static string Consume(IntPtr p)
        {
            if (p == IntPtr.Zero) return string.Empty;
            try { return Marshal.PtrToStringUTF8(p) ?? string.Empty; }
            finally { XrayFree(p); }
        }

        private static string Describe(string fallback)
        {
            var detail = LastError();
            return string.IsNullOrWhiteSpace(detail) ? fallback : $"{fallback}: {detail}";
        }
    }
}
