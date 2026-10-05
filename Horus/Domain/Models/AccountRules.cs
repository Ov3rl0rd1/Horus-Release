using System.Text.RegularExpressions;

namespace Horus.Domain.Models
{
    /// <summary>
    /// The sign-up rules HorusAPI enforces (<c>AuthEndpoints.UsernameRegex</c>,
    /// <c>MinPasswordLength</c>/<c>MaxPasswordLength</c>), checked before the request goes out.
    ///
    /// <para>The API refuses a bad username or password with a bare 400 and no <c>code</c>,
    /// so the app had nothing to explain it with: "Иван" as a username came back as "Не удалось
    /// создать аккаунт." The rules are copied here rather than discovered — keep them in step
    /// with the API.</para>
    /// </summary>
    public static class AccountRules
    {
        public const int MinPasswordLength = 8;
        public const int MaxPasswordLength = 128;

        private static readonly Regex Username = new("^[a-zA-Z0-9_]{3,32}$");

        /// <summary>Why the API would refuse this sign-up, in Russian; null when it would not.</summary>
        public static string? RegistrationProblem(string username, string password)
        {
            if (!Username.IsMatch(username ?? string.Empty))
                return "Имя пользователя — от 3 до 32 символов: латинские буквы, цифры и «_».";
            if ((password ?? string.Empty).Length is < MinPasswordLength or > MaxPasswordLength)
                return $"Пароль — от {MinPasswordLength} до {MaxPasswordLength} символов.";
            return null;
        }
    }
}
