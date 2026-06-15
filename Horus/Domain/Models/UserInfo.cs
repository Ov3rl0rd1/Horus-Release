namespace Horus.Domain.Models
{
    public class UserInfo
    {
        public string Login { get; set; }
        public string Session { get; set; }
        public DateTime ValidUntil { get; set; }
    }
}
