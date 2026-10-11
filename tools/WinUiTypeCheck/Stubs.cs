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
// Window sizing (Platforms/Windows/WindowSizing.cs): the Windows App SDK surface it touches.
namespace Microsoft.UI
{
    public struct WindowId { }
    public static class Win32Interop { public static WindowId GetWindowIdFromWindow(IntPtr hwnd) => default; }
}
namespace Windows.Graphics
{
    public struct RectInt32 { public int X, Y, Width, Height; public RectInt32(int x, int y, int width, int height) { X = x; Y = y; Width = width; Height = height; } }
}
namespace Microsoft.UI.Windowing
{
    public class AppWindowPresenter { }
    public sealed class OverlappedPresenter : AppWindowPresenter { public bool IsResizable { get; set; } public bool IsMaximizable { get; set; } }
    public sealed class AppWindow
    {
        public AppWindowPresenter Presenter { get; } = new OverlappedPresenter();
        public static AppWindow GetFromWindowId(Microsoft.UI.WindowId id) => new();
        public void MoveAndResize(global::Windows.Graphics.RectInt32 rect) { }
    }
    public enum DisplayAreaFallback { None, Primary, Nearest }
    public sealed class DisplayArea
    {
        public global::Windows.Graphics.RectInt32 WorkArea { get; }
        public static DisplayArea GetFromWindowId(Microsoft.UI.WindowId id, DisplayAreaFallback fallback) => new();
    }
}
