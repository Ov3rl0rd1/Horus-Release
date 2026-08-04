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

            return new ShareLink
            {
                Protocol = protocol,
                Credential = credential,
                Host = host,
                Port = port,
                PortRange = portRange,
                Tag = tag,
                Params = ParseQuery(query)
            };
        }

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
                var colon = authority.LastIndexOf(':');
                if (colon < 0)
                    throw new FormatException($"Share link has no port: '{Redact(link)}'.");

                host = authority[..colon];
                portPart = authority[(colon + 1)..];
            }

            // Hysteria2 port hopping: "443,20000-30000"
            string? range = null;
            var comma = portPart.IndexOf(',');
            if (comma >= 0)
            {
                range = portPart[(comma + 1)..].Trim();
                portPart = portPart[..comma];
                if (range.Length == 0) range = null;
            }

            if (!int.TryParse(portPart, out var port) || port is <= 0 or > 65535)
                throw new FormatException($"Share link has an invalid port: '{Redact(link)}'.");

            if (host.Length == 0)
                throw new FormatException($"Share link has no host: '{Redact(link)}'.");

            return (host, port, range);
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

        private static string Unescape(string value)
        {
            try { return Uri.UnescapeDataString(value.Replace('+', ' ')); }
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
