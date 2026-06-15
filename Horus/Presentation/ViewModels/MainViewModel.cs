using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Horus.Application;
using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Presentation.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly VpnManager _vpnManager;
        private readonly ITrafficMonitorService _traffic;
        private readonly ISubscriptionService _subscription;
        private readonly IAuthService _auth;

        [ObservableProperty] private VpnState _vpnState = VpnState.Disconnected;
        [ObservableProperty] private ServerInfo? _selectedServer;
        [ObservableProperty] private string _connectionStatus = "Disconnected";
        [ObservableProperty] private string _downloadSpeed = "0.0";
        [ObservableProperty] private string _uploadSpeed = "0.0";
        [ObservableProperty] private string _sessionDuration = "00:00:00";
        [ObservableProperty] private string _totalData = "0 MB";

        public MainViewModel(
            VpnManager vpnManager,
            ITrafficMonitorService traffic,
            ISubscriptionService subscription,
            IAuthService auth)
        {
            _vpnManager = vpnManager;
            _traffic = traffic;
            _subscription = subscription;
            _auth = auth;

            _vpnManager.StateChanged += OnVpnStateChanged;
            _traffic.TrafficUpdated += OnTrafficUpdated;
        }

        [RelayCommand]
        async Task ConnectAsync()
        {
            if (SelectedServer == null)
            {
                await SelectServerAsync();
                if (SelectedServer == null) return;
            }
            try
            {
                await _vpnManager.ConnectAsync(SelectedServer);
            }
            catch (Exception ex)
            {
                await Shell.Current.DisplayAlertAsync("Connection Failed", ex.Message, "OK");
            }
        }

        [RelayCommand]
        async Task DisconnectAsync()
        {
            try
            {
                await _vpnManager.DisconnectAsync();
            }
            catch (Exception ex)
            {
                await Shell.Current.DisplayAlertAsync("Disconnect Failed", ex.Message, "OK");
            }
        }

        [RelayCommand]
        async Task SelectServerAsync()
        {
            var servers = await _subscription.GetAvailableServersAsync();
            if (servers.Count == 0)
            {
                await Shell.Current.DisplayAlertAsync("No Servers", "No servers available.", "OK");
                return;
            }

            var names = servers.Select(s => $"{s.Name} — {s.Location}").ToArray();
            var choice = await Shell.Current.DisplayActionSheetAsync("Select Server", "Cancel", null, names);
            if (choice == null || choice == "Cancel") return;

            var idx = Array.IndexOf(names, choice);
            if (idx >= 0) SelectedServer = servers[idx];
        }

        private void OnVpnStateChanged(object? sender, VpnStateChangedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                VpnState = e.NewState;
                ConnectionStatus = e.NewState switch
                {
                    VpnState.Connected => "Connected",
                    VpnState.Connecting => "Connecting",
                    VpnState.Disconnecting => "Connecting",
                    _ => "Disconnected"
                };

                if (e.NewState == VpnState.Disconnected)
                {
                    DownloadSpeed = "0.0";
                    UploadSpeed = "0.0";
                    SessionDuration = "00:00:00";
                    TotalData = "0 MB";
                }
            });
        }

        private void OnTrafficUpdated(object? sender, TrafficUpdatedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var s = e.Stats;
                DownloadSpeed = FormatSpeed(s.SpeedDownBps);
                UploadSpeed = FormatSpeed(s.SpeedUpBps);
                SessionDuration = s.SessionDuration.ToString(@"hh\:mm\:ss");
                TotalData = FormatBytes(s.BytesDownTotal + s.BytesUpTotal);
            });
        }

        private static string FormatSpeed(long bps)
        {
            var mbps = bps / 1_000_000.0;
            return mbps >= 1 ? $"{mbps:F1}" : $"{bps / 1_000.0:F1}k";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1_000_000_000) return $"{bytes / 1_000_000_000.0:F1} GB";
            if (bytes >= 1_000_000) return $"{bytes / 1_000_000.0:F1} MB";
            return $"{bytes / 1_000.0:F1} KB";
        }
    }
}
