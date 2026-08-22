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
    /// olcRTC connection parameters.
    ///
    /// <para>Unlike the other two protocols this does not arrive as a share link, and could
    /// not: olcRTC is signalling-based, so it identifies a <i>room</i> rather than an
    /// address, and a URI has nowhere sensible to put that. The API sends the same four
    /// values the node registered (<c>olcrtc_provider</c>, <c>_transport</c>,
    /// <c>_room_id</c>, <c>_room_key</c>) plus the account's stable identity.</para>
    ///
    /// <para>Returned only to the app. The base64 subscription served to third-party
    /// clients carries vless and hysteria2 only, because no other client understands
    /// this.</para>
    /// </summary>
    public sealed class OlcRtcEndpoint
    {
        [JsonPropertyName("provider")] public string Provider { get; set; } = string.Empty;
        [JsonPropertyName("transport")] public string Transport { get; set; } = string.Empty;
        [JsonPropertyName("room_id")] public string RoomId { get; set; } = string.Empty;
        [JsonPropertyName("room_key")] public string RoomKey { get; set; } = string.Empty;

        /// <summary>The account's <c>vpn_uuid</c> — its identity on the node.</summary>
        [JsonPropertyName("uuid")] public string Uuid { get; set; } = string.Empty;

        /// <summary>Which node the room belongs to. Informational: nothing dials it.</summary>
        [JsonPropertyName("host")] public string Host { get; set; } = string.Empty;

        public bool IsUsable =>
            !string.IsNullOrWhiteSpace(RoomId) && !string.IsNullOrWhiteSpace(RoomKey);
    }

    /// <summary>
    /// Answer of <c>GET /servers/connect</c> for a session in the <c>X-Session-Key</c>
    /// header.
    ///
    /// <para>Binding is <b>not</b> done here any more — <c>POST /servers/select</c> owns
    /// that, and this endpoint only reads back what the caller is already bound to. It
    /// still binds by auto-pick if the account has no node at all, so a first connect
    /// works without the client having chosen.</para>
    /// </summary>
    public sealed class ServerConnection
    {
        [JsonPropertyName("server")]
        public BoundServer? Server { get; set; }

        /// <summary>
        /// Every VLESS variant the node publishes.
        ///
        /// <para>An array, and treated as one: today it holds a single REALITY endpoint,
        /// but the contract says a node may expose several. They are all offered to the
        /// fallback loop in order, so a node that adds a second transport gets used without
        /// an app release.</para>
        /// </summary>
        [JsonPropertyName("vless")]
        public List<string> Vless { get; set; } = [];

        [JsonPropertyName("hysteria2")]
        public string? Hysteria2 { get; set; }

        /// <summary>Null when the node has not announced a room.</summary>
        [JsonPropertyName("olcrtc")]
        public OlcRtcEndpoint? OlcRtc { get; set; }

        [JsonIgnore]
        public bool HasAny => Candidates().Count > 0;

        /// <summary>
        /// Everything this node can be dialled with, one entry per usable endpoint.
        ///
        /// <para>Order here is only the node's; the connect path re-orders by its own
        /// preference (see <c>VpnManager.FallbackOrder</c>). Empty strings and a
        /// half-filled olcRTC block are dropped, so a caller never has to re-check.</para>
        /// </summary>
        public IReadOnlyList<ConnectionCandidate> Candidates()
        {
            var result = new List<ConnectionCandidate>();

            if (!string.IsNullOrWhiteSpace(Hysteria2))
                result.Add(ConnectionCandidate.FromLink(ProtocolType.Hysteria2, Hysteria2.Trim()));

            foreach (var link in Vless)
            {
                if (string.IsNullOrWhiteSpace(link)) continue;
                result.Add(ConnectionCandidate.FromLink(ProtocolType.Vless, link.Trim()));
            }

            if (OlcRtc is { IsUsable: true } rtc)
                result.Add(ConnectionCandidate.FromOlcRtc(rtc));

            return result;
        }

        /// <summary>Whether the node published anything for <paramref name="type"/>.</summary>
        public bool Offers(ProtocolType type) => Candidates().Any(c => c.Protocol == type);
    }

    /// <summary>
    /// One dialable endpoint. Carries either a share link or an olcRTC block, because the
    /// two protocols are described in different shapes and collapsing them into a string
    /// would mean inventing a URI the API does not speak.
    /// </summary>
    public sealed class ConnectionCandidate
    {
        public required ProtocolType Protocol { get; init; }

        /// <summary>The <c>vless://</c> or <c>hysteria2://</c> link. Null for olcRTC.</summary>
        public string? Link { get; init; }

        /// <summary>The olcRTC parameters. Null for the link-based protocols.</summary>
        public OlcRtcEndpoint? OlcRtc { get; init; }

        public static ConnectionCandidate FromLink(ProtocolType protocol, string link) =>
            new() { Protocol = protocol, Link = link };

        public static ConnectionCandidate FromOlcRtc(OlcRtcEndpoint endpoint) =>
            new() { Protocol = ProtocolType.OlcRtc, OlcRtc = endpoint };

        /// <summary>Short description for logs. Never includes the credential.</summary>
        public override string ToString() => Protocol switch
        {
            ProtocolType.OlcRtc => $"olcRTC {OlcRtc?.Provider}/{OlcRtc?.Transport}",
            _ => Protocol.ToString()
        };
    }
}
