namespace Horus.Domain.Models
{
    public class RoutingRule
    {
        public string Id { get; set; }
        public RuleType Type { get; set; }
        public string Pattern { get; set; }   // "geoip:RU" | "192.168.0.0/16" | etc.
        public RuleAction Action { get; set; }
        public int Priority { get; set; }
        public bool IsEnabled { get; set; }
    }
}
