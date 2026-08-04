namespace Horus.Presentation.Navigation
{
    /// <summary>
    /// Top-level screens the custom root page (<c>RootPage</c>) switches between.
    /// Replaces MAUI Shell routing so Android (bottom tabs + onboarding) and
    /// Windows (left sidebar) can diverge while sharing the same screen views.
    /// </summary>
    public enum AppScreen
    {
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
