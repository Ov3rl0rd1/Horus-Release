namespace Horus.Domain.Models
{
    /// <summary>
    /// The signed-in account. <paramref name="expiresAt"/> is the <b>subscription</b>
    /// expiry (from <c>/whoami</c> or persisted storage) — not the session expiry,
    /// which the API reports separately on login.
    /// </summary>
    public record User(
        string username,
        DateTime? expiresAt,
        string? email = null,
        bool emailVerified = false);
}
