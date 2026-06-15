using System.Text.Json.Serialization;

namespace Horus.Domain.Models
{
    /// <summary>
    /// Root object persisted to local JSON when in local mode.
    /// Stored at: LocalApplicationData/Horus/local_config.json
    /// </summary>
    public class LocalConfig
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("updated_at")]
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("servers")]
        public List<LocalServerEntry> Servers { get; set; } = [];

        [JsonPropertyName("routing_rules")]
        public RoutingRulesFile? RoutingRules { get; set; }

        [JsonPropertyName("split_tunneling")]
        public LocalSplitTunnelingConfig SplitTunneling { get; set; } = new();

        [JsonPropertyName("geo_db_path")]
        public string? GeoDbPath { get; set; }

        [JsonPropertyName("default_server_id")]
        public string? DefaultServerId { get; set; }
    }

    /// <summary>
    /// A manually-configured VPN server entry stored locally.
    /// </summary>
    public class LocalServerEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("host")]
        public string Host { get; set; } = string.Empty;

        [JsonPropertyName("port")]
        public int Port { get; set; } = 443;

        [JsonPropertyName("protocol")]
        public string Protocol { get; set; } = "Hysteria2";

        [JsonPropertyName("auth_token")]
        public string AuthToken { get; set; } = string.Empty;

        [JsonPropertyName("obfs_type")]
        public string? ObfsType { get; set; }

        [JsonPropertyName("obfs_password")]
        public string? ObfsPassword { get; set; }

        [JsonPropertyName("ports_range")]
        public string? PortsRange { get; set; }

        [JsonPropertyName("lazy_tls")]
        public bool LazyTls { get; set; }

        /// <summary>
        /// Raw YAML/JSON config override. When set, all other fields are ignored
        /// and this is passed directly to the protocol binary.
        /// </summary>
        [JsonPropertyName("raw_config")]
        public string? RawConfig { get; set; }

        [JsonPropertyName("socks5_address")]
        public string Socks5Address { get; set; } = "127.0.0.1:1080";

        // ── Derived helpers ──────────────────────────────────────────────────

        public ServerInfo ToServerInfo() => new()
        {
            Id = int.TryParse(Id, out var intId) ? intId : Math.Abs(Id.GetHashCode() % 10000),
            Name = Name,
            Host = Host,
            Protocol = Protocol,
            Country = "?",
            City = "Local"
        };

        public string ToDisplayName() => $"{Name} [{Host}:{Port}]";

        // Compiled-binding-safe alias used in XAML CollectionView
        [System.Text.Json.Serialization.JsonIgnore]
        public string DisplayText => $"{Host}:{Port}  ({Protocol})";
    }

    public class LocalSplitTunnelingConfig
    {
        [JsonPropertyName("mode")]
        public SplitTunnelingMode Mode { get; set; } = SplitTunnelingMode.Disabled;

        [JsonPropertyName("entries")]
        public List<string> Entries { get; set; } = [];
    }
}
