using Horus.Domain.Interfaces;
using Horus.Domain.Models;
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

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public ApiService(IStorageService storage)
        {
            _storage = storage;
            _baseUrl = AppConfiguration.ApiBaseUrl.TrimEnd('/');

            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };
            _httpClient = new HttpClient(handler);
        }

        private void SetAuthHeader()
        {
            var token = _storage.Token();
            if (!string.IsNullOrEmpty(token))
                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
            else
                _httpClient.DefaultRequestHeaders.Authorization = null;
        }

        public async Task<AuthResult> LoginAsync(string username, string password, string? session = null)
        {
            try
            {
                var body = new { username, password, session = session ?? string.Empty };
                var json = JsonSerializer.Serialize(body);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{_baseUrl}/auth/login", content);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return new AuthResult { Success = false, Message = "Invalid credentials." };

                response.EnsureSuccessStatusCode();

                var responseText = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<LoginResponse>(responseText, JsonOptions)
                    ?? throw new InvalidOperationException("Empty login response.");

                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", result.Token);

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
                    Message = "Login successful"
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

        public async Task<IReadOnlyList<ServerInfo>> GetServersAsync()
        {
            SetAuthHeader();
            var response = await _httpClient.GetAsync($"{_baseUrl}/servers/");
            response.EnsureSuccessStatusCode();

            var text = await response.Content.ReadAsStringAsync();
            var servers = JsonSerializer.Deserialize<List<ServerInfo>>(text, JsonOptions);
            return servers?.AsReadOnly() ?? new List<ServerInfo>().AsReadOnly();
        }

        public async Task<string> GetServerConfigAsync(int serverId)
        {
            SetAuthHeader();
            var response = await _httpClient.GetAsync($"{_baseUrl}/servers/{serverId}/connect");
            response.EnsureSuccessStatusCode();

            var text = await response.Content.ReadAsStringAsync();
            var dto = JsonSerializer.Deserialize<ConnectResponseDto>(text, JsonOptions)
                ?? throw new InvalidOperationException("Invalid connect response.");
            return dto.Config;
        }

        private record ConnectResponseDto([property: JsonPropertyName("config")] string Config);
    }
}
