using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    public class RoutingService : IRoutingService
    {
        private readonly IVpnPlatformService _platform;
        private readonly List<RoutingRule> _rules = [];

        public RoutingService(IVpnPlatformService platform)
        {
            _platform = platform;
        }

        public IReadOnlyList<RoutingRule> CurrentRules => _rules.AsReadOnly();

        public Task AddRuleAsync(RoutingRule rule)
        {
            _rules.Add(rule);
            return Task.CompletedTask;
        }

        public Task RemoveRuleAsync(string ruleId)
        {
            _rules.RemoveAll(r => r.Id == ruleId);
            return Task.CompletedTask;
        }

        public Task ReorderRulesAsync(IEnumerable<string> orderedIds)
        {
            var ordered = orderedIds.ToList();
            _rules.Sort((a, b) => ordered.IndexOf(a.Id).CompareTo(ordered.IndexOf(b.Id)));
            return Task.CompletedTask;
        }

        public Task SetBypassListAsync(string[] ips, string[] domains)
        {
            _rules.RemoveAll(r => r.Action == RuleAction.Direct);
            foreach (var ip in ips)
                _rules.Add(new RoutingRule
                {
                    Id = Guid.NewGuid().ToString(),
                    Pattern = ip,
                    Type = RuleType.IpCidr,
                    Action = RuleAction.Direct,
                    Priority = 100,
                    IsEnabled = true
                });
            foreach (var domain in domains)
                _rules.Add(new RoutingRule
                {
                    Id = Guid.NewGuid().ToString(),
                    Pattern = domain,
                    Type = RuleType.Domain,
                    Action = RuleAction.Direct,
                    Priority = 100,
                    IsEnabled = true
                });
            return Task.CompletedTask;
        }

        public async Task ApplyAsync()
        {
            await _platform.ApplyRoutingRulesAsync(_rules.Where(r => r.IsEnabled));
        }
    }
}
