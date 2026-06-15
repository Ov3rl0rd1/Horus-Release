using Horus.Domain.Interfaces;
using AppService = Horus.Application.AuthService;

namespace Horus
{
    public partial class App : Microsoft.Maui.Controls.Application
    {
        private readonly IAuthService _auth;

        public App(IAuthService auth)
        {
            _auth = auth;
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell());
        }

        protected override async void OnStart()
        {
            base.OnStart();

            if (_auth is AppService concrete)
                await concrete.TryRestoreSessionAsync();

            if (!_auth.IsAuthenticated)
                await Shell.Current.GoToAsync($"{nameof(AuthPage)}");
        }
    }
}
