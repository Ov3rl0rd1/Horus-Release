using Horus.Application;
using Horus.Domain.Interfaces;
using Horus.Platforms.Android;
using Horus.Presentation.ViewModels;
using Horus.Protocols;
using Microsoft.Extensions.Logging;

namespace Horus
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                })
                .Services
                    .AddSingleton<VpnManager>()
                    .AddSingleton<ProtocolFactory>()
                    .AddSingleton<IAuthService, AuthService>()
                    .AddSingleton<ISubscriptionService, SubscriptionService>()
                    .AddSingleton<ITrafficMonitorService, TrafficMonitorService>()
                    .AddSingleton<IRoutingService, RoutingService>()
                    .AddSingleton<IGeoDataService, GeoDataService>()
#if ANDROID
                    .AddSingleton<IVpnPlatformService, AndroidVpnService>()
                    .AddSingleton<IProcessRunner, AndroidProcessRunner>()
#endif
                    .AddTransient<MainViewModel>();

#if DEBUG
    		builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
