using System.Diagnostics;
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
