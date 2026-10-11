namespace Horus.Application
{
    /// <summary>One way to reach support: its name, what to show, and what to open.</summary>
    public sealed record SupportChannel(string Name, string Display, Uri Address);

    /// <summary>
    /// The support channels from <c>appsettings.json</c>, turned into addresses the system can
    /// open. Pure, so the forms people actually write there are tested.
    ///
    /// <para>The settings were written as handles (<c>@horusping</c>) and are now written as
    /// links (<c>https://t.me/horusping</c>); both work, and an entry that is neither is left
    /// out rather than shown as a button that opens nothing.</para>
    /// </summary>
    public static class SupportChannels
    {
        public const string Telegram = "Telegram";
        public const string Vk = "ВКонтакте";
        public const string Email = "Почта";

        /// <summary>The configured channels, in the order they are offered.</summary>
        public static IReadOnlyList<SupportChannel> From(string? telegram, string? vk, string? email)
        {
            var list = new List<SupportChannel>();
            if (Web(telegram, "t.me") is { } tg) list.Add(new(Telegram, Handle(tg), tg));
            if (Web(vk, "vk.com") is { } v) list.Add(new(Vk, v.Host + v.AbsolutePath.TrimEnd('/'), v));
            if (Mail(email) is { } mail) list.Add(new(Email, mail.ToString()["mailto:".Length..], mail));
            return list;
        }

        /// <summary>The channels configured for this build.</summary>
        public static IReadOnlyList<SupportChannel> Configured =>
            From(AppConfiguration.SupportTelegram, AppConfiguration.SupportVk, AppConfiguration.SupportEmail);

        private static Uri? Web(string? value, string handleHost)
        {
            var text = value?.Trim();
            if (string.IsNullOrEmpty(text)) return null;
            if (text.StartsWith('@')) text = $"https://{handleHost}/{text[1..]}";
            else if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
            return Uri.TryCreate(text, UriKind.Absolute, out var uri)
                   && uri.Scheme is "https" or "http"
                   && uri.AbsolutePath.Trim('/').Length > 0
                ? uri
                : null;
        }

        private static Uri? Mail(string? value)
        {
            var text = value?.Trim();
            if (string.IsNullOrEmpty(text)) return null;
            if (text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) text = text[7..];
            var at = text.IndexOf('@');
            return at > 0 && at < text.Length - 1 && !text.Contains(' ')
                   && Uri.TryCreate("mailto:" + text, UriKind.Absolute, out var uri) ? uri : null;
        }

        /// <summary>"https://t.me/horusping" is shown as "@horusping", the way people name it.</summary>
        private static string Handle(Uri telegram) => "@" + telegram.AbsolutePath.Trim('/');
    }
}
