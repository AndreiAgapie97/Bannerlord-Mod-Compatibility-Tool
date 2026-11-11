namespace BannerlordModCompat.Core;

public sealed class CodeConflictAnalyzer
{
    private readonly CampaignRegistrationAnalyzer _campaignRegistrationAnalyzer = new();
    private readonly MissionRegistrationAnalyzer _missionRegistrationAnalyzer = new();
    private readonly LifecycleRegistrationAnalyzer _lifecycleRegistrationAnalyzer = new();

    public IReadOnlyList<ConflictFinding> Analyze(
        IReadOnlyList<ModuleManifest> modules,
        bool customOnlyFocus,
        List<string> warnings
    )
    {
        ModuleCapabilityProfiler profiler = new();
        return Analyze(profiler.BuildProfiles(modules, customOnlyFocus, warnings));
    }

    internal IReadOnlyList<ConflictFinding> Analyze(IReadOnlyList<ModuleCapabilityProfile> capabilityProfiles)
    {
        List<ModuleCodeProfile> profiles = NormalizeProfiles(capabilityProfiles);
        if (profiles.Count <= 1)
        {
            return [];
        }

        List<ConflictFinding> findings = [];
        findings.AddRange(_campaignRegistrationAnalyzer.Analyze(profiles));
        findings.AddRange(_missionRegistrationAnalyzer.Analyze(profiles));
        findings.AddRange(_lifecycleRegistrationAnalyzer.Analyze(profiles));
        return findings;
    }

    private static List<ModuleCodeProfile> NormalizeProfiles(IReadOnlyList<ModuleCapabilityProfile> capabilityProfiles)
    {
        List<ModuleCodeProfile> profiles = [];
        foreach (ModuleCapabilityProfile source in capabilityProfiles)
        {
            ModuleCodeProfile target = new(source.ModuleId)
            {
                CallsAddBehavior = source.CallsAddBehavior,
                CallsAddModel = source.CallsAddModel,
                CallsAddMissionBehavior = source.CallsAddMissionBehavior,
            };

            foreach (string typeName in source.CampaignBehaviorTypes)
            {
                target.CampaignBehaviorTypes.Add(typeName);
            }

            foreach (string typeName in source.GameModelTypes)
            {
                target.GameModelTypes.Add(typeName);
            }

            foreach (string typeName in source.MissionBehaviorTypes)
            {
                target.MissionBehaviorTypes.Add(typeName);
            }

            foreach (string eventName in source.CampaignEventHooks)
            {
                target.CampaignEventHooks.Add(eventName);
            }

            foreach ((string family, HashSet<string> typeNames) in source.ModelFamilies)
            {
                foreach (string typeName in typeNames)
                {
                    target.AddModelFamily(family, typeName);
                }
            }

            foreach ((string family, HashSet<string> typeNames) in source.MissionFamilies)
            {
                foreach (string typeName in typeNames)
                {
                    target.AddMissionFamily(family, typeName);
                }
            }

            target.LifecycleRegistrations.AddRange(source.LifecycleRegistrations);
            if (target.HasSignals)
            {
                profiles.Add(target);
            }
        }

        return profiles;
    }
}

