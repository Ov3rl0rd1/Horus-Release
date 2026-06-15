using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Horus.Application
{
    /// <summary>
    /// Collects sanitized error information (no personal data) and sends it to
    /// the backend on demand. Falls back to a local zip archive that the user
    /// can email manually.
    /// </summary>
    public class ErrorReportingService : IErrorReportingService
    {
        private const int MaxEntries = 200;
        private const string SupportEmail = "support@horus-vpn.app";

        private readonly IApiService _api;
        private readonly List<ErrorEntry> _entries = [];
        private readonly object _lock = new();
        private readonly string _reportDir;

        private string? _lastProtocolLog;

        public string? LastReportArchivePath { get; private set; }

        public ErrorReportingService(IApiService api)
        {
            _api = api;
            _reportDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Horus", "reports");
            Directory.CreateDirectory(_reportDir);
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
                    _lastProtocolLog = TruncateLog(protocolLog, maxLines: 200);
            }
        }

        public async Task<bool> FlushAsync(CancellationToken ct = default)
        {
            var report = BuildReport();
            var archivePath = BuildArchive(report);
            LastReportArchivePath = archivePath;

            // Do not send if we know there's no internet
            if (!await HasInternetAsync(ct))
                return false;

            var sent = await _api.SendErrorReportAsync(report, ct);
            if (sent)
            {
                lock (_lock)
                {
                    _entries.Clear();
                    _lastProtocolLog = null;
                }
            }
            return sent;
        }

        public string BuildSupportEmailUri()
        {
            var subject = Uri.EscapeDataString("Horus VPN — Error Report");
            var body = Uri.EscapeDataString(
                $"Please find the error report attached.\n\n" +
                $"App version: {AppConfiguration.AppVersion}\n" +
                $"Platform: {DeviceInfo.Platform}\n" +
                $"Report file: {LastReportArchivePath ?? "not generated yet"}\n\n" +
                "Please attach the file above to this email before sending.");
            return $"mailto:{SupportEmail}?subject={subject}&body={body}";
        }

        // ── Private helpers ──────────────────────────────────────────────────

        private ErrorReport BuildReport()
        {
            List<ErrorEntry> snapshot;
            string? log;
            lock (_lock)
            {
                snapshot = [.. _entries];
                log = _lastProtocolLog;
            }

            return new ErrorReport
            {
                AppVersion = AppConfiguration.AppVersion,
                Platform = DeviceInfo.Platform.ToString(),
                OsVersion = DeviceInfo.VersionString,
                OccurredAt = DateTime.UtcNow,
                Errors = snapshot,
                ProtocolLog = log,
                NetworkDiagnostics = null // populated by FlushAsync caller if needed
            };
        }

        private string BuildArchive(ErrorReport report)
        {
            var path = Path.Combine(_reportDir,
                $"horus_report_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip");

            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

            // report.json
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            var jsonEntry = zip.CreateEntry("report.json", CompressionLevel.Optimal);
            using (var sw = new StreamWriter(jsonEntry.Open()))
                sw.Write(json);

            // protocol.log (if any)
            if (!string.IsNullOrEmpty(report.ProtocolLog))
            {
                var logEntry = zip.CreateEntry("protocol.log", CompressionLevel.Optimal);
                using var sw = new StreamWriter(logEntry.Open());
                sw.Write(report.ProtocolLog);
            }

            return path;
        }

        private static async Task<bool> HasInternetAsync(CancellationToken ct)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("8.8.8.8", 53, ct);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Removes any tokens, IPs, or paths that might identify the user.</summary>
        private static string Sanitize(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;

            // Strip IP addresses (rough)
            message = System.Text.RegularExpressions.Regex.Replace(
                message, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", "[IP]");
            // Strip Bearer tokens
            message = System.Text.RegularExpressions.Regex.Replace(
                message, @"Bearer\s+[A-Za-z0-9\-._~+/]+=*", "Bearer [TOKEN]");
            // Strip file paths that contain username
            message = System.Text.RegularExpressions.Regex.Replace(
                message, @"[A-Z]:\\Users\\[^\\]+\\", @"C:\Users\[USER]\");
            message = System.Text.RegularExpressions.Regex.Replace(
                message, @"/home/[^/]+/", "/home/[USER]/");

            return message;
        }

        private static string? SanitizeStackTrace(string? trace)
        {
            if (trace == null) return null;
            // Strip full paths, keep class/method names
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
