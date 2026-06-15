using System.Text.Json.Serialization;

namespace Horus.Domain.Models
{
    /// <summary>
    /// JSON format served from the server at GET /routing-rules.
    /// Designed to be forward-compatible: unknown fields are ignored.
    /// </summary>
    public class RoutingRulesFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("updated_at")]
        public DateTime UpdatedAt { get; set; }

        [JsonPropertyName("default_action")]
        public string DefaultAction { get; set; } = "proxy";

        [JsonPropertyName("rules")]
        public List<RoutingRuleEntry> Rules { get; set; } = [];
    }

    public class RoutingRuleEntry
    {
        /// <summary>domain | domain_suffix | domain_keyword | ip_cidr | geoip | process</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// The pattern to match:
        ///   domain       → exact match, e.g. "youtube.com"
        ///   domain_suffix → suffix match, e.g. ".google.com"
        ///   domain_keyword → keyword match, e.g. "googlevideo"
        ///   ip_cidr      → CIDR block, e.g. "8.8.8.8/32" or "10.0.0.0/8"
        ///   geoip        → ISO 3166-1 alpha-2 country code, e.g. "RU"
        ///   process      → process image name, e.g. "chrome.exe" (Windows only)
        /// </summary>
        [JsonPropertyName("pattern")]
        public string Pattern { get; set; } = string.Empty;

        /// <summary>proxy | direct | reject</summary>
        [JsonPropertyName("action")]
        public string Action { get; set; } = "proxy";

        [JsonPropertyName("priority")]
        public int Priority { get; set; } = 0;
    }

    public class GeoDataVersion
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("updated_at")]
        public DateTime UpdatedAt { get; set; }

        [JsonPropertyName("size_bytes")]
        public long SizeBytes { get; set; }
    }
}
