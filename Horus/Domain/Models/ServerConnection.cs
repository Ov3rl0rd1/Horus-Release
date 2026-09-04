using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Horus.Domain.Models
{
    /// <summary>The node the account is bound to. Answer of <c>POST /servers/select</c>,
    /// and the <c>server</c> field of <c>GET /servers/connect</c>.</summary>
    public sealed class BoundServer
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("country")] public string Country { get; set; } = string.Empty;
        [JsonPropertyName("city")] public string City { get; set; } = string.Empty;
        [JsonPropertyName("host")] public string Host { get; set; } = string.Empty;

        public string Location => string.IsNullOrWhiteSpace(City) ? Country : $"{City}, {Country}";

        /// <summary>Projects onto the shape the UI already binds to.</summary>
        public ServerInfo ToServerInfo() => new()
        {
            Id = Id,
            Country = Country,
            City = City,
            Host = Host
        };
    }

    /// <summary>
    /// One ready-to-run outbound the node offers, with this account already substituted in.
    ///
    /// <para><b>The API stopped speaking in share links.</b> It now hands the app a complete
    /// xray outbound object built by the node itself. That is a deliberate inversion: the
    /// API models no protocol at all, so a node can start offering something this app has
    /// never heard of and its users get a working config the same day, with no release
    /// here. URIs still exist, but only on the subscription path for third-party clients.</para>
    ///
    /// <para>Which means <see cref="Outbound"/> is deliberately untyped and mostly opaque to
    /// us. The app reads exactly two things out of it — the <c>protocol</c> name, for a
    /// label, and the <c>address</c> fields, which have to be pre-resolved — and passes
    /// everything else through untouched.</para>
    /// </summary>
    public sealed class ClientOutbound
    {
        /// <summary>Stable id from the node's profile, e.g. <c>vless-reality</c>.</summary>
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

        /// <summary>Human-readable, shown in the UI.</summary>
        [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;

        /// <summary>The node-side inbound tag. Informational here.</summary>
        [JsonPropertyName("tag")] public string Tag { get; set; } = string.Empty;

        /// <summary>A complete xray outbound object.</summary>
        [JsonPropertyName("outbound")] public JsonNode? Outbound { get; set; }

        [JsonIgnore]
        public bool IsUsable => Outbound is JsonObject && !string.IsNullOrWhiteSpace(Id);
    }

    /// <summary>
    /// Answer of <c>GET /servers/connect</c> for a session in the <c>X-Session-Key</c>
    /// header.
    ///
    /// <para>Binding is <b>not</b> done here — <c>POST /servers/select</c> owns that, and
    /// this endpoint reads back what the caller is already bound to. It still binds by
    /// auto-pick if the account has no node at all, so a first connect works without the
    /// client having chosen.</para>
    /// </summary>
    public sealed class ServerConnection
    {
        [JsonPropertyName("server")]
        public BoundServer? Server { get; set; }

        /// <summary>
        /// Everything the node offers, <b>in the node's own order</b> — a profile lists its
        /// preferred outbound first. The connect path keeps that order and only moves an
        /// endpoint that has just failed to the back; it no longer imposes a preference of
        /// its own, because the node is the side that knows what it is running.
        /// </summary>
        [JsonPropertyName("outbounds")]
        public List<ClientOutbound> Outbounds { get; set; } = [];

        [JsonIgnore]
        public bool HasAny => Candidates().Count > 0;

        /// <summary>
        /// Everything this node can be dialled with. Unusable entries are dropped here so a
        /// caller never has to re-check.
        /// </summary>
        public IReadOnlyList<ConnectionCandidate> Candidates()
        {
            var result = new List<ConnectionCandidate>();

            foreach (var offer in Outbounds)
            {
                if (!offer.IsUsable) continue;
                result.Add(ConnectionCandidate.From(offer, Server?.Host));
            }

            return result;
        }
    }

    /// <summary>
    /// One dialable endpoint: the node's outbound plus what the app needs to know about it.
    ///
    /// <para>Identity is the offer <b>id</b>, a free-form string from the node's profile,
    /// not an enum. That is what lets the fallback loop demote "the thing that just failed"
    /// without the app having a name for it.</para>
    /// </summary>
    public sealed class ConnectionCandidate
    {
        /// <summary>Stable id within the node's profile. The key everything else uses.</summary>
        public required string Id { get; init; }

        /// <summary>What to show the user. Falls back to the id.</summary>
        public required string Label { get; init; }

        /// <summary>
        /// The <c>protocol</c> field of the outbound (<c>vless</c>, <c>hysteria</c>,
        /// <c>olcrtc</c>, …). Used for logging and for deciding nothing — a protocol this
        /// build has never heard of still runs, because the core is what has to understand
        /// it, not this app.
        /// </summary>
        public required string ProtocolName { get; init; }

        /// <summary>The outbound object, exactly as the node described it.</summary>
        public required JsonNode Outbound { get; init; }

        /// <summary>The node's hostname, from the <c>server</c> block. May be null.</summary>
        public string? NodeHost { get; init; }

        public static ConnectionCandidate From(ClientOutbound offer, string? nodeHost) => new()
        {
            Id = offer.Id,
            Label = string.IsNullOrWhiteSpace(offer.Label) ? offer.Id : offer.Label,
            ProtocolName = ReadProtocol(offer.Outbound),
            Outbound = offer.Outbound!.DeepClone(),
            NodeHost = nodeHost
        };

        private static string ReadProtocol(JsonNode? outbound) =>
            outbound is JsonObject obj && obj.TryGetPropertyValue("protocol", out var value)
                ? value?.GetValue<string>() ?? "unknown"
                : "unknown";

        /// <summary>Short description for logs. Never includes the credential.</summary>
        public override string ToString() => $"{Id} ({ProtocolName})";
    }
}
