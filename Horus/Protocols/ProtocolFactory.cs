using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Protocols
{
    public class ProtocolFactory
    {
        private readonly IServiceProvider _sp;

        public ProtocolFactory(IServiceProvider sp)
        {
            _sp = sp;
        }

        public IVpnProtocol Create(ProtocolType type) => type switch
        {
            ProtocolType.Hysteria2 => _sp.GetRequiredService<Hysteria2Protocol>(),
            ProtocolType.OlcRtc => _sp.GetRequiredService<OlcRtcProtocol>(),
            _ => throw new NotSupportedException($"Protocol {type} is not supported.")
        };

        public ProtocolConfig CreateDefaultConfig(ProtocolType type) => type switch
        {
            ProtocolType.Hysteria2 => new Hysteria2Config(),
            ProtocolType.OlcRtc => new OlcRtcConfig(),
            _ => throw new NotSupportedException($"Protocol {type} is not supported.")
        };
    }
}
