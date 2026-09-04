using Horus.Domain.Models;

namespace Horus.Protocols
{
    /// <summary>
    /// Parses the <c>vless://</c> / <c>hysteria2://</c> / <c>olcrtc://</c> share links the
    /// API returns from <c>GET /servers/connect</c>.
    ///
    /// Hand-rolled rather than built on <see cref="Uri"/> because Hysteria2 links put a
    /// port <i>range</i> in the authority (<c>host:443,20000-30000</c>), which
    /// <see cref="Uri"/> rejects.
    /// </summary>
    public static class ShareLinkParser
    {
        public static ShareLink Parse(string link)
        {
            if (string.IsNullOrWhiteSpace(link))
                throw new ArgumentException("Share link is empty.", nameof(link));

            link = link.Trim();

            var schemeEnd = link.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd <= 0)
                throw new FormatException($"Share link has no scheme: '{Redact(link)}'.");

            var scheme = link[..schemeEnd].ToLowerInvariant();
            var protocol = scheme switch
            {
                "vless" => ProtocolType.Vless,
                "hysteria2" or "hy2" => ProtocolType.Hysteria2,
                "olcrtc" => ProtocolType.OlcRtc,
                _ => throw new NotSupportedException($"Unsupported share-link scheme '{scheme}'.")
            };

            var rest = link[(schemeEnd + 3)..];

            // …#fragment
            var tag = string.Empty;
            var hash = rest.IndexOf('#');
            if (hash >= 0)
            {
                tag = Unescape(rest[(hash + 1)..]);
                rest = rest[..hash];
            }

            // …?query
            var query = string.Empty;
            var mark = rest.IndexOf('?');
            if (mark >= 0)
            {
                query = rest[(mark + 1)..];
                rest = rest[..mark];
            }

            // Drop the path segment Hysteria2 links carry ("host:port,range/?…").
            var slash = rest.IndexOf('/');
            if (slash >= 0) rest = rest[..slash];

            // userinfo@host:port  — split on the LAST '@' so credentials may contain one.
            var at = rest.LastIndexOf('@');
            if (at < 0)
                throw new FormatException($"Share link has no credential: '{Redact(link)}'.");

            var credential = Unescape(rest[..at]);
            var authority = rest[(at + 1)..];

            var (host, port, portRange) = ParseAuthority(authority, link);
            var parameters = ParseQuery(query);

            // The hop range moved out of the authority and into the query. Both forms are
            // accepted, and the authority still wins: a link that carries it in both places
            // is self-consistent by construction, and endpoints cached from before the
            // change are still in the old shape for up to 24 hours after a server update.
            portRange ??= ReadHopRange(parameters);

            return new ShareLink
            {
                Protocol = protocol,
                Credential = credential,
                Host = host,
                Port = port,
                PortRange = portRange,
                Tag = tag,
                Params = parameters
            };
        }

        /// <summary>
        /// Reads the Hysteria2 port-hopping range from <c>?mport=</c>.
        ///
        /// The API used to render the range inside the authority
        /// (<c>host:9443,31111:49999/?…</c>) and now renders it as a query parameter
        /// (<c>host:9443?…&amp;mport=31111-49999</c>). Only the spelling changed — the value
        /// means the same thing and goes to the same place, <c>quicParams.udpHop</c>.
        ///
        /// Normalised through the same helper as the authority form, so a colon-separated
        /// range arriving here would be corrected rather than reaching the core and failing
        /// every dial with <c>too many colons in address</c>.
        /// </summary>
        private static string? ReadHopRange(IReadOnlyDictionary<string, string> parameters) =>
            parameters.TryGetValue("mport", out var raw) ? NormalizePortRange(raw) : null;

        /// <summary>
        /// Checks the handshake parameters a link must carry for its protocol, and throws
        /// a message that names the offending field.
        ///
        /// The core's own rejection ("invalid \"shortId\": System.String[]") arrives after a
        /// config round-trip and reads like a client bug, when in practice it means the
        /// server rendered the link wrong — interpolating a string[] into the URI is the
        /// classic case. Failing here turns that into something actionable.
        /// </summary>

        public static void Validate(ShareLink link)
        {
            if (link.Protocol != ProtocolType.Vless || !link.IsReality) return;

            if (string.IsNullOrEmpty(link.PublicKey))
                throw new FormatException(
                    "REALITY link has no public key (pbk) — the server issued an incomplete link.");

            // A REALITY short id is 0–8 bytes of hex, so at most 16 characters and always
            // an even count. Anything else never reaches the wire usefully.
            var sid = link.ShortId;
            if (!string.IsNullOrEmpty(sid) && !IsShortId(sid))
                throw new FormatException(
                    $"REALITY link has an invalid short id (sid=\"{sid}\"). Expected up to 16 hex " +
                    "characters. A value like \"System.String[]\" means the server interpolated an " +
                    "array into the link instead of one of its short ids.");
        }

