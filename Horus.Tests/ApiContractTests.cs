using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Horus.Application;
using Horus.Domain.Models;
using Horus.Protocols;
using Horus.Tests.TestSupport;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// The app's own API client (<see cref="ApiService"/>, linked from the app) against a
/// running HorusAPI. Fixtures drift; a live server does not lie about its contract.
///
/// <para>Skipped unless <c>HORUS_API_URL</c> is set. To run against a local API:
/// <c>HORUS_API_URL=http://127.0.0.1:5102</c>, plus for the signed-in half
/// <c>HORUS_API_USER</c>/<c>HORUS_API_PASSWORD</c> — a confirmed account with an active
/// subscription, bound to a node whose profile offers outbounds — and optionally
/// <c>HORUS_API_EXPIRED_USER</c>/<c>HORUS_API_EXPIRED_PASSWORD</c>.</para>
///
/// <para>Each run registers one throwaway account, which the API deletes by itself after
/// seven days unconfirmed.</para>
/// </summary>
public class ApiContractTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("HORUS_API_URL");

    private static ApiService Client(out MemoryStorage storage)
    {
        AppConfiguration.ApiBaseUrl = Url!;
        storage = new MemoryStorage();
        return new ApiService(storage);
    }

    private static void AssertRussian(string? message)
    {
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Matches("[А-Яа-яЁё]", message!);
        Assert.DoesNotMatch("[A-Za-z]{4,}", message!.Replace("e-mail", ""));
    }

    [LiveApiFact]
    public async Task Registration_hands_out_a_ticket_and_an_unconfirmed_login_continues_with_it()
    {
        var api = Client(out _);
        var name = "app" + Guid.NewGuid().ToString("N")[..10];
        var email = name + "@example.com";

        var registered = await api.RegisterAsync(name, email, "password123");
        Assert.True(registered.Success, registered.Message);
        Assert.Equal(email, registered.Email);
        Assert.True(registered.CodeExpiresInSeconds > 0);
        Assert.False(string.IsNullOrEmpty(registered.PendingToken));

        // Signing in by USERNAME before confirming: not a dead end but a ticket and a mask.
        var login = await api.LoginAsync(name, "password123");
        Assert.False(login.Success);
        Assert.Equal(403, login.StatusCode);
        Assert.Equal(ErrorCodes.EmailUnverified, login.ErrorCode);
        Assert.NotNull(login.Pending);
        Assert.False(string.IsNullOrEmpty(login.Pending!.PendingToken));
        Assert.Contains("*", login.Pending.EmailMasked);
        Assert.True(login.Pending.CodeExpiresInSeconds > 0);
        AssertRussian(login.Message);

        // The ticket identifies the account — the address field is what the user typed.
        var wrong = await api.VerifyEmailAsync(name, "000000", login.Pending.PendingToken);
        Assert.False(wrong.Success);
        Assert.Equal(ErrorCodes.InvalidCode, wrong.ErrorCode);
        Assert.Matches(new Regex("Осталось попыток: [1-4] из 5"), wrong.Message);

        // A code went out at registration seconds ago: the cooldown answers, in Russian.
        var resend = await api.ResendCodeAsync(name, login.Pending.PendingToken);
        Assert.False(resend.Success);
        Assert.Equal(ErrorCodes.ResendTooSoon, resend.ErrorCode);
        AssertRussian(resend.Message);

        var again = await api.RegisterAsync(name, "other." + email, "password123");
        Assert.False(again.Success);
        Assert.Equal(ErrorCodes.UsernameTaken, again.ErrorCode);
        AssertRussian(again.Message);
    }

    /// <summary><see cref="AccountRules"/> is a copy of the API's rules; this is what keeps it one.</summary>
    [LiveApiFact]
    public async Task The_api_refuses_what_the_app_refuses_before_sending()
    {
        // Straight HTTP, one forwarded address per request: /auth/register allows three a
        // minute per IP, and this is four on top of the registration test's two. The API
        // trusts X-Forwarded-For from loopback, so it only isolates against a local server.
        using var http = new HttpClient { BaseAddress = new Uri(Url!) };
        var tail = Guid.NewGuid().ToString("N")[..8];

        foreach (var (username, password) in new[] { ("Иван" + tail, "password123"), ("i-" + tail, "password123"),
                                                      ("ok" + tail, "short"), ("ok" + tail, new string('x', 129)) })
        {
            Assert.NotNull(AccountRules.RegistrationProblem(username, password));

            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/register")
            {
                Content = JsonContent.Create(new { username, email = $"x{tail}@example.com", password })
            };
            request.Headers.Add("X-Forwarded-For", $"10.{Random.Shared.Next(256)}.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [LiveApiFact]
    public async Task A_wrong_password_is_explained_in_russian()
    {
        var api = Client(out _);
        var login = await api.LoginAsync("nobody" + Guid.NewGuid().ToString("N")[..8], "wrong-password");
        Assert.False(login.Success);
        Assert.Equal(401, login.StatusCode);
        AssertRussian(login.Message);
    }

    [LiveApiFact(RequiresUser = true)]
    public async Task A_subscriber_signs_in_picks_a_node_and_gets_a_config_the_core_accepts()
    {
        var api = Client(out var storage);

        var login = await api.LoginAsync(Env("HORUS_API_USER"), Env("HORUS_API_PASSWORD"));
        Assert.True(login.Success, login.Message);
        Assert.False(string.IsNullOrEmpty(storage.Session()));

        var me = await api.GetWhoAmIAsync();
        Assert.NotNull(me);
        Assert.Equal(Env("HORUS_API_USER"), me!.Username);
        Assert.True(me.SubscriptionExpiresAt > DateTime.UtcNow);
        Assert.False(string.IsNullOrEmpty(me.Ip));

        var servers = await api.GetServersAsync();
        Assert.NotNull(servers);
        Assert.NotEmpty(servers!);
        Assert.All(servers!, s =>
        {
            Assert.True(s.Id > 0);
            Assert.False(string.IsNullOrEmpty(s.Host));
            Assert.True(s.MaxReservations > 0, "max_reservations must reach the app: it is the hard cap");
            Assert.True(s.HasCapacity);
        });

        var bound = await api.SelectServerAsync();
        Assert.True(bound.Id > 0);
        Assert.False(string.IsNullOrEmpty(bound.Host));

        var connection = await api.GetServerConnectionAsync();
        Assert.Equal(bound.Id, connection.Server?.Id);
        Assert.True(connection.HasAny);

        // The node's outbound, rendered by the app's own builder, must give a config with it.
        var candidate = connection.Candidates()[0];
        var json = XrayConfigBuilder.Build(new XrayConfig
        {
            Outbound = candidate.Outbound,
            Offer = candidate.Id,
            Label = candidate.Label,
            ProtocolName = candidate.ProtocolName,
            NodeAddress = "127.0.0.1"
        });
        using var doc = JsonDocument.Parse(json);
        var proxy = doc.RootElement.GetProperty("outbounds").EnumerateArray()
            .Single(o => o.GetProperty("tag").GetString() == "proxy");
        Assert.Equal(candidate.ProtocolName, proxy.GetProperty("protocol").GetString());

        Assert.True(await api.LogoutOtherDevicesAsync());
    }

    [LiveApiFact(RequiresExpiredUser = true)]
    public async Task A_lapsed_subscription_still_signs_in_and_is_refused_a_node_in_russian()
    {
        var api = Client(out _);

        // A session has no expiry of its own: a lapsed subscriber must be able to sign in
        // (to renew on the site), and only connecting is refused.
        var login = await api.LoginAsync(Env("HORUS_API_EXPIRED_USER"), Env("HORUS_API_EXPIRED_PASSWORD"));
        Assert.True(login.Success, login.Message);

        var select = await Assert.ThrowsAsync<SubscriptionExpiredException>(() => api.SelectServerAsync());
        AssertRussian(select.Message);
        var connect = await Assert.ThrowsAsync<SubscriptionExpiredException>(() => api.GetServerConnectionAsync());
        AssertRussian(connect.Message);
    }

    private static string Env(string name) => Environment.GetEnvironmentVariable(name)!;
}

/// <summary>A fact that runs only when the live-API environment it needs is configured.</summary>
public sealed class LiveApiFactAttribute : FactAttribute
{
    public bool RequiresUser { get; set; }
    public bool RequiresExpiredUser { get; set; }

    public override string? Skip
    {
        get
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HORUS_API_URL")))
                return "HORUS_API_URL is not set — live API contract not checked";
            if (RequiresUser && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HORUS_API_USER")))
                return "HORUS_API_USER is not set";
            if (RequiresExpiredUser && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HORUS_API_EXPIRED_USER")))
                return "HORUS_API_EXPIRED_USER is not set";
            return base.Skip;
        }
        set => base.Skip = value;
    }
}
