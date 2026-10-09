using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horus.Platforms.Windows.Tunnel
{
    /// <summary>One TUN connection as <c>XrayConnections</c> reports it.</summary>
    public sealed record CoreConnection
    {
        [JsonPropertyName("id")] public ulong Id { get; init; }
        [JsonPropertyName("net")] public string Network { get; init; } = string.Empty;
        [JsonPropertyName("src")] public string Source { get; init; } = string.Empty;
        [JsonPropertyName("dst")] public string Destination { get; init; } = string.Empty;
        [JsonPropertyName("target")] public string? Target { get; init; }
        [JsonPropertyName("outbound")] public string? Outbound { get; init; }
        [JsonPropertyName("up")] public long Up { get; init; }
        [JsonPropertyName("down")] public long Down { get; init; }
        [JsonPropertyName("startedMs")] public long StartedMs { get; init; }

        /// <summary>The local port of the application's socket — what maps the connection to a process.</summary>
        public int SourcePort => PortOf(Source);

        public static int PortOf(string endpoint)
        {
            var colon = endpoint.LastIndexOf(':');
            return colon >= 0 && int.TryParse(endpoint.AsSpan(colon + 1), out var p) ? p : 0;
        }

        public static IReadOnlyList<CoreConnection> Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return [];
            try { return JsonSerializer.Deserialize<List<CoreConnection>>(json) ?? []; }
            catch (JsonException) { return []; }
        }
    }

    public enum StallVerdict
    {
        /// <summary>Bytes came back through the proxy since the last look.</summary>
        Healthy,

        /// <summary>Too little was sent to say anything. Silence is not failure.</summary>
        Idle,

        /// <summary>One sample's worth of sending into silence. Not acted on alone.</summary>
        Suspect,

        /// <summary>Two in a row. Worth a probe — never, by itself, a reconnect.</summary>
        Stalled
    }

    /// <summary>
    /// Decides from the core's own per-connection byte counts whether the proxy has stopped
    /// answering.
    ///
    /// <para><b>Why not the adapter counters.</b> The shared health monitor reads the TUN's
    /// packet counters, which worked while a bridge forwarded raw packets. With the core
    /// owning the TUN, TCP is terminated locally by gVisor: the application's handshake
    /// completes and its data is acknowledged whether or not the proxy behind it is alive,
    /// so a dead tunnel still shows healthy-looking traffic in both directions. The core
    /// knows what actually came back from the far side — that is what <c>down</c> is — and
    /// that is what this watches.</para>
    ///
    /// <para><b>Conservative by construction.</b> The previous design turned a suspicion
    /// into a reconnect, and a reconnect is every connection on the machine dropped — a
    /// player kicked from a match. Here a verdict only ever buys a probe, and it takes all
    /// of: the proxy's downlink flat across every connection, uplink that actually moved,
    /// and at least two connections old enough to have been answered that never were.</para>
    /// </summary>
    public sealed class ProxyStallDetector
    {
        public const string ProxyTag = "proxy";

        /// <summary>Uplink below this in a sample says nothing either way.</summary>
        public const long MinUplinkBytes = 4 * 1024;

        /// <summary>How old a connection must be before "never answered" means something.</summary>
        public const long AnswerDeadlineMs = 6_000;

        /// <summary>Unanswered connections needed to suspect the proxy rather than one slow server.</summary>
        public const int MinUnanswered = 2;

        private Dictionary<ulong, (long Up, long Down)> _last = [];
        private bool _primed;
        private int _suspects;

        public void Reset()
        {
            _last = [];
            _primed = false;
            _suspects = 0;
        }

        public StallVerdict Feed(IReadOnlyList<CoreConnection> connections, long nowMs)
        {
            long upDelta = 0, downDelta = 0;
            var unanswered = 0;
            var next = new Dictionary<ulong, (long, long)>(connections.Count);

            foreach (var c in connections)
            {
                if (c.Outbound != ProxyTag) continue;
                next[c.Id] = (c.Up, c.Down);

                var (pu, pd) = _last.TryGetValue(c.Id, out var prev) ? prev : (0L, 0L);
                upDelta += Math.Max(0, c.Up - pu);
                downDelta += Math.Max(0, c.Down - pd);

                if (c.Up > 0 && c.Down == 0 && nowMs - c.StartedMs >= AnswerDeadlineMs) unanswered++;
            }

            var primed = _primed;
            _last = next;
            _primed = true;

            // The first look has no baseline: everything would count as a delta.
            if (!primed) return StallVerdict.Idle;

            if (downDelta > 0)
            {
                _suspects = 0;
                return StallVerdict.Healthy;
            }

            if (upDelta < MinUplinkBytes || unanswered < MinUnanswered)
            {
                _suspects = 0;
                return StallVerdict.Idle;
            }

            return ++_suspects >= 2 ? StallVerdict.Stalled : StallVerdict.Suspect;
        }
    }
}
