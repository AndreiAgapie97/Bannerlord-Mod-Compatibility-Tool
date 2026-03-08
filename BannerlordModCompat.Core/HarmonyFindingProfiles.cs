namespace BannerlordModCompat.Core;

internal enum HarmonyPatchShape
{
    Unknown,
    PostfixOnly,
    PrefixMix,
    TranspilerPresent,
    FinalizerOnly,
    Mixed,
}

internal enum HarmonyOrderState
{
    Unknown,
    ExplicitlyOrdered,
    SamePriorityAmbiguous,
    Unordered,
    Cycle,
}

internal enum HarmonyOwnershipShape
{
    Unknown,
    SingleKindDuplicated,
    StackedMixedKinds,
    SingleModuleRepeated,
    MultiModule,
}

internal enum HarmonyTargetFamily
{
    Unknown,
    EconomicCalculation,
    SettlementCampaignRule,
    MissionStartup,
    AccessGate,
}

internal sealed record HarmonyRiskProfile(
    string TargetMethod,
    IReadOnlyList<string> PatchKinds,
    HarmonyPatchShape PatchShape,
    HarmonyOrderState OrderState,
    HarmonyOwnershipShape OwnershipShape,
    HarmonyTargetFamily TargetFamily,
    bool HasStaticMetadataEvidence,
    bool HasDuplicateScannerEvidence,
    bool HasOrderingGraphEvidence,
    bool HasRuntimeCorrelationEvidence,
    int ModuleCount,
    int UnorderedPairs,
    int AmbiguousPairs,
    int ExplicitEdgeCount,
    bool HasOrderingCycle
);

internal sealed record HarmonyRiskAssessment(
    ConflictCategory Category,
    ConflictSeverity Severity,
    double Confidence,
    string Reason,
    string LikelyOutcome,
    string Recommendation
);

internal static class HarmonyFindingProfiles
{
    internal const string TargetPrefix = "harmony-target:";
    internal const string KindsPrefix = "harmony-kinds:";
    internal const string GraphPrefix = "harmony-graph:";
    internal const string PriorityPrefix = "harmony-priority:";
    internal const string ProfilePrefix = "harmony-profile:";
    internal const string SourcePrefix = "harmony-source:";

