using Horus.Domain.Events;
using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    public interface IAuthService
    {
        User? CurrentUser { get; }
        bool IsAuthenticated { get; }

        Task<AuthResult> LoginAsync(string username, string password);

        /// <summary>
        /// Creates the account and mails a 6-digit code. The user is <b>not</b> signed in
        /// until <see cref="VerifyEmailAsync"/> succeeds.
        /// </summary>
        Task<RegisterResult> RegisterAsync(string username, string email, string password);

        /// <summary>Confirms the mailed code, which issues the session and signs the user in.</summary>
        Task<AuthResult> VerifyEmailAsync(string email, string code);

        Task<RegisterResult> ResendCodeAsync(string email);
        Task<RegisterResult> RequestPasswordResetAsync(string email);

        Task LogoutAsync();

        /// <summary>Called on app startup to restore a persisted session.</summary>
        Task<bool> TryRestoreSessionAsync();

        /// <summary>Re-reads the account snapshot from <c>/whoami</c>. Returns null when offline.</summary>
        Task<WhoAmIResponse?> RefreshAccountAsync();

        event EventHandler<AuthStateChangedEventArgs> AuthStateChanged;

#if DEBUG
        /// <summary>DEBUG only: sign in a fake local user without contacting the API.</summary>
        void DevSignIn(string email);
#endif
    }
}
