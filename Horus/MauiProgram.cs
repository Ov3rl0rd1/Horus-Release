using Horus.Application;
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

            // ── Core Application Services ────────────────────────────────────
            services
                .AddSingleton<VpnManager>()
                .AddSingleton<ProtocolFactory>()
                .AddSingleton<XrayProtocol>()
                .AddSingleton<IStorageService, StorageService>()
                .AddSingleton<IApiService, ApiService>()
                .AddSingleton<IAuthService, AuthService>()
                .AddSingleton<ISubscriptionService, SubscriptionService>()
                .AddSingleton<ITrafficMonitorService, TrafficMonitorService>()
                .AddSingleton<IRoutingService, RoutingService>()
                .AddSingleton<IGeoDataService, GeoDataService>()
                .AddSingleton<IErrorReportingService, ErrorReportingService>();

            // ── Platform Services ────────────────────────────────────────────
#if ANDROID
            services
                .AddSingleton<IVpnPlatformService, AndroidVpnService>()
                .AddSingleton<ISplitTunnelingService, AndroidSplitTunnelingService>();
#elif WINDOWS
            services
                .AddSingleton<IVpnPlatformService, WindowsVpnService>()
                .AddSingleton<ISplitTunnelingService, WindowsSplitTunnelingService>();
#elif IOS || MACCATALYST
            services
                .AddSingleton<IVpnPlatformService, iOSVpnService>()
                .AddSingleton<ISplitTunnelingService, StubSplitTunnelingService>();
#else
            services
                .AddSingleton<IVpnPlatformService, StubVpnPlatformService>()
                .AddSingleton<ISplitTunnelingService, StubSplitTunnelingService>();
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

                if (root.TryGetProperty("SupportHandle", out var handle))
                    AppConfiguration.SupportHandle = handle.GetString() ?? AppConfiguration.SupportHandle;
            }
            catch { /* appsettings parse failure is non-fatal */ }
        }
    }
}
