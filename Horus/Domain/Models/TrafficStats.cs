namespace Horus.Domain.Models
{
    public class TrafficStats
    {
        public long BytesUpTotal { get; set; }
        public long BytesDownTotal { get; set; }
        public long SpeedUpBps { get; set; }
        public long SpeedDownBps { get; set; }
        public TimeSpan SessionDuration { get; set; }
        public string ConnectedServer { get; set; }
        public DateTime ConnectedAt { get; set; }
    }
}
