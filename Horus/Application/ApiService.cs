using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horus.Application
{
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

        public ApiService(
            IStorageService storage)
        {
            _storage = storage;
            _baseUrl = AppConfiguration.ApiBaseUrl.TrimEnd('/');

            _httpAuthHandler = new HttpAuthHandler(storage
#if DEBUG 
                , verifyServerCertificate: false
#endif
                );

            _httpClient = new HttpClient(_httpAuthHandler);

            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        // ── Auth ─────────────────────────────────────────────────────────────

        public async Task<AuthResult> LoginAsync(string username, string password, string? session = null)
        {
            try
            {
                var body = new { username, password };
                using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync($"{_baseUrl}/auth/login", content);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return new AuthResult { Success = false, Message = "Invalid credentials." };

                response.EnsureSuccessStatusCode();

                var result = await DeserializeAsync<LoginResponse>(response)
                    ?? throw new InvalidOperationException("Empty login response.");

                await _httpAuthHandler.UpdateSession(result.session);

                return new AuthResult
                {
                    Success = true,
                    Response = result,
                    Message = "OK"
                };
            }
            catch (HttpRequestException ex)
            {
                return new AuthResult { Success = false, Message = $"Network error: {ex.Message}" };
            }
            catch (Exception ex)
            {
                return new AuthResult { Success = false, Message = ex.Message };
            }
        }

        public async Task<AuthResult> RegisterAsync(string username, string email, string password)
        {
            try
            {
                var body = new { username, email, password };
                using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync($"{_baseUrl}/auth/register", content);

                if (response.StatusCode == HttpStatusCode.Conflict)
                    return new AuthResult { Success = false, Message = "Username or email already taken." };

                if (response.StatusCode == HttpStatusCode.UnprocessableEntity ||
                    response.StatusCode == HttpStatusCode.BadRequest)
                {
                    var errText = await response.Content.ReadAsStringAsync();
                    return new AuthResult { Success = false, Message = errText };
                }

                response.EnsureSuccessStatusCode();

                var result = await DeserializeAsync<LoginResponse>(response)
                    ?? throw new InvalidOperationException("Empty register response.");

                await _httpAuthHandler.UpdateSession(result.session);

                return new AuthResult
                {
                    Success = true,
                    Response = result,
                    Message = "Registration successful"
                };
            }
            catch (HttpRequestException ex)
            {
                return new AuthResult { Success = false, Message = $"Network error: {ex.Message}" };
            }
            catch (Exception ex)
            {
                return new AuthResult { Success = false, Message = ex.Message };
            }
        }

        // Servers

        public async Task<IReadOnlyList<ServerInfo>?> GetServersAsync()
        {
            if(_httpAuthHandler.HasSession() == false)
                throw new InvalidOperationException("No valid session. Please log in first.");

            try
            {
                using var response = await _httpClient.GetAsync($"{_baseUrl}/servers/");
                response.EnsureSuccessStatusCode();
                var list = await DeserializeAsync<List<ServerInfo>>(response);
                return list?.AsReadOnly() ?? (IReadOnlyList<ServerInfo>)Array.Empty<ServerInfo>();
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        public async Task<string?> GetServerConfigAsync(int serverId)
        {
            if (_httpAuthHandler.HasSession() == false)
                throw new InvalidOperationException("No valid session. Please log in first.");

            try
            {
                using var response = await _httpClient.GetAsync($"{_baseUrl}/servers/{serverId}/connect");
                response.EnsureSuccessStatusCode();
                var dto = await DeserializeAsync<ConnectResponseDto>(response)
                    ?? throw new InvalidOperationException("Invalid connect response.");
                return dto.Config;
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        // ── Geo data ─────────────────────────────────────────────────────────

        public async Task<Stream> DownloadGeoDataAsync(CancellationToken ct = default)
        {
            var url = $"{_baseUrl}/geo/country.mmdb";
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(ct);
        }

        public async Task<GeoDataVersion> GetGeoDataVersionAsync()
        {
            try
            {
                using var response = await _httpClient.GetAsync($"{_baseUrl}/geo/version");
                response.EnsureSuccessStatusCode();
                return await DeserializeAsync<GeoDataVersion>(response) ?? new GeoDataVersion();
            }
            catch
            {
                return new GeoDataVersion { Version = "unknown", UpdatedAt = DateTime.MinValue };
            }
        }

        // ── Routing rules ─────────────────────────────────────────────────────

        public async Task<RoutingRulesFile?> GetRoutingRulesAsync(CancellationToken ct = default)
        {
            if (_httpAuthHandler.HasSession() == false)
                throw new InvalidOperationException("No valid session. Please log in first.");

            try
            {
                using var response = await _httpClient.GetAsync($"{_baseUrl}/routing-rules", ct);
                response.EnsureSuccessStatusCode();
                return await DeserializeAsync<RoutingRulesFile>(response) ?? new RoutingRulesFile();
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        // Error reporting

        public async Task<bool> SendErrorReportAsync(ErrorReport report, CancellationToken ct = default)
        {
            try
            {
                using var content = new StringContent(
                    JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync($"{_baseUrl}/logs/error", content, ct);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        // Helpers

        private static async Task<T?> DeserializeAsync<T>(HttpResponseMessage response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
        }

        private record ConnectResponseDto([property: JsonPropertyName("config")] string Config);
    }
}
