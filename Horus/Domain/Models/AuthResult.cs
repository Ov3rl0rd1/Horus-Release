namespace Horus.Domain.Models
{
    public class AuthResult
    {
        public bool Success { get; set; }
        public LoginResponse? Response { get; set; }
        public string Message { get; set; }
    }
}
