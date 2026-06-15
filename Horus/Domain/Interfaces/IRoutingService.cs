using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    public interface IRoutingService
    {
        IReadOnlyList<RoutingRule> CurrentRules { get; }

        Task AddRuleAsync(RoutingRule rule);
        Task RemoveRuleAsync(string ruleId);
        Task ReorderRulesAsync(IEnumerable<string> orderedIds);
        Task SetBypassListAsync(string[] ips, string[] domains);
        Task ApplyAsync();
        Task RefreshFromServerAsync(CancellationToken ct = default);
    }
}
