namespace BannerlordModCompat.Core;

public sealed class RuntimeEvidenceCorrelator
{
    private static readonly HashSet<ConflictCategory> RuntimeSignalCategories =
    [
        ConflictCategory.RuntimeModuleSetMismatch,
        ConflictCategory.RuntimeLoaderFailure,
        ConflictCategory.RuntimeCrashSession,
    ];

    private static readonly HashSet<ConflictCategory> CorrelatableCategories =
    [
        ConflictCategory.HarmonyPatchConflict,
        ConflictCategory.HarmonyPatchStack,
        ConflictCategory.BehaviorEventOverlap,
        ConflictCategory.GameModelOverlap,
        ConflictCategory.MissionBehaviorOverlap,
        ConflictCategory.LifecycleRegistrationOverlap,
        ConflictCategory.LoadOrderViolation,
        ConflictCategory.MissingDependency,
        ConflictCategory.AssemblyReferenceMismatch,
    ];

    private static readonly HashSet<ConflictCategory> SeverityEscalationCategories =
    [
        ConflictCategory.HarmonyPatchConflict,
        ConflictCategory.HarmonyPatchStack,
        ConflictCategory.BehaviorEventOverlap,
        ConflictCategory.GameModelOverlap,
        ConflictCategory.MissionBehaviorOverlap,
        ConflictCategory.LifecycleRegistrationOverlap,
        ConflictCategory.LoadOrderViolation,
        ConflictCategory.MissingDependency,
        ConflictCategory.AssemblyReferenceMismatch,
    ];

    public IReadOnlyList<ConflictFinding> Correlate(
        IReadOnlyList<ConflictFinding> findings,
        List<string> warnings
    )
    {
        if (findings.Count == 0)
        {
            return [];
        }

        List<int> runtimeIndices = [];
        for (int i = 0; i < findings.Count; i++)
        {
            if (IsRuntimeSignalCategory(findings[i].Category))
            {
                runtimeIndices.Add(i);
            }
        }

        if (runtimeIndices.Count == 0)
        {
            return findings.ToList();
        }

        Dictionary<int, ConflictFinding> promotedByIndex = [];
        Dictionary<int, int> correlationCountByRuntimeIndex = [];
        int promotedCount = 0;
        int evidenceAppendCount = 0;
        for (int i = 0; i < findings.Count; i++)
        {
            ConflictFinding finding = findings[i];
            if (IsRuntimeSignalCategory(finding.Category) || !CorrelatableCategories.Contains(finding.Category))
            {
                continue;
            }

            CorrelationAggregate aggregate = BuildAggregate(finding, findings, runtimeIndices);
            if (!aggregate.HasMatches)
            {
                continue;
            }

            foreach (int runtimeIndex in aggregate.MatchedRuntimeIndices)
            {
                correlationCountByRuntimeIndex[runtimeIndex] = correlationCountByRuntimeIndex.TryGetValue(runtimeIndex, out int count)
                    ? count + 1
                    : 1;
            }

            IReadOnlyList<string> mergedEvidence = MergeEvidence(finding.Evidence, aggregate.RuntimeEvidence);
            evidenceAppendCount += Math.Max(0, mergedEvidence.Count - finding.Evidence.Count);
            promotedByIndex[i] = new ConflictFinding
            {
                Category = finding.Category,
                Severity = PromoteSeverity(finding.Category, finding.Severity, aggregate),
                Confidence = Math.Clamp(finding.Confidence + aggregate.ConfidenceBoost, finding.Confidence, 0.99),
                ModuleIds = finding.ModuleIds,
                Reason = AppendRuntimeReason(finding.Reason, aggregate),
                LikelyInGameOutcome = AppendRuntimeOutcome(finding.LikelyInGameOutcome),
                Recommendation = AppendRuntimeRecommendation(finding.Recommendation),
                Evidence = mergedEvidence,
            };
            promotedCount++;
        }

        if (promotedCount == 0)
        {
            return findings.ToList();
        }

        List<ConflictFinding> result = [];
        int suppressedRuntimeSignals = 0;
        for (int i = 0; i < findings.Count; i++)
        {
            if (promotedByIndex.TryGetValue(i, out ConflictFinding? promoted))
            {
                result.Add(promoted);
                continue;
            }

            ConflictFinding finding = findings[i];
            if (IsRuntimeSignalCategory(finding.Category)
                && correlationCountByRuntimeIndex.TryGetValue(i, out int correlatedCount)
                && ShouldSuppressRuntimeSignal(finding, correlatedCount))
            {
                suppressedRuntimeSignals++;
                continue;
            }

            result.Add(finding);
        }

        warnings.Add(
            $"Runtime evidence correlation promoted {promotedCount} structural finding(s), added {evidenceAppendCount} runtime evidence item(s), suppressed {suppressedRuntimeSignals} redundant runtime signal finding(s).");
        return result;
    }

