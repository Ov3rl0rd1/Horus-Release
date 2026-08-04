namespace Horus.Domain.Models
{
    /// <summary>
    /// 200 body of <c>POST /auth/login</c> and <c>POST /auth/verify</c>.
    /// <paramref name="expiresAt"/> is the <b>session</b> expiry — subscription state
    /// comes from <c>GET /whoami</c>.
    /// </summary>
    public record LoginResponse(string session, DateTime? expiresAt);
}
