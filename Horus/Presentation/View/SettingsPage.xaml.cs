using Horus.Domain.Interfaces;

namespace Horus
{
    public partial class SettingsPage : ContentPage
    {
        private readonly IAuthService? _auth;

        public SettingsPage(IAuthService auth)
        {
            _auth = auth;
            InitializeComponent();
        }

        private async void OnBackTapped(object sender, TappedEventArgs e)
        {
            await Shell.Current.GoToAsync("..");
        }

        private async void OnDnsTapped(object sender, TappedEventArgs e)
        {
            await DisplayAlert("Custom DNS", "DNS configuration coming soon.", "OK");
        }

        private void OnSplitTunnelToggled(object sender, ToggledEventArgs e)
        {
            // Split tunneling toggle — placeholder
        }

        private async void OnSignOutTapped(object? sender, EventArgs e)
        {
            bool confirm = await DisplayAlertAsync("Sign Out", "Sign out of Horus VPN?", "Sign Out", "Cancel");
            if (!confirm) return;

            if (_auth != null)
                await _auth.LogoutAsync();

            await Shell.Current.GoToAsync(nameof(AuthPage));
        }
    }
}
