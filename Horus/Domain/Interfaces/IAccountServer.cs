using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    /// <summary>
    /// The server the account is bound to — one for every device signed in to it.
    ///
    /// <para>There is no "auto" any more: the account's server is what the app shows and what
    /// every connect goes to. The API still picks one for an account that has none, at its
    /// first connect. Moving the account is the user's choice on the servers screen; a move
    /// made from another device, or by support, is followed by a running tunnel.</para>
    /// </summary>
    public interface IAccountServer
    {
        /// <summary>The account's server; null until it is known, or while the account has none.</summary>
        ServerInfo? Current { get; }

        /// <summary>Raised when <see cref="Current"/> changed, on whichever thread learnt it.</summary>
        event EventHandler? Changed;

        /// <summary>
        /// Moves the account to <paramref name="server"/>, and a running tunnel with it. Every
        /// other device on the account loses its connection until it follows.
        /// </summary>
        /// <exception cref="InvalidOperationException">The node is full or gone.</exception>
        /// <exception cref="SubscriptionExpiredException">The subscription has lapsed.</exception>
        Task SelectAsync(ServerInfo server, CancellationToken ct = default);
    }
}
