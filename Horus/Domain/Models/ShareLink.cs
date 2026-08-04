namespace Horus.Domain.Models
{
    /// <summary>
    /// A share link from <c>/servers/connect</c> decomposed into the fields the xray
    /// outbound needs. Only the members relevant to <see cref="Protocol"/> are set.
    /// </summary>
    public sealed class ShareLink
    {
        public required ProtocolType Protocol { get; init; }

        /// <summary>Userinfo part: the VLESS user id, or the Hysteria2 auth password.</summary>
        public required string Credential { get; init; }

        public required string Host { get; init; }
        public required int Port { get; init; }

        /// <summary>Fragment after <c>#</c> — the node's label for this endpoint.</summary>
        public string Tag { get; init; } = string.Empty;

        /// <summary>All query parameters, lower-cased keys.</summary>
        public IReadOnlyDictionary<string, string> Params { get; init; } =
            new Dictionary<string, string>();

        // ── VLESS / REALITY ──────────────────────────────────────────────────
        public string Encryption => Get("encryption", "none");
        public string? Flow => GetOrNull("flow");
        public string Security => Get("security", "none");
        public string? Sni => GetOrNull("sni") ?? GetOrNull("peer");
        public string Fingerprint => Get("fp", "chrome");
        public string? PublicKey => GetOrNull("pbk");
        public string? ShortId => GetOrNull("sid");
        public string? SpiderX => GetOrNull("spx");
        public string Network => Get("type", "tcp");

        // ── Hysteria2 ────────────────────────────────────────────────────────
        public string? Obfs => GetOrNull("obfs");
        public string? ObfsPassword => GetOrNull("obfs-password") ?? GetOrNull("obfs_password");

        /// <summary>
        /// Extra port range for UDP port hopping, taken from the <c>host:port,range</c>
        /// form Hysteria2 links use (e.g. <c>20000-30000</c>).
        /// </summary>
        public string? PortRange { get; init; }

        public bool IsReality => string.Equals(Security, "reality", StringComparison.OrdinalIgnoreCase);
        public bool IsTls => IsReality || string.Equals(Security, "tls", StringComparison.OrdinalIgnoreCase);

        private string Get(string key, string fallback)
        {
            var value = GetOrNull(key);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        private string? GetOrNull(string key) =>
            Params.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
    }
}
