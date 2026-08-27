using Horus.Application;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
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
            var permissions = _services.GetRequiredService<ISystemPermissions>();
            var notices = _services.GetRequiredService<INoticeService>();
            var updates = _services.GetRequiredService<IUpdateService>();

            window.Resumed += (_, __) =>
            {
                // Drives the sampling cadence of the traffic monitor. A graph nobody can
                // see does not need to be a second old, and this is the signal that says
                // whether anybody can.
                AppVisibility.SetForeground();
                accounts.OnForeground();

                // Resume is the only signal Android gives for "the user may have changed a
                // permission". There is no broadcast for granting "install unknown apps",
                // so the alternative would be polling — which on a battery-sensitive VPN is
                // not an alternative at all. The user leaves for Settings and comes back,
                // and that round trip ends here.
                permissions.Refresh();
                notices.Refresh();

                // Order matters: the re-read above has to land first, so that a parked
                // update is released only once the permission it was waiting for is
                // actually there. A parked update costs nothing while it waits — no timer,
                // no wakeup — so this is the only thing that ever un-sticks it.
                updates.RetryNow();
            };

            window.Stopped += (_, __) =>
            {
                AppVisibility.SetBackground();
                accounts.OnBackground();
            };

            // Created foreground: Resumed does not fire for the first activation on every
            // platform, and starting in the background state would leave the first session
            // sampling at the idle interval with the user watching.
            AppVisibility.SetForeground();

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
                Diag.Error("app", $"startup error: {ex.Message}", ex.ToString());
            }

            // After startup, never before, and the ordering is load-bearing: every route
            // into the API needs the session that EnsureStartedAsync restores, and a
            // connect attempted before it lands fails with "no session" while still
            // advancing the reconnect backoff.
            //
            // This is also the safety net for a tunnel that died with its process. The
            // system does not always restart a sticky foreground service — measured on a
            // real device, it did not restart it at all — so this is what actually brings
            // the VPN back for a user who never turned it off.
            try { await _services.GetRequiredService<VpnManager>().TryRestoreOrAutoConnectAsync(); }
            catch (Exception ex) { Diag.Warn("app", $"startup connect failed: {ex.Message}"); }
        }
    }
}
