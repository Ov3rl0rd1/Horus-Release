namespace Horus.Presentation.Navigation
{
    /// <summary>
    /// Top-level screens the custom root page (<c>RootPage</c>) switches between.
    /// Replaces MAUI Shell routing so Android (bottom tabs + onboarding) and
    /// Windows (left sidebar) can diverge while sharing the same screen views.
    /// </summary>
    public enum AppScreen
    {
        /// <summary>
        /// Nothing decided yet — the state the app opens in, before the stored session has
        /// been read. Must stay the default: any real screen here renders for a moment on
        /// every launch, and if startup stalls or throws the user is left staring at it.
        /// </summary>
        Startup,

        // ── Auth / onboarding ──
        Onboarding,
        Login,
        Register,
        Confirm,
        Reset,

        // ── In-app ──
        Home,
        Servers,
        Settings,
        Split
    }
}
