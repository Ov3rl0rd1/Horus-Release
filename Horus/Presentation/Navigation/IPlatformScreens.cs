namespace Horus.Presentation.Navigation
{
    /// <summary>
    /// Screens a platform builds for itself.
    ///
    /// <para>The shared pages stay shared; a platform whose UI genuinely differs — the
    /// Windows client's application list, its home without the phone-era cards, its gaming
    /// settings — supplies its own views here, and the root page hosts them. A platform that
    /// registers nothing gets exactly the shared UI, which is how Android is unaffected.</para>
    /// </summary>
    public interface IPlatformScreens
    {
        /// <summary>The platform's own view for <paramref name="screen"/>, or null to use the shared one.</summary>
        Microsoft.Maui.Controls.View? Create(AppScreen screen);

        /// <summary>The platform's connection section for Settings, shown in place of the shared one; null keeps the shared one.</summary>
        Microsoft.Maui.Controls.View? SettingsSection();

        /// <summary>A platform screen became visible or stopped being visible (start or stop refreshing).</summary>
        void VisibilityChanged(AppScreen screen, bool visible);
    }
}
