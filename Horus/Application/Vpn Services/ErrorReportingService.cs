using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Horus.Application
{
    /// <summary>
    /// Collects sanitized diagnostics into a zip the user can hand back.
    ///
    /// There is no ingest endpoint on HorusAPI v1, so nothing is uploaded: the archive is
    /// written to the cache directory — the only place on Android another app can be
    /// granted access to — and delivered through the system share sheet, or Explorer on
    /// Windows.
    /// </summary>
    public class ErrorReportingService : IErrorReportingService
    {
        private const int MaxEntries = 200;
        private const int MaxLogLines = 500;

        private readonly List<ErrorEntry> _entries = [];
        private readonly Queue<string> _sessionLog = new();
        private readonly Dictionary<string, string> _context = [];
        private readonly object _lock = new();

        private string? _lastProtocolLog;

        public string? LastReportArchivePath { get; private set; }

        public bool HasPendingReports
        {
            get { lock (_lock) return _entries.Count > 0; }
        }

        public IReadOnlyList<string> SessionLog
        {
            get { lock (_lock) return [.. _sessionLog]; }
        }

        private static string ReportDirectory
        {
            get
            {
                var dir = Path.Combine(FileSystem.CacheDirectory, "reports");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public void RecordException(Exception ex, string context, Dictionary<string, string>? extra = null)
        {
            var entry = new ErrorEntry
            {
                Context = context,
                ExceptionType = ex.GetType().Name,
                Message = Sanitize(ex.Message),
                StackTrace = SanitizeStackTrace(ex.StackTrace),
                Timestamp = DateTime.UtcNow,
                Extra = extra ?? []
            };

            lock (_lock)
            {
                _entries.Add(entry);
                if (_entries.Count > MaxEntries)
                    _entries.RemoveAt(0);
            }
        }

        public void RecordConnectionFailure(string protocol, string reason, string? protocolLog = null)
        {
            RecordException(
                new Exception(reason),
                $"ConnectionFailure/{protocol}",
                new Dictionary<string, string> { ["protocol"] = protocol });

            if (!string.IsNullOrEmpty(protocolLog))
            {
                lock (_lock)
                    _lastProtocolLog = TruncateLog(protocolLog, maxLines: MaxLogLines);
            }
        }

        public void AppendLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            lock (_lock)
            {
                _sessionLog.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {line}");
                while (_sessionLog.Count > MaxLogLines) _sessionLog.Dequeue();
            }
        }

        public void SetContext(string key, string? value)
        {
            lock (_lock)
            {
                if (value is null) _context.Remove(key);
                else _context[key] = value;
            }
        }

        public async Task<string> BuildArchiveAsync(CancellationToken ct = default)
        {
            var report = BuildReport();
            var path = Path.Combine(ReportDirectory,
                $"horus_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

            await Task.Run(() => WriteArchive(path, report), ct);

            LastReportArchivePath = path;
            PruneOldArchives();
            return path;
        }

        public async Task<bool> ShareArchiveAsync(string archivePath)
        {
            if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
                return false;

            try
            {
#if WINDOWS
                // Explorer, with the archive preselected — the user attaches it themselves.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{archivePath}\"",
                    UseShellExecute = true
                });
                return true;
#else
                await Share.Default.RequestAsync(new ShareFileRequest
                {
                    Title = "Диагностика Horus",
                    File = new ShareFile(archivePath)
                });
                return true;
#endif
            }
            catch
            {
                return false;
            }
        }

        // ── Private helpers ──────────────────────────────────────────────────

        private ErrorReport BuildReport()
        {
            List<ErrorEntry> snapshot;
            string? log;
            Dictionary<string, string> context;

            lock (_lock)
            {
                snapshot = [.. _entries];
                log = _lastProtocolLog;
                context = new Dictionary<string, string>(_context);
            }

            // Device/build context is written unsanitized: it identifies the build, not
            // the user, and stripping it is what made previous reports unactionable.
            context["appVersion"] = AppConfiguration.AppVersion;
            context["platform"] = DeviceInfo.Platform.ToString();
            context["osVersion"] = DeviceInfo.VersionString;
            context["model"] = $"{DeviceInfo.Manufacturer} {DeviceInfo.Model}";
            context["idiom"] = DeviceInfo.Idiom.ToString();
            context["connectivity"] = Connectivity.Current.NetworkAccess.ToString();
            context["apiBaseUrl"] = AppConfiguration.ApiBaseUrl;

            return new ErrorReport
            {
                AppVersion = AppConfiguration.AppVersion,
                Platform = DeviceInfo.Platform.ToString(),
                OsVersion = DeviceInfo.VersionString,
                OccurredAt = DateTime.UtcNow,
                Errors = snapshot,
                ProtocolLog = log,
                Context = context,
                SessionLog = SessionLog
            };
        }

        private void WriteArchive(string path, ErrorReport report)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

            AddText(zip, "report.json",
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

            if (report.SessionLog.Count > 0)
                AddText(zip, "session.log", string.Join(Environment.NewLine, report.SessionLog));

            if (!string.IsNullOrEmpty(report.ProtocolLog))
                AddText(zip, "protocol.log", report.ProtocolLog);

            // The two native components log to their own files; both halves of the
            // pipeline are useless to diagnose without them.
            AddFileIfPresent(zip, DiagnosticPaths.XrayLog, "xray.log");
            AddFileIfPresent(zip, DiagnosticPaths.HevLog, "hev.log");
        }

        private static void AddText(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var sw = new StreamWriter(entry.Open());
            sw.Write(content);
        }

        private static void AddFileIfPresent(ZipArchive zip, string sourcePath, string entryName)
        {
            try
            {
                if (!File.Exists(sourcePath)) return;

                // Copy first: the native side may still hold the file open.
                using var source = new FileStream(
                    sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var target = entry.Open();
                source.CopyTo(target);
            }
            catch { /* a missing or locked log must not fail the archive */ }
        }

        /// <summary>Keeps the newest few archives so the cache doesn't grow without bound.</summary>
        private static void PruneOldArchives()
        {
            try
            {
                var stale = Directory.GetFiles(ReportDirectory, "horus_*.zip")
                    .OrderByDescending(File.GetCreationTimeUtc)
                    .Skip(5);
                foreach (var file in stale) File.Delete(file);
            }
            catch { /* housekeeping only */ }
        }

        /// <summary>Removes tokens, IPs and paths that might identify the user. Applied to
        /// exception text only — the context block is deliberately left intact.</summary>
        private static string Sanitize(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;

            message = System.Text.RegularExpressions.Regex.Replace(
                message, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", "[IP]");
            message = System.Text.RegularExpressions.Regex.Replace(
                message, @"Bearer\s+[A-Za-z0-9\-._~+/]+=*", "Bearer [TOKEN]");
            message = System.Text.RegularExpressions.Regex.Replace(
                message, @"[A-Z]:\\Users\\[^\\]+\\", @"C:\Users\[USER]\");
            message = System.Text.RegularExpressions.Regex.Replace(
                message, @"/home/[^/]+/", "/home/[USER]/");

            return message;
        }

        private static string? SanitizeStackTrace(string? trace)
        {
            if (trace == null) return null;
            return System.Text.RegularExpressions.Regex.Replace(
                trace, @" in [A-Za-z]:\\.*?\.cs:line \d+", " in [source]");
        }

        private static string TruncateLog(string log, int maxLines)
        {
            var lines = log.Split('\n');
            if (lines.Length <= maxLines) return log;
            var sb = new StringBuilder();
            foreach (var line in lines.TakeLast(maxLines))
                sb.AppendLine(line);
            return sb.ToString();
        }
    }
}