    internal static HarmonyRiskProfile Create(
        string targetMethod,
        IReadOnlyList<string> patchKinds,
        HarmonyOrderState orderState,
        HarmonyOwnershipShape ownershipShape,
        bool hasStaticMetadataEvidence,
        bool hasDuplicateScannerEvidence,
        bool hasOrderingGraphEvidence,
        bool hasRuntimeCorrelationEvidence,
        int moduleCount,
        int unorderedPairs = 0,
        int ambiguousPairs = 0,
        int explicitEdgeCount = 0,
        bool hasOrderingCycle = false
    )
    {
        string normalizedTarget = string.IsNullOrWhiteSpace(targetMethod)
            ? "<unknown>"
            : targetMethod.Trim();
        List<string> normalizedKinds = patchKinds
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Select(NormalizePatchKind)
            .Where(kind => kind.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new HarmonyRiskProfile(
            normalizedTarget,
            normalizedKinds,
            DeterminePatchShape(normalizedKinds),
            orderState,
            ownershipShape,
            ClassifyTargetFamily(normalizedTarget),
            hasStaticMetadataEvidence,
            hasDuplicateScannerEvidence,
            hasOrderingGraphEvidence,
            hasRuntimeCorrelationEvidence,
            Math.Max(0, moduleCount),
            Math.Max(0, unorderedPairs),
            Math.Max(0, ambiguousPairs),
            Math.Max(0, explicitEdgeCount),
            hasOrderingCycle || orderState == HarmonyOrderState.Cycle
        );
    }

    internal static HarmonyRiskAssessment Assess(HarmonyRiskProfile profile)
    {
        ConflictSeverity severity = ComputeSeverity(profile);
        ConflictCategory category = profile.PatchShape == HarmonyPatchShape.PostfixOnly
            ? ConflictCategory.HarmonyPatchStack
            : ConflictCategory.HarmonyPatchConflict;
        double confidence = ComputeConfidence(profile);

        string patchKindsText = profile.PatchKinds.Count == 0
            ? "unknown patch kinds"
            : string.Join(", ", profile.PatchKinds);
        string target = profile.TargetMethod;

        string reason;
        string likelyOutcome;
        string recommendation;
        string validationHint = BuildValidationHint(profile.TargetFamily);
        string outcomeHint = BuildOutcomeHint(profile.TargetFamily);

        bool advisoryStack = profile.PatchShape == HarmonyPatchShape.PostfixOnly
            && profile.OrderState is HarmonyOrderState.ExplicitlyOrdered or HarmonyOrderState.Unknown
            && !profile.HasRuntimeCorrelationEvidence
            && !profile.HasOrderingCycle;
        bool orderingRisk = profile.OrderState is HarmonyOrderState.SamePriorityAmbiguous or HarmonyOrderState.Unordered or HarmonyOrderState.Cycle;
        bool ownershipRisk = profile.PatchShape is HarmonyPatchShape.TranspilerPresent or HarmonyPatchShape.PrefixMix
            || profile.OwnershipShape == HarmonyOwnershipShape.SingleKindDuplicated;

        if (advisoryStack)
        {
            reason = $"Harmony evidence shows a postfix stack on '{target}' ({patchKindsText}).";
            likelyOutcome = $"These mods patch the same method, but the current signal looks like a stack to validate, not a likely hard conflict. {outcomeHint}";
            recommendation = $"Keep the current stack together and validate the affected gameplay path first. {validationHint}";
        }
        else if (orderingRisk)
        {
            string orderText = profile.OrderState switch
            {
                HarmonyOrderState.Cycle => "circular ordering constraints",
                HarmonyOrderState.SamePriorityAmbiguous => "same-priority ambiguity",
                HarmonyOrderState.Unordered => "no stable module order",
                _ => "ordering instability",
            };
            reason = $"Harmony evidence shows unstable patch ordering on '{target}' ({patchKindsText}; {orderText}).";
            likelyOutcome = $"These mods patch the same method and their patch order is not clearly stable. {outcomeHint}";
            recommendation = $"Prefer explicit Harmony ordering for this method, then validate the current stack in game before splitting mods. {validationHint}";
        }
        else if (ownershipRisk)
        {
            reason = $"Harmony evidence shows overlapping patch ownership on '{target}' ({patchKindsText}).";
            likelyOutcome = $"These mods both try to control the same method path. {outcomeHint}";
            recommendation = $"Treat one mod as the primary owner for this method path or ship a compatibility patch. {validationHint}";
        }
        else
        {
            reason = $"Harmony evidence shows shared method patches on '{target}' ({patchKindsText}).";
            likelyOutcome = $"These mods patch the same method. Validate the affected gameplay path before treating this as a hard conflict. {outcomeHint}";
            recommendation = $"Validate the current stack first and only add explicit Harmony ordering if the affected gameplay path actually changes. {validationHint}";
        }

        return new HarmonyRiskAssessment(category, severity, confidence, reason, likelyOutcome, recommendation);
    }

    internal static HarmonyRiskProfile Merge(IReadOnlyList<HarmonyRiskProfile> profiles)
    {
        if (profiles.Count == 0)
        {
            throw new ArgumentException("At least one Harmony profile is required.", nameof(profiles));
        }

        string targetMethod = profiles
            .Select(p => p.TargetMethod)
            .FirstOrDefault(target => !string.IsNullOrWhiteSpace(target))
            ?? "<unknown>";
        List<string> patchKinds = profiles
            .SelectMany(p => p.PatchKinds)
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)
            .ToList();
        HarmonyOrderState orderState = MergeOrderState(profiles);
        HarmonyOwnershipShape ownershipShape = MergeOwnershipShape(profiles);
        HarmonyTargetFamily targetFamily = profiles
            .Select(p => p.TargetFamily)
            .FirstOrDefault(family => family != HarmonyTargetFamily.Unknown);

        return new HarmonyRiskProfile(
            targetMethod,
            patchKinds,
            DeterminePatchShape(patchKinds),
            orderState,
            ownershipShape,
            targetFamily == default ? ClassifyTargetFamily(targetMethod) : targetFamily,
            profiles.Any(p => p.HasStaticMetadataEvidence),
            profiles.Any(p => p.HasDuplicateScannerEvidence),
            profiles.Any(p => p.HasOrderingGraphEvidence),
            profiles.Any(p => p.HasRuntimeCorrelationEvidence),
            profiles.Max(p => p.ModuleCount),
            profiles.Max(p => p.UnorderedPairs),
            profiles.Max(p => p.AmbiguousPairs),
            profiles.Max(p => p.ExplicitEdgeCount),
            profiles.Any(p => p.HasOrderingCycle) || orderState == HarmonyOrderState.Cycle
        );
    }

