using System;
using System.Collections.Generic;
using System.Text;

namespace Horus.Domain.Models
{
    public class Hysteria2Config : ProtocolConfig
    {
        public string? RenderedConfig { get; set; }

        public string Server { get; set; }
        public string Auth { get; set; }
        public string Obfs { get; set; }   // salamander | null
        public string ObfsPassword { get; set; }
        public string PortsRange { get; set; }
        public int HopInterval { get; set; }
        public string Socks5Address { get; set; }   // 127.0.0.1:1080

        public override string ToConfig() 
        {
            return $"""
server: {Server}{(String.IsNullOrEmpty(PortsRange) ? "" : $",{PortsRange}")}

auth: {Auth}

{(String.IsNullOrEmpty(Socks5Address) ? "" : $"socks5:\n  listen: {Socks5Address}")}

fastOpen: true

quic:
  maxIdleTimeout: 30s 
  keepAlivePeriod: 20s

{(String.IsNullOrEmpty(PortsRange) ? "" : $"transport:\n  udp:\n  hopInterval: {HopInterval}s")}

{(String.IsNullOrEmpty(Obfs) ? "" : $"obfs:\n  type: salamander \n  salamander:\n    password: {ObfsPassword}")}
""";
        }
    }
}
