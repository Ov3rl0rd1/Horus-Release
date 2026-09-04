using System.Text.Json.Nodes;

namespace Horus.Protocols
{
    /// <summary>
    /// Rewrites the node's hostname to a resolved IP inside an outbound the node built.
    ///
    /// <para><b>Why this has to happen at all.</b> The core's Go resolver reads its
    /// nameservers from <c>/etc/resolv.conf</c>, which Android does not have. Inside the
    /// core every lookup fails instantly without sending a packet, and the outbound never
    /// dials: the SOCKS inbound accepts every session the bridge offers, hundreds pile up,
    /// bytes leave and only RSTs come back, and the app reports ЗАЩИЩЕНО over a tunnel
    /// carrying nothing. Handing the core a literal address is the only thing that avoids
    /// it, and it is why a failed pre-resolution aborts the attempt rather than proceeding.</para>
    ///
    /// <para><b>Why it is not a blind search-and-replace.</b> The same hostname appears in
    /// fields that must keep it. <c>tlsSettings.serverName</c> is the certificate name, and
    /// the Hysteria2 profile sets it to the node host; replacing it with an IP makes every
    /// TLS handshake fail certificate validation. The same goes for a REALITY
    /// <c>serverName</c>, an SNI, or an HTTP <c>Host</c> header. So only properties named
    /// <c>address</c> are touched — that is the field xray dials, in every outbound shape
    /// that has one (<c>settings.address</c>, <c>vnext[].address</c>,
    /// <c>servers[].address</c>).</para>
    ///
    /// <para>Deliberately ignorant of protocols, which is the point of the new contract: a
    /// node can offer something this build has never heard of, and as long as it dials an
    /// <c>address</c>, this works on it.</para>
    /// </summary>
    public static class OutboundAddress
    {
        /// <summary>The one property name xray dials from.</summary>
        private const string AddressProperty = "address";

        /// <summary>
        /// Finds the hostname the outbound dials, or null when it dials nothing — which is
        /// a real case, not an error: an olcRTC outbound identifies a signalling room and
        /// has no address at all.
        /// </summary>
        public static string? FindHost(JsonNode? outbound)
        {
            string? found = null;
            Walk(outbound, value =>
            {
                found ??= string.IsNullOrWhiteSpace(value) ? null : value;
                return value;
            });
            return found;
        }

        /// <summary>
        /// Replaces every <c>address</c> that equals <paramref name="host"/> with
        /// <paramref name="resolved"/>, in place. Returns how many were rewritten.
        ///
        /// Only an exact match is replaced. An outbound that dials somewhere other than the
        /// node — a chained proxy, a signalling service — is left alone, because we have not
        /// resolved that name and must not pretend we have.
        /// </summary>
        public static int Rewrite(JsonNode? outbound, string host, string resolved)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(resolved)) return 0;

            var count = 0;
            Walk(outbound, value =>
            {
                if (!string.Equals(value, host, StringComparison.OrdinalIgnoreCase)) return value;
                count++;
                return resolved;
            });

            return count;
        }

        /// <summary>
        /// Visits every string-valued property named <c>address</c>, letting the visitor
        /// replace it.
        /// </summary>
        private static void Walk(JsonNode? node, Func<string, string> visit)
        {
            switch (node)
            {
                case JsonObject obj:
                {
                    // Materialised because the visitor assigns back into the object, and
                    // mutating a JsonObject while enumerating it throws.
                    foreach (var key in obj.Select(p => p.Key).ToList())
                    {
                        var child = obj[key];

                        if (string.Equals(key, AddressProperty, StringComparison.OrdinalIgnoreCase)
                            && child is JsonValue value
                            && value.TryGetValue<string>(out var text))
                        {
                            var replacement = visit(text);
                            if (!ReferenceEquals(replacement, text)) obj[key] = replacement;
                            continue;
                        }

                        Walk(child, visit);
                    }
                    break;
                }

                case JsonArray array:
                {
                    foreach (var item in array) Walk(item, visit);
                    break;
                }
            }
        }
    }
}
