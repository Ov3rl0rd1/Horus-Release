using System.Text.Json.Serialization;

namespace Horus.Domain.Models
{
    /// <summary>
    /// <c>GET /whoami</c> — the caller's egress IP as the API sees it, plus account
    /// state. This is the authoritative source for the <b>subscription</b> expiry;
    /// the <c>expiresAt</c> on a login response is the <b>session</b> expiry.
    /// </summary>
    public class WhoAmIResponse
    {
        [JsonPropertyName("ip")]
        public string Ip { get; set; } = string.Empty;

        [JsonPropertyName("ipVersion")]
        public string IpVersion { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("emailVerified")]
        public bool EmailVerified { get; set; }

        [JsonPropertyName("subscriptionExpiresAt")]
        public DateTime? SubscriptionExpiresAt { get; set; }

        [JsonPropertyName("currentServerId")]
        public int? CurrentServerId { get; set; }

        [JsonPropertyName("observedAt")]
        public DateTime ObservedAt { get; set; }
    }
}
