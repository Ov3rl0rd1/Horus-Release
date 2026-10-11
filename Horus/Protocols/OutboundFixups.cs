using System.Text.Json.Nodes;
using Horus.Domain.Models;

namespace Horus.Protocols
{
    /// <summary>
    /// Small corrections to a node's outbound before the core sees it, each for a way a
    /// correct-looking profile fails inside the core while the same node works from other
    /// clients.
    ///
    /// <para>Deliberately narrow: only what is unambiguous is rewritten, and every rewrite is
    /// reported so the log says what the core actually ran.</para>
    /// </summary>
    public static class OutboundFixups
    {
        /// <summary>
        /// Applies the corrections in place and returns a short note per change (empty when
        /// the outbound was already right).
        ///
        /// <list type="bullet">
        /// <item><b>A port written as a string</b> (<c>"port": "8443"</c>, which a template
        /// substitution easily produces) becomes a number. The core's outbound settings take a
        /// number and reject the whole config otherwise, so the protocol is lost before it
        /// dials.</item>
        /// <item><b>Hysteria2 over TLS without <c>h3</c> in ALPN</b> gets it. The node listens
        /// with <c>h3</c> only; when <c>alpn</c> is empty the TLS layer offers its own
        /// defaults instead, and QUIC without a common ALPN never completes a handshake.
        /// Real Hysteria2 clients offer <c>h3</c> on their own, which is why the subscription
        /// link works where the profile does not.</item>
        /// </list>
        /// </summary>
        public static IReadOnlyList<string> Apply(JsonNode? outbound)
        {
            var notes = new List<string>();
            if (outbound is not JsonObject root) return notes;

            CoercePorts(root, notes);

            var protocol = root["protocol"]?.GetValue<string>();
            if (string.Equals(protocol, "hysteria", StringComparison.OrdinalIgnoreCase)
                && root["streamSettings"] is JsonObject stream
                && string.Equals(stream["security"]?.GetValue<string>(), "tls", StringComparison.OrdinalIgnoreCase))
            {
                if (stream["tlsSettings"] is not JsonObject tls)
                {
                    tls = new JsonObject();
                    stream["tlsSettings"] = tls;
                }
                var alpn = tls["alpn"] as JsonArray;
                var values = alpn?.Select(v => v?.ToString()).ToList() ?? [];
                if (!values.Contains("h3"))
                {
                    var fixedAlpn = new JsonArray("h3");
                    foreach (var v in values) if (!string.IsNullOrEmpty(v)) fixedAlpn.Add(v);
                    tls["alpn"] = fixedAlpn;
                    notes.Add(values.Count == 0 ? "alpn h3 added (was empty)" : $"alpn h3 added to [{string.Join(",", values)}]");
                }
            }

            return notes;
        }

        private static void CoercePorts(JsonNode? node, List<string> notes)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj.ToList())
                    {
                        if (key == "port" && value is JsonValue v && v.TryGetValue<string>(out var text)
                            && int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port)
                            && port is > 0 and < 65536)
                        {
                            obj[key] = port;
                            notes.Add($"port \"{text}\" written as a number");
                            continue;
                        }
                        CoercePorts(value, notes);
                    }
                    break;
                case JsonArray array:
                    foreach (var item in array) CoercePorts(item, notes);
                    break;
            }
        }

        /// <summary>
        /// Whether the outbound changes ports on its own (the <c>udpHop</c> finalmask). It
        /// only works while the node redirects the whole port range to its listener; when that
        /// NAT rule is missing, every packet goes to a port nothing listens on and the
        /// protocol carries nothing at all — measured on the bench, 0 of 5 requests.
        /// </summary>
        public static bool HopsPorts(JsonNode? outbound) =>
            UdpMasks(outbound)?.Any(IsHop) == true;

        /// <summary>A copy of the outbound with port hopping removed: the node's own port only.</summary>
        public static JsonNode WithoutPortHopping(JsonNode outbound)
        {
            var copy = outbound.DeepClone();
            if (UdpMasks(copy) is { } masks)
            {
                foreach (var hop in masks.Where(IsHop).ToList()) masks.Remove(hop);
            }
            return copy;
        }

        /// <summary>
        /// The candidates with a second chance for each one that hops ports: the same outbound
        /// without hopping, tried right after it. A node whose range redirect broke (Docker
        /// rewrites the NAT chain on every restart) is then still reachable on its main port
        /// instead of being skipped for the next protocol.
        /// </summary>
        public static List<ConnectionCandidate> WithHopFallbacks(IEnumerable<ConnectionCandidate> candidates)
        {
            var result = new List<ConnectionCandidate>();
            foreach (var candidate in candidates)
            {
                result.Add(candidate);
                if (!HopsPorts(candidate.Outbound) || candidate.Id.EndsWith(NoHopSuffix, StringComparison.Ordinal)) continue;
                result.Add(new ConnectionCandidate
                {
                    Id = candidate.Id + NoHopSuffix,
                    Label = candidate.Label + " (без смены портов)",
                    ProtocolName = candidate.ProtocolName,
                    Outbound = WithoutPortHopping(candidate.Outbound),
                    NodeHost = candidate.NodeHost
                });
            }
            return result;
        }

        /// <summary>Suffix of the id of a candidate made by <see cref="WithHopFallbacks"/>.</summary>
        public const string NoHopSuffix = "-nohop";

        private static JsonArray? UdpMasks(JsonNode? outbound) =>
            outbound?["streamSettings"]?["finalmask"]?["udp"] as JsonArray;

        private static bool IsHop(JsonNode? mask) =>
            string.Equals(mask?["type"]?.ToString(), "udpHop", StringComparison.OrdinalIgnoreCase);
    }
}
