namespace BannerlordModCompat.Core;

internal sealed record LoadOrderRuleEdge(
    string FromModuleId,
    string ToModuleId,
    bool Optional,
    LoadOrderRationaleKind RationaleKind,
    string RuleId
);

internal static class LoadOrderRuleSet
{
    public static IReadOnlyList<LoadOrderRuleEdge> BuildEdges(
        IReadOnlySet<string> moduleIds,
        IReadOnlyDictionary<string, int> currentIndex,
        IReadOnlyList<ModuleCapabilityProfile>? capabilityProfiles)
    {
        List<LoadOrderRuleEdge> edges = [];

        List<string> chain = [];
        foreach (string[] aliases in BannerlordKnowledgeBase.GetCanonicalBootstrapGroups())
        {
            string? chosen = aliases
                .Where(moduleIds.Contains)
                .OrderBy(id => currentIndex.GetValueOrDefault(id, int.MaxValue))
                .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(chosen))
            {
                chain.Add(chosen);
            }
        }

        for (int i = 0; i < chain.Count - 1; i++)
        {
            edges.Add(new LoadOrderRuleEdge(
                chain[i],
                chain[i + 1],
                Optional: true,
                LoadOrderRationaleKind.Bootstrap,
                RuleId: $"bootstrap:{chain[i]}->{chain[i + 1]}"));
        }

        if (capabilityProfiles is null || capabilityProfiles.Count == 0)
        {
            return edges;
        }

        foreach (KnownCompatibilityRule rule in BannerlordKnowledgeBase.BuildUiPrecedenceRules(capabilityProfiles))
        {
            if (rule.ModuleIds.Count < 2)
            {
                continue;
            }

            string from = rule.ModuleIds[0];
            string to = rule.ModuleIds[1];
            if (!moduleIds.Contains(from) || !moduleIds.Contains(to))
            {
                continue;
            }

            edges.Add(new LoadOrderRuleEdge(
                from,
                to,
                Optional: false,
                RationaleKind: rule.LoadOrderRationaleKind ?? LoadOrderRationaleKind.OfficialUiPrecedence,
                RuleId: rule.Id));
        }

        return edges;
    }
}
