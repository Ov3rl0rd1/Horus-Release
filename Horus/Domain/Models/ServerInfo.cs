namespace Horus.Domain.Models
{
    public class ServerInfo
    {
        public string Domain { get; set; }
        public string Location { get; set; }
        public int MaxUsers { get; set; }
        public int CurrentUserCount { get; set; }
    }
}
