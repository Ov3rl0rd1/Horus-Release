namespace Horus.Domain.Models
{
    /// <summary>
    /// The app's runtime diagnostic stream, on a channel that survives a Release build.
    ///
    /// <para>This exists because of a failure that could not be diagnosed in the field.
    /// Everything the connect path, the health monitor and the updater had to say went
    /// through <see cref="System.Diagnostics.Debug.WriteLine"/>, which carries
    /// <c>[Conditional("DEBUG")]</c> and is therefore <b>compiled out of every shipping
    /// build</b>. The one line that would have identified the fault — "DNS FAILED — using
    /// hostname" — was written that way. The in-memory ring buffer kept it, but that is
    /// only reachable through the Settings screen, and xray's own log lives in a private
    /// cache directory that neither <c>adb</c> nor the user can read on a non-debuggable
    /// build.</para>
    ///
    /// <para>So: one tag, always compiled in, readable with
    /// <c>adb logcat -s HorusDiag</c> on any build. The volume is a handful of lines per
    /// connect plus one per health check, which is nothing next to what the platform itself
    /// logs.</para>
    /// </summary>
    public static class Diag
    {
        /// <summary>Filter with <c>adb logcat -s HorusDiag</c>.</summary>
        public const string Tag = "HorusDiag";

        public static void Write(string line)
        {
#if ANDROID
            global::Android.Util.Log.Info(Tag, line);
#else
            System.Diagnostics.Trace.WriteLine($"[{Tag}] {line}");
#endif
        }
    }
}
