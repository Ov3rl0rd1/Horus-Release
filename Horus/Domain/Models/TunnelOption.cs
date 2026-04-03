namespace Horus.Domain.Models
{
    public class TunnelOptions
    {
        public string TunAddress { get; set; }   // 172.19.0.1
        public int TunPrefix { get; set; }   // 30
        public int Mtu { get; set; }   // 1500
        public string[] DnsServers { get; set; }
        public string[] BypassApps { get; set; }   // Android per-app VPN
        public bool AllTraffic { get; set; }
    }
}
