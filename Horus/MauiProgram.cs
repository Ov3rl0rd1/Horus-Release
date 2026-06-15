using Horus.Application;
using Horus.Domain.Interfaces;
using Horus.Presentation.ViewModels;
using Horus.Protocols;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Text.Json;
#if ANDROID
using Horus.Platforms.Android;
#endif

namespace Horus
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();

            // Load appsettings.json from embedded resource
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("Horus.appsettings.json");
            if (stream != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(stream);
                    if (doc.RootElement.TryGetProperty("ApiBaseUrl", out var urlProp))
                        AppConfiguration.ApiBaseUrl = urlProp.GetString() ?? AppConfiguration.ApiBaseUrl;
                }
                catch { /* appsettings.json parse failure is non-fatal */ }
            }

            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            builder.Services
                .AddSingleton<VpnManager>()
                .AddSingleton<ProtocolFactory>()
                .AddSingleton<Hysteria2Protocol>()
                .AddSingleton<IStorageService, StorageService>()
                .AddSingleton<IApiService, ApiService>()
                .AddSingleton<IAuthService, AuthService>()
                .AddSingleton<ISubscriptionService, SubscriptionService>()
                .AddSingleton<ITrafficMonitorService, TrafficMonitorService>()
                .AddSingleton<IRoutingService, RoutingService>()
                .AddSingleton<IGeoDataService, GeoDataService>()
#if ANDROID
                .AddSingleton<IVpnPlatformService, AndroidVpnService>()
                .AddSingleton<IProcessRunner, AndroidProcessRunner>()
#else
                .AddSingleton<IVpnPlatformService, StubVpnPlatformService>()
                .AddSingleton<IProcessRunner, StubProcessRunner>()
#endif
                .AddTransient<MainViewModel>()
                .AddTransient<AuthViewModel>()
                .AddTransient<MainPage>()
                .AddTransient<AuthPage>()
                .AddTransient<SettingsPage>();

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
