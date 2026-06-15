using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using System.Text.Json;

namespace Horus.Application
{
    public class RoutingService : IRoutingService
    {
        private readonly IVpnPlatformService _platform;
        private readonly IGeoDataService _geo;
        private readonly IApiService _api;

        private readonly List<RoutingRule> _rules = [];
        private readonly string _cachedRulesPath;
        private readonly object _lock = new();

        public IReadOnlyList<RoutingRule> CurrentRules
        {
            get { lock (_lock) return [.. _rules]; }
        }

        public RoutingService(IVpnPlatformService platform, IGeoDataService geo, IApiService api)
        {
            _platform = platform;
            _geo = geo;
            _api = api;

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Horus");
            Directory.CreateDirectory(dir);
            _cachedRulesPath = Path.Combine(dir, "routing-rules.json");

            LoadBuiltInRules();
        }

        // ── Built-in rules ──────────────────────────────────────────────────

        private void LoadBuiltInRules()
        {
            lock (_lock)
            {
                _rules.Clear();

                // RU traffic always goes direct (higher priority overrides server rules)
                _rules.Add(new RoutingRule
                {
                    Id = "builtin-geoip-ru",
                    Type = RuleType.GeoIP,
                    Pattern = "RU",
                    Action = RuleAction.Direct,
                    Priority = 1000,
                    IsEnabled = true
                });

                // Local RFC-1918 and link-local ranges go direct
                foreach (var cidr in new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",
                                              "169.254.0.0/16", "127.0.0.0/8", "::1/128" })
                {
                    _rules.Add(new RoutingRule
                    {
                        Id = $"builtin-local-{cidr.Replace("/", "_")}",
                        Type = RuleType.IpCidr,
                        Pattern = cidr,
                        Action = RuleAction.Direct,
                        Priority = 2000,
                        IsEnabled = true
                    });
                }
            }
        }

        // ── Remote rules update ──────────────────────────────────────────────

        /// <summary>
        /// Downloads routing rules from the server and merges them with built-in rules.
        /// Falls back to the cached copy on failure.
        /// </summary>
        public async Task RefreshFromServerAsync(CancellationToken ct = default)
        {
            RoutingRulesFile? serverRules = null;

            try
            {
                serverRules = await _api.GetRoutingRulesAsync(ct);
                // Cache on disk
                var json = JsonSerializer.Serialize(serverRules, new JsonSerializerOptions { WriteIndented = false });
                await File.WriteAllTextAsync(_cachedRulesPath, json, ct);
            }
            catch
            {
                // Try loading from cache
                if (File.Exists(_cachedRulesPath))
                {
                    try
                    {
                        var cached = await File.ReadAllTextAsync(_cachedRulesPath, ct);
                        serverRules = JsonSerializer.Deserialize<RoutingRulesFile>(cached);
                    }
                    catch { /* use built-in rules only */ }
                }
            }

            if (serverRules != null)
                MergeServerRules(serverRules);
        }

        private void MergeServerRules(RoutingRulesFile file)
        {
            lock (_lock)
            {
                // Remove previously downloaded server rules, keep built-in
                _rules.RemoveAll(r => r.Id.StartsWith("server-"));

                var priority = 500;
                foreach (var entry in file.Rules)
                {
                    _rules.Add(new RoutingRule
                    {
                        Id = $"server-{Guid.NewGuid():N}",
                        Type = entry.Type switch
                        {
                            "domain" => RuleType.Domain,
                            "domain_suffix" => RuleType.Domain,
                            "domain_keyword" => RuleType.Domain,
                            "ip_cidr" => RuleType.IpCidr,
                            "geoip" => RuleType.GeoIP,
                            "process" => RuleType.ProcessName,
                            _ => RuleType.Domain
                        },
                        Pattern = entry.Pattern,
                        Action = entry.Action switch
                        {
                            "direct" => RuleAction.Direct,
                            "reject" => RuleAction.Reject,
                            _ => RuleAction.Proxy
                        },
                        Priority = entry.Priority > 0 ? entry.Priority : priority--,
                        IsEnabled = true
                    });
                }
            }
        }

