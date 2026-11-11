namespace BannerlordModCompat.Core;

internal sealed class CampaignRegistrationAnalyzer
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

        List<ConflictFinding> findings = [];
        findings.AddRange(AnalyzeModelOverlaps(profiles, byId));
        findings.AddRange(AnalyzeBehaviorEventOverlaps(profiles));
        return findings;
    }

    private static IEnumerable<ConflictFinding> AnalyzeModelOverlaps(
        IReadOnlyList<ModuleCodeProfile> profiles,
        IReadOnlyDictionary<string, ModuleCodeProfile> byId)
    {
        Dictionary<string, List<(string ModuleId, string TypeName)>> byFamily = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModuleCodeProfile profile in profiles)
        {
            foreach ((string family, HashSet<string> typeNames) in profile.ModelFamilies)
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

            bool explicitAddModel = modules.Any(id =>
                byId.TryGetValue(id, out ModuleCodeProfile? profile) && profile.CallsAddModel);

            findings.Add(new ConflictFinding
            {
                Category = ConflictCategory.GameModelOverlap,
                Severity = explicitAddModel ? ConflictSeverity.High : ConflictSeverity.Medium,
                Confidence = explicitAddModel ? 0.86 : 0.72,
                ModuleIds = modules,
                Reason = $"Multiple modules provide GameModel implementations in family '{AnalyzerNameHelpers.ShortName(family)}'.",
                LikelyInGameOutcome =
                    "Only one effective model path may win for core calculations, causing balance or behavior drift.",
                Recommendation =
                    "Validate in campaign with the stack unchanged first. If results drift, keep one mod as the owner for this system or use a compatibility patch.",
                Evidence = entries.Select(x => $"{x.ModuleId}:{x.TypeName}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToList(),
                StructuredEvidence = EvidenceProfiles.Create(
                    FindingEvidenceScope.Module,
                    [FindingEvidenceSource.StaticMetadata],
                    [FindingEvidenceKind.CampaignRegistration],
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["signal"] = "game-model-overlap",
                        ["model-family"] = family,
                        ["explicit-add-model"] = explicitAddModel ? "true" : "false",
                    }),
            });
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.ModuleIds.Count)
            .Take(80)
            .ToList();
    }

    private static IEnumerable<ConflictFinding> AnalyzeBehaviorEventOverlaps(IReadOnlyList<ModuleCodeProfile> profiles)
    {
        Dictionary<string, HashSet<string>> eventToModules = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModuleCodeProfile profile in profiles.Where(p => p.CampaignBehaviorTypes.Count > 0))
        {
            foreach (string eventName in profile.CampaignEventHooks)
            {
                if (!eventToModules.TryGetValue(eventName, out HashSet<string>? moduleSet))
                {
                    moduleSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    eventToModules[eventName] = moduleSet;
                }

                moduleSet.Add(profile.ModuleId);
            }
        }

        List<ConflictFinding> findings = [];
        foreach ((string eventName, HashSet<string> moduleSet) in eventToModules
                     .OrderByDescending(x => x.Value.Count)
                     .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (moduleSet.Count < 2)
            {
                continue;
            }

            string[] modules = moduleSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            bool highImpact = IsHighImpactCampaignEvent(eventName);
            findings.Add(new ConflictFinding
            {
                Category = ConflictCategory.BehaviorEventOverlap,
                Severity = highImpact ? ConflictSeverity.High : ConflictSeverity.Medium,
                Confidence = highImpact ? 0.76 : 0.64,
                ModuleIds = modules,
                Reason = $"Multiple modules attach campaign behavior handlers to '{eventName}'.",
                LikelyInGameOutcome =
                    "Event listener order can stack side effects, duplicate logic, or alter campaign state progression.",
                Recommendation =
                    "Validate together first. If you notice duplicated effects, keep load order fixed and isolate which mod should own the feature (or install a compatibility patch).",
                Evidence = profiles
                    .Where(p => moduleSet.Contains(p.ModuleId))
                    .SelectMany(p => p.CampaignBehaviorTypes.Take(2).Select(t => $"{p.ModuleId}:{t}"))
                    .Take(10)
                    .ToList(),
                StructuredEvidence = EvidenceProfiles.Create(
                    FindingEvidenceScope.Module,
                    [FindingEvidenceSource.StaticMetadata],
                    [FindingEvidenceKind.CampaignRegistration],
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["signal"] = "campaign-event-overlap",
                        ["event-name"] = eventName,
                        ["high-impact"] = highImpact ? "true" : "false",
                    }),
            });
        }

        return findings.Take(80).ToList();
    }

    private static bool IsHighImpactCampaignEvent(string eventName)
    {
        return eventName.Contains("SessionLaunched", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("GameLoaded", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("NewGameCreated", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("DailyTick", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("HourlyTick", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("Settlement", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("Hero", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("Clan", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("Party", StringComparison.OrdinalIgnoreCase);
    }
}
