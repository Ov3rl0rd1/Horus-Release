using Horus.Application;
using Horus.Application.Update;
using Horus.Domain.Interfaces;
using Horus.Presentation.Navigation;
using Horus.Presentation.View;
using Horus.Presentation.ViewModels;
using Horus.Protocols;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Text.Json;

#if ANDROID
using Horus.Platforms.Android;
#elif IOS || MACCATALYST
using Horus.Platforms.iOS;
#elif WINDOWS
using Horus.Platforms.Windows;
#endif

namespace Horus
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();

            // Load appsettings.json from embedded resource
            LoadAppSettings();

            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");

                    // v2 design system fonts. Drop these exact .ttf files into
                    // Resources/Fonts/. Missing files fall back to the system font at
                    // runtime (no build break), so the UI renders before they are added.
                    //   Manrope-Regular.ttf, Manrope-SemiBold.ttf, Manrope-Bold.ttf
                    //   Unbounded-Bold.ttf, Unbounded-ExtraBold.ttf
                    fonts.AddFont("Manrope-Regular.ttf", "Manrope");
                    fonts.AddFont("Manrope-SemiBold.ttf", "ManropeSemiBold");
                    fonts.AddFont("Manrope-Bold.ttf", "ManropeBold");
                    fonts.AddFont("Unbounded-Bold.ttf", "Unbounded");
                    fonts.AddFont("Unbounded-ExtraBold.ttf", "UnboundedExtraBold");
                });

            // Strip the platform-default Entry chrome (WinUI focus underline / rounded
            // border, Android bottom underline) so our own rounded Border shows cleanly.
            Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("HzNoNativeFrame", (handler, view) =>
            {
#if ANDROID
                handler.PlatformView.BackgroundTintList =
                    Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
                handler.PlatformView.SetPadding(0, 0, 0, 0);
#elif WINDOWS
                var tb = handler.PlatformView;
                var zero = new Microsoft.UI.Xaml.Thickness(0);
                tb.BorderThickness = zero;
                tb.Resources["TextControlBorderThemeThickness"] = zero;
                tb.Resources["TextControlBorderThemeThicknessFocused"] = zero;
                var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                tb.Resources["TextControlBackground"] = transparent;
                tb.Resources["TextControlBackgroundPointerOver"] = transparent;
                tb.Resources["TextControlBackgroundFocused"] = transparent;
                tb.Resources["TextControlBackgroundDisabled"] = transparent;
#endif
            });

            var services = builder.Services;

            // The updater and both release sources need short-lived clients with their own
            // headers and timeouts; ApiService keeps its long-lived one with the auth handler.
            services.AddHttpClient();

            // ── Core Application Services ────────────────────────────────────
            services
                .AddSingleton<VpnManager>()
                .AddSingleton<ProtocolFactory>()
                .AddSingleton<XrayProtocol>()
                .AddSingleton<IStorageService, StorageService>()
                .AddSingleton<IApiService, ApiService>()
                .AddSingleton<IAuthService, AuthService>()
                .AddSingleton<ISubscriptionService, SubscriptionService>()
                .AddSingleton<IAccountSync, AccountSyncService>()
                .AddSingleton<ITrafficMonitorService, TrafficMonitorService>()
                .AddSingleton<IRoutingService, RoutingService>()
                .AddSingleton<IGeoDataService, GeoDataService>()
                .AddSingleton<IErrorReportingService, ErrorReportingService>()
                .AddSingleton<TunnelHealthMonitor>();

            // ── Updates ──────────────────────────────────────────────────────
            // Both sources are registered; UpdateService tries GitHub first and falls back
            // to the site, which is the path that actually works for a blocked user.
            services
                .AddSingleton<IUpdateSource, GitHubReleaseSource>()
                .AddSingleton<IUpdateSource, SiteReleaseSource>()
                .AddSingleton<IUpdateService, UpdateService>();

            // ── Platform Services ────────────────────────────────────────────
#if ANDROID
            services
                .AddSingleton<IVpnPlatformService, AndroidVpnService>()
                .AddSingleton<ISplitTunnelingService, AndroidSplitTunnelingService>()
                .AddSingleton<INetworkMonitor, AndroidNetworkMonitor>()
                .AddSingleton<IUserNotifier, Platforms.Android.Update.AndroidUserNotifier>()
                .AddSingleton<IDeviceConditions, Platforms.Android.Update.AndroidDeviceConditions>()
                .AddSingleton<IUpdateInstaller, Platforms.Android.Update.AndroidUpdateInstaller>();
