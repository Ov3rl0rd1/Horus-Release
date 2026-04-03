using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Horus.Domain.Models;

namespace Horus.Presentation.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        [ObservableProperty] private VpnState _vpnState;

        [ObservableProperty] private TrafficStats _trafficStats;

        [ObservableProperty] private ServerInfo? _selectedServer;
        [ObservableProperty] private SubscriptionInfo? _subscription;
        [ObservableProperty] private string _connectionDuration;

        [RelayCommand]
        Task ConnectAsync()
        {
            throw new NotImplementedException();
        }
        [RelayCommand]
        Task DisconnectAsync()
        {
            throw new NotImplementedException();
        }
        [RelayCommand]
        Task SelectServerAsync()
        {
            throw new NotImplementedException();
        }
    }
}
