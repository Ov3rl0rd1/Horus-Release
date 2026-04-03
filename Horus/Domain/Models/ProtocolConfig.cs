namespace Horus.Domain.Models
{
    public abstract class ProtocolConfig
    {
        public string ServerId { get; set; }
        public string Name { get; set; }
        public ProtocolType ProtocolType { get; set; }
        public abstract string ToConfig();
    }
}
