namespace Horus.Domain.Models
{
    /// <summary>
    /// Where the diagnostic files live.
    ///
    /// <para>All of them are under the cache directory, for two reasons: the OS can reclaim
    /// them under pressure, and it is the only location on Android another app can be
    /// granted access to — which the share sheet needs, since the archive is delivered by
    /// handing it to a mail or messaging client. Anything under
    /// <c>LocalApplicationData</c> is invisible to every other app on the device.</para>
    /// </summary>
    public static class DiagnosticPaths
    {
        public static string LogDirectory
        {
            get
            {
                var dir = Path.Combine(FileSystem.CacheDirectory, "logs");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>xray-core's error log, set via the generated config's <c>log.error</c>.</summary>
        public static string XrayLog => Path.Combine(LogDirectory, "xray.log");

        /// <summary>hev-socks5-tunnel's log, set via its YAML <c>misc.log-file</c>.</summary>
        public static string HevLog => Path.Combine(LogDirectory, "hev.log");

        /// <summary>
        /// Unhandled exceptions, appended synchronously as the process dies. Never
        /// truncated on connect — a crash log the next session erases is not a crash log.
        /// Trimmed by size only, and only from the front.
        /// </summary>
        public static string CrashLog => Path.Combine(LogDirectory, "crash.log");

        /// <summary>The previous session's copy of <paramref name="path"/>.</summary>
        public static string Previous(string path) => path + ".prev";

        /// <summary>
        /// Starts a fresh log for this session while keeping the last one.
        ///
        /// <para>This used to be <c>File.Delete</c>, and that was the bug: a session that
        /// died was diagnosable right up until the app restarted and connected, at which
        /// point the only record of what went wrong was erased. Moving instead of deleting
        /// costs one file and keeps the failing session readable through the next one.</para>
        ///
        /// <para>Both native components hold their log file open, so the rename relies on
        /// POSIX semantics — the inode survives and the writer keeps writing to the
        /// renamed file. That is fine here because this is only called while the component
        /// is stopped; calling it mid-session would silently send new lines to
        /// <c>.prev</c>. 🔧 A size cap during a live session needs support inside the
        /// components themselves.</para>
        /// </summary>
        public static void Rotate(string path)
        {
            try
            {
                if (!File.Exists(path)) return;

                var prev = Previous(path);
                if (File.Exists(prev)) File.Delete(prev);
                File.Move(path, prev);
            }
            catch { /* a locked log is not worth failing a connect over */ }
        }

        /// <summary>
        /// Caps a file that no component holds open, keeping the newest bytes.
        /// Used for <see cref="CrashLog"/>, which is appended to across sessions and would
        /// otherwise be the one file here that grows forever.
        /// </summary>
        public static void CapFromFront(string path, long maxBytes)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= maxBytes) return;

                var keep = new byte[maxBytes];
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    source.Seek(-maxBytes, SeekOrigin.End);
                    source.ReadExactly(keep);
                }
                File.WriteAllBytes(path, keep);
            }
            catch { }
        }

        /// <summary>Every diagnostic file that exists, as (path, name-in-archive) pairs.</summary>
        public static IEnumerable<(string Path, string Name)> ArchiveFiles()
        {
            foreach (var (path, name) in new[]
            {
                (XrayLog, "xray.log"),
                (Previous(XrayLog), "xray.prev.log"),
                (HevLog, "hev.log"),
                (Previous(HevLog), "hev.prev.log"),
                (CrashLog, "crash.log"),
            })
            {
                if (File.Exists(path)) yield return (path, name);
            }
        }
    }
}
