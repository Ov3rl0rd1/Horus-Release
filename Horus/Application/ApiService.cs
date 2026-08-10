using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Horus.Application
{
    /// <summary>
    /// HorusAPI v1 client. Every authenticated call relies on
    /// <see cref="HttpAuthHandler"/> to attach the <c>X-Session-Key</c> header.
    /// </summary>
    public class ApiService : IApiService
    {
        private readonly HttpClient _httpClient;
        private readonly HttpAuthHandler _httpAuthHandler;
        private readonly string _baseUrl;
        private readonly IStorageService _storage;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public event EventHandler? SessionExpired;

        event EventHandler IApiService.SessionExpired
        {
            add => SessionExpired += value;
            remove => SessionExpired -= value;
        }

        public ApiService(IStorageService storage)
        {
            _storage = storage;
            _baseUrl = AppConfiguration.ApiBaseUrl.TrimEnd('/');

            _httpAuthHandler = new HttpAuthHandler(
                storage,
                verifyServerCertificate: !AppConfiguration.AllowInsecureTls);

            _httpAuthHandler.Unauthorized += (_, _) => SessionExpired?.Invoke(this, EventArgs.Empty);

            _httpClient = new HttpClient(_httpAuthHandler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        // ── Auth ─────────────────────────────────────────────────────────────

        public async Task<AuthResult> LoginAsync(string username, string password, CancellationToken ct = default)
        {
            try
            {
                using var response = await PostAsync(ApiConsts.AUTH_LOGIN, new { username, password }, ct);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return AuthResult.Fail("Неверный логин или пароль.", status: 401);

                if (!response.IsSuccessStatusCode)
                    return AuthResult.Fail(await ReadErrorAsync(response, "Не удалось войти."),
                        await ReadErrorCodeAsync(response), (int)response.StatusCode);

                var result = await DeserializeAsync<LoginResponse>(response, ct);
                if (result is null || string.IsNullOrEmpty(result.session))
                    return AuthResult.Fail("Сервер вернул пустой ответ.");

                await _httpAuthHandler.UpdateSession(result.session);

                return new AuthResult { Success = true, Response = result, Message = "OK" };
            }
            catch (Exception ex) { return AuthResult.Fail(DescribeException(ex)); }
        }

        public async Task<RegisterResult> RegisterAsync(
            string username, string email, string password, CancellationToken ct = default)
        {
            try
            {
                using var response = await PostAsync(ApiConsts.AUTH_REGISTER, new { username, password, email }, ct);

                if (response.StatusCode == HttpStatusCode.Conflict)
                    return RegisterResult.Fail(
                        await ReadErrorAsync(response, "Такой логин или email уже занят."),
                        await ReadErrorCodeAsync(response), 409);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    return RegisterResult.Fail(
                        await ReadErrorAsync(response, "Слишком много попыток. Попробуйте позже."),
                        await ReadErrorCodeAsync(response), 429);

                if (!response.IsSuccessStatusCode)
                    return RegisterResult.Fail(
                        await ReadErrorAsync(response, "Не удалось создать аккаунт."),
                        await ReadErrorCodeAsync(response), (int)response.StatusCode);

                var body = await DeserializeAsync<RegisterResponse>(response, ct);
                return new RegisterResult
                {
                    Success = true,
                    Email = body?.Email ?? email,
                    CodeExpiresInSeconds = body?.CodeExpiresInSeconds ?? 0,
                    Message = body?.Status
                };
            }
            catch (Exception ex) { return RegisterResult.Fail(DescribeException(ex)); }
        }

        public async Task<AuthResult> VerifyEmailAsync(string email, string code, CancellationToken ct = default)
        {
            try
            {
                using var response = await PostAsync(ApiConsts.AUTH_VERIFY, new { email, code }, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var message = response.StatusCode switch
                    {
                        HttpStatusCode.BadRequest => "Неверный или просроченный код.",
                        HttpStatusCode.Conflict => "Этот email уже подтверждён.",
                        HttpStatusCode.TooManyRequests => "Слишком много попыток. Попробуйте позже.",
                        _ => "Не удалось подтвердить email."
                    };
                    return AuthResult.Fail(await ReadErrorAsync(response, message),
                        await ReadErrorCodeAsync(response), (int)response.StatusCode);
                }

                var result = await DeserializeAsync<LoginResponse>(response, ct);
                if (result is null || string.IsNullOrEmpty(result.session))
                    return AuthResult.Fail("Сервер вернул пустой ответ.");

                await _httpAuthHandler.UpdateSession(result.session);

                return new AuthResult { Success = true, Response = result, Message = "OK" };
            }
            catch (Exception ex) { return AuthResult.Fail(DescribeException(ex)); }
        }

        public async Task<RegisterResult> ResendCodeAsync(string email, CancellationToken ct = default)
        {
            try
            {
                using var response = await PostAsync(ApiConsts.AUTH_RESEND_CODE, new { email }, ct);

                if (!response.IsSuccessStatusCode)
                    return RegisterResult.Fail(
                        await ReadErrorAsync(response, response.StatusCode == HttpStatusCode.TooManyRequests
                            ? "Код уже отправлен. Подождите немного."
                            : "Не удалось отправить код."),
                        await ReadErrorCodeAsync(response), (int)response.StatusCode);

                var body = await DeserializeAsync<RegisterResponse>(response, ct);
                return new RegisterResult
                {
                    Success = true,
                    Email = body?.Email ?? email,
                    CodeExpiresInSeconds = body?.CodeExpiresInSeconds ?? 0,
                    Message = body?.Status
                };
            }
            catch (Exception ex) { return RegisterResult.Fail(DescribeException(ex)); }
        }

        // ── Password reset ────────────────────────────────────────────────────

        public async Task<RegisterResult> RequestPasswordResetAsync(string email, CancellationToken ct = default)
        {
            try
            {
                using var response = await PostAsync(ApiConsts.AUTH_RESET_REQUEST, new { email }, ct);

                // The endpoint answers 202 whether or not the address exists, so a
                // failure here is a transport/rate-limit problem, never "unknown email".
                if (!response.IsSuccessStatusCode)
                    return RegisterResult.Fail(
                        await ReadErrorAsync(response, response.StatusCode == HttpStatusCode.TooManyRequests
                            ? "Письмо уже отправлено. Подождите немного."
                            : "Не удалось отправить письмо."),
                        await ReadErrorCodeAsync(response), (int)response.StatusCode);

                return new RegisterResult { Success = true, Email = email };
            }
            catch (Exception ex) { return RegisterResult.Fail(DescribeException(ex)); }
        }

        public async Task<bool> IsResetTokenValidAsync(string token, CancellationToken ct = default)
        {
            try
            {
                using var response = await _httpClient.GetAsync(
                    $"{_baseUrl}{ApiConsts.AUTH_RESET_CHECK}?token={Uri.EscapeDataString(token)}", ct);
                if (!response.IsSuccessStatusCode) return false;

                var body = await DeserializeAsync<StatusResponse>(response, ct);
                return body is not null &&
                       !body.Status.Contains("invalid", StringComparison.OrdinalIgnoreCase) &&
                       !body.Status.Contains("expired", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public async Task<RegisterResult> ConfirmPasswordResetAsync(
            string token, string password, CancellationToken ct = default)
        {
            try
            {
                using var response = await PostAsync(ApiConsts.AUTH_RESET_CONFIRM, new { token, password }, ct);

                if (!response.IsSuccessStatusCode)
                    return RegisterResult.Fail(
                        await ReadErrorAsync(response, "Ссылка недействительна или устарела."),
                        await ReadErrorCodeAsync(response), (int)response.StatusCode);

                return new RegisterResult { Success = true };
            }
            catch (Exception ex) { return RegisterResult.Fail(DescribeException(ex)); }
        }

        public async Task<bool> LogoutOtherDevicesAsync(CancellationToken ct = default)
        {
            if (!_httpAuthHandler.HasSession()) return false;
            try
            {
                // The session to keep is taken from the X-Session-Key header, so the
                // body carries nothing.
                using var response = await PostAsync(ApiConsts.AUTH_LOGOUT_OTHERS, new { }, ct);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // ── Servers ──────────────────────────────────────────────────────────

        public async Task<IReadOnlyList<ServerInfo>?> GetServersAsync(CancellationToken ct = default)
        {
            RequireSession();
            try
            {
                using var response = await _httpClient.GetAsync($"{_baseUrl}{ApiConsts.SERVERS_BEST}", ct);
                if (!response.IsSuccessStatusCode) return null;

                var list = await DeserializeAsync<List<ServerInfo>>(response, ct);
                return list?.AsReadOnly() ?? (IReadOnlyList<ServerInfo>)Array.Empty<ServerInfo>();
            }
            catch (HttpRequestException) { return null; }
            catch (TaskCanceledException) { return null; }
        }

        public async Task<ServerConnection> GetServerConnectionAsync(CancellationToken ct = default)
        {
            RequireSession();

            using var response = await _httpClient.GetAsync($"{_baseUrl}{ApiConsts.SERVERS_CONNECT}", ct);

            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new SubscriptionExpiredException(
                    await ReadErrorAsync(response, "Подписка истекла."));

            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException(
                    await ReadErrorAsync(response, "Нет доступных серверов."));

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new UnauthorizedAccessException("Сессия недействительна. Войдите заново.");

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    await ReadErrorAsync(response, "Не удалось получить конфигурацию сервера."));

            // Body is a flat { key: link } map — keys are server-side constants, so the
            // links are matched by URI scheme rather than by name.
            var map = await DeserializeAsync<Dictionary<string, string?>>(response, ct)
                      ?? throw new InvalidOperationException("Пустой ответ сервера подключения.");

            var connection = ServerConnection.Parse(map);
            if (!connection.HasAny)
                throw new InvalidOperationException(
                    "Сервер не вернул ни одной ссылки подключения (vless:// / hysteria2://).");

            return connection;
        }

        // ── Account ──────────────────────────────────────────────────────────

        public async Task<WhoAmIResponse?> GetWhoAmIAsync(CancellationToken ct = default)
        {
            try
            {
                using var response = await _httpClient.GetAsync($"{_baseUrl}{ApiConsts.WHOAMI}", ct);
                if (!response.IsSuccessStatusCode) return null;
                return await DeserializeAsync<WhoAmIResponse>(response, ct);
            }
            catch { return null; }
        }

        public async Task<string?> GetEgressIpAsync(string? socksProxy = null, CancellationToken ct = default)
        {
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    Proxy = socksProxy is null ? null : new WebProxy(socksProxy),
                    UseProxy = socksProxy is not null,
                    ConnectTimeout = TimeSpan.FromSeconds(6)
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

                // /whoami rather than a plain echo: it already exists and reports the
                // address the API observes, which is exactly the comparison we need.
                using var request = new HttpRequestMessage(
                    HttpMethod.Get, $"{_baseUrl}{ApiConsts.WHOAMI}");

                var session = _storage.Session();
                if (!string.IsNullOrWhiteSpace(session))
                    request.Headers.Add(ApiConsts.SESSION_HEADER, session);

                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return null;

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var body = await JsonSerializer.DeserializeAsync<WhoAmIResponse>(stream, JsonOptions, ct);

                return string.IsNullOrWhiteSpace(body?.Ip) ? null : body.Ip;
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
        {
            try
            {
                using var response = await _httpClient.GetAsync($"{_baseUrl}{ApiConsts.HEALTH}", ct);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private void RequireSession()
        {
            if (!_httpAuthHandler.HasSession())
                throw new UnauthorizedAccessException("Нет активной сессии. Войдите в аккаунт.");
        }

        private Task<HttpResponseMessage> PostAsync(string route, object body, CancellationToken ct)
        {
            var content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            return _httpClient.PostAsync($"{_baseUrl}{route}", content, ct);
        }

        private static async Task<T?> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken ct)
        {
            if (response.Content.Headers.ContentLength == 0) return default;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
        }

        /// <summary>Pulls <c>message</c> out of the API error envelope, falling back to <paramref name="fallback"/>.</summary>
        private static async Task<string> ReadErrorAsync(HttpResponseMessage response, string fallback)
        {
            var error = await TryReadErrorEnvelopeAsync(response);
            return string.IsNullOrWhiteSpace(error?.Message) ? fallback : error!.Message;
        }

        private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response) =>
            (await TryReadErrorEnvelopeAsync(response))?.Code;

        private static async Task<ApiError?> TryReadErrorEnvelopeAsync(HttpResponseMessage response)
        {
            try
            {
                // Buffered by HttpClient, so reading twice (message + code) is safe.
                var text = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(text)) return null;
                return JsonSerializer.Deserialize<ApiError>(text, JsonOptions);
            }
            catch { return null; }
        }

        private static string DescribeException(Exception ex) => ex switch
        {
            HttpRequestException => "Нет связи с сервером. Проверьте подключение к интернету.",
            TaskCanceledException => "Сервер не отвечает. Попробуйте ещё раз.",
            _ => ex.Message
        };
    }
}
