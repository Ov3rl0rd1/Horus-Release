using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Presentation.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly IAuthService _auth;
        private readonly IGeoDataService _geo;
        private readonly IRoutingService _routing;
        private readonly ISplitTunnelingService _splitTunneling;
        private readonly IBinaryUpdaterService _updater;
        private readonly IErrorReportingService _errorReporting;

        // ── Account ──────────────────────────────────────────────────────────
        [ObservableProperty] private string _username = string.Empty;
        [ObservableProperty] private string _planBadge = "Free";
        [ObservableProperty] private string _renewalDate = "—";

        // ── GeoIP ────────────────────────────────────────────────────────────
        [ObservableProperty] private string _geoDbStatus = "Not loaded";
        [ObservableProperty] private bool _isUpdatingGeoDb;
        [ObservableProperty] private bool _ruBypassEnabled = true;

        // ── Routing rules ─────────────────────────────────────────────────────
        [ObservableProperty] private string _routingRulesStatus = "Not fetched";
        [ObservableProperty] private bool _isUpdatingRules;

        // ── Split tunneling ───────────────────────────────────────────────────
        [ObservableProperty] private bool _splitTunnelingSupported;
        [ObservableProperty] private SplitTunnelingMode _splitTunnelingMode = SplitTunnelingMode.Disabled;
        [ObservableProperty] private IReadOnlyList<AppOrProcessEntry> _availableApps = [];
        [ObservableProperty] private bool _isLoadingApps;

        // ── Connection ────────────────────────────────────────────────────────
        [ObservableProperty] private bool _autoConnect;
        [ObservableProperty] private bool _killSwitch;
        [ObservableProperty] private string _customDns = "1.1.1.1, 8.8.8.8";

        // ── Binary update ─────────────────────────────────────────────────────
        [ObservableProperty] private string _hysteria2Version = "Bundled";
        [ObservableProperty] private bool _isUpdatingBinary;

        // ── Diagnostics ───────────────────────────────────────────────────────
        // Diagnostics card is only surfaced when the error reporting service has
        // recorded something worth sending.
        [ObservableProperty] private bool _hasDiagnostics;

        // ── Status ────────────────────────────────────────────────────────────
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private string _statusMessage = string.Empty;

        public SettingsViewModel(
            IAuthService auth,
            IGeoDataService geo,
            IRoutingService routing,
            ISplitTunnelingService splitTunneling,
            IBinaryUpdaterService updater,
            IErrorReportingService errorReporting)
        {
            _auth = auth;
            _geo = geo;
            _routing = routing;
            _splitTunneling = splitTunneling;
            _updater = updater;
            _errorReporting = errorReporting;

            SplitTunnelingSupported = splitTunneling.IsSupported;
        }

        public async Task InitializeAsync()
        {
            // Account info
            if (_auth.CurrentUser != null)
            {
                Username = _auth.CurrentUser.Login;
                RenewalDate = _auth.CurrentUser.ValidUntil.ToLocalTime().ToString("d MMM yyyy");
            }

            // GeoIP status
            RefreshGeoStatus();

            // Split tunneling
            SplitTunnelingMode = _splitTunneling.Mode;

            // Hysteria2 version
            var installed = _updater.GetInstalledBinaryPath("hysteria2");
            Hysteria2Version = string.IsNullOrEmpty(installed) ? "Bundled" : "Updated";

            // Only show diagnostics when there's something recorded to report.
            HasDiagnostics = _errorReporting.HasPendingReports;

            await Task.CompletedTask;
        }

        // ── GeoIP ────────────────────────────────────────────────────────────

        [RelayCommand]
        async Task UpdateGeoDbAsync()
        {
            if (IsUpdatingGeoDb) return;
            IsUpdatingGeoDb = true;
            try
            {
                await _geo.UpdateGeoDataAsync(string.Empty, string.Empty);
                RefreshGeoStatus();
                ShowStatus("GeoIP database updated.");
            }
            catch (Exception ex)
            {
                ShowStatus($"GeoIP update failed: {ex.Message}");
            }
            finally
            {
                IsUpdatingGeoDb = false;
            }
        }

        // ── Routing rules ─────────────────────────────────────────────────────

        [RelayCommand]
        async Task RefreshRoutingRulesAsync()
        {
            if (IsUpdatingRules) return;
            IsUpdatingRules = true;
            try
            {
                await _routing.RefreshFromServerAsync();
                RoutingRulesStatus = $"Updated: {DateTime.Now:HH:mm}";
                ShowStatus("Routing rules refreshed.");
            }
            catch (Exception ex)
            {
                ShowStatus($"Failed: {ex.Message}");
            }
            finally
            {
                IsUpdatingRules = false;
            }
        }

        // ── Split tunneling ───────────────────────────────────────────────────

        [RelayCommand]
        async Task LoadAppsAsync()
        {
            if (IsLoadingApps) return;
            IsLoadingApps = true;
            try
            {
                AvailableApps = await _splitTunneling.GetAvailableEntriesAsync();
            }
            finally
            {
                IsLoadingApps = false;
            }
        }

        [RelayCommand]
        async Task ToggleAppAsync(AppOrProcessEntry entry)
        {
            var current = _splitTunneling.SelectedEntries.ToHashSet();
            if (current.Contains(entry.Id))
                current.Remove(entry.Id);
            else
                current.Add(entry.Id);

            await _splitTunneling.SetSelectedEntriesAsync(current);
            await _splitTunneling.ApplyAsync();
        }

        partial void OnSplitTunnelingModeChanged(SplitTunnelingMode value)
        {
            _splitTunneling.Mode = value;
        }

        // ── Binary update ─────────────────────────────────────────────────────

        [RelayCommand]
        async Task UpdateHysteria2Async()
        {
            if (IsUpdatingBinary) return;
            IsUpdatingBinary = true;
            try
            {
                var info = await _updater.CheckForUpdateAsync("hysteria2");
                if (!info.UpdateAvailable)
                {
                    ShowStatus($"Already up to date ({info.LatestVersion}).");
                    return;
                }

                ShowStatus($"Downloading {info.LatestVersion}...");
                await _updater.DownloadAndInstallAsync(info,
                    new Progress<double>(p => ShowStatus($"Downloading… {p:P0}")));
                Hysteria2Version = info.LatestVersion;
                ShowStatus($"Updated to {info.LatestVersion}.");
            }
            catch (Exception ex)
            {
                ShowStatus($"Update failed: {ex.Message}");
            }
            finally
            {
                IsUpdatingBinary = false;
            }
        }

        // ── Error reporting ───────────────────────────────────────────────────

        [RelayCommand]
        async Task SendDiagnosticsAsync()
        {
            IsBusy = true;
            try
            {
                var sent = await _errorReporting.FlushAsync();
                if (sent)
                {
                    ShowStatus("Diagnostics sent successfully.");
                }
                else
                {
                    var emailUri = _errorReporting.BuildSupportEmailUri();
                    await Launcher.OpenAsync(emailUri);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ── Account ───────────────────────────────────────────────────────────

        [RelayCommand]
        async Task SignOutAsync()
        {
            var confirm = await Shell.Current.DisplayAlertAsync(
                "Sign Out", "Are you sure you want to sign out?", "Sign Out", "Cancel");
            if (!confirm) return;

            await _auth.LogoutAsync();
            await Shell.Current.GoToAsync("//AuthPage");
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private void RefreshGeoStatus()
        {
            if (_geo.IsGeoIpLoaded)
                GeoDbStatus = $"Loaded — {_geo.GeoIpLastUpdated?.ToLocalTime().ToString("d MMM yyyy") ?? "unknown date"}";
            else
                GeoDbStatus = "Not loaded";
        }

        private void ShowStatus(string message)
        {
            StatusMessage = message;
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await Task.Delay(4000);
                if (StatusMessage == message) StatusMessage = string.Empty;
            });
        }

        public bool IsAppSelected(string appId) =>
            _splitTunneling.SelectedEntries.Contains(appId);
    }
}
