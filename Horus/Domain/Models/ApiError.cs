using System.Text.Json.Serialization;

namespace Horus.Domain.Models
{
    /// <summary>Error envelope returned by every non-2xx HorusAPI response.</summary>
    public class ApiError
    {
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("code")]
        public string? Code { get; set; }
    }
}
