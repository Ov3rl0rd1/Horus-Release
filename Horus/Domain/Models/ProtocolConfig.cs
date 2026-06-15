namespace Horus.Domain.Models
{
    public abstract class ProtocolConfig
    {
        public string ServerId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public abstract ProtocolType ProtocolType { get; }
        public abstract string ToConfig();
    }
}
