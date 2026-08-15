using System.Diagnostics;
using Horus.Domain.Interfaces;
using Horus.Presentation.View;
using Horus.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Horus
{
    public partial class App : Microsoft.Maui.Controls.Application
    {
        private readonly IServiceProvider _services;

        public App(IServiceProvider services)
        {
            _services = services;
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var root = _services.GetRequiredService<RootPage>();
            var window = new Window(root) { Title = "Horus" };

            // Coming back to the app is the moment a stale subscription is most obviously
            // wrong — the user has usually just been told access was granted. Polling only
            // runs while the window is up: nothing here is worth waking a sleeping phone,
            // and the tunnel does not depend on it.
            var accounts = _services.GetRequiredService<IAccountSync>();
            window.Resumed += (_, __) => accounts.OnForeground();
            window.Stopped += (_, __) => accounts.OnBackground();

            // The updater keeps running in the background: while the tunnel is up the
            // foreground service holds this process alive, and that is exactly the state
            // the "turn it on and forget about it" user is in.
            _services.GetRequiredService<IUpdateService>().Start();

            return window;
        }

        protected override async void OnStart()
        {
            base.OnStart();
            try
            {
                // Idempotent — RootPage.OnAppearing races this and whichever wins, wins.
                await _services.GetRequiredService<ShellViewModel>().EnsureStartedAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Horus] Startup error: {ex}");
            }
        }
    }
}
