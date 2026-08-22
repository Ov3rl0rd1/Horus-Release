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

        /// <summary>
        /// Candidate nodes to measure: the least-loaded one with free capacity in each
        /// country. Binds nothing — the client probes these and then calls
        /// <see cref="SelectServerAsync"/>.
        ///
        /// <para>Returns null when the request did not complete, and an empty list when the
        /// API genuinely has nothing to offer. The two mean different things to the UI.</para>
        /// </summary>
        Task<IReadOnlyList<ServerInfo>?> GetServersAsync(CancellationToken ct = default);

        /// <summary>
        /// Reserves a slot on <paramref name="serverId"/> and binds the account to it, or
        /// picks the least-loaded node when it is null.
        ///
        /// <para>This is where capacity is enforced, so it is where a full node is refused:
        /// a node with no free slot throws rather than silently binding somewhere else.
        /// Idempotent when the account is already on that node.</para>
        /// </summary>
        /// <exception cref="SubscriptionExpiredException">The subscription has lapsed.</exception>
        /// <exception cref="InvalidOperationException">No such node, or it is full.</exception>
        Task<BoundServer> SelectServerAsync(int? serverId = null, CancellationToken ct = default);

        /// <summary>
        /// Connection endpoints for the node the account is bound to.
        ///
        /// <para>Binds by auto-pick if the account has no node yet, so a first connect works
        /// without a prior <see cref="SelectServerAsync"/> — but it will not move an
        /// account that is already bound, which is what makes server choice explicit.</para>
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
