namespace BannerlordModCompat.Core;

internal sealed class UiOverrideAnalyzer
{
    public IReadOnlyList<ConflictFinding> Analyze(
        IReadOnlyList<string> currentOrder,
        IReadOnlyList<ModuleCapabilityProfile> capabilityProfiles)
    {
        if (capabilityProfiles.Count == 0 || currentOrder.Count == 0)
        {
            return [];
        }

        Dictionary<string, int> currentIndex = currentOrder
            .Select((id, index) => new { id, index })
            .GroupBy(x => x.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);

        List<ConflictFinding> findings = [];
        foreach (KnownCompatibilityRule rule in BannerlordKnowledgeBase.BuildUiPrecedenceRules(capabilityProfiles))
        {
            if (rule.ModuleIds.Count < 2)
            {
                continue;
            }

            string uiModule = rule.ModuleIds[0];
            string officialTarget = rule.ModuleIds[1];
            if (!currentIndex.TryGetValue(uiModule, out int uiIndex)
                || !currentIndex.TryGetValue(officialTarget, out int officialIndex))
            {
                continue;
            }

            if (uiIndex < officialIndex)
            {
                continue;
            }

            findings.Add(new ConflictFinding
            {
                Category = ConflictCategory.LoadOrderViolation,
                Severity = rule.SeverityFloor ?? ConflictSeverity.Medium,
                Confidence = 0.92,
                ModuleIds = [uiModule, officialTarget],
                Reason = $"Known Bannerlord UI rule: codeless Gauntlet override module '{uiModule}' should load before official module '{officialTarget}'.",
                LikelyInGameOutcome = "Official UI screens can ignore or partially override the mod's UI XML when the module order is reversed.",
                Recommendation = $"Move '{uiModule}' before '{officialTarget}', then validate the affected screen in game.",
                Evidence =
                [
                    $"known-rule:{rule.Id}",
                    $"ui-module:{uiModule}",
                    $"official-target:{officialTarget}",
                    $"current-order:{uiModule}(#{uiIndex + 1}) after {officialTarget}(#{officialIndex + 1})",
                ],
                StructuredEvidence = EvidenceProfiles.Create(
                    FindingEvidenceScope.Module,
                    [FindingEvidenceSource.RulePack, FindingEvidenceSource.LauncherOrder],
                    [FindingEvidenceKind.KnownRule, FindingEvidenceKind.UiPrecedence],
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["rule-id"] = rule.Id,
                        ["ui-module"] = uiModule,
                        ["official-target"] = officialTarget,
                        ["player-summary"] = rule.PlayerSummary,
                    }),
            });
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.Confidence)
            .ToList();
    }
}