    private static CorrelationAggregate BuildAggregate(
        ConflictFinding finding,
        IReadOnlyList<ConflictFinding> allFindings,
        IReadOnlyList<int> runtimeIndices
    )
    {
        HashSet<string> findingModules = finding.ModuleIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (findingModules.Count == 0)
        {
            return CorrelationAggregate.None;
        }

        bool hasLoaderSignal = false;
        bool hasCrashSignal = false;
        bool hasHarmonyRuntimeProof = HasHarmonyRuntimeProof(finding.Evidence);
        bool hasRecurringRuntimeSignal = false;
        int maxOverlap = 0;
        double confidenceBoost = 0.0;
        HashSet<int> matchedRuntimeIndices = [];
        HashSet<string> runtimeEvidence = new(StringComparer.OrdinalIgnoreCase);
        List<string> signalLabels = [];

        foreach (int runtimeIndex in runtimeIndices)
        {
            ConflictFinding runtimeSignal = allFindings[runtimeIndex];
            int overlap = CountOverlap(findingModules, runtimeSignal.ModuleIds);
            if (!MeetsCorrelationThreshold(finding, runtimeSignal, overlap, findingModules.Count))
            {
                continue;
            }

            matchedRuntimeIndices.Add(runtimeIndex);
            maxOverlap = Math.Max(maxOverlap, overlap);
            double overlapFactor = Math.Clamp(overlap / (double)Math.Max(1, findingModules.Count), 0.35, 1.0);
            confidenceBoost += BaseConfidenceBoost(runtimeSignal.Category) * overlapFactor;
            signalLabels.Add(SignalLabel(runtimeSignal.Category));
            if (runtimeSignal.Category == ConflictCategory.RuntimeLoaderFailure)
            {
                hasLoaderSignal = true;
            }

            if (runtimeSignal.Category == ConflictCategory.RuntimeCrashSession)
            {
                hasCrashSignal = true;
            }
            if (HasRecurringClusterEvidence(runtimeSignal.Evidence))
            {
                hasRecurringRuntimeSignal = true;
            }

            runtimeEvidence.Add($"runtime-correlation:{runtimeSignal.Category}:module-overlap={overlap}/{findingModules.Count}");
            foreach (string item in runtimeSignal.Evidence)
            {
                if (LooksLikeRuntimePath(item))
                {
                    runtimeEvidence.Add(item);
                    hasHarmonyRuntimeProof |= IsHarmonyRuntimeEvidenceItem(item);
                }
                else if (item.StartsWith("issue:", StringComparison.OrdinalIgnoreCase))
                {
                    runtimeEvidence.Add(item);
                    hasHarmonyRuntimeProof |= IsHarmonyRuntimeEvidenceItem(item);
                }
            }
        }

        if (matchedRuntimeIndices.Count == 0)
        {
            return CorrelationAggregate.None;
        }

        string signalSummary = string.Join(", ", signalLabels.Distinct(StringComparer.OrdinalIgnoreCase));
        return new CorrelationAggregate(
            HasMatches: true,
            HasLoaderSignal: hasLoaderSignal,
            HasCrashSignal: hasCrashSignal,
            HasHarmonyRuntimeProof: hasHarmonyRuntimeProof,
            HasRecurringRuntimeSignal: hasRecurringRuntimeSignal,
            MaxOverlap: maxOverlap,
            ConfidenceBoost: confidenceBoost,
            SignalSummary: signalSummary,
            MatchedRuntimeIndices: matchedRuntimeIndices.ToList(),
            RuntimeEvidence: runtimeEvidence.ToList()
        );
    }

