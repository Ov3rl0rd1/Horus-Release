namespace Horus.Domain.Models
{
    public class ErrorReport
    {
        public string AppVersion { get; set; } = string.Empty;
        public string Platform { get; set; } = string.Empty;
        public string OsVersion { get; set; } = string.Empty;
        public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

        // Sanitized: no personal data, only error context
        public List<ErrorEntry> Errors { get; set; } = [];
        public NetworkDiagnostics? NetworkDiagnostics { get; set; }
        public string? ProtocolLog { get; set; }
    }

    public class ErrorEntry
    {
        public string Context { get; set; } = string.Empty;
        public string ExceptionType { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? StackTrace { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public Dictionary<string, string> Extra { get; set; } = [];
    }

    public class NetworkDiagnostics
    {
        public bool HasInternet { get; set; }
        public long? PingServerMs { get; set; }
        public double? PacketLossPercent { get; set; }
        public string? LocalIp { get; set; }
        public bool DnsResolutionWorking { get; set; }
    }
}
