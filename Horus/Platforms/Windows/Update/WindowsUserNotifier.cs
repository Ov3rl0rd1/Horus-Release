using Horus.Domain.Interfaces;

namespace Horus.Platforms.Windows.Update
{
    /// <summary>
    /// A Windows toast, when the platform will give us one.
    ///
    /// Horus ships unpackaged (MSI and a zip), and toast notifications from an unpackaged
    /// app need the App SDK's notification manager to register an identity first. That
    /// registration can fail for reasons entirely outside the app — an App SDK runtime that
    /// is not deployed, a machine policy, a system account. None of those are worth an
    /// error: the update already happened, and the version on the Settings screen is the
    /// durable record. So every failure here is swallowed and the toast is simply not
    /// shown.
    /// </summary>
    public sealed class WindowsUserNotifier : IUserNotifier
    {
        private static bool _registered;
        private static bool _unavailable;

        public Task NotifyAsync(string title, string message)
        {
            if (_unavailable) return Task.CompletedTask;

            try
            {
                var manager = Microsoft.Windows.AppNotifications.AppNotificationManager.Default;
                if (!_registered)
                {
                    manager.Register();
                    _registered = true;
                }

                var xml =
                    "<toast><visual><binding template='ToastGeneric'>" +
                    $"<text>{Escape(title)}</text>" +
                    $"<text>{Escape(message)}</text>" +
                    "</binding></visual></toast>";

                manager.Show(new Microsoft.Windows.AppNotifications.AppNotification(xml));
            }
            catch
            {
                // Stop trying: if registration failed once it will fail every time, and
                // this runs on a loop.
                _unavailable = true;
            }

            return Task.CompletedTask;
        }

        private static string Escape(string text) =>
            text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
