using Horus.Application;

namespace Horus.Presentation.Navigation
{
    /// <summary>
    /// Opens a support channel (<see cref="SupportChannels"/>): Telegram and VK in the browser
    /// (which hands them to the app when it is installed), mail in the mail client.
    /// </summary>
    public static class SupportPage
    {
        /// <summary>Opens one channel; when the system cannot, says where to write instead.</summary>
        public static async Task OpenAsync(SupportChannel channel)
        {
            try
            {
                var opened = channel.Address.Scheme == Uri.UriSchemeMailto
                    ? await Launcher.Default.TryOpenAsync(channel.Address)
                    : await Browser.Default.OpenAsync(channel.Address, BrowserLaunchMode.External);
                if (opened) return;
            }
            catch (Exception ex)
            {
                Horus.Domain.Models.Diag.Info("support", $"{channel.Name} did not open: {ex.Message}");
            }

            await Dialog.Alert("Не удалось открыть", $"Напишите нам: {channel.Name} — {channel.Display}");
        }

        /// <summary>
        /// Asks which channel to use and opens it. <paramref name="title"/> says why the user
        /// is being offered help, when there is a reason.
        /// </summary>
        public static async Task ChooseAsync(string? title = null)
        {
            var channels = SupportChannels.Configured;
            if (channels.Count == 0)
            {
                await Dialog.Alert("Поддержка", "Контакты поддержки не настроены в этой сборке.");
                return;
            }

            var picked = await Dialog.ActionSheet(title ?? "Написать в поддержку", "Отмена", null,
                channels.Select(Label).ToArray());
            var channel = channels.FirstOrDefault(c => Label(c) == picked);
            if (channel is not null) await OpenAsync(channel);
        }

        private static string Label(SupportChannel c) => $"{c.Name} · {c.Display}";
    }
}
