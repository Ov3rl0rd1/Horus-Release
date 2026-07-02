using System.Reflection;

namespace Horus
{
    public static class AppConfiguration
    {
        public static string ApiBaseUrl { get; set; } = "http://localhost";

        public static string AppVersion { get; } =
            typeof(AppConfiguration).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? "1.0.0";

        public static string SupportEmail { get; set; } = "support@horus-vpn.app";
    }
}
