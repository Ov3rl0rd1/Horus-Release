using Horus.Application;
using Horus.Domain.Interfaces;
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
                });

            var services = builder.Services;

            // ── Core Application Services ────────────────────────────────────
            services
                // Local mode must be registered before ApiService and AuthService
                .AddSingleton<ILocalModeService, LocalModeService>()
                .AddSingleton<ILocalConfigService, LocalConfigService>()
                .AddSingleton<VpnManager>()
                .AddSingleton<ProtocolFactory>()
                .AddSingleton<Hysteria2Protocol>()
                .AddSingleton<OlcRtcProtocol>()
                .AddSingleton<IStorageService, StorageService>()
                .AddSingleton<IApiService, ApiService>()
                .AddSingleton<IAuthService, AuthService>()
                .AddSingleton<ISubscriptionService, SubscriptionService>()
                .AddSingleton<ITrafficMonitorService, TrafficMonitorService>()
                .AddSingleton<IRoutingService, RoutingService>()
                .AddSingleton<IGeoDataService, GeoDataService>()
                .AddSingleton<IBinaryUpdaterService, BinaryUpdaterService>()
                .AddSingleton<IErrorReportingService, ErrorReportingService>();

            // ── Platform Services ────────────────────────────────────────────
#if ANDROID
            services
                .AddSingleton<IVpnPlatformService, AndroidVpnService>()
                .AddSingleton<IProcessRunner, AndroidProcessRunner>()
                .AddSingleton<ISplitTunnelingService, AndroidSplitTunnelingService>();
#elif WINDOWS
            services
                .AddSingleton<IVpnPlatformService, WindowsVpnService>()
                .AddSingleton<IProcessRunner, WindowsProcessRunner>()
                .AddSingleton<ISplitTunnelingService, WindowsSplitTunnelingService>();
#elif IOS || MACCATALYST
            services
                .AddSingleton<IVpnPlatformService, iOSVpnService>()
                .AddSingleton<IProcessRunner, iOSProcessRunner>()
                .AddSingleton<ISplitTunnelingService, StubSplitTunnelingService>();
#else
            services
                .AddSingleton<IVpnPlatformService, StubVpnPlatformService>()
                .AddSingleton<IProcessRunner, StubProcessRunner>()
                .AddSingleton<ISplitTunnelingService, StubSplitTunnelingService>();
#endif

            // ── ViewModels (transient) ───────────────────────────────────────
            services
                .AddTransient<MainViewModel>()
                .AddTransient<AuthViewModel>()
                .AddTransient<RegisterViewModel>()
                .AddTransient<SettingsViewModel>();

#if ADMIN_MODE
            services.AddTransient<AdminViewModel>();
#endif

            // ── Pages (transient) ────────────────────────────────────────────
            services
                .AddTransient<MainPage>()
                .AddTransient<AuthPage>()
                .AddTransient<RegisterPage>()
                .AddTransient<SettingsPage>();

#if ADMIN_MODE
            services.AddTransient<AdminPage>();
#endif

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
            }
            catch { /* appsettings parse failure is non-fatal */ }
        }
    }
}
