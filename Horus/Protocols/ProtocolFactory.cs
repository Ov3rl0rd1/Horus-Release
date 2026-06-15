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
            //ProtocolType.DTLS => _sp.GetRequiredService<DTLSProtocol>(),
            _ => throw new NotSupportedException(type.ToString())
        };
    }
}
