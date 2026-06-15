using System.Text.Json.Serialization;

namespace Horus.Domain.Models
{
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

        [JsonPropertyName("protocol")]
        public string Protocol { get; set; } = string.Empty;

        [JsonPropertyName("current_load")]
        public int CurrentLoad { get; set; }

        [JsonPropertyName("max_clients")]
        public int MaxClients { get; set; }

        public string Location => $"{City}, {Country}";
        public int MaxUsers => MaxClients;
        public int CurrentUserCount => CurrentLoad;
    }
}
