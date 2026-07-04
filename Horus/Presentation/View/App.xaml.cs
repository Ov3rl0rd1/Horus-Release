using Horus.Application;
using Horus.Domain.Interfaces;
using Horus.Presentation.View;

namespace Horus
{
    public partial class App : Microsoft.Maui.Controls.Application
    {
        private readonly IAuthService _auth;
        private readonly VpnManager _vpnManager;

        public App(IAuthService auth, VpnManager vpnManager)
        {
            _auth = auth;
            _vpnManager = vpnManager;
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell());
        }

        protected override async void OnStart()
        {
            base.OnStart();

            // Attempt to restore persisted session (probes API, may switch to local mode)
            var restored = await _auth.TryRestoreSessionAsync();

            if (!restored)
            {
                await Shell.Current.GoToAsync("AuthPage");
                return;
            }

            // Background: check for binary updates silently (non-blocking)
            _ = _vpnManager.CheckAndUpdateBinariesAsync();
        }
    }
}
