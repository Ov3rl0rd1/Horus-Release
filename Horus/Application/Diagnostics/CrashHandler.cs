using Horus.Domain.Models;
using System.Text;

namespace Horus.Application.Diagnostics
{
    /// <summary>
    /// Catches what would otherwise vanish: an exception on a background thread or the
    /// finalizer queue, which takes the process down without touching a single
    /// <c>catch</c> block in this app.
    ///
    /// <para>Before this, a nightly crash left nothing behind. <see cref="Diag"/> went to
    /// logcat, which a user on a non-debuggable build cannot read; the session log lived in
    /// memory; and the native logs were truncated by the next connect. The failures worth
    /// investigating were exactly the ones that erased their own evidence.</para>
    ///
    /// <para><b>Everything here is synchronous.</b> The process has microseconds between
    /// the handler running and the runtime aborting. <c>Task.Run</c>, <c>async</c>, or
    /// anything that defers will simply not run, so the record is written with a blocking
    /// <c>File.AppendAllText</c> and the event log is flushed on the calling thread.</para>
    ///
    /// <para>🔧 <b>Does not cover a Go panic.</b> xray-core runs in this process, and a
    /// panic inside it calls <c>abort()</c> directly — no managed exception is ever raised
    /// and none of these handlers fire. That needs a <c>defer recover()</c> on every
    /// exported function in the core fork; until then, a crash with no <c>crash.log</c>
    /// entry but a truncated <c>xray.log</c> is the signature to look for.</para>
    /// </summary>
    public static class CrashHandler
    {
        private const long MaxCrashLogBytes = 128 * 1024;

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private static readonly object Sync = new();
        private static bool _installed;

        /// <summary>
        /// Whether the previous run ended without unwinding — no managed exception, no clean
        /// exit, the process simply stopped existing.
        ///
        /// <para>This is the only evidence available for the crash class the handlers below
        /// cannot see. xray-core runs in this process and a Go panic calls <c>abort()</c>
        /// directly; so does a fault in any of the native libraries. Nothing managed is
        /// raised, nothing is written, and the user's report is "it closed by itself and
        /// there is nothing in the logs" — which is accurate and unactionable. A marker file
        /// written at start and removed on a clean exit turns that into a fact.</para>
        /// </summary>
        public static bool PreviousSessionEndedAbruptly { get; private set; }

        /// <summary>
        /// Hooks every managed crash path. Call as early as possible — before the DI
        /// container is built, and before MAUI initialises — so a failure during startup is
        /// covered too.
        /// </summary>
        public static void Install()
        {
            lock (Sync)
            {
                if (_installed) return;
                _installed = true;
            }

            // Read before the new one is written, or the run always looks like it crashed.
            PreviousSessionEndedAbruptly = ClaimSessionMarker();

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Capture("AppDomain", e.ExceptionObject as Exception, terminating: e.IsTerminating);

            // A clean exit is the only thing that clears the marker. ProcessExit does not run
            // for a kill or a native abort, which is exactly the distinction being drawn.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseSessionMarker();

            // Without SetObserved this escalates to a process kill on some configurations.
            // An unobserved task exception is a bug worth recording, not worth dying for.
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Capture("UnobservedTask", e.Exception, terminating: false);
                e.SetObserved();
            };

#if ANDROID
            // The one that actually fires for exceptions crossing the JNI boundary — an
            // exception thrown on a thread the Android runtime owns never reaches
            // AppDomain.UnhandledException. Handled stays false: swallowing it would leave
            // the app running in a state the runtime has already given up on.
            global::Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
            {
                Capture("AndroidEnvironment", e.Exception, terminating: true);
                e.Handled = false;
            };
#endif
        }

        /// <summary>
        /// Notes that this session is running, and reports whether the previous one ever
        /// said it had finished.
        /// </summary>
        private static bool ClaimSessionMarker()
        {
            var path = DiagnosticPaths.SessionMarker;

            try
            {
                var abrupt = File.Exists(path);
                File.WriteAllText(path, DateTimeOffset.Now.ToString("O"), Utf8NoBom);
                return abrupt;
            }
            catch
            {
                // Unable to tell. Claiming a crash on no evidence would put a warning in
                // front of the user every launch.
                return false;
            }
        }

        private static void ReleaseSessionMarker()
        {
            try { File.Delete(DiagnosticPaths.SessionMarker); } catch { }
        }

        /// <summary>
        /// Records a crash that has already been caught elsewhere. Public so the connect
        /// path can log a fatal it decided to handle, and get the same on-disk treatment.
        /// </summary>
        public static void Capture(string source, Exception? ex, bool terminating)
        {
            try
            {
                var text = new StringBuilder()
                    .Append("=== ").Append(DateTimeOffset.Now.ToString("O"))
                    .Append(" [").Append(source).Append(']')
                    .Append(terminating ? " TERMINATING" : "")
                    .Append(" ===\n")
                    .Append(ex?.ToString() ?? "(no exception object)")
                    .Append("\n\n")
                    .ToString();

                var path = DiagnosticPaths.CrashLog;

                // No BOM: this file is read back by LastCrash and shipped in the archive,
                // and a mark in front of the first record is noise in both.
                File.AppendAllText(path, text, Utf8NoBom);
                DiagnosticPaths.CapFromFront(path, MaxCrashLogBytes);
            }
            catch { /* nothing useful left to do */ }

            // Both sinks, in this order: the file above is the durable record, the event
            // log gives the crash its place in the timeline of what led up to it.
            try { Diag.Critical("crash", $"{source}: {ex?.GetType().Name}: {ex?.Message}"); } catch { }
            try { EventLog.Instance?.Flush(); } catch { }
        }

        /// <summary>
        /// Whether the previous session ended in a crash, and when. Read at startup so the
        /// Settings screen can say so instead of the user having to notice.
        /// </summary>
        public static (bool Crashed, DateTimeOffset? At, string? Summary) LastCrash()
        {
            try
            {
                var path = DiagnosticPaths.CrashLog;
                if (!File.Exists(path)) return (false, null, null);

                // Only the last record matters, and the file is capped at 128 KB, so
                // reading it whole is cheaper than seeking backwards for a delimiter.
                var text = File.ReadAllText(path);
                var start = text.LastIndexOf("=== ", StringComparison.Ordinal);
                if (start < 0) return (false, null, null);

                var header = text[start..];
                var newline = header.IndexOf('\n');
                if (newline < 0) return (false, null, null);

                var stamp = header[4..].Split(' ')[0];
                var summary = header[(newline + 1)..].Split('\n')[0].Trim();

                return DateTimeOffset.TryParse(stamp, out var at)
                    ? (true, at, summary)
                    : (true, null, summary);
            }
            catch { return (false, null, null); }
        }

        /// <summary>Clears the record after the user has acknowledged it.</summary>
        public static void ClearLastCrash()
        {
            try { File.Delete(DiagnosticPaths.CrashLog); } catch { }
        }
    }
}