    private static bool MeetsCorrelationThreshold(
        ConflictFinding finding,
        ConflictFinding runtimeSignal,
        int overlap,
        int findingModuleCount
    )
    {
        if (overlap <= 0)
        {
            return false;
        }

        int runtimeModuleCount = runtimeSignal.ModuleIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (finding.Category is ConflictCategory.HarmonyPatchConflict or ConflictCategory.HarmonyPatchStack)
        {
            return runtimeSignal.Category switch
            {
                ConflictCategory.RuntimeLoaderFailure =>
                    overlap >= 2 && runtimeModuleCount <= 8,
                ConflictCategory.RuntimeCrashSession =>
                    overlap >= 2 && runtimeModuleCount <= 10,
                _ => false,
            };
        }

        return runtimeSignal.Category switch
        {
            ConflictCategory.RuntimeModuleSetMismatch =>
                finding.Category is ConflictCategory.LoadOrderViolation or ConflictCategory.MissingDependency
                && overlap >= Math.Min(2, Math.Max(1, findingModuleCount)),
            ConflictCategory.RuntimeLoaderFailure =>
                overlap >= 1 && (runtimeModuleCount <= 10 || overlap >= 2),
            ConflictCategory.RuntimeCrashSession =>
                overlap >= 2 || (runtimeModuleCount <= 8 && overlap >= 1),
            _ => false,
        };
    }

    private static IReadOnlyList<string> MergeEvidence(
        IReadOnlyList<string> baseEvidence,
        IReadOnlyList<string> runtimeEvidence
    )
    {
        HashSet<string> merged = new(StringComparer.OrdinalIgnoreCase);
        List<string> ordered = [];
        foreach (string item in baseEvidence)
        {
            if (merged.Add(item))
            {
                ordered.Add(item);
            }
        }

        foreach (string item in runtimeEvidence)
        {
            if (merged.Add(item))
            {
                ordered.Add(item);
            }
        }

        return ordered.Take(18).ToList();
    }

    private static ConflictSeverity PromoteSeverity(
        ConflictCategory category,
        ConflictSeverity current,
        CorrelationAggregate aggregate
    )
    {
        if (!SeverityEscalationCategories.Contains(category))
        {
            return current;
        }

        if (category == ConflictCategory.LifecycleRegistrationOverlap)
        {
            if (!aggregate.HasRecurringRuntimeSignal)
            {
                return current;
            }

            bool shouldBumpLifecycle = (aggregate.HasLoaderSignal || aggregate.HasCrashSignal)
                && aggregate.MaxOverlap >= 2;
            return shouldBumpLifecycle ? NextHigher(current) : current;
        }

        if (category is ConflictCategory.HarmonyPatchConflict or ConflictCategory.HarmonyPatchStack)
        {
            if (!aggregate.HasHarmonyRuntimeProof)
            {
                return current;
            }

            bool shouldBumpHarmony = (aggregate.HasLoaderSignal || aggregate.HasCrashSignal)
                && aggregate.MaxOverlap >= 2;
            return shouldBumpHarmony ? NextHigher(current) : current;
        }

        bool shouldBump = aggregate.HasLoaderSignal
            || (aggregate.HasCrashSignal && aggregate.MaxOverlap >= 2);
        return shouldBump ? NextHigher(current) : current;
    }

    private static ConflictSeverity NextHigher(ConflictSeverity severity)
    {
        return severity switch
        {
            ConflictSeverity.Info => ConflictSeverity.Low,
            ConflictSeverity.Low => ConflictSeverity.Medium,
            ConflictSeverity.Medium => ConflictSeverity.High,
            ConflictSeverity.High => ConflictSeverity.Critical,
            _ => ConflictSeverity.Critical,
        };
    }

    private static bool ShouldSuppressRuntimeSignal(ConflictFinding runtimeSignal, int correlatedCount)
    {
        return runtimeSignal.Category switch
        {
            ConflictCategory.RuntimeModuleSetMismatch => correlatedCount >= 1,
            ConflictCategory.RuntimeLoaderFailure => correlatedCount >= 1,
            ConflictCategory.RuntimeCrashSession => correlatedCount >= 2,
            _ => false,
        };
    }

