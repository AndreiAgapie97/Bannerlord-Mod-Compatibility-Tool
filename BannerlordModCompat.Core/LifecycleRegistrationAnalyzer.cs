namespace BannerlordModCompat.Core;

internal sealed class LifecycleRegistrationAnalyzer
{
    public IReadOnlyList<ConflictFinding> Analyze(IReadOnlyList<ModuleCodeProfile> profiles)
    {
        if (profiles.Count <= 1)
        {
            return [];
        }

        List<(string ModuleId, LifecycleRegistrationRecord Record)> campaignRegistrations = profiles
            .SelectMany(profile => profile.LifecycleRegistrations
                .Where(r => r.ActionKind is RegistrationActionKind.AddBehavior or RegistrationActionKind.AddModel)
                .Select(r => (profile.ModuleId, r)))
            .ToList();

        List<(string ModuleId, LifecycleRegistrationRecord Record)> missionRegistrations = profiles
            .SelectMany(profile => profile.LifecycleRegistrations
                .Where(r => r.ActionKind == RegistrationActionKind.AddMissionBehavior)
                .Select(r => (profile.ModuleId, r)))
            .ToList();

        List<ConflictFinding> findings = [];

        ConflictFinding? campaignFinding = BuildLifecycleFinding(
            campaignRegistrations,
            evidenceKind: FindingEvidenceKind.CampaignRegistration,
            targetLabel: "campaign behavior/model handlers",
            likelyOutcome: "Hook-phase and load-order precedence can change event listener order and model override chains between runs.",
            recommendation: "Validate together first. If the issue reproduces, keep order stable while isolating which mod owns registration for this phase."
        );
        if (campaignFinding is not null)
        {
            findings.Add(campaignFinding);
        }

        ConflictFinding? missionFinding = BuildLifecycleFinding(
            missionRegistrations,
            evidenceKind: FindingEvidenceKind.MissionRegistration,
            targetLabel: "mission behavior handlers",
            likelyOutcome: "Mission startup behavior can drift when multiple modules inject handlers in overlapping mission lifecycle phases.",
            recommendation: "Validate one battle/mission scenario with the stack unchanged. If it reproduces, keep order stable while isolating module cohorts."
        );
        if (missionFinding is not null)
        {
            findings.Add(missionFinding);
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.Confidence)
            .Take(20)
            .ToList();
    }

    private static ConflictFinding? BuildLifecycleFinding(
        IReadOnlyList<(string ModuleId, LifecycleRegistrationRecord Record)> records,
        FindingEvidenceKind evidenceKind,
        string targetLabel,
        string likelyOutcome,
        string recommendation
    )
    {
        string[] moduleIds = records
            .Select(r => r.ModuleId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (moduleIds.Length < 2)
        {
            return null;
        }

        string[] phaseList = records
            .Select(r => r.Record.Phase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetLifecyclePhaseSortOrder)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] sharedPhases = records
            .GroupBy(r => r.Record.Phase, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.ModuleId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(g => g.Key)
            .OrderBy(GetLifecyclePhaseSortOrder)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        bool hasSharedEarlyPhase = sharedPhases.Any(phase =>
            phase.Equals("Bootstrap", StringComparison.OrdinalIgnoreCase)
            || phase.Equals("GameStart", StringComparison.OrdinalIgnoreCase)
            || phase.Equals("CampaignStart", StringComparison.OrdinalIgnoreCase)
            || phase.Equals("MissionInit", StringComparison.OrdinalIgnoreCase));

        ConflictSeverity severity = hasSharedEarlyPhase
            ? ConflictSeverity.High
            : ConflictSeverity.Medium;
        double confidence = 0.68
            + (sharedPhases.Length > 0 ? 0.10 : 0.00)
            + (moduleIds.Length >= 3 ? 0.05 : 0.00)
            + (hasSharedEarlyPhase ? 0.07 : 0.00);
        confidence = Math.Clamp(confidence, 0.55, 0.92);

        string phaseSummary = phaseList.Length == 0
            ? "unknown phase"
            : string.Join(", ", phaseList.Take(4));
        if (phaseList.Length > 4)
        {
            phaseSummary += $", +{phaseList.Length - 4} more";
        }

        string sharedSummary = sharedPhases.Length == 0
            ? "no explicit shared phase was resolved"
            : string.Join(", ", sharedPhases.Take(3));

        return new ConflictFinding
        {
            Category = ConflictCategory.LifecycleRegistrationOverlap,
            Severity = severity,
            Confidence = confidence,
            ModuleIds = moduleIds,
            Reason = $"Multiple modules register {targetLabel} from MBSubModuleBase lifecycle hooks ({phaseSummary}); shared phase signal: {sharedSummary}.",
            LikelyInGameOutcome = likelyOutcome,
            Recommendation = recommendation,
            Evidence = records
                .Select(r => $"{r.ModuleId}:{r.Record.Phase}:{r.Record.ActionKind} -> {r.Record.EvidencePath}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList(),
            StructuredEvidence = EvidenceProfiles.Create(
                FindingEvidenceScope.Module,
                [FindingEvidenceSource.StaticMetadata],
                [evidenceKind],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["signal"] = "lifecycle-registration-overlap",
                    ["target-label"] = targetLabel,
                    ["phase-summary"] = phaseSummary,
                    ["shared-phase-summary"] = sharedSummary,
                    ["shared-early-phase"] = hasSharedEarlyPhase ? "true" : "false",
                    ["module-count"] = moduleIds.Length.ToString(),
                }),
        };
    }

    private static int GetLifecyclePhaseSortOrder(string phase)
    {
        return phase switch
        {
            "Bootstrap" => 0,
            "GameStart" => 1,
            "CampaignStart" => 2,
            "MissionInit" => 3,
            _ => 10,
        };
    }
}