        // ── IRoutingService ──────────────────────────────────────────────────

        public Task AddRuleAsync(RoutingRule rule)
        {
            lock (_lock) _rules.Add(rule);
            return Task.CompletedTask;
        }

        public Task RemoveRuleAsync(string ruleId)
        {
            lock (_lock) _rules.RemoveAll(r => r.Id == ruleId);
            return Task.CompletedTask;
        }

        public Task ReorderRulesAsync(IEnumerable<string> orderedIds)
        {
            var ordered = orderedIds.ToList();
            lock (_lock)
                _rules.Sort((a, b) => ordered.IndexOf(a.Id).CompareTo(ordered.IndexOf(b.Id)));
            return Task.CompletedTask;
        }

        public Task SetBypassListAsync(string[] ips, string[] domains)
        {
            lock (_lock)
            {
                _rules.RemoveAll(r => r.Id.StartsWith("bypass-"));

                foreach (var ip in ips)
                    _rules.Add(new RoutingRule
                    {
                        Id = $"bypass-ip-{Guid.NewGuid():N}",
                        Type = RuleType.IpCidr,
                        Pattern = ip,
                        Action = RuleAction.Direct,
                        Priority = 900,
                        IsEnabled = true
                    });

                foreach (var domain in domains)
                    _rules.Add(new RoutingRule
                    {
                        Id = $"bypass-domain-{Guid.NewGuid():N}",
                        Type = RuleType.Domain,
                        Pattern = domain,
                        Action = RuleAction.Direct,
                        Priority = 900,
                        IsEnabled = true
                    });
            }
            return Task.CompletedTask;
        }

        public async Task ApplyAsync()
        {
            List<RoutingRule> snapshot;
            lock (_lock) snapshot = _rules.Where(r => r.IsEnabled).ToList();
            await _platform.ApplyRoutingRulesAsync(snapshot);
        }

        /// <summary>Determines the action for a given IP address using loaded rules.</summary>
        public async Task<RuleAction> ResolveActionForIpAsync(string ip)
        {
            List<RoutingRule> snapshot;
            lock (_lock) snapshot = [.. _rules.Where(r => r.IsEnabled).OrderByDescending(r => r.Priority)];

            foreach (var rule in snapshot)
            {
                if (rule.Type == RuleType.GeoIP)
                {
                    var geoResult = await _geo.MatchIpAsync(ip);
                    if (geoResult.HasMatch &&
                        string.Equals(geoResult.GeoFile, rule.Pattern, StringComparison.OrdinalIgnoreCase))
                        return rule.Action;
                }
                else if (rule.Type == RuleType.IpCidr)
                {
                    if (IpMatchesCidr(ip, rule.Pattern))
                        return rule.Action;
                }
            }

            return RuleAction.Proxy;
        }

        private static bool IpMatchesCidr(string ip, string cidr)
        {
            try
            {
                var parts = cidr.Split('/');
                if (parts.Length != 2) return false;
                if (!System.Net.IPAddress.TryParse(ip, out var addr)) return false;
                if (!System.Net.IPAddress.TryParse(parts[0], out var network)) return false;
                if (!int.TryParse(parts[1], out var prefix)) return false;

                var addrBytes = addr.GetAddressBytes();
                var netBytes = network.GetAddressBytes();
                if (addrBytes.Length != netBytes.Length) return false;

                int fullBytes = prefix / 8;
                int remainBits = prefix % 8;

                for (int i = 0; i < fullBytes && i < addrBytes.Length; i++)
                    if (addrBytes[i] != netBytes[i]) return false;

                if (remainBits > 0 && fullBytes < addrBytes.Length)
                {
                    var mask = (byte)(0xFF << (8 - remainBits));
                    if ((addrBytes[fullBytes] & mask) != (netBytes[fullBytes] & mask))
                        return false;
                }

                return true;
            }
            catch { return false; }
        }
    }
}