    private static string AppendRuntimeReason(string reason, CorrelationAggregate aggregate)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return $"Runtime evidence correlation: {aggregate.SignalSummary}.";
        }

        if (reason.Contains("Runtime evidence correlation", StringComparison.OrdinalIgnoreCase))
        {
            return reason;
        }

        return $"{reason} Runtime evidence correlation: {aggregate.SignalSummary}.";
    }

    private static string AppendRuntimeOutcome(string? outcome)
    {
        const string suffix = "Runtime logs reported instability signals in an overlapping module set (correlation, not direct causation).";
        if (string.IsNullOrWhiteSpace(outcome))
        {
            return suffix;
        }

        if (outcome.Contains("correlation, not direct causation", StringComparison.OrdinalIgnoreCase))
        {
            return outcome;
        }

        return $"{outcome.Trim()} {suffix}";
    }

    private static string AppendRuntimeRecommendation(string? recommendation)
    {
        const string suffix = "Validate this interaction in the same runtime module set captured by logs after each change.";
        if (string.IsNullOrWhiteSpace(recommendation))
        {
            return suffix;
        }

        if (recommendation.Contains("same runtime module set captured by logs", StringComparison.OrdinalIgnoreCase))
        {
            return recommendation;
        }

        return $"{recommendation.Trim()} {suffix}";
    }

    private static string SignalLabel(ConflictCategory category)
    {
        return category switch
        {
            ConflictCategory.RuntimeModuleSetMismatch => "runtime module-set divergence",
            ConflictCategory.RuntimeLoaderFailure => "runtime loader/runtime failures",
            ConflictCategory.RuntimeCrashSession => "runtime crash telemetry",
            _ => category.ToString(),
        };
    }

    private static bool LooksLikeRuntimePath(string value)
    {
        return value.Contains(@"\Mount and Blade II Bannerlord\logs\", StringComparison.OrdinalIgnoreCase)
            && (value.Contains("launcher_log_", StringComparison.OrdinalIgnoreCase)
                || value.Contains("watchdog_log_", StringComparison.OrdinalIgnoreCase)
                || value.Contains("rgl_log_", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("crashlist.txt", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasRecurringClusterEvidence(IReadOnlyList<string> evidence)
    {
        foreach (string item in evidence)
        {
            if (!item.StartsWith("cluster-session-count:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string raw = item["cluster-session-count:".Length..].Trim();
            if (int.TryParse(raw, out int count) && count >= 2)
            {
                return true;
            }
        }

        return evidence.Any(e => e.StartsWith("cluster-signature:", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasHarmonyRuntimeProof(IReadOnlyList<string> evidence)
    {
        return evidence.Any(IsHarmonyRuntimeEvidenceItem);
    }

    private static bool IsHarmonyRuntimeEvidenceItem(string item)
    {
        return item.EndsWith("AllHarmonyPatches.txt", StringComparison.OrdinalIgnoreCase)
            || item.EndsWith("DuplicateHarmonyPatches.txt", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith("harmony-target:", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith("harmony-graph:", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith("harmony-priority:", StringComparison.OrdinalIgnoreCase)
            || item.Contains("harmony", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOverlap(IReadOnlySet<string> left, IReadOnlyList<string> right)
    {
        int overlap = 0;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string id in right)
        {
            if (!seen.Add(id))
            {
                continue;
            }

            if (left.Contains(id))
            {
                overlap++;
            }
        }

        return overlap;
    }

    private static bool IsRuntimeSignalCategory(ConflictCategory category)
        => RuntimeSignalCategories.Contains(category);

    private static double BaseConfidenceBoost(ConflictCategory category)
    {
        return category switch
        {
            ConflictCategory.RuntimeModuleSetMismatch => 0.06,
            ConflictCategory.RuntimeLoaderFailure => 0.12,
            ConflictCategory.RuntimeCrashSession => 0.09,
            _ => 0.0,
        };
    }

    private sealed record CorrelationAggregate(
        bool HasMatches,
        bool HasLoaderSignal,
        bool HasCrashSignal,
        bool HasHarmonyRuntimeProof,
        bool HasRecurringRuntimeSignal,
        int MaxOverlap,
        double ConfidenceBoost,
        string SignalSummary,
        IReadOnlyList<int> MatchedRuntimeIndices,
        IReadOnlyList<string> RuntimeEvidence
    )
    {
        public static CorrelationAggregate None { get; } = new(
            false,
            false,
            false,
            false,
            false,
            0,
            0.0,
            string.Empty,
            [],
            []
        );
    }
}
