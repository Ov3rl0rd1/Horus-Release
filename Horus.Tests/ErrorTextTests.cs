using Horus.Domain.Models;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// HorusAPI explains refusals in English, for developers. The user must only ever see
/// Russian, so every code the API sends to the app has a sentence here.
/// </summary>
public class ErrorTextTests
{
    // Every code /auth, /servers and /whoami can answer the app with (HorusAPI dev, 05.10.2026).
    [Theory]
    [InlineData(ErrorCodes.InvalidCode)]
    [InlineData(ErrorCodes.CodeExpired)]
    [InlineData(ErrorCodes.TooManyAttempts)]
    [InlineData(ErrorCodes.AlreadyVerified)]
    [InlineData(ErrorCodes.InvalidTicket)]
    [InlineData(ErrorCodes.ResendTooSoon)]
    [InlineData(ErrorCodes.EmailRateLimited)]
    [InlineData(ErrorCodes.RateLimited)]
    [InlineData(ErrorCodes.UsernameTaken)]
    [InlineData(ErrorCodes.EmailTaken)]
    [InlineData(ErrorCodes.InvalidToken)]
    [InlineData(ErrorCodes.NoCapacity)]
    [InlineData(ErrorCodes.SubscriptionExpired)]
    [InlineData(ErrorCodes.ServerNotFound)]
    public void Every_code_the_api_sends_has_russian_text(string code)
    {
        var text = ErrorText.Explain(code);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Matches("[А-Яа-яЁё]", text!);
        Assert.DoesNotMatch("[A-Za-z]{4,}", text!.Replace("e-mail", ""));
    }

    [Fact]
    public void An_unnamed_429_still_says_how_long_to_wait()
    {
        Assert.Equal("Слишком много попыток. Попробуйте через 2 мин.", ErrorText.Explain(null, 429, 90));
        Assert.Equal("Слишком много попыток. Попробуйте немного позже.", ErrorText.Explain(null, 429));
    }

    [Fact]
    public void Resend_cooldown_counts_the_seconds_the_api_named()
    {
        Assert.Contains("через 38 с", ErrorText.Explain(ErrorCodes.ResendTooSoon, 429, 38));
    }

    [Fact]
    public void Unknown_codes_fall_back_to_the_callers_own_text() =>
        Assert.Null(ErrorText.Explain("something_new", 400));

    [Theory]
    [InlineData(null, "немного позже")]
    [InlineData(0, "немного позже")]
    [InlineData(40, "через 40 с")]
    [InlineData(61, "через 2 мин")]
    [InlineData(3600, "через час")]
    public void Waits_read_naturally(int? seconds, string expected) =>
        Assert.Equal(expected, ErrorText.In(seconds));
}
