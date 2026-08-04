using System.Text.Json.Serialization;

namespace Horus.Domain.Models
{
    /// <summary>Maps <c>BestServerItem</c> from <c>GET /servers/best</c>.</summary>
    public class ServerInfo
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("country")]
        public string Country { get; set; } = string.Empty;

        [JsonPropertyName("city")]
        public string City { get; set; } = string.Empty;

        [JsonPropertyName("host")]
        public string Host { get; set; } = string.Empty;

        [JsonPropertyName("current_load")]
        public int CurrentLoad { get; set; }

        [JsonPropertyName("max_clients")]
        public int MaxClients { get; set; }

        /// <summary>Measured latency in ms. Not from the API — set for mock/dev data;
        /// null for real servers until a latency-probe service exists.</summary>
        [JsonIgnore]
        public int? PingMs { get; set; }

        public string Location => $"{City}, {Country}";
        public int MaxUsers => MaxClients;
        public int CurrentUserCount => CurrentLoad;
    }
}
