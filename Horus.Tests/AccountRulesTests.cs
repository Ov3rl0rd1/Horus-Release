using Horus.Domain.Models;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// The sign-up rules mirrored from HorusAPI. The API refuses these with a bare 400 and no code,
/// so this is the only place the user learns what was wrong.
/// </summary>
public class AccountRulesTests
{
    [Theory]
    [InlineData("ivan")]
    [InlineData("Ivan_2026")]
    [InlineData("abc")]
    [InlineData("a234567890123456789012345678901b")]   // 32
    public void A_username_the_api_accepts_passes(string username) =>
        Assert.Null(AccountRules.RegistrationProblem(username, "password123"));

    [Theory]
    [InlineData("Иван")]
    [InlineData("ab")]
    [InlineData("ivan.petrov")]
    [InlineData("ivan-petrov")]
    [InlineData("ivan petrov")]
    [InlineData("a234567890123456789012345678901bc")]  // 33
    [InlineData("")]
    public void A_username_the_api_refuses_is_explained(string username)
    {
        var problem = AccountRules.RegistrationProblem(username, "password123");
        Assert.NotNull(problem);
        Assert.Contains("латинские", problem);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(129)]
    public void A_password_of_the_wrong_length_is_explained(int length)
    {
        var problem = AccountRules.RegistrationProblem("ivan", new string('x', length));
        Assert.NotNull(problem);
        Assert.Contains("Пароль", problem);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(128)]
    public void A_password_at_the_limits_passes(int length) =>
        Assert.Null(AccountRules.RegistrationProblem("ivan", new string('x', length)));
}
