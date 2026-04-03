namespace Horus.Domain.Models
{
    public class UserInfo
    {
        public int Id { get; set; }
        public string Login { get; set; }
        public string ApiKey { get; set; }
        public DateTime ValidUntil { get; set; }
    }
}
