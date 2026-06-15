using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horus.Application
{
    public class ApiService : IApiService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly IStorageService _storage;
        private readonly ILocalModeService _localMode;
        private readonly ILocalConfigService _localConfig;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public ApiService(
            IStorageService storage,
            ILocalModeService localMode,
            ILocalConfigService localConfig)
        {
            _storage = storage;
            _localMode = localMode;
            _localConfig = localConfig;
            _baseUrl = AppConfiguration.ApiBaseUrl.TrimEnd('/');

#if DEBUG
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            _httpClient = new HttpClient(handler);
#else
            _httpClient = new HttpClient();
#endif
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        private void SetAuthHeader()
        {
            var token = _storage.Token();
            _httpClient.DefaultRequestHeaders.Authorization = !string.IsNullOrEmpty(token)
                ? new AuthenticationHeaderValue("Bearer", token)
                : null;
        }

        // ── Auth ─────────────────────────────────────────────────────────────

        public async Task<AuthResult> LoginAsync(string username, string password, string? session = null)
        {
            // In local mode, auth is completely bypassed
            if (_localMode.IsLocalMode)
                return LocalAuthResult(username);

            try
            {
                var body = new { username, password, session = session ?? string.Empty };
                using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync($"{_baseUrl}/auth/login", content);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return new AuthResult { Success = false, Message = "Invalid credentials." };

                response.EnsureSuccessStatusCode();

                var result = await DeserializeAsync<LoginResponse>(response)
                    ?? throw new InvalidOperationException("Empty login response.");

                ApplyToken(result.Token);

                return new AuthResult
                {
                    Success = true,
                    Token = result.Token,
                    User = new UserInfo
                    {
                        Login = result.Username,
                        Session = result.Session,
                        ValidUntil = result.ExpiresAt
                    },
                    Message = "OK"
                };
            }
            catch (HttpRequestException)
            {
                // Network failure during login → fall back to local mode
                _localMode.SetLocalMode(true);
                return LocalAuthResult(username);
            }
            catch (Exception ex)
            {
                return new AuthResult { Success = false, Message = ex.Message };
            }
        }

        public async Task<AuthResult> RegisterAsync(string username, string email, string password)
        {
            if (_localMode.IsLocalMode)
                return new AuthResult { Success = false, Message = "Registration not available in local mode." };

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

                ApplyToken(result.Token);

                return new AuthResult
                {
                    Success = true,
                    Token = result.Token,
                    User = new UserInfo
                    {
                        Login = result.Username,
                        Session = result.Session,
                        ValidUntil = result.ExpiresAt
                    },
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

        // ── Servers ──────────────────────────────────────────────────────────

        public async Task<IReadOnlyList<ServerInfo>> GetServersAsync()
        {
            if (_localMode.IsLocalMode)
                return _localConfig.Config.Servers.Select(s => s.ToServerInfo()).ToList();

            SetAuthHeader();
            try
            {
                using var response = await _httpClient.GetAsync($"{_baseUrl}/servers/");
                response.EnsureSuccessStatusCode();
                var list = await DeserializeAsync<List<ServerInfo>>(response);
                return list?.AsReadOnly() ?? (IReadOnlyList<ServerInfo>)Array.Empty<ServerInfo>();
            }
            catch (HttpRequestException)
            {
                _localMode.SetLocalMode(true);
                return _localConfig.Config.Servers.Select(s => s.ToServerInfo()).ToList();
            }
        }

        public async Task<string> GetServerConfigAsync(int serverId)
        {
            if (_localMode.IsLocalMode)
                return BuildLocalConfig(serverId);

            SetAuthHeader();
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
                _localMode.SetLocalMode(true);
                return BuildLocalConfig(serverId);
            }
        }

        // ── Geo data ─────────────────────────────────────────────────────────

        public async Task<Stream> DownloadGeoDataAsync(CancellationToken ct = default)
        {
            if (_localMode.IsLocalMode)
            {
                var path = _localConfig.Config.GeoDbPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return File.OpenRead(path);
                throw new InvalidOperationException("No local GeoIP database configured.");
            }

            var url = $"{_baseUrl}/geo/country.mmdb";
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(ct);
        }

        public async Task<GeoDataVersion> GetGeoDataVersionAsync()
        {
            if (_localMode.IsLocalMode)
                return new GeoDataVersion { Version = "local", UpdatedAt = DateTime.UtcNow };

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

        public async Task<RoutingRulesFile> GetRoutingRulesAsync(CancellationToken ct = default)
        {
            if (_localMode.IsLocalMode)
                return _localConfig.Config.RoutingRules ?? new RoutingRulesFile();

            SetAuthHeader();
            try
            {
                using var response = await _httpClient.GetAsync($"{_baseUrl}/routing-rules", ct);
                response.EnsureSuccessStatusCode();
                return await DeserializeAsync<RoutingRulesFile>(response) ?? new RoutingRulesFile();
            }
            catch (HttpRequestException)
            {
                return _localConfig.Config.RoutingRules ?? new RoutingRulesFile();
            }
        }

        // ── Error reporting ───────────────────────────────────────────────────

        public async Task<bool> SendErrorReportAsync(ErrorReport report, CancellationToken ct = default)
        {
            if (_localMode.IsLocalMode) return false;
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

        // ── Helpers ───────────────────────────────────────────────────────────

        private static AuthResult LocalAuthResult(string username) => new()
        {
            Success = true,
            Token = "local-mode-token",
            Message = "Local mode — no auth required",
            User = new UserInfo
            {
                Login = string.IsNullOrEmpty(username) ? "admin" : username,
                Session = "local",
                ValidUntil = DateTime.UtcNow.AddYears(10)
            }
        };

        private string BuildLocalConfig(int serverId)
        {
            // Try to find the server by id hash in local config
            var idStr = serverId.ToString();
            var entry = _localConfig.Config.Servers.FirstOrDefault(s =>
                s.Id == idStr ||
                Math.Abs(s.Id.GetHashCode() % 10000) == serverId) ??
                _localConfig.Config.Servers.FirstOrDefault();

            if (entry == null)
                throw new InvalidOperationException(
                    "No local server configurations found. Please add a server in Admin → Local Config.");

            if (!string.IsNullOrEmpty(entry.RawConfig))
                return entry.RawConfig;

            // Build Hysteria2 YAML from the entry
            var cfg = new Hysteria2Config
            {
                ServerId = entry.Id,
                Name = entry.Name,
                Server = $"{entry.Host}:{entry.Port}",
                Auth = entry.AuthToken,
                Obfs = entry.ObfsType,
                ObfsPassword = entry.ObfsPassword ?? string.Empty,
                PortsRange = entry.PortsRange,
                LazyTls = entry.LazyTls,
                Socks5Address = entry.Socks5Address
            };
            return cfg.ToConfig();
        }

        private void ApplyToken(string token)
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }

        private static async Task<T?> DeserializeAsync<T>(HttpResponseMessage response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
        }

        private record ConnectResponseDto([property: JsonPropertyName("config")] string Config);
    }
}
