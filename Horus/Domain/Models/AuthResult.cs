namespace Horus.Domain.Models
{
    public class AuthResult
    {
        public bool Success { get; set; }
        public string Token { get; set; }
        public UserInfo User { get; set; }
        public string Message { get; set; }
    }
}
