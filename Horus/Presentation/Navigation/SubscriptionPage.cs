namespace Horus.Presentation.Navigation
{
    /// <summary>
    /// Subscriptions are bought on the website, not in the app. Every "Оформить / Продлить
    /// подписку" opens the site's tariff page in the browser.
    ///
    /// <para>The site already has the whole flow the app never did: tariffs from
    /// <c>/billing/plans</c>, the auto-payment consent, promo and invitation codes, the bank
    /// redirect and waiting for its confirmation. Doing it twice would mean keeping two
    /// checkouts in step with the offer.</para>
    ///
    /// <para>The browser does not share the app's session, so the first visit asks the user
    /// to sign in on the site; <c>/pay</c> remembers where they were going and returns them
    /// there after sign-in. Back in the app, the account refreshes by itself — returning to
    /// the foreground triggers <see cref="Domain.Interfaces.IAccountSync.OnForeground"/>, and
    /// while the subscription is inactive the poll runs every 20 seconds anyway.</para>
    /// </summary>
    public static class SubscriptionPage
    {
        /// <summary>The site and the API share an origin (nginx routes the API paths).</summary>
        public static Uri Address => new($"{AppConfiguration.ApiBaseUrl.TrimEnd('/')}/pay");

        /// <summary>Opens the tariff page. Says where it is when no browser could be opened.</summary>
        public static async Task OpenAsync()
        {
            try
            {
                if (await Browser.Default.OpenAsync(Address, BrowserLaunchMode.External))
                    return;
            }
            catch (Exception ex)
            {
                Horus.Domain.Models.Diag.Info("pay", $"browser did not open: {ex.Message}");
            }

            await Dialog.Alert("Не удалось открыть браузер",
                $"Оформить или продлить подписку можно на сайте: {Address}");
        }

        /// <summary>
        /// For the moments the user did not ask for a payment page — they tapped "connect"
        /// without a subscription. A question first rather than a browser out of nowhere.
        /// </summary>
        public static async Task OfferAsync()
        {
            var open = await Dialog.Confirm(
                "Нужна подписка",
                "Подписка не активна. Оформить или продлить её можно на сайте — откроем страницу с тарифами.",
                "Открыть сайт", "Не сейчас");
            if (open) await OpenAsync();
        }
    }
}
