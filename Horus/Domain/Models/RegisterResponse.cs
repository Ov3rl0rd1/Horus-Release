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
    }

    /// <summary>Body of the reset-request / reset-check / reset-confirm endpoints.</summary>
    public class StatusResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;
    }
}
