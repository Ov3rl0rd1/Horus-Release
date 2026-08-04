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

        public void SelectAuto()
        {
            IsAutoSelect = true;
            SelectedServer = null;
        }

        public void Select(ServerInfo server)
        {
            IsAutoSelect = false;
            SelectedServer = server;
        }
    }
}