#elif WINDOWS
            services
                .AddSingleton<IVpnPlatformService, WindowsVpnService>()
                .AddSingleton<ISplitTunnelingService, WindowsSplitTunnelingService>()
                .AddSingleton<INetworkMonitor, WindowsNetworkMonitor>()
                .AddSingleton<IPublisherTrustService, WindowsPublisherTrustService>()
                .AddSingleton<IUserNotifier, Platforms.Windows.Update.WindowsUserNotifier>()
                .AddSingleton<IDeviceConditions, Platforms.Windows.Update.WindowsDeviceConditions>()
                .AddSingleton<IUpdateInstaller, Platforms.Windows.Update.WindowsUpdateInstaller>();
#elif IOS || MACCATALYST
            services
                .AddSingleton<IVpnPlatformService, iOSVpnService>()
                .AddSingleton<ISplitTunnelingService, StubSplitTunnelingService>();
#else
            services
                .AddSingleton<IVpnPlatformService, StubVpnPlatformService>()
                .AddSingleton<ISplitTunnelingService, StubSplitTunnelingService>();
#endif

#if !ANDROID && !WINDOWS
            // Store platforms update themselves; there is nothing here to replace.
            services
                .AddSingleton<INetworkMonitor, Application.PlatformStubs.StubNetworkMonitor>()
                .AddSingleton<IUserNotifier, Application.PlatformStubs.StubUserNotifier>()
                .AddSingleton<IDeviceConditions, Application.PlatformStubs.StubDeviceConditions>()
                .AddSingleton<IUpdateInstaller, Application.PlatformStubs.StubUpdateInstaller>();
#endif

#if !WINDOWS
            // Every other platform verifies package signatures itself at install time.
            services.AddSingleton<IPublisherTrustService, StubPublisherTrustService>();
#endif

            // ── Navigation + shared UI state (v2 custom root, no Shell) ──────
            services
                .AddSingleton<Navigator>()
                .AddSingleton<AppSession>();

            // ── ViewModels ───────────────────────────────────────────────────
            // Singletons: the shell holds them and screens are reused (e.g. Settings
            // drives both the Settings and Split screens), so state must be shared.
            services
                .AddSingleton<ShellViewModel>()
                .AddSingleton<MainViewModel>()
                .AddSingleton<ServersViewModel>()
                .AddSingleton<SettingsViewModel>()
                .AddSingleton<AuthFlowViewModel>()
                .AddSingleton<PaymentViewModel>();

            // ── Root page ────────────────────────────────────────────────────
            services.AddSingleton<RootPage>();

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }

        private static void LoadAppSettings()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("Horus.appsettings.json");
            if (stream == null) return;

            try
            {
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                if (root.TryGetProperty("ApiBaseUrl", out var url))
                    AppConfiguration.ApiBaseUrl = url.GetString() ?? AppConfiguration.ApiBaseUrl;

                if (root.TryGetProperty("SupportEmail", out var email))
                    AppConfiguration.SupportEmail = email.GetString() ?? AppConfiguration.SupportEmail;

                // SupportHandleTG is the current key; SupportHandle is the older spelling.
                // Without the first, appsettings said "@horusping" while every screen kept
                // showing the built-in default "@horus_vpn" — a support handle that does
                // not exist.
                if (root.TryGetProperty("SupportHandleTG", out var tg))
                    AppConfiguration.SupportHandle = tg.GetString() ?? AppConfiguration.SupportHandle;
                else if (root.TryGetProperty("SupportHandle", out var handle))
                    AppConfiguration.SupportHandle = handle.GetString() ?? AppConfiguration.SupportHandle;

                if (root.TryGetProperty("UpdateReleasesUrl", out var releases))
                    AppConfiguration.UpdateReleasesUrl = releases.GetString() ?? AppConfiguration.UpdateReleasesUrl;

                if (root.TryGetProperty("BlockedPackages", out var blocked)
                    && blocked.ValueKind == JsonValueKind.Array)
                {
                    AppConfiguration.BlockedPackages = [.. blocked.EnumerateArray()
                        .Select(e => e.GetString())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s!.Trim())];
                }
            }
            catch { /* appsettings parse failure is non-fatal */ }
        }
    }
}
