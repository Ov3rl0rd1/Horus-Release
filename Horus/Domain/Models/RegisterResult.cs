namespace Horus.Domain.Models
{
    public class RegisterResult
    {
        public bool Success { get; set; }
        public string? Token { get; set; }
        public UserInfo? User { get; set; }
        public string? Message { get; set; }
        public Dictionary<string, string[]>? ValidationErrors { get; set; }
    }
}
