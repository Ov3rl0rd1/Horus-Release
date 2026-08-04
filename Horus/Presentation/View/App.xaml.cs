using System.Diagnostics;
using Horus.Application;
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
            return new Window(root) { Title = "Horus" };
        }

        protected override async void OnStart()
        {
            base.OnStart();
            try
            {
                var auth = _services.GetRequiredService<IAuthService>();
                var shell = _services.GetRequiredService<ShellViewModel>();

                // Restore a persisted session if one exists, then route to the first screen.
                var restored = await auth.TryRestoreSessionAsync();
                shell.Initialize(restored);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Horus] Startup error: {ex}");
            }
        }
    }
}