    internal static HarmonyRiskProfile Parse(ConflictFinding finding)
    {
        string targetMethod = finding.Evidence
            .FirstOrDefault(e => e.StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase))?
            [TargetPrefix.Length..]
            .Trim()
            ?? "<unknown>";

        List<string> patchKinds = ParsePatchKinds(finding.Evidence);
        HarmonyPatchShape patchShape = TryParsePatchShape(finding.Evidence)
            ?? DeterminePatchShape(patchKinds);
        HarmonyOrderState orderState = TryParseOrderState(finding.Evidence)
            ?? DeriveOrderStateFromGraphEvidence(finding.Evidence);
        HarmonyOwnershipShape ownershipShape = TryParseOwnershipShape(finding.Evidence)
            ?? HarmonyOwnershipShape.Unknown;
        HarmonyTargetFamily targetFamily = TryParseTargetFamily(finding.Evidence)
            ?? ClassifyTargetFamily(targetMethod);
        int moduleCount = ParseIntProfileValue(finding.Evidence, "module-count")
            ?? finding.ModuleIds.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int unorderedPairs = ParseIntProfileValue(finding.Evidence, "unordered-pairs")
            ?? TryParseGraphMetric(finding.Evidence, "unordered")
            ?? 0;
        int ambiguousPairs = ParseIntProfileValue(finding.Evidence, "ambiguous-pairs")
            ?? TryParseGraphMetric(finding.Evidence, "ambiguous")
            ?? 0;
        int explicitEdges = ParseIntProfileValue(finding.Evidence, "explicit-edges")
            ?? TryParseGraphMetric(finding.Evidence, "edges")
            ?? 0;
        bool hasCycle = ParseBoolProfileValue(finding.Evidence, "cycle")
            ?? ((TryParseGraphMetric(finding.Evidence, "cycle") ?? 0) > 0)
            || orderState == HarmonyOrderState.Cycle;

