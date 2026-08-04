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

        public ProtocolConfig CreateConfig(ProtocolType type, ServerConnection connection)
        {
            var link = connection.LinkFor(type)
                ?? throw new NotSupportedException($"The server did not offer a {type} endpoint.");

            // Start each attempt with a clean log so a failure report shows this
            // connect, not an accumulation of every previous one.
            DiagnosticPaths.Truncate(DiagnosticPaths.XrayLog);

            return new XrayConfig
            {
                Link = ShareLinkParser.Parse(link),
                LogFilePath = DiagnosticPaths.XrayLog
            };
        }
    }
}
