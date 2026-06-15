using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    public interface IErrorReportingService
    {
        /// <summary>Records an exception. Call from any catch block.</summary>
        void RecordException(Exception ex, string context, Dictionary<string, string>? extra = null);

        /// <summary>Records a connection failure (includes protocol logs, diagnostics).</summary>
        void RecordConnectionFailure(string protocol, string reason, string? protocolLog = null);

        /// <summary>
        /// Collects all recorded errors into a zip archive and tries to send it to the server.
        /// Returns true if sent, false if we need to prompt the user.
        /// </summary>
        Task<bool> FlushAsync(CancellationToken ct = default);

        /// <summary>Returns a mailto: URI pre-filled with the report attachment instructions.</summary>
        string BuildSupportEmailUri();

        /// <summary>Path to the latest report archive (if flush was called but send failed).</summary>
        string? LastReportArchivePath { get; }
    }
}
