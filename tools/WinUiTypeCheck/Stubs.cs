// Stand-ins for Windows-only surface, so the Windows head type-checks off Windows. Nothing runs.
namespace Horus.Platforms.Windows
{
    internal static class ExeIcons
    {
        public static Task<string?> ExtractAsync(string exePath, string pngPath, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
namespace Microsoft.UI.Xaml { public class Window { } }
namespace WinRT.Interop { public static class WindowNative { public static IntPtr GetWindowHandle(object o) => IntPtr.Zero; } }
