namespace BannerlordModCompat.Core;

internal sealed class MissionRegistrationAnalyzer
{
    public IReadOnlyList<ConflictFinding> Analyze(IReadOnlyList<ModuleCodeProfile> profiles)
    {
        if (profiles.Count <= 1)
        {
            return [];
        }

        Dictionary<string, ModuleCodeProfile> byId = profiles
            .GroupBy(p => p.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        return AnalyzeMissionBehaviorOverlaps(profiles, byId);
    }

    private static IReadOnlyList<ConflictFinding> AnalyzeMissionBehaviorOverlaps(
        IReadOnlyList<ModuleCodeProfile> profiles,
        IReadOnlyDictionary<string, ModuleCodeProfile> byId)
    {
        Dictionary<string, List<(string ModuleId, string TypeName)>> byFamily = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModuleCodeProfile profile in profiles)
        {
            foreach ((string family, HashSet<string> typeNames) in profile.MissionFamilies)
            {
                if (!byFamily.TryGetValue(family, out List<(string ModuleId, string TypeName)>? list))
                {
                    list = [];
                    byFamily[family] = list;
                }

                foreach (string typeName in typeNames)
                {
                    list.Add((profile.ModuleId, typeName));
                }
            }
        }

        List<ConflictFinding> findings = [];
        foreach ((string family, List<(string ModuleId, string TypeName)> entries) in byFamily)
        {
            string[] modules = entries.Select(x => x.ModuleId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (modules.Length < 2)
            {
                continue;
            }

            bool explicitMissionAdd = modules.Any(id =>
                byId.TryGetValue(id, out ModuleCodeProfile? profile) && profile.CallsAddMissionBehavior);

            findings.Add(new ConflictFinding
            {
                Category = ConflictCategory.MissionBehaviorOverlap,
                Severity = explicitMissionAdd ? ConflictSeverity.Medium : ConflictSeverity.Low,
                Confidence = explicitMissionAdd ? 0.70 : 0.56,
                ModuleIds = modules,
                Reason = $"Multiple modules provide mission behavior logic in family '{AnalyzerNameHelpers.ShortName(family)}'.",
                LikelyInGameOutcome =
                    "Mission initialization order can change AI/UI/agent behavior at runtime.",
                Recommendation =
                    "Validate one battle/mission scenario with the stack unchanged. If behavior drifts, keep load order fixed while isolating which mod owns the behavior.",
                Evidence = entries.Select(x => $"{x.ModuleId}:{x.TypeName}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToList(),
                StructuredEvidence = EvidenceProfiles.Create(
                    FindingEvidenceScope.Module,
                    [FindingEvidenceSource.StaticMetadata],
                    [FindingEvidenceKind.MissionRegistration],
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["signal"] = "mission-behavior-overlap",
                        ["mission-family"] = family,
                        ["explicit-add-mission-behavior"] = explicitMissionAdd ? "true" : "false",
                    }),
            });
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.ModuleIds.Count)
            .Take(50)
            .ToList();
    }
}
