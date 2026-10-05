using System.Text.Json.Serialization;

namespace Horus.Domain.Models
{
    /// <summary>
    /// 202 body of <c>POST /auth/register</c> and <c>POST /auth/resend-code</c>.
    /// No session is issued here — the account stays unverified until
    /// <c>POST /auth/verify</c> succeeds.
    /// </summary>
    public class RegisterResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("codeExpiresInSeconds")]
        public int CodeExpiresInSeconds { get; set; }

        /// <summary>Seconds before another code may be requested (the resend cooldown).</summary>
        [JsonPropertyName("resendAvailableInSeconds")]
        public int ResendAvailableInSeconds { get; set; }

        /// <summary>
        /// Confirmation ticket — <b>not</b> a session. Only <c>/auth/register</c> fills it in
        /// (the caller just created the account); it lets <c>verify</c> and
        /// <c>resend-code</c> identify the account without the address. Lives 30 minutes.
        /// </summary>
        [JsonPropertyName("pendingToken")]
        public string? PendingToken { get; set; }
    }

    /// <summary>
    /// Extra body of <c>403 email_unverified</c> from <c>POST /auth/login</c>: the account
    /// exists but its address was never confirmed. Not a dead end — the ticket opens exactly
    /// <c>verify</c>, <c>resend-code</c> and <c>change-email</c>. Someone who signed in with
    /// their <b>username</b> was never told the address, so it comes back masked
    /// (<c>a***@gmail.com</c>) and the ticket is what identifies the account.
    /// </summary>
    public class PendingVerification
    {
        [JsonPropertyName("emailMasked")]
        public string EmailMasked { get; set; } = string.Empty;

        [JsonPropertyName("pendingToken")]
        public string PendingToken { get; set; } = string.Empty;

        [JsonPropertyName("pendingExpiresInSeconds")]
        public int PendingExpiresInSeconds { get; set; }

        [JsonPropertyName("resendAvailableInSeconds")]
        public int ResendAvailableInSeconds { get; set; }

        /// <summary>Life left in the code already mailed; 0 when no live code is pending.</summary>
        [JsonPropertyName("codeExpiresInSeconds")]
        public int CodeExpiresInSeconds { get; set; }
    }

    /// <summary><c>400 invalid_code</c> on the ticket path also says how many guesses remain.</summary>
    public class VerifyCodeError
    {
        [JsonPropertyName("attemptsLeft")]
        public int? AttemptsLeft { get; set; }
    }

    /// <summary>Body of the reset-request / reset-check / reset-confirm endpoints.</summary>
    public class StatusResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;
    }
}
