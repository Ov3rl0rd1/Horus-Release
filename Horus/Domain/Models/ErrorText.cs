namespace Horus.Domain.Models
{
    /// <summary>The <c>code</c> values of HorusAPI's error envelope that the app branches on.</summary>
    public static class ErrorCodes
    {
        public const string EmailUnverified = "email_unverified";
        public const string InvalidCode = "invalid_code";
        public const string CodeExpired = "code_expired";
        public const string TooManyAttempts = "too_many_attempts";
        public const string AlreadyVerified = "already_verified";
        public const string InvalidTicket = "invalid_ticket";
        public const string ResendTooSoon = "resend_too_soon";
        public const string EmailRateLimited = "email_rate_limited";
        public const string RateLimited = "rate_limited";
        public const string UsernameTaken = "username_taken";
        public const string EmailTaken = "email_taken";
        public const string InvalidToken = "invalid_token";
        public const string NoCapacity = "no_capacity";
        public const string SubscriptionExpired = "subscription_expired";
        public const string ServerNotFound = "server_not_found";
    }

    /// <summary>
    /// Russian text for every refusal HorusAPI names by <c>code</c>.
    ///
    /// <para>The API's own <c>message</c> is English and written for developers ("Username
    /// already taken."), and the app used to show it verbatim whenever one came back — so a
    /// Russian-speaking user got English exactly at the moments things went wrong. The code
    /// is the contract; the wording lives here, next to the rest of the UI's language.</para>
    ///
    /// <para>Pure on purpose, so the mapping is pinned by tests.</para>
    /// </summary>
    public static class ErrorText
    {
        /// <summary>
        /// Text for <paramref name="code"/>; for an unnamed 429, a generic "too many" with
        /// the wait. Null when there is nothing specific to say — the caller's own fallback
        /// then applies.
        /// </summary>
        public static string? Explain(string? code, int status = 0, int? retryAfterSeconds = null) => code switch
        {
            ErrorCodes.InvalidCode => "Код не подошёл. Сверьте цифры с последним письмом.",
            ErrorCodes.CodeExpired => "Срок кода истёк. Запросите новый.",
            ErrorCodes.TooManyAttempts => "Код заблокирован после пяти ошибок. Запросите новый.",
            ErrorCodes.AlreadyVerified => "Адрес уже подтверждён. Войдите с паролем.",
            ErrorCodes.InvalidTicket => "Время на подтверждение вышло. Войдите ещё раз — код придёт заново.",
            ErrorCodes.ResendTooSoon => "Письмо уже в пути. Новый код можно запросить " + In(retryAfterSeconds ?? 60) + ".",
            ErrorCodes.EmailRateLimited =>
                "Больше писем на этот адрес в этот час не отправим. Код из последнего письма ещё действует.",
            ErrorCodes.UsernameTaken => "Это имя пользователя уже занято. Придумайте другое.",
            ErrorCodes.EmailTaken => "На этот e-mail уже есть аккаунт. Войдите или восстановите пароль.",
            ErrorCodes.InvalidToken => "Ссылка для сброса пароля недействительна или устарела.",
            ErrorCodes.NoCapacity => "Свободных мест на серверах сейчас нет. Попробуйте позже или выберите другую страну.",
            ErrorCodes.SubscriptionExpired => "Подписка закончилась. Продлите её на сайте.",
            ErrorCodes.ServerNotFound => "Этого сервера больше нет. Выберите другой.",
            ErrorCodes.RateLimited => "Слишком много попыток. Попробуйте " + In(retryAfterSeconds) + ".",
            _ when status == 429 => "Слишком много попыток. Попробуйте " + In(retryAfterSeconds) + ".",
            _ => null
        };

        /// <summary>"через 40 с", "через 2 мин", "через час", or "немного позже" when unknown.</summary>
        public static string In(int? seconds)
        {
            if (seconds is not int s || s <= 0) return "немного позже";
            if (s < 60) return $"через {s} с";
            if (s >= 3000 && s <= 3600) return "через час";
            return $"через {(int)Math.Ceiling(s / 60.0)} мин";
        }
    }
}
