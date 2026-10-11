using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Horus.Platforms.Windows.Tunnel
{
    /// <summary>
    /// The core's live controls: change a running instance instead of restarting it.
    ///
    /// <para>On Windows a restart is expensive in a way it is not on Android: the core owns
    /// the TUN, so stopping it removes the adapter, every application sees its network go
    /// away, and every connection — the game's included — is dropped. These exports exist
    /// so that a split-tunnel change, a protocol fallback and "restart this app's
    /// connections" cost exactly what they have to and nothing more.</para>
    ///
    /// <para>Kept out of the shared <c>XrayInterop</c> so the Android head does not change.
    /// Every call degrades: against a core built before these exports the answer is
    /// <c>Unsupported</c>, and the caller falls back to a restart.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class XrayLive
    {
        private const string Lib = "xray";

        public enum Result { Ok, Failed, Unsupported }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int XrayReloadRouting(byte[] routingJson);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int XrayReplaceOutbound(byte[] outboundJson);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr XrayConnections();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int XrayCloseConnections(byte[] ids);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr XrayLastError();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void XrayFree(IntPtr s);

        /// <summary>Set once an export turned out to be missing, so the probe is not repeated per call.</summary>
        private static volatile bool _unsupported;

        public static bool IsSupported => !_unsupported;

        /// <summary>The last failure's text, from the core.</summary>
        public static string LastError
        {
            get
            {
                try { return Consume(XrayLastError()); }
                catch { return string.Empty; }
            }
        }

        public static Result ReloadRouting(string routingJson) =>
            Call(() => XrayReloadRouting(Utf8(routingJson)));

        public static Result ReplaceOutbound(string outboundJson) =>
            Call(() => XrayReplaceOutbound(Utf8(outboundJson)));

        /// <summary>The TUN's live connections as the core reports them, or null when unsupported.</summary>
        public static string? Connections()
        {
            if (_unsupported) return null;
            try { return Consume(XrayConnections()); }
            catch (EntryPointNotFoundException) { _unsupported = true; return null; }
            catch (DllNotFoundException) { return null; }
        }

        /// <summary>Closes the listed connections (empty: none); returns how many, or -1 when unsupported.</summary>
        public static int CloseConnections(IEnumerable<ulong> ids)
        {
            var spec = string.Join(',', ids);
            if (spec.Length == 0 || _unsupported) return _unsupported ? -1 : 0;
            try { return XrayCloseConnections(Utf8(spec)); }
            catch (EntryPointNotFoundException) { _unsupported = true; return -1; }
            catch (DllNotFoundException) { return -1; }
        }

        private static Result Call(Func<int> call)
        {
            if (_unsupported) return Result.Unsupported;
            try { return call() == 0 ? Result.Ok : Result.Failed; }
            catch (EntryPointNotFoundException) { _unsupported = true; return Result.Unsupported; }
            catch (DllNotFoundException) { return Result.Unsupported; }
        }

        private static byte[] Utf8(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var buffer = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, buffer, 0, bytes.Length);
            return buffer;
        }

        private static string Consume(IntPtr p)
        {
            if (p == IntPtr.Zero) return string.Empty;
            try { return Marshal.PtrToStringUTF8(p) ?? string.Empty; }
            finally { XrayFree(p); }
        }
    }
}
