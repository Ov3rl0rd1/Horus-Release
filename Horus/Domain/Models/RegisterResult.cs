namespace Horus.Domain.Models
{
    /// <summary>
    /// Outcome of <c>POST /auth/register</c> / <c>POST /auth/resend-code</c>. Registration
    /// no longer signs the user in — it mails a 6-digit code and the caller must follow up
    /// with <c>POST /auth/verify</c> to obtain a session.
    /// </summary>
    public class RegisterResult
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? ErrorCode { get; set; }
        public int StatusCode { get; set; }

        /// <summary>Address the confirmation code was mailed to.</summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>Lifetime of the mailed code, for the resend countdown.</summary>
        public int CodeExpiresInSeconds { get; set; }

        /// <summary>Seconds before another code may be requested.</summary>
        public int ResendAvailableInSeconds { get; set; }

        /// <summary>Confirmation ticket from <c>/auth/register</c> (null from resend-code).</summary>
        public string? PendingToken { get; set; }

        public static RegisterResult Fail(string message, string? code = null, int status = 0) =>
            new() { Success = false, Message = message, ErrorCode = code, StatusCode = status };
    }
}
