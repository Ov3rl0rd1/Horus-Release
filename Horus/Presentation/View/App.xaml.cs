using Horus.Application;
using Horus.Domain.Interfaces;
using Horus.Presentation.View;

namespace Horus
{
    public partial class App : Microsoft.Maui.Controls.Application
    {
        private readonly IAuthService _auth;
        private readonly VpnManager _vpnManager;
        private readonly ILocalConfigService _localConfig;

        public App(IAuthService auth, VpnManager vpnManager, ILocalConfigService localConfig)
        {
            _auth = auth;
            _vpnManager = vpnManager;
            _localConfig = localConfig;
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell());
        }

        protected override async void OnStart()
        {
            base.OnStart();

            // Load local config before anything else (needed for local mode fallback)
            await _localConfig.LoadAsync();

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
