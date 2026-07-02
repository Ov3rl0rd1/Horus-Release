using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using System.Text.Json;

namespace Horus.Application
{
    public class LocalConfigService : ILocalConfigService
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly string _configPath;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private LocalConfig _config = new();

        public LocalConfig Config => _config;

        public LocalConfigService()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Horus");
            Directory.CreateDirectory(dir);
            _configPath = Path.Combine(dir, "local_config.json");
        }

        public async Task LoadAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (!File.Exists(_configPath)) return;
                await using var fs = File.OpenRead(_configPath);
                _config = await JsonSerializer.DeserializeAsync<LocalConfig>(fs, JsonOpts)
                    ?? new LocalConfig();
            }
            catch
            {
                _config = new LocalConfig();
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SaveAsync()
        {
            await _lock.WaitAsync();
            try
            {
                _config.UpdatedAt = DateTime.UtcNow;
                var json = JsonSerializer.Serialize(_config, JsonOpts);
                await File.WriteAllTextAsync(_configPath, json);
            }
            finally
            {
                _lock.Release();
            }
        }

        // ── Servers ──────────────────────────────────────────────────────────

        public async Task AddServerAsync(LocalServerEntry entry)
        {
            _config.Servers.Add(entry);
            await SaveAsync();
        }

        public async Task UpdateServerAsync(LocalServerEntry entry)
        {
            var idx = _config.Servers.FindIndex(s => s.Id == entry.Id);
            if (idx >= 0) _config.Servers[idx] = entry;
            else _config.Servers.Add(entry);
            await SaveAsync();
        }

        public async Task RemoveServerAsync(string id)
        {
            _config.Servers.RemoveAll(s => s.Id == id);
            if (_config.DefaultServerId == id)
                _config.DefaultServerId = _config.Servers.FirstOrDefault()?.Id;
            await SaveAsync();
        }

        public async Task SetDefaultServerAsync(string id)
        {
            _config.DefaultServerId = id;
            await SaveAsync();
        }

        // ── Routing ──────────────────────────────────────────────────────────

        public async Task SaveRoutingRulesAsync(RoutingRulesFile rules)
        {
            _config.RoutingRules = rules;
            await SaveAsync();
        }

        // ── Split tunneling ──────────────────────────────────────────────────

        public async Task SaveSplitTunnelingAsync(SplitTunnelingMode mode, IEnumerable<string> entries)
        {
            _config.SplitTunneling = new LocalSplitTunnelingConfig
            {
                Mode = mode,
                Entries = [.. entries]
            };
            await SaveAsync();
        }

        // ── GeoIP ────────────────────────────────────────────────────────────

        public async Task SetGeoDbPathAsync(string path)
        {
            _config.GeoDbPath = path;
            await SaveAsync();
        }
    }
}
