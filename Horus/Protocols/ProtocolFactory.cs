using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Protocols
{
    /// <summary>
    /// Every protocol is an outbound of the same xray-core process, so there is one
    /// <see cref="IVpnProtocol"/> implementation. The factory survives as the seam
    /// <see cref="Horus.Application.VpnManager"/> uses to obtain it.
    /// </summary>
    public class ProtocolFactory
    {
        private readonly IServiceProvider _sp;

        public ProtocolFactory(IServiceProvider sp)
        {
            _sp = sp;
        }

        /// <summary>Protocols the bundled core can proxy through, ignoring node availability.</summary>
        public static IReadOnlyList<ProtocolType> Supported =>
            [ProtocolType.Hysteria2, ProtocolType.Vless, ProtocolType.OlcRtc];

        public IVpnProtocol Create() => _sp.GetRequiredService<XrayProtocol>();

        public IVpnProtocol Create(ProtocolType type)
        {
            if (!Supported.Contains(type))
                throw new NotSupportedException($"Protocol {type} is not supported.");
            return Create();
        }

        public async Task<ProtocolConfig> CreateConfigAsync(
            ProtocolType type, ServerConnection connection, CancellationToken ct = default)
        {
            var raw = connection.LinkFor(type)
                ?? throw new NotSupportedException($"The server did not offer a {type} endpoint.");

            var link = ShareLinkParser.Parse(raw);
            ShareLinkParser.Validate(link);

            link.ResolvedHost = await ResolveAsync(link.Host, ct);

            System.Diagnostics.Debug.WriteLine(
                $"[Horus] {type}: {link.Host} -> {link.DialAddress}:{link.Port}" +
                $" hop={link.PortRange ?? "none"}" +
                (link.ResolvedHost is null ? "  (DNS FAILED — using hostname)" : ""));

            return new XrayConfig
            {
                Link = link,
                LogFilePath = DiagnosticPaths.XrayLog
            };
        }

        /// <summary>
        /// Resolves the node hostname with the platform resolver.
        ///
        /// This is not an optimisation. The core's Go resolver reads its nameservers from
        /// <c>/etc/resolv.conf</c>, which Android does not have, so inside the core every
        /// lookup fails instantly without sending a packet and the outbound never dials.
        /// The app's own UID is excluded from the tunnel, so resolving here goes out over
        /// the real network and works.
        ///
        /// Returns null on failure — the hostname is then passed through unchanged rather
        /// than failing the connect outright.
        /// </summary>
        private static async Task<string?> ResolveAsync(string host, CancellationToken ct)
        {
            if (System.Net.IPAddress.TryParse(host, out _)) return host;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));

                var addresses = await System.Net.Dns.GetHostAddressesAsync(host, timeout.Token);

                // IPv4 first: carrier IPv6 is frequently broken even where v4 is fine.
                var v4 = addresses.FirstOrDefault(a =>
                    a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                return (v4 ?? addresses.FirstOrDefault())?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