        private static bool IsShortId(string sid) =>
            sid.Length <= 16
            && sid.Length % 2 == 0
            && sid.All(Uri.IsHexDigit);

        public static bool TryParse(string? link, out ShareLink? parsed)
        {
            parsed = null;
            if (string.IsNullOrWhiteSpace(link)) return false;
            try
            {
                parsed = Parse(link);
                return true;
            }
            catch { return false; }
        }

        // ── Private ──────────────────────────────────────────────────────────

        /// <summary>Splits <c>host:port[,portRange]</c>, tolerating bracketed IPv6 literals.</summary>
        private static (string Host, int Port, string? PortRange) ParseAuthority(string authority, string link)
        {
            string host;
            string portPart;

            if (authority.StartsWith('['))
            {
                var close = authority.IndexOf(']');
                if (close < 0)
                    throw new FormatException($"Malformed IPv6 host in '{Redact(link)}'.");

                host = authority[1..close];
                var after = authority[(close + 1)..];
                portPart = after.StartsWith(':') ? after[1..] : string.Empty;
            }
            else
            {
                // FIRST colon, not last: a colon-separated hop range ("8443,31111:49999")
                // puts more colons after the port, and splitting on the last one would tear
                // the range in half. An IPv6 literal must be bracketed in a URI authority,
                // so it never reaches this branch.
                var colon = authority.IndexOf(':');
                if (colon < 0)
                    throw new FormatException($"Share link has no port: '{Redact(link)}'.");

                host = authority[..colon];
                portPart = authority[(colon + 1)..];
            }

            // Hysteria2 port hopping appears in two forms in the wild:
            //   "443,20000-50000" — a dial port plus a hop range (what the API emits)
            //   "20000-50000"     — a bare hop range, dial port implied (hand-written links)
            string? range = null;
            var comma = portPart.IndexOf(',');
            if (comma >= 0)
            {
                range = NormalizePortRange(portPart[(comma + 1)..]);
                portPart = portPart[..comma];
            }
            else if (portPart.Contains('-'))
            {
                // Bare range: hop over all of it, and dial the low end to get started.
                range = NormalizePortRange(portPart)!;
                portPart = range[..range.IndexOf('-')];
            }

            if (!int.TryParse(portPart, out var port) || port is <= 0 or > 65535)
                throw new FormatException($"Share link has an invalid port: '{Redact(link)}'.");

            if (host.Length == 0)
                throw new FormatException($"Share link has no host: '{Redact(link)}'.");

            return (host, port, range);
        }

        /// <summary>
        /// Normalises a hop-port range to the hyphen form the core requires.
        ///
        /// HorusAPI stores the range colon-separated (<c>31111:49999</c>) while the core's
        /// <c>PortList</c> only understands <c>31111-49999</c>. Passed through unchanged the
        /// colon ends up inside the dial address, and the core fails every connection with
        /// <c>too many colons in address</c> — after the tunnel has already come up, so the
        /// app reports success while carrying nothing.
        /// </summary>
        private static string? NormalizePortRange(string raw)
        {
            var range = raw.Trim().Replace(':', '-');
            return range.Length == 0 ? null : range;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return result;

            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = Unescape(eq < 0 ? pair : pair[..eq]);
                var value = eq < 0 ? string.Empty : Unescape(pair[(eq + 1)..]);
                if (key.Length > 0) result[key] = value;
            }

            return result;
        }

        /// <summary>
        /// Percent-decoding only. <c>+</c> is deliberately left alone: translating it to a
        /// space is an <c>application/x-www-form-urlencoded</c> rule, not a URI one, and
        /// share links are URIs. Applying it here silently corrupts any base64 secret that
        /// happens to contain a plus — which is most of them — turning a valid credential
        /// into one the node rejects, with nothing in the logs to say why.
        /// </summary>
        private static string Unescape(string value)
        {
            try { return Uri.UnescapeDataString(value); }
            catch { return value; }
        }

        /// <summary>Strips the credential so malformed links can be logged safely.</summary>
        private static string Redact(string link)
        {
            var schemeEnd = link.IndexOf("://", StringComparison.Ordinal);
            var at = link.LastIndexOf('@');
            return schemeEnd >= 0 && at > schemeEnd
                ? string.Concat(link.AsSpan(0, schemeEnd + 3), "***", link.AsSpan(at))
                : link;
        }
    }
}
