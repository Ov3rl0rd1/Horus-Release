using System.Runtime.InteropServices;
using Horus.Domain.Models;
using Horus.Platforms.Windows.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Horus.Platforms.Windows
{
    /// <summary>
    /// Gives the window its one size (<see cref="HomeLayout"/>), centred on the screen it
    /// opens on, and keeps the user from resizing or maximising it: the home screen is laid
    /// out for that size and has no scroll bar.
    /// </summary>
    internal static class WindowSizing
    {
        public static void Apply(Microsoft.UI.Xaml.Window window)
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                var id = Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(id);

                // The work area is in physical pixels; the layout is in effective ones.
                var area = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary).WorkArea;
                var scale = Math.Max(1.0, GetDpiForWindow(hwnd) / 96.0);
                var (width, height) = HomeLayout.FitWindow(area.Width / scale, area.Height / scale);
                var w = (int)Math.Round(width * scale);
                var h = (int)Math.Round(height * scale);

                appWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
                if (appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsResizable = false;
                    presenter.IsMaximizable = false;
                }
                Diag.Info("window", $"{width:0}×{height:0} at {scale:0.##}x on a {area.Width / scale:0}×{area.Height / scale:0} work area");
            }
            catch (Exception ex)
            {
                // The default size still works; it only scrolls on a small screen.
                Diag.Warn("window", $"size not applied: {ex.Message}");
            }
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
    }
}
