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
