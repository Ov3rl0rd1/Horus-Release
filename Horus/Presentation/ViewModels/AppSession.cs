using CommunityToolkit.Mvvm.ComponentModel;
using Horus.Domain.Models;

namespace Horus.Presentation.ViewModels
{
    /// <summary>
    /// Shared, app-wide UI state (singleton). Holds the server the user has picked
    /// so the Home screen and the Servers screen stay in sync. "Auto" means "let
    /// the app pick the fastest server" — resolved to the first server at connect time.
    /// </summary>
    public partial class AppSession : ObservableObject
    {
        /// <summary>The concrete server the user selected, or null when in Auto mode.</summary>
        [ObservableProperty] private ServerInfo? _selectedServer;

        /// <summary>True when the user chose "Автовыбор" instead of a specific server.</summary>
        [ObservableProperty] private bool _isAutoSelect = true;

        /// <summary>
        /// Whether the current selection is the user's own choice, as opposed to the node the
        /// account turned out to be bound to.
        ///
        /// <para>The distinction decides whether the next connect sends
        /// <c>POST /servers/select</c>. Binding is a reservation, so re-sending it for a node
        /// the account is already on costs a round trip and a re-provision for nothing — and
        /// "auto" never meant "give me a different node each time", it meant the user does not
        /// care which one. So a pin made on our side stays display-only.</para>
        /// </summary>
        public bool IsExplicit { get; private set; }

        public void SelectAuto()
        {
            IsAutoSelect = true;
            IsExplicit = false;
            SelectedServer = null;
        }

        public void Select(ServerInfo server)
        {
            IsAutoSelect = false;
            IsExplicit = true;
            SelectedServer = server;
        }

        /// <summary>
        /// Records the node a connect actually landed on, so the UI stops saying "Автовыбор"
        /// once the choice has been made and fixed.
        ///
        /// <para>After the first connect the account is bound (<c>users.current_server_id</c>)
        /// and the endpoints are cached on the device, so "auto" has already resolved to one
        /// specific node and will keep resolving to it. Continuing to show "Автовыбор" is
        /// simply out of date.</para>
        ///
        /// <para>Does not mark the selection explicit — see <see cref="IsExplicit"/> — and
        /// leaves an explicit choice alone, so this can never quietly overwrite what the user
        /// asked for.</para>
        /// </summary>
        public void PinResolved(ServerInfo server)
        {
            if (IsExplicit) return;

            IsAutoSelect = false;
            SelectedServer = server;
        }
    }
}
