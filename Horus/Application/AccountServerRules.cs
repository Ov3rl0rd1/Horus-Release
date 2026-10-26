using Horus.Domain.Models;

namespace Horus.Application
{
    /// <summary>
    /// The decisions about the account's server, apart from the calls that act on them.
    ///
    /// <para>The server belongs to the account (<c>users.current_server_id</c>), shared by
    /// every device signed in to it: the app shows it, moves it only when the user picks
    /// another one, and follows it when it is moved from another device or by support.</para>
    /// </summary>
    public static class AccountServerRules
    {
        /// <summary>The account's server has to be looked up when it is not the one already known.</summary>
        public static bool NeedsLookup(int? accountServerId, int? knownServerId) => accountServerId != knownServerId;

        /// <summary>
        /// A running tunnel on another server than the account's must move to it: the old node
        /// no longer has the account. One that is not running takes the account's server at its
        /// next connect anyway, and nothing is moved on a difference nobody knows of.
        /// </summary>
        public static bool TunnelMustFollow(int? accountServerId, int? tunnelServerId, VpnState state) =>
            accountServerId is { } account
            && tunnelServerId is { } running
            && account != running
            && state is VpnState.Connected or VpnState.Reconnecting;

        /// <summary>Picking the server the account is already on is not a change, and asks nothing.</summary>
        public static bool IsChange(int chosenServerId, int? accountServerId) => chosenServerId != accountServerId;
    }
}
