namespace Horus.Domain.Models
{
    /// <summary>
    /// What a notice is about. The kind is the identity — one notice per kind at a time —
    /// and it is what the action handler switches on.
    /// </summary>
    public enum NoticeKind
    {
        /// <summary>Subscription expiring or gone. The original banner, now one of several.</summary>
        Subscription,

        /// <summary>
        /// "Install unknown apps" is off, so a downloaded update can never be applied.
        /// Blocking rather than advisory: the updater parks until this clears.
        /// </summary>
        InstallPermission,

        /// <summary>Notifications are switched off, so nothing the app has to say arrives.</summary>
        Notifications,

        /// <summary>The app is subject to battery optimisation, which is what kills a sleeping tunnel.</summary>
        BatteryOptimisation
    }

    /// <summary>How loudly a notice presents. Two levels only — more would be decoration.</summary>
    public enum NoticeTone
    {
        /// <summary>Something to do soon. The gold treatment the renew banner already used.</summary>
        Suggestion,

        /// <summary>Something is broken and a feature does not work until it is fixed.</summary>
        Problem
    }

    /// <summary>
    /// One actionable message on the Home screen.
    ///
    /// This exists because the subscription banner was hard-coded into the view, and three
    /// more conditions needed the same treatment: a permission that silently prevents
    /// updates, notifications the user has switched off, and battery optimisation that ends
    /// tunnels overnight. Four bespoke banners would be four places to get the layout
    /// wrong, so the view now renders a list and this is its item.
    ///
    /// Every notice must be <i>actionable</i>. A banner the user cannot do anything about
    /// is a permanent accusation, and the fastest way to teach people to ignore the whole
    /// mechanism — so each carries the label of the one button that resolves it.
    /// </summary>
    /// <param name="Kind">Identity; also selects the action.</param>
    /// <param name="Title">One short line, Russian, sentence case.</param>
    /// <param name="Message">One line of why it matters.</param>
    /// <param name="ActionLabel">The button. A verb.</param>
    /// <param name="Tone">Drives the colour treatment only.</param>
    /// <param name="CanDismiss">
    /// Whether the user may hide it. Dismissal is remembered for a while rather than
    /// forever: the conditions here come back, and a notice that can be silenced
    /// permanently is one the user will silence on the day it first appears.
    /// </param>
    public sealed record AppNotice(
        NoticeKind Kind,
        string Title,
        string Message,
        string ActionLabel,
        NoticeTone Tone,
        bool CanDismiss);
}
