namespace Horus.Domain.Models
{
    public class Hysteria2Config : ProtocolConfig
    {
        public override ProtocolType ProtocolType => ProtocolType.Hysteria2;

        /// <summary>When set, ToConfig() returns this verbatim (server-rendered YAML).</summary>
        public string? RenderedConfig { get; set; }

        public string Server { get; set; } = string.Empty;
        public string Auth { get; set; } = string.Empty;
        public string? Obfs { get; set; }           // salamander | null
        public string? ObfsPassword { get; set; }
        public string? PortsRange { get; set; }
        public int HopInterval { get; set; } = 30;
        public string Socks5Address { get; set; } = "127.0.0.1:1080";
        public bool LazyTls { get; set; }

        public override string ToConfig()
        {
            if (RenderedConfig != null) return RenderedConfig;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"server: {Server}{(string.IsNullOrEmpty(PortsRange) ? "" : $",{PortsRange}")}");
            sb.AppendLine();
            sb.AppendLine($"auth: {Auth}");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(Socks5Address))
            {
                sb.AppendLine("socks5:");
                sb.AppendLine($"  listen: {Socks5Address}");
                sb.AppendLine();
            }

            sb.AppendLine("fastOpen: true");
            sb.AppendLine();
            sb.AppendLine("quic:");
            sb.AppendLine("  maxIdleTimeout: 30s");
            sb.AppendLine("  keepAlivePeriod: 20s");

            if (!string.IsNullOrEmpty(PortsRange))
            {
                sb.AppendLine();
                sb.AppendLine("transport:");
                sb.AppendLine("  udp:");
                sb.AppendLine($"    hopInterval: {HopInterval}s");
            }

            if (!string.IsNullOrEmpty(Obfs))
            {
                sb.AppendLine();
                sb.AppendLine("obfs:");
                sb.AppendLine($"  type: {Obfs}");
                if (Obfs == "salamander" && !string.IsNullOrEmpty(ObfsPassword))
                {
                    sb.AppendLine("  salamander:");
                    sb.AppendLine($"    password: {ObfsPassword}");
                }
            }

            if (LazyTls)
            {
                sb.AppendLine();
                sb.AppendLine("tls:");
                sb.AppendLine("  insecure: true");
            }

            return sb.ToString();
        }
    }
}
