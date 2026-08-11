using Horus.Domain.Models;
using System.Text;

namespace Horus.Protocols
{
    /// <summary>
    /// The native binaries each platform needs, and the startup check that refuses to let
    /// the app run without the required ones.
    ///
    /// Android ships its <c>.so</c> files inside the APK, so they cannot go missing at
    /// runtime — the csproj turns an absent one into a build error instead. Windows loads
    /// loose files from the app directory, which is where a broken install actually shows
    /// up, so that is what this guards.
    /// </summary>
    public static class NativeDependencies
    {
        /// <summary>Subdirectory for the optional driver components.</summary>
        public const string NativeDirectory = @"Resources\Native";

        public static IReadOnlyList<NativeDependency> All { get; } = BuildList();

        private static IReadOnlyList<NativeDependency> BuildList()
        {
#if WINDOWS
            return
            [
                new NativeDependency(
                    "xray.dll", string.Empty, Required: true,
                    "Ядро VPN. Без него подключение невозможно."),

                // The TUN bridge. All three live in one directory because hev resolves
                // wintun.dll with LOAD_LIBRARY_SEARCH_APPLICATION_DIR — the directory of
                // the exe, not of this app.
                new NativeDependency(
                    "hev-socks5-tunnel.exe", NativeDirectory, Required: true,
                    "Мост между TUN-адаптером и SOCKS5-входом ядра. Без него туннель не поднимется."),

                new NativeDependency(
                    "msys-2.0.dll", NativeDirectory, Required: true,
                    "Рантайм, с которым собран мост туннеля."),

                new NativeDependency(
                    "wintun.dll", NativeDirectory, Required: true,
                    "Драйвер TUN-адаптера."),

                new NativeDependency(
                    "WinDivert.dll", NativeDirectory, Required: false,
                    "Split tunneling по процессам."),

                new NativeDependency(
                    "WinDivert64.sys", NativeDirectory, Required: false,
                    "Драйвер WinDivert. Нужен вместе с WinDivert.dll."),
            ];
#else
            // Packaged into the app bundle; nothing to verify at runtime.
            return [];
#endif
        }

        /// <summary>
        /// Checks the required binaries. Presence is necessary but not sufficient — a
        /// wrong-architecture or blocked DLL exists and still fails to load — so the core
        /// is additionally asked for its version, which is the cheapest call that proves
        /// the library actually resolved.
        /// </summary>
        public static NativeDependencyReport Check()
        {
            var missing = All.Where(d => d.Required && !d.Exists).ToList();
            if (missing.Count > 0)
                return new NativeDependencyReport(missing, null);

            if (All.Count == 0)
                return new NativeDependencyReport([], null);

            var version = XrayInterop.Version();
            var loadFailed = version.Length == 0
                             || version is "not found" or "incompatible build"
                             || !version.Contains("Xray", StringComparison.OrdinalIgnoreCase);

            return new NativeDependencyReport(
                [],
                loadFailed
                    ? $"xray.dll найден, но не загружается ({version}). " +
                      "Обычно это несовпадение разрядности (нужна x64) или файл заблокирован " +
                      "системой — откройте свойства файла и снимите «Разблокировать»."
                    : null);
        }

        /// <summary>User-facing explanation of a failed check, including where files go.</summary>
        public static string Describe(NativeDependencyReport report)
        {
            var sb = new StringBuilder();

            if (report.Missing.Count > 0)
            {
                sb.AppendLine("Не хватает файлов, без которых приложение не работает:");
                sb.AppendLine();
                foreach (var dep in report.Missing)
                    sb.AppendLine($"  • {dep.DisplayPath} — {dep.Purpose}");

                sb.AppendLine();
                sb.AppendLine("Пути указаны относительно папки приложения:");
                sb.AppendLine(AppContext.BaseDirectory);
            }

            if (report.LoadFailure is not null)
                sb.AppendLine(report.LoadFailure);

            return sb.ToString().TrimEnd();
        }
    }
}