        return new HarmonyRiskProfile(
            targetMethod,
            patchKinds,
            patchShape,
            hasCycle ? HarmonyOrderState.Cycle : orderState,
            ownershipShape,
            targetFamily,
            finding.Evidence.Any(e => e.Equals($"{SourcePrefix}static", StringComparison.OrdinalIgnoreCase)),
            finding.Evidence.Any(e => e.Equals($"{SourcePrefix}duplicate-block", StringComparison.OrdinalIgnoreCase)),
            finding.Evidence.Any(e => e.Equals($"{SourcePrefix}log-graph", StringComparison.OrdinalIgnoreCase))
                || finding.Evidence.Any(e => e.StartsWith(GraphPrefix, StringComparison.OrdinalIgnoreCase)),
            HasRuntimeCorrelationEvidence(finding),
            moduleCount,
            unorderedPairs,
            ambiguousPairs,
            explicitEdges,
            hasCycle
        );
    }
    internal static IReadOnlyList<string> BuildEvidenceTags(HarmonyRiskProfile profile)
    {
        List<string> evidence =
        [
            $"{ProfilePrefix}patch-shape={ToToken(profile.PatchShape)}",
            $"{ProfilePrefix}order-state={ToToken(profile.OrderState)}",
            $"{ProfilePrefix}ownership-shape={ToToken(profile.OwnershipShape)}",
            $"{ProfilePrefix}target-family={ToToken(profile.TargetFamily)}",
            $"{ProfilePrefix}module-count={profile.ModuleCount}",
            $"{ProfilePrefix}unordered-pairs={profile.UnorderedPairs}",
            $"{ProfilePrefix}ambiguous-pairs={profile.AmbiguousPairs}",
            $"{ProfilePrefix}explicit-edges={profile.ExplicitEdgeCount}",
            $"{ProfilePrefix}cycle={(profile.HasOrderingCycle ? 1 : 0)}",
        ];

        if (profile.PatchShape == HarmonyPatchShape.PostfixOnly)
        {
            evidence.Add($"{ProfilePrefix}postfix-only");
        }

        if (profile.HasStaticMetadataEvidence)
        {
            evidence.Add($"{SourcePrefix}static");
        }

        if (profile.HasDuplicateScannerEvidence)
        {
            evidence.Add($"{SourcePrefix}duplicate-block");
        }

        if (profile.HasOrderingGraphEvidence)
        {
            evidence.Add($"{SourcePrefix}log-graph");
        }

        return evidence;
    }

    internal static FindingEvidenceDescriptor BuildStructuredEvidence(HarmonyRiskProfile profile)
    {
        List<FindingEvidenceSource> sources = [];
        if (profile.HasStaticMetadataEvidence)
        {
            sources.Add(FindingEvidenceSource.StaticMetadata);
        }

        if (profile.HasDuplicateScannerEvidence)
        {
            sources.Add(FindingEvidenceSource.DuplicateScanner);
        }

        if (profile.HasOrderingGraphEvidence)
        {
            sources.Add(FindingEvidenceSource.HarmonyGraph);
        }

        if (profile.HasRuntimeCorrelationEvidence)
        {
            sources.Add(FindingEvidenceSource.RuntimeLog);
        }

        List<FindingEvidenceKind> kinds = [];
        if (profile.PatchShape == HarmonyPatchShape.PostfixOnly)
        {
            kinds.Add(FindingEvidenceKind.Advisory);
        }

        if (profile.OrderState is HarmonyOrderState.SamePriorityAmbiguous or HarmonyOrderState.Unordered or HarmonyOrderState.Cycle)
        {
            kinds.Add(FindingEvidenceKind.OrderAmbiguity);
        }

        if (profile.PatchShape is HarmonyPatchShape.TranspilerPresent or HarmonyPatchShape.PrefixMix
            || profile.OwnershipShape == HarmonyOwnershipShape.SingleKindDuplicated)
        {
            kinds.Add(FindingEvidenceKind.OwnershipClash);
        }

        return EvidenceProfiles.Create(
            FindingEvidenceScope.Method,
            sources,
            kinds,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["target-method"] = profile.TargetMethod,
                ["patch-shape"] = ToToken(profile.PatchShape),
                ["order-state"] = ToToken(profile.OrderState),
                ["ownership-shape"] = ToToken(profile.OwnershipShape),
                ["target-family"] = ToToken(profile.TargetFamily),
                ["module-count"] = profile.ModuleCount.ToString(),
            });
    }

    internal static bool ShouldSuppressLowValue(HarmonyRiskProfile profile)
    {
        return profile.ModuleCount == 2
            && profile.PatchShape == HarmonyPatchShape.PostfixOnly
            && profile.OrderState == HarmonyOrderState.ExplicitlyOrdered
            && profile.HasStaticMetadataEvidence
            && !profile.HasDuplicateScannerEvidence
            && !profile.HasOrderingGraphEvidence
            && !profile.HasRuntimeCorrelationEvidence
            && profile.TargetFamily == HarmonyTargetFamily.Unknown;
    }

    internal static HarmonyOwnershipShape DetermineOwnershipShape(
        IEnumerable<(string ModuleId, string PatchKind)> patchOwnership
    )
    {
        List<(string ModuleId, string PatchKind)> normalized = patchOwnership
            .Where(p => !string.IsNullOrWhiteSpace(p.ModuleId) && !string.IsNullOrWhiteSpace(p.PatchKind))
            .Select(p => (p.ModuleId.Trim(), NormalizePatchKind(p.PatchKind)))
            .Where(p => p.Item2.Length > 0)
            .ToList();
        if (normalized.Count == 0)
        {
            return HarmonyOwnershipShape.Unknown;
        }

        bool duplicateKindAcrossModules = normalized
            .GroupBy(p => p.PatchKind, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Select(item => item.ModuleId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
        if (duplicateKindAcrossModules)
        {
            return HarmonyOwnershipShape.SingleKindDuplicated;
        }

        bool singleModuleRepeated = normalized
            .GroupBy(p => p.ModuleId, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1);
        if (singleModuleRepeated)
        {
            return HarmonyOwnershipShape.SingleModuleRepeated;
        }

        return normalized
            .Select(p => p.PatchKind)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() > 1
            ? HarmonyOwnershipShape.StackedMixedKinds
            : HarmonyOwnershipShape.MultiModule;
    }

    internal static HarmonyTargetFamily ClassifyTargetFamily(string? targetMethod)
    {
        if (string.IsNullOrWhiteSpace(targetMethod))
        {
            return HarmonyTargetFamily.Unknown;
        }

        string normalized = targetMethod.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

        if (normalized.Contains("canmainheroenter")
            || normalized.Contains("canenter")
            || normalized.Contains("accessmodel")
            || normalized.Contains("canuse")
            || normalized.Contains("canapply"))
        {
            return HarmonyTargetFamily.AccessGate;
        }

        if (normalized.Contains("mission")
            || normalized.Contains("battle")
            || normalized.Contains("missionview")
            || normalized.Contains("missionlogic")
            || normalized.Contains("onmission"))
        {
            return HarmonyTargetFamily.MissionStartup;
        }

        if (normalized.Contains("defaultsettlement")
            || normalized.Contains("settlementfood")
            || normalized.Contains("settlementprosperity")
            || normalized.Contains("settlementsecurity")
            || normalized.Contains("hearth")
            || normalized.Contains("prosperity")
            || normalized.Contains("security"))
        {
            return HarmonyTargetFamily.SettlementCampaignRule;
        }

        if (normalized.Contains("calculate")
            || normalized.Contains("consumption")
            || normalized.Contains("food")
            || normalized.Contains("economy")
            || normalized.Contains("wage")
            || normalized.Contains("price")
            || normalized.Contains("influence"))
        {
            return HarmonyTargetFamily.EconomicCalculation;
        }

        return HarmonyTargetFamily.Unknown;
    }

    internal static string BuildValidationHint(HarmonyTargetFamily targetFamily) => targetFamily switch
    {
        HarmonyTargetFamily.SettlementCampaignRule =>
            "Check the affected settlement or campaign system over a few in-game ticks or days.",
        HarmonyTargetFamily.EconomicCalculation =>
            "Check the affected calculation in a normal campaign flow and compare the result you actually get.",
        HarmonyTargetFamily.MissionStartup =>
            "Run one short battle or mission and compare startup behavior with the current stack unchanged.",
        HarmonyTargetFamily.AccessGate =>
            "Check the entry or decision path this method controls and confirm the result still matches what you expect.",
        _ =>
            "Test the gameplay path that reaches this method before changing the stack.",
    };

    internal static string BuildOutcomeHint(HarmonyTargetFamily targetFamily) => targetFamily switch
    {
        HarmonyTargetFamily.SettlementCampaignRule =>
            "Campaign progression behavior may differ on the affected settlement or rule path.",
        HarmonyTargetFamily.EconomicCalculation =>
            "Final calculation results may differ.",
        HarmonyTargetFamily.MissionStartup =>
            "Battle or mission behavior may differ.",
        HarmonyTargetFamily.AccessGate =>
            "Entry conditions or decision results may differ.",
        _ =>
            "Gameplay behavior may differ on this method path.",
    };

    private static ConflictSeverity ComputeSeverity(HarmonyRiskProfile profile)
    {
        if (profile.PatchShape == HarmonyPatchShape.PostfixOnly)
        {
            if (profile.HasOrderingCycle)
            {
                return profile.HasRuntimeCorrelationEvidence
                    ? ConflictSeverity.High
                    : ConflictSeverity.Medium;
            }

            if (profile.OrderState is HarmonyOrderState.SamePriorityAmbiguous or HarmonyOrderState.Unordered)
            {
                return profile.HasRuntimeCorrelationEvidence
                    || profile.AmbiguousPairs > 1
                    || profile.UnorderedPairs > 1
                    ? ConflictSeverity.Medium
                    : ConflictSeverity.Low;
            }

            return profile.HasRuntimeCorrelationEvidence
                ? ConflictSeverity.Medium
                : ConflictSeverity.Low;
        }

        if (profile.PatchShape == HarmonyPatchShape.TranspilerPresent)
        {
            if (profile.HasOrderingCycle
                || profile.HasRuntimeCorrelationEvidence
                || profile.OrderState is HarmonyOrderState.SamePriorityAmbiguous or HarmonyOrderState.Unordered)
            {
                return ConflictSeverity.Critical;
            }

            return profile.OwnershipShape == HarmonyOwnershipShape.SingleKindDuplicated
                ? ConflictSeverity.High
                : ConflictSeverity.High;
        }

        if (profile.HasOrderingCycle)
        {
            return profile.PatchShape == HarmonyPatchShape.PostfixOnly && !profile.HasRuntimeCorrelationEvidence
                ? ConflictSeverity.Medium
                : ConflictSeverity.Critical;
        }

        if (profile.PatchShape == HarmonyPatchShape.PrefixMix)
        {
            if (profile.OrderState is HarmonyOrderState.SamePriorityAmbiguous or HarmonyOrderState.Unordered)
            {
                return ConflictSeverity.High;
            }

            if (profile.OwnershipShape == HarmonyOwnershipShape.SingleKindDuplicated)
            {
                return profile.OrderState == HarmonyOrderState.ExplicitlyOrdered
                    ? ConflictSeverity.Medium
                    : ConflictSeverity.High;
            }

            return ConflictSeverity.Medium;
        }

        if (profile.PatchShape == HarmonyPatchShape.FinalizerOnly)
        {
            return profile.OrderState is HarmonyOrderState.SamePriorityAmbiguous or HarmonyOrderState.Unordered
                ? ConflictSeverity.Medium
                : ConflictSeverity.Low;
        }

        return profile.OrderState is HarmonyOrderState.SamePriorityAmbiguous or HarmonyOrderState.Unordered
            ? ConflictSeverity.High
            : profile.OwnershipShape == HarmonyOwnershipShape.SingleKindDuplicated
                && profile.OrderState != HarmonyOrderState.ExplicitlyOrdered
                ? ConflictSeverity.High
                : ConflictSeverity.Medium;
    }

    private static double ComputeConfidence(HarmonyRiskProfile profile)
    {
        double confidence = 0.56;
        if (profile.HasStaticMetadataEvidence)
        {
            confidence += 0.06;
        }

        if (profile.HasDuplicateScannerEvidence)
        {
            confidence += 0.14;
        }

        if (profile.HasOrderingGraphEvidence)
        {
            confidence += 0.12;
        }

        if (profile.ExplicitEdgeCount > 0)
        {
            confidence += 0.04;
        }

        if (profile.OrderState == HarmonyOrderState.ExplicitlyOrdered)
        {
            confidence -= 0.03;
        }

        if (profile.OrderState == HarmonyOrderState.Unordered)
        {
            confidence += 0.03;
        }

        if (profile.OrderState == HarmonyOrderState.SamePriorityAmbiguous)
        {
            confidence += 0.05;
        }

        if (profile.HasOrderingCycle)
        {
            confidence += 0.06;
        }

        if (profile.HasRuntimeCorrelationEvidence)
        {
            confidence += 0.08;
        }

        if (profile.PatchShape == HarmonyPatchShape.PostfixOnly && !profile.HasRuntimeCorrelationEvidence)
        {
            confidence -= 0.02;
        }

        if (profile.ModuleCount > 2)
        {
            confidence += 0.02;
        }

        return Math.Clamp(confidence, 0.58, 0.97);
    }
    private static HarmonyPatchShape DeterminePatchShape(IReadOnlyCollection<string> patchKinds)
    {
        if (patchKinds.Count == 0)
        {
            return HarmonyPatchShape.Unknown;
        }

        if (patchKinds.All(kind => kind.Equals("Postfix", StringComparison.OrdinalIgnoreCase)))
        {
            return HarmonyPatchShape.PostfixOnly;
        }

        if (patchKinds.All(kind => kind.Equals("Finalizer", StringComparison.OrdinalIgnoreCase)))
        {
            return HarmonyPatchShape.FinalizerOnly;
        }

        if (patchKinds.Any(kind => kind.Equals("Transpiler", StringComparison.OrdinalIgnoreCase)))
        {
            return HarmonyPatchShape.TranspilerPresent;
        }

        if (patchKinds.Any(kind => kind.Equals("Prefix", StringComparison.OrdinalIgnoreCase)))
        {
            return HarmonyPatchShape.PrefixMix;
        }

        return HarmonyPatchShape.Mixed;
    }

    private static string NormalizePatchKind(string? patchKind)
    {
        if (string.IsNullOrWhiteSpace(patchKind))
        {
            return string.Empty;
        }

        return patchKind.Trim().ToLowerInvariant() switch
        {
            "prefix" => "Prefix",
            "postfix" => "Postfix",
            "transpiler" => "Transpiler",
            "finalizer" => "Finalizer",
            _ => patchKind.Trim(),
        };
    }

    private static List<string> ParsePatchKinds(IReadOnlyList<string> evidence)
    {
        string? raw = evidence.FirstOrDefault(e => e.StartsWith(KindsPrefix, StringComparison.OrdinalIgnoreCase));
        if (raw is null)
        {
            return [];
        }

        return raw[KindsPrefix.Length..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizePatchKind)
            .Where(kind => kind.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static HarmonyPatchShape? TryParsePatchShape(IReadOnlyList<string> evidence)
    {
        string? value = GetProfileValue(evidence, "patch-shape");
        if (value is null && evidence.Any(e => e.Equals($"{ProfilePrefix}postfix-only", StringComparison.OrdinalIgnoreCase)))
        {
            return HarmonyPatchShape.PostfixOnly;
        }

        return value?.ToLowerInvariant() switch
        {
            "postfix-only" => HarmonyPatchShape.PostfixOnly,
            "prefix-mix" => HarmonyPatchShape.PrefixMix,
            "transpiler-present" => HarmonyPatchShape.TranspilerPresent,
            "finalizer-only" => HarmonyPatchShape.FinalizerOnly,
            "mixed" => HarmonyPatchShape.Mixed,
            _ => null,
        };
    }

    private static HarmonyOrderState? TryParseOrderState(IReadOnlyList<string> evidence)
    {
        return GetProfileValue(evidence, "order-state")?.ToLowerInvariant() switch
        {
            "explicitly-ordered" => HarmonyOrderState.ExplicitlyOrdered,
            "same-priority-ambiguous" => HarmonyOrderState.SamePriorityAmbiguous,
            "unordered" => HarmonyOrderState.Unordered,
            "cycle" => HarmonyOrderState.Cycle,
            _ => null,
        };
    }

    private static HarmonyOwnershipShape? TryParseOwnershipShape(IReadOnlyList<string> evidence)
    {
        return GetProfileValue(evidence, "ownership-shape")?.ToLowerInvariant() switch
        {
            "single-kind-duplicated" => HarmonyOwnershipShape.SingleKindDuplicated,
            "stacked-mixed-kinds" => HarmonyOwnershipShape.StackedMixedKinds,
            "single-module-repeated" => HarmonyOwnershipShape.SingleModuleRepeated,
            "multi-module" => HarmonyOwnershipShape.MultiModule,
            _ => null,
        };
    }

    private static HarmonyTargetFamily? TryParseTargetFamily(IReadOnlyList<string> evidence)
    {
        return GetProfileValue(evidence, "target-family")?.ToLowerInvariant() switch
        {
            "economic-calculation" => HarmonyTargetFamily.EconomicCalculation,
            "settlement-campaign-rule" => HarmonyTargetFamily.SettlementCampaignRule,
            "mission-startup" => HarmonyTargetFamily.MissionStartup,
            "access-gate" => HarmonyTargetFamily.AccessGate,
            _ => null,
        };
    }

    private static int? ParseIntProfileValue(IReadOnlyList<string> evidence, string key)
    {
        string? raw = GetProfileValue(evidence, key);
        return int.TryParse(raw, out int value)
            ? value
            : null;
    }

    private static bool? ParseBoolProfileValue(IReadOnlyList<string> evidence, string key)
    {
        string? raw = GetProfileValue(evidence, key);
        if (raw is null)
        {
            return null;
        }

        return raw switch
        {
            "1" => true,
            "0" => false,
            _ => bool.TryParse(raw, out bool value) ? value : null,
        };
    }

    private static string? GetProfileValue(IReadOnlyList<string> evidence, string key)
    {
        string prefix = $"{ProfilePrefix}{key}=";
        return evidence.FirstOrDefault(e => e.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?
            [prefix.Length..]
            .Trim();
    }

    private static int? TryParseGraphMetric(IReadOnlyList<string> evidence, string key)
    {
        string? raw = evidence.FirstOrDefault(e => e.StartsWith(GraphPrefix, StringComparison.OrdinalIgnoreCase));
        if (raw is null)
        {
            return null;
        }

        foreach (string token in raw[GraphPrefix.Length..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separatorIndex = token.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            string metricKey = token[..separatorIndex].Trim();
            if (!metricKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string metricValue = token[(separatorIndex + 1)..].Trim();
            return int.TryParse(metricValue, out int value)
                ? value
                : null;
        }

        return null;
    }

    private static HarmonyOrderState DeriveOrderStateFromGraphEvidence(IReadOnlyList<string> evidence)
    {
        int cycle = TryParseGraphMetric(evidence, "cycle") ?? 0;
        if (cycle > 0)
        {
            return HarmonyOrderState.Cycle;
        }

        int ambiguous = TryParseGraphMetric(evidence, "ambiguous") ?? 0;
        if (ambiguous > 0)
        {
            return HarmonyOrderState.SamePriorityAmbiguous;
        }

        int unordered = TryParseGraphMetric(evidence, "unordered") ?? 0;
        if (unordered > 0)
        {
            return HarmonyOrderState.Unordered;
        }

        int edges = TryParseGraphMetric(evidence, "edges") ?? 0;
        return edges > 0
            ? HarmonyOrderState.ExplicitlyOrdered
            : HarmonyOrderState.Unknown;
    }

    private static HarmonyOrderState MergeOrderState(IEnumerable<HarmonyRiskProfile> profiles)
    {
        if (profiles.Any(p => p.OrderState == HarmonyOrderState.Cycle || p.HasOrderingCycle))
        {
            return HarmonyOrderState.Cycle;
        }

        if (profiles.Any(p => p.OrderState == HarmonyOrderState.SamePriorityAmbiguous))
        {
            return HarmonyOrderState.SamePriorityAmbiguous;
        }

        if (profiles.Any(p => p.OrderState == HarmonyOrderState.Unordered))
        {
            return HarmonyOrderState.Unordered;
        }

        if (profiles.Any(p => p.OrderState == HarmonyOrderState.ExplicitlyOrdered))
        {
            return HarmonyOrderState.ExplicitlyOrdered;
        }

        return HarmonyOrderState.Unknown;
    }

    private static HarmonyOwnershipShape MergeOwnershipShape(IEnumerable<HarmonyRiskProfile> profiles)
    {
        if (profiles.Any(p => p.OwnershipShape == HarmonyOwnershipShape.SingleKindDuplicated))
        {
            return HarmonyOwnershipShape.SingleKindDuplicated;
        }

        if (profiles.Any(p => p.OwnershipShape == HarmonyOwnershipShape.StackedMixedKinds))
        {
            return HarmonyOwnershipShape.StackedMixedKinds;
        }

        if (profiles.Any(p => p.OwnershipShape == HarmonyOwnershipShape.SingleModuleRepeated))
        {
            return HarmonyOwnershipShape.SingleModuleRepeated;
        }

        if (profiles.Any(p => p.OwnershipShape == HarmonyOwnershipShape.MultiModule))
        {
            return HarmonyOwnershipShape.MultiModule;
        }

        return HarmonyOwnershipShape.Unknown;
    }
    private static string ToToken(HarmonyPatchShape value) => value switch
    {
        HarmonyPatchShape.PostfixOnly => "postfix-only",
        HarmonyPatchShape.PrefixMix => "prefix-mix",
        HarmonyPatchShape.TranspilerPresent => "transpiler-present",
        HarmonyPatchShape.FinalizerOnly => "finalizer-only",
        HarmonyPatchShape.Mixed => "mixed",
        _ => "unknown",
    };

    private static string ToToken(HarmonyOrderState value) => value switch
    {
        HarmonyOrderState.ExplicitlyOrdered => "explicitly-ordered",
        HarmonyOrderState.SamePriorityAmbiguous => "same-priority-ambiguous",
        HarmonyOrderState.Unordered => "unordered",
        HarmonyOrderState.Cycle => "cycle",
        _ => "unknown",
    };

    private static string ToToken(HarmonyOwnershipShape value) => value switch
    {
        HarmonyOwnershipShape.SingleKindDuplicated => "single-kind-duplicated",
        HarmonyOwnershipShape.StackedMixedKinds => "stacked-mixed-kinds",
        HarmonyOwnershipShape.SingleModuleRepeated => "single-module-repeated",
        HarmonyOwnershipShape.MultiModule => "multi-module",
        _ => "unknown",
    };

    private static string ToToken(HarmonyTargetFamily value) => value switch
    {
        HarmonyTargetFamily.EconomicCalculation => "economic-calculation",
        HarmonyTargetFamily.SettlementCampaignRule => "settlement-campaign-rule",
        HarmonyTargetFamily.MissionStartup => "mission-startup",
        HarmonyTargetFamily.AccessGate => "access-gate",
        _ => "unknown",
    };

    private static bool HasRuntimeCorrelationEvidence(ConflictFinding finding)
    {
        return finding.Evidence.Any(e =>
            e.StartsWith("runtime-correlation:", StringComparison.OrdinalIgnoreCase)
            || e.Contains(@"\Mount and Blade II Bannerlord\logs\launcher_log_", StringComparison.OrdinalIgnoreCase)
            || e.Contains(@"\Mount and Blade II Bannerlord\logs\watchdog_log_", StringComparison.OrdinalIgnoreCase)
            || e.Contains(@"\Mount and Blade II Bannerlord\logs\rgl_log_", StringComparison.OrdinalIgnoreCase));
    }
}
