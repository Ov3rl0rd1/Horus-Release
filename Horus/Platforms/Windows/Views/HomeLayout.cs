namespace Horus.Platforms.Windows.Views
{
    /// <summary>
    /// The size of the Windows window, and how the home screen fits inside it without a
    /// scroll bar. Pure arithmetic, so the small-screen cases are tested on any OS.
    ///
    /// <para><b>One size for everyone.</b> The window is <see cref="WindowWidth"/> ×
    /// <see cref="WindowHeight"/> effective pixels and cannot be resized, so the home screen
    /// is laid out once and looks the same on every monitor. The height is the one that
    /// matters: a 1920×1080 laptop at 150% has 672 effective pixels of work area above the
    /// taskbar, and 640 leaves it room. A screen smaller still — 1366×768 at 125% has
    /// about 580 — gets the same window shrunk to its work area, and the connect ring shrinks
    /// with it (<see cref="RingDiameter"/>), so nothing is cut off or scrolled.</para>
    /// </summary>
    public static class HomeLayout
    {
        public const double WindowWidth = 1040;
        public const double WindowHeight = 640;

        /// <summary>Kept clear around the window on a screen too small for it.</summary>
        public const double ScreenMargin = 12;

        /// <summary>The sidebar takes 268 of the width; below this the two home columns no longer fit.</summary>
        public const double MinWindowWidth = 760;
        public const double MinWindowHeight = 480;

        public const double MaxRing = 250;
        public const double MinRing = 130;

        /// <summary>
        /// Everything in the connect column except the ring: the status pill, the protocol
        /// line, the caption, the session timer, the server card and the spacing between them.
        /// </summary>
        public const double ConnectColumnFixed = 262;

        /// <summary>The window's size for a monitor whose work area is <paramref name="workWidth"/> × <paramref name="workHeight"/> effective pixels.</summary>
        public static (double Width, double Height) FitWindow(double workWidth, double workHeight) =>
            (Fit(WindowWidth, MinWindowWidth, workWidth), Fit(WindowHeight, MinWindowHeight, workHeight));

        private static double Fit(double wanted, double minimum, double work) =>
            Math.Max(Math.Min(minimum, work), Math.Min(wanted, work - 2 * ScreenMargin));

        /// <summary>The connect ring's diameter for a column <paramref name="columnHeight"/> high.</summary>
        public static double RingDiameter(double columnHeight) =>
            Math.Clamp(columnHeight - ConnectColumnFixed, MinRing, MaxRing);
    }
}
