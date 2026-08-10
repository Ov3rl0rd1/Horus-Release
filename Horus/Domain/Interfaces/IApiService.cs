using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    /// <summary>
    /// Transport for HorusAPI v1. Auth is a custom session scheme: a token from
    /// <c>/auth/login</c> or <c>/auth/verify</c> is replayed in the
    /// <c>X-Session-Key</c> header by <see cref="Horus.Application.HttpAuthHandler"/>.
    /// </summary>
    public interface IApiService
    {
        /// <summary>
        /// Raised when the API rejects the stored session on an authenticated route.
        /// Most calls degrade a 401 into an empty result, so without this a revoked
        /// session shows up as "no servers" and stale account data rather than as a
        /// prompt to sign in again.
        /// </summary>
        event EventHandler SessionExpired;

        // ── Auth ─────────────────────────────────────────────────────────────
        Task<AuthResult> LoginAsync(string username, string password, CancellationToken ct = default);

        /// <summary>Creates the account and mails a 6-digit code. Does <b>not</b> issue a session.</summary>
        Task<RegisterResult> RegisterAsync(string username, string email, string password, CancellationToken ct = default);

        /// <summary>Confirms the mailed code and issues the session.</summary>
        Task<AuthResult> VerifyEmailAsync(string email, string code, CancellationToken ct = default);

        Task<RegisterResult> ResendCodeAsync(string email, CancellationToken ct = default);

        // ── Password reset ────────────────────────────────────────────────────
        Task<RegisterResult> RequestPasswordResetAsync(string email, CancellationToken ct = default);
        Task<bool> IsResetTokenValidAsync(string token, CancellationToken ct = default);
        Task<RegisterResult> ConfirmPasswordResetAsync(string token, string password, CancellationToken ct = default);

        /// <summary>Revokes every session except the current one.</summary>
        Task<bool> LogoutOtherDevicesAsync(CancellationToken ct = default);

        // ── Servers ──────────────────────────────────────────────────────────
        Task<IReadOnlyList<ServerInfo>?> GetServersAsync(CancellationToken ct = default);

        /// <summary>
        /// Asks the API to pick and bind a server, returning its per-protocol share links.
        /// Takes no server id — selection is server-side.
        /// </summary>
        Task<ServerConnection> GetServerConnectionAsync(CancellationToken ct = default);

        // ── Account ──────────────────────────────────────────────────────────
        Task<WhoAmIResponse?> GetWhoAmIAsync(CancellationToken ct = default);

        /// <summary>
        /// Egress IP as the API sees it, optionally asked <i>through</i> a SOCKS5 proxy.
        ///
        /// Comparing the direct answer with the proxied one is what proves a tunnel is
        /// really carrying traffic: the core starts happily even when its outbound cannot
        /// dial, so "connected" is otherwise an unverified claim. Returns null when the
        /// request did not complete.
        /// </summary>
        Task<string?> GetEgressIpAsync(string? socksProxy = null, CancellationToken ct = default);

        Task<bool> IsHealthyAsync(CancellationToken ct = default);
    }
}
