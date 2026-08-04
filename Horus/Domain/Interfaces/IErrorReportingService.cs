namespace Horus.Domain.Interfaces
{
    public interface IErrorReportingService
    {
        /// <summary>Records an exception. Call from any catch block.</summary>
        void RecordException(Exception ex, string context, Dictionary<string, string>? extra = null);

        /// <summary>Records a connection failure (includes protocol logs, diagnostics).</summary>
        void RecordConnectionFailure(string protocol, string reason, string? protocolLog = null);

        /// <summary>
        /// Appends one line to the rolling session log that ends up in the archive.
        /// Fed from the VPN manager's protocol output, so a "connected but nothing loads"
        /// session still produces something to read — the failure-only capture path never
        /// fires in that case.
        /// </summary>
        void AppendLog(string line);

        /// <summary>The rolling session log, newest last.</summary>
        IReadOnlyList<string> SessionLog { get; }

        /// <summary>Extra key/value context folded into the next archive (preflight IPs, core version…).</summary>
        void SetContext(string key, string? value);

        /// <summary>
        /// Writes a diagnostics archive to a shareable location and returns its path.
        /// Nothing is uploaded — the API has no ingest endpoint — so delivery is the
        /// caller's job via <see cref="ShareArchiveAsync"/>.
        /// </summary>
        Task<string> BuildArchiveAsync(CancellationToken ct = default);

        /// <summary>
        /// Hands the archive to the user: the system share sheet on Android/iOS, or the
        /// containing folder in Explorer on Windows. Returns false if neither worked.
        /// </summary>
        Task<bool> ShareArchiveAsync(string archivePath);

        /// <summary>Path to the most recent archive, if one has been built.</summary>
        string? LastReportArchivePath { get; }

        /// <summary>True when errors have been recorded that the user has not yet sent.</summary>
        bool HasPendingReports { get; }
    }
}
