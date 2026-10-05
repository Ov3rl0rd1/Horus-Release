namespace Horus.Domain.Models
{
    /// <summary>
    /// 200 body of <c>POST /auth/login</c> and <c>POST /auth/verify</c>.
    /// <paramref name="expiresAt"/> is the account's <b>subscription</b> end
    /// (<c>users.expires_at</c>, null for none) — a session has no expiry of its own; the
    /// API keeps the last ten and drops the oldest. Never sign anyone out on this date: a
    /// user whose subscription lapsed must still be able to sign in and renew.
    /// <c>GET /whoami</c> stays the source the UI reads subscription state from.
    /// </summary>
    public record LoginResponse(string session, DateTime? expiresAt);
}
