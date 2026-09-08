using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Protocols
{
    /// <summary>
    /// Every offer is an outbound of the same xray-core process, so there is one
    /// <see cref="IVpnProtocol"/> implementation. The factory survives as the seam
    /// <see cref="Horus.Application.VpnManager"/> uses to obtain it, and as the place the
    /// node's outbound is prepared for the core.
    /// </summary>
    public class ProtocolFactory
    {
        private readonly IServiceProvider _sp;

        public ProtocolFactory(IServiceProvider sp)
        {
            _sp = sp;
        }

        public IVpnProtocol Create() => _sp.GetRequiredService<XrayProtocol>();

        /// <summary>
        /// Prepares one of the node's outbounds for the core.
        ///
        /// <para>The outbound arrives complete: the node built it and the API substituted
        /// this account into it. Two things still have to happen on the device, and both are
        /// about the address:</para>
        ///
        /// <list type="number">
        /// <item>The hostname has to become a literal IP, because the core's Go resolver has
        /// no nameservers here. See <see cref="OutboundAddress"/> for what goes wrong
        /// otherwise, and why only <c>address</c> fields may be rewritten.</item>
        /// <item>That IP has to be reported back, so the platform can route it around the
        /// tunnel — without which the core's own socket is carried by the tunnel it is
        /// feeding.</item>
        /// </list>
        ///
        /// <para>An outbound that dials nothing is a legitimate case rather than an error:
        /// an olcRTC offer identifies a signalling room and has no address at all. It gets
        /// no resolution and no bypass route, and the platform decides whether it can work
        /// with that.</para>
        /// </summary>
        public async Task<ProtocolConfig> CreateConfigAsync(
            ConnectionCandidate candidate, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            var outbound = candidate.Outbound.DeepClone();

            // The address the outbound actually dials, which is normally the node but is
            // read from the outbound rather than assumed: a profile is free to point an
            // offer somewhere else, and resolving a name it does not use would be a lie.
            var dialHost = OutboundAddress.FindHost(outbound);
            string? resolved = null;

            if (dialHost is null)
            {
                Diag.Info("connect", $"{candidate.Id} dials no address (signalling offer)");
            }
            else
            {
                resolved = await ResolveAsync(dialHost, ct);

                if (resolved is null)
                    throw new InvalidOperationException(
                        $"Не удалось определить адрес узла {dialHost}. " +
                        "Проверьте подключение к сети и попробуйте ещё раз.");

                var rewritten = OutboundAddress.Rewrite(outbound, dialHost, resolved);
                Diag.Info("connect",
                    $"{candidate.Id} ({candidate.ProtocolName}) {dialHost} -> {resolved}, {rewritten} address field(s)");
            }

            return new XrayConfig
            {
                Outbound = outbound,
                Offer = candidate.Id,
                Label = candidate.Label,
                ProtocolName = candidate.ProtocolName,
                NodeAddress = resolved,

                LogFilePath = DiagnosticPaths.XrayLog,
                LogLevel = Horus.Application.UserPreferences.XrayLogLevel,

                Geo = ResolveGeoRouting(),

                // Chosen per attempt rather than fixed at 1080. The fallback loop stops the
                // core between attempts, so a retry re-picks the same port unless something
                // else took it meanwhile — which is exactly when moving is the right answer.
                SocksPort = SocksPortAllocator.Allocate()
            };
        }

        /// <summary>
        /// Turns the user's geo-routing preference into routing options the core can
        /// actually resolve — or into nothing at all.
        ///
        /// <para><b>The preference is a request, not a state, and this is the one place
        /// that decides.</b> Emitting <c>geoip:ru</c> without the <c>.dat</c> files present
        /// is not a degraded configuration: xray rejects it during <c>XrayTest</c> and the
        /// tunnel never comes up. So the order here matters — point the core at the asset
        /// directory <i>first</i>, and only build the rules if that succeeded. Everything
        /// that can go wrong (files never downloaded, a core too old to export
        /// <c>XraySetAssetPath</c>, a platform where <c>direct</c> is not direct) collapses
        /// into the same outcome: the user connects without geo routing rather than not
        /// connecting.</para>
        ///
        /// <para>Deliberately in the config builder's path rather than at start-up. The
        /// asset path is process-global state in the core, and this runs immediately before
        /// the config that depends on it is rendered — which makes "the core knows where
        /// the files are" and "the config names a category" impossible to get out of
        /// step.</para>
        /// </summary>
        private GeoRoutingOptions ResolveGeoRouting()
        {
            if (!Horus.Application.UserPreferences.GeoRoutingEnabled)
                return GeoRoutingOptions.Disabled;

            var assets = _sp.GetService<IGeoAssetService>();

            if (assets is null || !assets.IsSupported)
            {
                Diag.Info("geo", "geo routing requested but unsupported on this platform");
                return GeoRoutingOptions.Disabled;
            }

            if (!assets.Activate())
            {
                Diag.Warn("geo", "geo routing requested but the rule files are not usable; connecting without it");
                return GeoRoutingOptions.Disabled;
            }

            Diag.Info("geo", "geo routing on: Russian sites and networks go direct");
            return GeoRoutingOptions.ForRussianBypass();
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
