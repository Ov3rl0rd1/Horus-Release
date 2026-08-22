namespace Horus.Domain.Models
{
    /// <summary>Severity of a diagnostic record. Ordered, so a level filter is a comparison.</summary>
    public enum DiagLevel
    {
        /// <summary>Per-sample detail. Off unless the user turns verbose logging on.</summary>
        Trace,
        /// <summary>Normal lifecycle: connected, network changed, protocol chosen.</summary>
        Info,
        /// <summary>Something recoverable went wrong.</summary>
        Warn,
        /// <summary>A failure the user will notice.</summary>
        Error,
        /// <summary>The process is about to die, or the tunnel is unrecoverable.</summary>
        Critical
    }

    /// <summary>
    /// Where <see cref="Diag"/> records end up. Declared here so the Domain layer can be
    /// logged from without depending on the Application layer that does the file I/O;
    /// <c>EventLog.Install()</c> plugs the real implementation in at startup.
    /// </summary>
    public interface IDiagSink
    {
        void Emit(DiagLevel level, string category, string message, string? detail, bool userAction);

        /// <summary>
        /// Writes everything buffered, synchronously, right now. Called from the crash
        /// handler, where the process has microseconds left and an async flush will not run.
        /// </summary>
        void Flush();
    }

    /// <summary>
    /// The app's diagnostic stream.
    ///
    /// <para><b>Why this exists at all.</b> Everything the connect path, the health monitor
    /// and the updater had to say used to go through
    /// <c>System.Diagnostics.Debug.WriteLine</c>, which carries
    /// <c>[Conditional("DEBUG")]</c> and is therefore compiled out of every shipping build.
    /// A field failure was undiagnosable because the one line that identified it —
    /// "DNS FAILED — using hostname" — was written that way.</para>
    ///
    /// <para><b>Two sinks, on purpose.</b> logcat under one tag
    /// (<c>adb logcat -s HorusDiag</c>) is what a developer with a cable uses. The
    /// <see cref="Sink"/> is what survives to a user's bug report: it writes to a file that
    /// outlives the process, which logcat does not — a Release build's logcat is
    /// unreachable to the person actually hitting the bug.</para>
    ///
    /// <para>Call sites use <see cref="Write"/> with a bracketed prefix
    /// (<c>"[health] OutboundDead: …"</c>) and the category is lifted out of it, so the
    /// hundreds of existing calls did not have to change to become structured.</para>
    /// </summary>
    public static class Diag
    {
        /// <summary>Filter with <c>adb logcat -s HorusDiag</c>.</summary>
        public const string Tag = "HorusDiag";

        /// <summary>
        /// Records below this are dropped before they reach either sink. Raised to
        /// <see cref="DiagLevel.Trace"/> when the user enables verbose logging.
        /// </summary>
        public static DiagLevel MinLevel { get; set; } = DiagLevel.Info;

        /// <summary>The persistent sink. Null until the Application layer installs one.</summary>
        public static IDiagSink? Sink { get; set; }

        /// <summary>
        /// Legacy entry point, and still the common one. Infers severity and category from
        /// a leading <c>[tag]</c> so existing call sites keep working unchanged.
        /// </summary>
        public static void Write(string line)
        {
            var (category, message) = SplitCategory(line);
            Emit(InferLevel(category, message), category, message, null, userAction: false);
        }

        public static void Trace(string category, string message, string? detail = null) =>
            Emit(DiagLevel.Trace, category, message, detail, false);

        public static void Info(string category, string message, string? detail = null, bool userAction = false) =>
            Emit(DiagLevel.Info, category, message, detail, userAction);

        public static void Warn(string category, string message, string? detail = null) =>
            Emit(DiagLevel.Warn, category, message, detail, false);

        public static void Error(string category, string message, string? detail = null) =>
            Emit(DiagLevel.Error, category, message, detail, false);

        public static void Critical(string category, string message, string? detail = null) =>
            Emit(DiagLevel.Critical, category, message, detail, false);

        /// <summary>
        /// Records something the user did, so a bug report can separate "the app decided"
        /// from "the user asked". Borrowed from Rethink's EventLogger, where it turned out
        /// to be the fastest way to read a timeline nobody was watching at the time.
        /// </summary>
        public static void User(string category, string message, string? detail = null) =>
            Emit(DiagLevel.Info, category, message, detail, userAction: true);

        private static void Emit(DiagLevel level, string category, string message, string? detail, bool userAction)
        {
            if (level < MinLevel) return;

            try
            {
#if ANDROID
                var text = detail is null ? $"[{category}] {message}" : $"[{category}] {message} — {detail}";
                switch (level)
                {
                    case DiagLevel.Trace: global::Android.Util.Log.Debug(Tag, text); break;
                    case DiagLevel.Warn: global::Android.Util.Log.Warn(Tag, text); break;
                    case DiagLevel.Error:
                    case DiagLevel.Critical: global::Android.Util.Log.Error(Tag, text); break;
                    default: global::Android.Util.Log.Info(Tag, text); break;
                }
#else
                System.Diagnostics.Trace.WriteLine($"[{Tag}][{level}][{category}] {message}");
#endif
            }
            catch { /* a logging failure must never propagate */ }

            try { Sink?.Emit(level, category, message, detail, userAction); }
            catch { /* ditto */ }
        }

        /// <summary>Pulls "net" out of "[net] wifi -&gt; cellular".</summary>
        private static (string Category, string Message) SplitCategory(string line)
        {
            if (line.Length > 2 && line[0] == '[')
            {
                var close = line.IndexOf(']');
                if (close > 1 && close < 24)
                    return (line[1..close], line[(close + 1)..].TrimStart());
            }
            return ("app", line);
        }

        /// <summary>
        /// Best-effort severity for the untyped <see cref="Write"/> path. Deliberately
        /// conservative: a false Info is noise, a false Error is a bug report chasing
        /// something that never happened.
        /// </summary>
        private static DiagLevel InferLevel(string category, string message)
        {
            if (category is "crash") return DiagLevel.Critical;

            if (message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("err ", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("не удалось", StringComparison.OrdinalIgnoreCase))
                return DiagLevel.Error;

            if (category is "health" or "recover") return DiagLevel.Warn;

            return DiagLevel.Info;
        }
    }
}
