using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    public class RoutingService : IRoutingService
    {
        public IReadOnlyList<RoutingRule> CurrentRules => throw new NotImplementedException();

        public Task AddRuleAsync(RoutingRule rule)
        {
            throw new NotImplementedException();
        }

        public Task ApplyAsync()
        {
            throw new NotImplementedException();
        }

        public Task RemoveRuleAsync(string ruleId)
        {
            throw new NotImplementedException();
        }

        public Task ReorderRulesAsync(IEnumerable<string> orderedIds)
        {
            throw new NotImplementedException();
        }

        public Task SetBypassListAsync(string[] ips, string[] domains)
        {
            throw new NotImplementedException();
        }
    }
}
