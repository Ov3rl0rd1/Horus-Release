namespace Horus.Domain.Models
{
    public class AuthResult
    {
        public bool Success { get; set; }
        public LoginResponse? Response { get; set; }
        public string Message { get; set; } = string.Empty;

        /// <summary>Machine-readable <c>code</c> from the API's error envelope, when present.</summary>
        public string? ErrorCode { get; set; }

        /// <summary>HTTP status of the failing call — lets callers branch on 409/429 etc.</summary>
        public int StatusCode { get; set; }

        public static AuthResult Fail(string message, string? code = null, int status = 0) =>
            new() { Success = false, Message = message, ErrorCode = code, StatusCode = status };
    }
}
