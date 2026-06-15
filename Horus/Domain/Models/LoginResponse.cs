namespace Horus.Domain.Models
{
    public class LoginResponse
    {
        public string Token { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string Username { get; set; }
        public string Session { get; set; }
    }
}
