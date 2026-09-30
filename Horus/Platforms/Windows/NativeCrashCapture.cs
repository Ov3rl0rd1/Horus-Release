using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Horus.Domain.Models;
using Horus.Protocols;
using Microsoft.Win32.SafeHandles;

namespace Horus.Platforms.Windows
{
    /// <summary>
    /// Gives the native code in this process somewhere to leave its last words, and reads
    /// back what the previous session left.
    ///
    /// <para><b>Why this exists.</b> A tester's archive (30.09.2026) held seven sessions in a
    /// row that ended with "no managed exception was raised" and nothing else: xray.log
    /// stopped mid-traffic without an error, and hev lost every SOCKS session in the same
    /// second. Two things that end this process explain themselves only on stderr — a Go
    /// panic or fatal error inside xray-core, which prints the reason and the goroutine
    /// stack and then calls <c>ExitProcess(2)</c>; and the .NET runtime's own fatal paths —
    /// a stack overflow, a fail-fast, an access violation in native code — which print
    /// "Stack overflow." or "Fatal error." and terminate. A GUI process has no stderr, so all
    /// of it went nowhere.</para>
    ///
    /// <para><b>Why SetStdHandle is enough.</b> Both writers look the handle up at the moment
    /// they write: Go's runtime calls <c>GetStdHandle(STD_ERROR_HANDLE)</c> on every write to
    /// fd 2, and CoreCLR's <c>PrintToStdErr</c> does the same. Pointing the process's standard
    /// error at a file before anything can fail is therefore all it takes, with no change to
    /// the core. It is still done before xray.dll loads, so Go's <c>os.Stderr</c>, which is
    /// bound once at package init, lands in the same file.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class NativeCrashCapture
    {
        /// <summary>What the previous session wrote to stderr.</summary>
        /// <param name="At">When the file was last written — for a crash, the moment of it.</param>
        /// <param name="Headline">The line that names a fatal error, or null if none does.</param>
        /// <param name="Text">The head of the output.</param>
        public sealed record PreviousOutput(DateTimeOffset At, string? Headline, string Text);

        private const int StdErrorHandle = -12;
        private const uint SemNoGpFaultErrorBox = 0x0002;

        /// <summary>
        /// How much of the previous output is read back. The panic line and the goroutine that
        /// raised it come first; a Go fatal error goes on to dump every goroutine, which in a
        /// busy core runs to megabytes. The whole file still goes into the report archive.
        /// </summary>
        private const int HeadBytes = 16 * 1024;

        /// <summary>
        /// Lines that open a fatal report, most specific first. Go: <c>panic:</c>,
        /// <c>fatal error:</c>, and <c>Exception 0x…</c> for a Windows exception raised in Go
        /// code. CoreCLR: the unhandled-exception, fail-fast, stack-overflow and fatal-error
        /// banners.
        /// </summary>
        private static readonly string[] FatalPrefixes =
        [
            "panic:",
            "fatal error:",
            "Fatal error.",
            "Stack overflow.",
            "Process terminated.",
            "Unhandled exception.",
            "Unhandled Exception:",
            "Exception 0x",
            "unexpected fault address",
        ];

        /// <summary>
        /// Rooted for the life of the process: the OS handle behind stderr must never be
        /// closed, and a finalised SafeFileHandle would close it.
        /// </summary>
        private static SafeFileHandle? _stderr;

        /// <summary>
        /// Keeps the previous session's output as <c>.prev</c> and points this process's
        /// standard error at a fresh file. Call before anything native is loaded.
        /// </summary>
        public static void Install()
        {
            var path = DiagnosticPaths.NativeStderrLog;
            DiagnosticPaths.Rotate(path);

            try
            {
                // Shared for reading so the report archive can copy it while it is open. Not
                // inheritable — the default — so the bridge, netsh and route, which inherit
                // handles, cannot write into it.
                _stderr = File.OpenHandle(
                    path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

                if (!SetStdHandle(StdErrorHandle, _stderr.DangerousGetHandle()))
                    Diag.Warn("app", $"stderr capture unavailable: SetStdHandle failed ({Marshal.GetLastWin32Error()})");
            }
            catch (Exception ex)
            {
                Diag.Warn("app", $"stderr capture unavailable: {ex.Message}");
            }
        }

        /// <summary>
        /// What the previous session wrote to stderr, or null when it wrote nothing. Only
        /// meaningful when that session ended abruptly; after a clean exit the file holds at
        /// most harmless noise.
        /// </summary>
        public static PreviousOutput? ReadPrevious()
        {
            try
            {
                var path = DiagnosticPaths.Previous(DiagnosticPaths.NativeStderrLog);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0) return null;

                var text = ReadHead(path).Trim();
                if (text.Length == 0) return null;

                return new PreviousOutput(new DateTimeOffset(info.LastWriteTime), FindHeadline(text), text);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Undoes what loading the Go runtime does to crash reporting for the whole process.
        ///
        /// <para>Go's runtime start-up calls <c>SetErrorMode(SEM_NOGPFAULTERRORBOX)</c>. In a Go
        /// program that is a decision about Go's own crashes. Loaded as xray.dll it is made for
        /// this entire process: from then on an access violation anywhere — in WinUI, behind a
        /// P/Invoke, in the runtime — terminates without Windows Error Reporting ever being
        /// invoked, so there is no Application Error event and no dump. That is the other half
        /// of "nothing in the logs". Go's own crashes do not change: they print and exit through
        /// the runtime, not through WER.</para>
        ///
        /// <para>The Go runtime initialises asynchronously, on a thread of its own, when the
        /// DLL loads; every export waits for that to finish. So the core is loaded with a cheap
        /// exported call first, and the flag is cleared only after it returns. That also loads
        /// the core at start-up rather than at the first connect — on a desktop, a fair price
        /// for knowing the error mode is right before anything can fail.</para>
        /// </summary>
        public static void RestoreErrorReportingAfterCoreLoads()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    XrayInterop.Version();

                    var mode = GetErrorMode();
                    if ((mode & SemNoGpFaultErrorBox) == 0) return;

                    SetErrorMode(mode & ~SemNoGpFaultErrorBox);
                    Diag.Trace("app", "crash reporting re-enabled after the core turned it off");
                }
                catch (Exception ex)
                {
                    Diag.Trace("app", $"crash reporting not restored: {ex.Message}");
                }
            });
        }

        private static string ReadHead(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var buffer = new byte[(int)Math.Min(stream.Length, HeadBytes)];
            stream.ReadExactly(buffer);

            // Both writers emit ASCII for everything that matters; UTF-8 decoding keeps any
            // path or message that is not.
            return Encoding.UTF8.GetString(buffer);
        }

        private static string? FindHeadline(string text)
        {
            var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            foreach (var prefix in FatalPrefixes)
                foreach (var line in lines)
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                        return line.Length <= 300 ? line : line[..300];

            return null;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetStdHandle(int stdHandle, IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern uint GetErrorMode();

        [DllImport("kernel32.dll")]
        private static extern uint SetErrorMode(uint mode);
    }
}
