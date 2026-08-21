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

            // Handing the core a hostname produces a tunnel that is dead on arrival, and
            // dead in a way that is almost invisible: the core starts, its SOCKS inbound
            // accepts every session the bridge offers, and it never dials out, because its
            // Go resolver has no nameservers here and its configured DNS servers route
            // through the very proxy it is trying to build. Sessions pile up in the
            // hundreds, bytes leave and only RSTs come back, and the app reports ЗАЩИЩЕНО.
            //
            // Observed on a real device: 190 established SOCKS sessions and not one socket
            // to any external address. Failing here is the only honest option — the
            // fallback loop moves to the next protocol, and if none resolves the user gets
            // an error instead of a tunnel that silently carries nothing.
            if (link.ResolvedHost is null)
                throw new InvalidOperationException(
                    $"Не удалось определить адрес узла {link.Host}. " +
                    "Проверьте подключение к сети и попробуйте ещё раз.");

            Diag.Write($"[{type}] {link.Host} -> {link.DialAddress}:{link.Port} " +
                       $"hop={link.PortRange ?? "none"}");

            return new XrayConfig
            {
                Link = link,
                LogFilePath = DiagnosticPaths.XrayLog,
                LogLevel = Horus.Application.UserPreferences.XrayLogLevel,

                // Chosen per attempt rather than fixed at 1080. The fallback loop stops the
                // core between attempts, so a retry re-picks the same port unless something
                // else took it meanwhile — which is exactly when moving is the right answer.
                SocksPort = SocksPortAllocator.Allocate()
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
        /// Retried, because the moment this runs is the worst one for a lookup: a reconnect
        /// happens while the old tunnel is being torn down and the system resolver is in
        /// flux, so a single attempt fails far more often than the network warrants.
        ///
        /// Returns null only after every attempt failed. The caller must then abandon the
        /// attempt — see <see cref="CreateConfigAsync"/>.
        /// </summary>
        private static async Task<string?> ResolveAsync(string host, CancellationToken ct)
        {
            if (System.Net.IPAddress.TryParse(host, out _)) return host;

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));

                    var addresses = await System.Net.Dns.GetHostAddressesAsync(host, timeout.Token);

                    // IPv4 first: carrier IPv6 is frequently broken even where v4 is fine.
                    var v4 = addresses.FirstOrDefault(a =>
                        a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                    var picked = (v4 ?? addresses.FirstOrDefault())?.ToString();
                    if (picked is not null) return picked;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Diag.Write($"[dns] {host} attempt {attempt}/3 failed: {ex.Message}");
                }

                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }

            return null;
        }
    }
}
