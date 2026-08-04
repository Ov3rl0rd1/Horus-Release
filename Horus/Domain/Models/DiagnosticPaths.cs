namespace Horus.Domain.Models
{
    /// <summary>
    /// Where the two native components write their logs. Both live in the cache
    /// directory so the OS can reclaim them, and so the diagnostics archive — which is
    /// shared out through the system share sheet — can reach them. Anything under
    /// <c>LocalApplicationData</c> on Android is invisible to every other app,
    /// including mail and messaging clients.
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

        /// <summary>Truncates a log so each session starts clean and the files stay bounded.</summary>
        public static void Truncate(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* a locked log is not worth failing a connect over */ }
        }
    }
}
