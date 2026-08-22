using System.Text.Json.Serialization;

namespace Horus.Domain.Models
{
    /// <summary>
    /// A node the client may choose, from <c>GET /servers</c> (<c>PingCandidate</c>).
    ///
    /// <para>The endpoint returns the least-loaded node <b>with free capacity</b> in each
    /// country. It deliberately carries no display name — the API only names a node once
    /// the caller is bound to it (<see cref="BoundServer"/>) — so <see cref="Name"/> is
    /// derived from the location here.</para>
    ///
    /// <para><see cref="ReservedCount"/> and <see cref="CurrentLoad"/> measure different
    /// things and are easy to confuse. Reserved is how many accounts are <i>bound</i> to
    /// the node, and it is what capacity is checked against; current load is how many are
    /// <i>online right now</i>, reported by the node's own telemetry. A node can be full
    /// (<c>ReservedCount == MaxClients</c>) with almost nobody connected.</para>
    /// </summary>
    public class ServerInfo
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("country")]
        public string Country { get; set; } = string.Empty;

        [JsonPropertyName("city")]
        public string City { get; set; } = string.Empty;

        [JsonPropertyName("host")]
        public string Host { get; set; } = string.Empty;

        /// <summary>Online now, from the node's telemetry. Display and sorting only.</summary>
        [JsonPropertyName("current_load")]
        public int CurrentLoad { get; set; }

        /// <summary>Accounts bound to this node. This is what capacity is measured on.</summary>
        [JsonPropertyName("reserved_count")]
        public int ReservedCount { get; set; }

        [JsonPropertyName("max_clients")]
        public int MaxClients { get; set; }

        /// <summary>
        /// Round-trip time measured by the client, in ms; null until probed, and null again
        /// when the node did not answer.
        ///
        /// <para>Not from the API, and it cannot be: the API sees its own latency to the
        /// node, not the user's. Measuring it is the whole reason <c>GET /servers</c>
        /// returns a list instead of a decision.</para>
        /// </summary>
        [JsonIgnore]
        public int? PingMs { get; set; }

        /// <summary>
        /// Display name. Only <see cref="BoundServer"/> carries a server-side name, and
        /// only after binding, so candidates are labelled by where they are.
        /// </summary>
        [JsonIgnore]
        public string Name => string.IsNullOrWhiteSpace(City) ? Country : City;

        public string Location => string.IsNullOrWhiteSpace(City) ? Country : $"{City}, {Country}";

        /// <summary>Free slots. Zero means the node cannot be selected — <c>409 no_capacity</c>.</summary>
        [JsonIgnore]
        public int FreeSlots => Math.Max(0, MaxClients - ReservedCount);

        [JsonIgnore]
        public bool HasCapacity => FreeSlots > 0;

        public int MaxUsers => MaxClients;
        public int CurrentUserCount => CurrentLoad;
    }
}
