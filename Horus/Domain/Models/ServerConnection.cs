namespace Horus.Domain.Models
{
    /// <summary>
    /// Result of <c>GET /servers/connect</c>. The endpoint takes no parameters —
    /// the API picks a server for the caller and binds the account to it — and
    /// answers with a flat string map of share links, one per protocol the node
    /// exposes. Keys are server-side constants, so <see cref="Parse"/> locates the
    /// links by URI scheme instead of by key name.
    /// </summary>
    public sealed class ServerConnection
    {
        public string? Hysteria2Link { get; init; }
        public string? VlessLink { get; init; }
        public string? OlcRtcLink { get; init; }

        /// <summary>Every key/value the API returned, for diagnostics.</summary>
        public IReadOnlyDictionary<string, string> Raw { get; init; } =
            new Dictionary<string, string>();

        public bool HasAny => Hysteria2Link is not null
                           || VlessLink is not null
                           || OlcRtcLink is not null;

        /// <summary>Returns the link for <paramref name="type"/>, or null if the node didn't offer it.</summary>
        public string? LinkFor(ProtocolType type) => type switch
        {
            ProtocolType.Hysteria2 => Hysteria2Link,
            ProtocolType.Vless => VlessLink,
            ProtocolType.OlcRtc => OlcRtcLink,
            _ => null
        };

        public static ServerConnection Parse(IReadOnlyDictionary<string, string?> values)
        {
            var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? vless = null, hysteria = null, olcrtc = null;

            foreach (var (key, value) in values)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                var link = value.Trim();
                raw[key] = link;

                if (link.StartsWith(ApiConsts.SCHEME_VLESS, StringComparison.OrdinalIgnoreCase))
                    vless ??= link;
                else if (link.StartsWith(ApiConsts.SCHEME_HYSTERIA2, StringComparison.OrdinalIgnoreCase))
                    hysteria ??= link;
                else if (link.StartsWith(ApiConsts.SCHEME_OLCRTC, StringComparison.OrdinalIgnoreCase))
                    olcrtc ??= link;
            }

            return new ServerConnection
            {
                VlessLink = vless,
                Hysteria2Link = hysteria,
                OlcRtcLink = olcrtc,
                Raw = raw
            };
        }
    }
}
