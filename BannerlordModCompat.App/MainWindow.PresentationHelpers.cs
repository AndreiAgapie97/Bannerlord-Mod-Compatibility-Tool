using System.IO;
using BannerlordModCompat.Core;

namespace BannerlordModCompat.App;

public partial class MainWindow
{
    private static string BuildTopRecommendation(ScanReport report)
    {
        ConflictFinding? critical = report.Conflicts
            .FirstOrDefault(c => c.Severity == ConflictSeverity.Critical && !string.IsNullOrWhiteSpace(c.Recommendation));
        if (critical is not null)
        {
            return critical.Recommendation!;
        }

        ConflictFinding? high = report.Conflicts
            .FirstOrDefault(c => c.Severity == ConflictSeverity.High && !string.IsNullOrWhiteSpace(c.Recommendation));
        if (high is not null)
        {
            return high.Recommendation!;
        }

        int playerVisibleMoves = PlayerLoadOrderProjectionBuilder
            .BuildEnabledSingleplayerProjection(report.LoadOrder)
            .Recommendation
            .Moves
            .Count;
        if (playerVisibleMoves > 0)
        {
            return $"Apply suggested load order ({playerVisibleMoves} enabled move(s)) before next campaign test.";
        }

        return "No high-priority actions detected. Keep this scan as baseline and retest after mod updates.";
    }

    private static (string Summary, string Breakdown) BuildConfidenceSummary(ScanReport report)
    {
        int total = report.Conflicts.Count;
        int deterministicOrRuntime = report.Conflicts.Count(f =>
            BuildEvidenceStrength(f) is "Seen In Logs" or "Direct Mod Scan");
        int runtimeConfirmed = report.Conflicts.Count(IsRuntimeConfirmed);
        int heuristic = Math.Max(0, total - deterministicOrRuntime);

        double deterministicRatio = total == 0 ? 1.0 : deterministicOrRuntime / (double)total;
        double runtimeRatio = total == 0 ? 1.0 : runtimeConfirmed / (double)total;
        double confidence = Math.Clamp(
            0.40 + (0.40 * deterministicRatio) + (0.20 * runtimeRatio),
            0.25,
            0.98
        );

        string summary = $"Confidence {confidence:P0}. Deterministic/runtime-backed findings: {deterministicOrRuntime}. Heuristic findings: {heuristic}.";
        string breakdown = runtimeConfirmed > 0
            ? $"Runtime-confirmed findings: {runtimeConfirmed}. Load-order planner confidence: {report.LoadOrder.Confidence:P0}."
            : $"No runtime-confirmed findings in this scan. Use Collect Runtime Evidence to raise confidence.";

        return (summary, breakdown);
    }

    private static List<string> BuildActionPlan(ScanReport report, int criticalCount, int highCount, int mediumCount)
    {
        List<string> steps = [];

        if (criticalCount > 0)
        {
            steps.Add($"Resolve {criticalCount} critical issue(s) first. These can block launch or break saves.");
        }
        else if (highCount > 0)
        {
            steps.Add($"Address {highCount} high-severity issue(s) before long campaign sessions.");
        }
        else if (mediumCount > 0)
        {
            steps.Add($"Review {mediumCount} medium findings for potential balance or behavior drift.");
        }
        else
        {
            steps.Add("No major issues detected. Your current set appears stable.");
        }

        int playerVisibleMoves = PlayerLoadOrderProjectionBuilder
            .BuildEnabledSingleplayerProjection(report.LoadOrder)
            .Recommendation
            .Moves
            .Count;
        if (playerVisibleMoves > 0)
        {
            steps.Add($"Apply suggested load order ({playerVisibleMoves} enabled move(s)).");
        }

        foreach (ConflictFinding finding in report.Conflicts
                     .Where(f => !string.IsNullOrWhiteSpace(f.Recommendation))
                     .Take(4))
        {
            steps.Add(finding.Recommendation!);
        }

        if (report.SaveFiles.Count > 0)
        {
            HashSet<string> currentOrder = report.LoadOrder.CurrentOrder
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int riskySaves = report.SaveFiles.Count(save =>
                save.ReferencedInstalledMods.Any(id => !currentOrder.Contains(id)));
            if (riskySaves > 0)
            {
                steps.Add($"Save risk warnings found for {riskySaves} save(s). Keep backups before loading with changed mod sets.");
            }
            else
            {
                steps.Add($"Scanned {report.SaveFiles.Count} save(s) with no immediate save mismatch warnings.");
            }
        }

        if (steps.Count == 0)
        {
            steps.Add("Run a scan to generate actionable steps.");
        }

        return steps.Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
    }

    private static string ToDisplayState(CompatibilityState state) => state switch
    {
        CompatibilityState.Compatible => "Looks Stable",
        CompatibilityState.LikelyIssue => "Needs Validation",
        CompatibilityState.Incompatible => "High Risk Profile",
        _ => state.ToString(),
    };

    private static string ToDisplayCategory(ConflictCategory category) => category switch
    {
        ConflictCategory.MissingDependency => "Missing Dependency",
        ConflictCategory.ExplicitIncompatibility => "Explicit Incompatibility",
        ConflictCategory.DependencyVersionMismatch => "Dependency Version Mismatch",
        ConflictCategory.LoadOrderViolation => "Load Order Violation",
        ConflictCategory.HarmonyPatchConflict => "Shared Method Patch",
        ConflictCategory.HarmonyPatchStack => "Shared Method Patch",
        ConflictCategory.BehaviorEventOverlap => "Shared Campaign Hook",
        ConflictCategory.GameModelOverlap => "Shared Gameplay Model",
        ConflictCategory.MissionBehaviorOverlap => "Shared Mission Logic",
        ConflictCategory.LifecycleRegistrationOverlap => "Shared Registration Phase",
        ConflictCategory.DllCollision => "DLL Collision",
        ConflictCategory.XmlEntityCollision => "XML Data Overlap",
        ConflictCategory.ModuleDataFileCollision => "ModuleData File Collision",
        ConflictCategory.AssemblyReferenceMismatch => "Game API Reference Mismatch",
        ConflictCategory.SaveFileRisk => "Save Uses Missing Mods",
        ConflictCategory.AnalyzerWarning => "Limited Scan Visibility",
        ConflictCategory.RuntimeModuleSetMismatch => "Runtime Profile Differs",
        ConflictCategory.RuntimeLoaderFailure => "Load Error Seen In Logs",
        ConflictCategory.RuntimeCrashSession => "Crash Seen In Logs",
        _ => category.ToString(),
    };

    private static string BuildPlayerHeadline(ConflictFinding finding) => finding.Category switch
    {
        ConflictCategory.RuntimeCrashSession => IsRecurringRuntimeCluster(finding)
            ? "Several recorded sessions crashed with similar mod sets"
            : "A recorded session crashed with this mod set active",
        ConflictCategory.RuntimeLoaderFailure => BuildRuntimeLoaderHeadline(finding),
        ConflictCategory.RuntimeModuleSetMismatch => "Game launched with a different mod set",
        ConflictCategory.SaveFileRisk => GetSaveRiskEntries(finding).Count == 1
            ? "This save was created with mods now missing"
            : "These saves were created with mods now missing",
        ConflictCategory.HarmonyPatchConflict or ConflictCategory.HarmonyPatchStack => BuildHarmonyPlayerHeadline(finding),
        ConflictCategory.LoadOrderViolation => "Current load order breaks a dependency rule",
        ConflictCategory.GameModelOverlap => "Several mods change the same gameplay calculation",
        ConflictCategory.BehaviorEventOverlap => "Several mods react to the same campaign events",
        ConflictCategory.MissionBehaviorOverlap => "Several mods change mission or battle startup logic",
        ConflictCategory.AssemblyReferenceMismatch => "Mods target different game API versions",
        ConflictCategory.ExplicitIncompatibility => "The mod author marked this pair as incompatible",
        ConflictCategory.AnalyzerWarning => "The scan could not fully verify this mod's patching",
        _ => ToDisplayCategory(finding.Category),
    };

    private static string BuildImpactSummary(ConflictFinding finding) => finding.Category switch
    {
        _ when IsRecurringRuntimeCluster(finding) =>
            "The same runtime failure signature keeps repeating across sessions. This is a real stability pattern, not a one-off guess.",
        ConflictCategory.HarmonyPatchConflict or ConflictCategory.HarmonyPatchStack =>
            BuildHarmonyImpactSummary(finding),
        ConflictCategory.LoadOrderViolation => "The current module order breaks a declared dependency or precedence rule.",
        ConflictCategory.MissingDependency => "A required mod is missing.",
        ConflictCategory.ExplicitIncompatibility => "Mod author marked this pair as incompatible.",
        ConflictCategory.DependencyVersionMismatch => "Installed dependency version differs from expected.",
        ConflictCategory.DllCollision => "Same DLL name appears in multiple mods with different binaries.",
        ConflictCategory.XmlEntityCollision => "Multiple mods override the same game data entity.",
        ConflictCategory.ModuleDataFileCollision => "Multiple mods ship conflicting high-impact ModuleData files.",
        ConflictCategory.AssemblyReferenceMismatch => "Mods target different game API versions in referenced assemblies.",
        ConflictCategory.BehaviorEventOverlap => "Several mods react to the same campaign events. Usually this means duplicated or reordered effects, not a guaranteed crash.",
        ConflictCategory.GameModelOverlap => "Several mods change the same gameplay system. One mod can end up dominating the final calculation.",
        ConflictCategory.MissionBehaviorOverlap => "Several mods add mission or battle logic in the same area. This is a weaker warning about behavior order, not a direct crash claim.",
        ConflictCategory.LifecycleRegistrationOverlap => "Several mods register logic in the same startup phase. This is mainly an order-of-registration warning.",
        ConflictCategory.SaveFileRisk => "These saves reference mods that are not active right now. This is about save compatibility, not a live load-order crash by itself.",
        ConflictCategory.AnalyzerWarning => "The analyzer hit dynamic code paths it could not fully resolve. Treat this as incomplete visibility, not a confirmed conflict.",
        ConflictCategory.RuntimeModuleSetMismatch => "The modules that actually ran do not match the current launcher profile, so the live session differed from the planned order.",
        ConflictCategory.RuntimeLoaderFailure => BuildRuntimeLoaderImpactSummary(finding),
        ConflictCategory.RuntimeCrashSession => "Bannerlord logs show a crash for a recent session with this profile. That proves the session crashed, not which one mod caused it.",
        _ => "Potential compatibility risk detected.",
    };

    private static string BuildEvidenceStrength(ConflictFinding finding)
    {
        if (finding.Category == ConflictCategory.RuntimeCrashSession)
        {
            return "Crash marker in logs";
        }

        if (finding.Category == ConflictCategory.RuntimeLoaderFailure)
        {
            return IsRecurringRuntimeCluster(finding)
                ? "Repeated log evidence"
                : "Concrete log evidence";
        }

        if (finding.Category == ConflictCategory.RuntimeModuleSetMismatch)
        {
            return "Active mod list in logs";
        }

        if (finding.Category == ConflictCategory.SaveFileRisk)
        {
            return "Save scan";
        }

        if (IsRuntimeConfirmed(finding))
        {
            return "Seen In Logs";
        }

        if (finding.Category is ConflictCategory.HarmonyPatchConflict
            or ConflictCategory.HarmonyPatchStack)
        {
            return BuildHarmonyEvidenceStrength(finding);
        }

        if (finding.Category is ConflictCategory.MissingDependency
            or ConflictCategory.ExplicitIncompatibility
            or ConflictCategory.LoadOrderViolation
            or ConflictCategory.AssemblyReferenceMismatch)
        {
            return "Direct Mod Scan";
        }

        if (finding.Category is ConflictCategory.GameModelOverlap
            or ConflictCategory.BehaviorEventOverlap
            or ConflictCategory.MissionBehaviorOverlap
            or ConflictCategory.LifecycleRegistrationOverlap)
        {
            return "Likely From Mod Scan";
        }

        return finding.Evidence.Count > 0 ? "Save/Metadata Scan" : "Weak Analyzer Guess";
    }

    private static string BuildImpactRisk(ConflictFinding finding) => finding.Category switch
    {
        ConflictCategory.MissingDependency => "Can Block Startup",
        ConflictCategory.ExplicitIncompatibility => "Can Break Startup Or Saves",
        ConflictCategory.LoadOrderViolation => "Can Change Startup Order",
        ConflictCategory.HarmonyPatchConflict or ConflictCategory.HarmonyPatchStack =>
            BuildHarmonyImpactRiskLabel(finding),
        ConflictCategory.AssemblyReferenceMismatch => "Can Crash On Load",
        ConflictCategory.SaveFileRisk => GetSaveRiskEntries(finding).Count == 1 ? "Can Break This Save" : "Can Break These Saves",
        ConflictCategory.GameModelOverlap => "Can Change Calculations",
        ConflictCategory.BehaviorEventOverlap => "Can Change Campaign Behavior",
        ConflictCategory.MissionBehaviorOverlap => "Can Change Mission Behavior",
        ConflictCategory.LifecycleRegistrationOverlap => "Startup Order Matters",
        ConflictCategory.AnalyzerWarning => "Scan Could Not Prove It",
        ConflictCategory.RuntimeModuleSetMismatch => "Actual Session Differed",
        ConflictCategory.RuntimeLoaderFailure => BuildRuntimeLoaderRiskLabel(finding),
        ConflictCategory.RuntimeCrashSession => IsRecurringRuntimeCluster(finding) ? "Repeated Recorded Crashes" : "Recorded Crash Session",
        _ => finding.Severity switch
        {
            ConflictSeverity.Critical => "High Impact",
            ConflictSeverity.High => "Likely Important",
            ConflictSeverity.Medium => "Needs Validation",
            _ => "Low Priority",
        },
    };

    private static bool IsRuntimeConfirmed(ConflictFinding finding)
    {
        return finding.Evidence.Any(e =>
            e.EndsWith("AllHarmonyPatches.txt", StringComparison.OrdinalIgnoreCase)
            || e.EndsWith("DuplicateHarmonyPatches.txt", StringComparison.OrdinalIgnoreCase)
            || e.Contains(@"\Mount and Blade II Bannerlord\logs\launcher_log_", StringComparison.OrdinalIgnoreCase)
            || e.Contains(@"\Mount and Blade II Bannerlord\logs\watchdog_log_", StringComparison.OrdinalIgnoreCase)
            || e.Contains(@"\Mount and Blade II Bannerlord\logs\rgl_log_", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasHarmonyRuntimeProof(ConflictFinding finding)
    {
        return finding.Evidence.Any(e =>
            e.EndsWith("AllHarmonyPatches.txt", StringComparison.OrdinalIgnoreCase)
            || e.EndsWith("DuplicateHarmonyPatches.txt", StringComparison.OrdinalIgnoreCase)
            || e.StartsWith("harmony-target:", StringComparison.OrdinalIgnoreCase)
            || e.StartsWith("harmony-graph:", StringComparison.OrdinalIgnoreCase)
            || e.StartsWith("harmony-priority:", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasHarmonyRuntimeCrashEvidence(ConflictFinding finding)
    {
        if (!HasHarmonyRuntimeProof(finding))
        {
            return false;
        }

        return finding.Evidence.Any(e =>
            e.StartsWith("runtime-correlation:RuntimeCrashSession", StringComparison.OrdinalIgnoreCase)
            || e.Contains(@"\Mount and Blade II Bannerlord\logs\rgl_log_", StringComparison.OrdinalIgnoreCase)
            || e.EndsWith("crashlist.txt", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildMeaningText(ConflictFinding finding)
    {
        if (finding.Category == ConflictCategory.SaveFileRisk)
        {
            string saveImpact = BuildImpactSummary(finding);
            string saveSummary = BuildSaveRiskSaveSummary(finding, limit: 8);
            return $"{saveImpact} Exact affected saves: {saveSummary}.";
        }

        string summary = BuildImpactSummary(finding);
        string moduleFocus = finding.ModuleIds.Count == 0
            ? "module set"
            : string.Join(", ", finding.ModuleIds.Take(3)) + (finding.ModuleIds.Count > 3 ? "..." : string.Empty);
        int? clusterSessions = GetRuntimeClusterSessionCount(finding);
        List<string> runtimeFiles = finding.Evidence
            .Where(IsRuntimeForensicsPath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList()!;
        string certainty = finding.Category is ConflictCategory.HarmonyPatchConflict or ConflictCategory.HarmonyPatchStack
            ? BuildHarmonyMeaningCertainty(finding)
            : IsRuntimeConfirmed(finding)
            ? "This was observed in Bannerlord logs."
            : "This is an analyzer warning, not direct proof from logs.";
        string sourceFiles = runtimeFiles.Count == 0
            ? string.Empty
            : $" Source files: {string.Join(", ", runtimeFiles)}.";

        return clusterSessions is null
            ? $"{summary} Focus mods: {moduleFocus}. {certainty}{sourceFiles}"
            : $"{summary} Focus mods: {moduleFocus}. Seen across {clusterSessions.Value} recent session(s). {certainty}{sourceFiles}";
    }

    private static string BuildConfidenceInterpretation(ConflictFinding finding)
    {
        string evidenceStrength = BuildEvidenceStrength(finding);
        if (finding.Category is ConflictCategory.HarmonyPatchConflict or ConflictCategory.HarmonyPatchStack)
        {
            string profileText = IsAdvisoryHarmonyStack(finding)
                ? "This currently looks like a stack-to-validate warning."
                : HasHarmonyOrderingRisk(finding)
                    ? "This currently looks like an ordering-stability warning."
                    : HasHarmonyOwnershipRisk(finding)
                        ? "This currently looks like a shared patch-ownership warning."
                        : "This currently looks like a structural overlap warning.";
            return $"How sure the app is: {finding.Confidence:P0}. {profileText} The signal comes from {evidenceStrength.ToLowerInvariant()}, not from a guaranteed crash claim.";
        }

        if (IsRuntimeConfirmed(finding))
        {
            return $"How sure the app is: {finding.Confidence:P0}. This warning is backed by Bannerlord logs ({evidenceStrength}). Confidence is about the warning being real, not your exact chance to crash.";
        }

        if (finding.Confidence >= 0.90
            && evidenceStrength is "Direct Mod Scan" or "Likely From Mod Scan")
        {
            return $"How sure the app is: {finding.Confidence:P0}. The app found a strong signal in mod files ({evidenceStrength}). That means the overlap is probably real, not that a crash is guaranteed.";
        }

        if (finding.Confidence >= 0.70)
        {
            return $"How sure the app is: {finding.Confidence:P0}. This is a plausible warning from the scan ({evidenceStrength}), but it still needs a short in-game validation before you treat it as proven.";
        }

        return $"How sure the app is: {finding.Confidence:P0}. This is a low-certainty scan signal ({evidenceStrength}). Treat it as watchlist data until logs or a clean repro confirm it.";
    }

    private static string BuildCriticalDrawerReason(ConflictFinding finding)
    {
        if (finding.Category == ConflictCategory.RuntimeCrashSession)
        {
            return IsRecurringRuntimeCluster(finding)
                ? "Similar crash markers repeat across sessions with overlapping mod sets; this still does not identify the guilty mod by itself."
                : "A recorded session crashed with this mod set active; this row is forensic context, not proof of the guilty mod.";
        }

        if (finding.Category == ConflictCategory.RuntimeLoaderFailure)
        {
            return IsRecurringRuntimeCluster(finding)
                ? $"The same logged issue keeps repeating: {GetRuntimeIssueSummary(finding)}."
                : $"{GetRuntimeIssueSummary(finding)} was seen in Bannerlord logs for this profile.";
        }

        if (finding.Category == ConflictCategory.RuntimeModuleSetMismatch)
        {
            return "Runtime-active modules differ from launcher profile; actual execution chain may diverge.";
        }

        if (finding.Category is ConflictCategory.HarmonyPatchConflict or ConflictCategory.HarmonyPatchStack)
        {
            string? target = TryExtractHarmonyTarget(finding);
            if (IsAdvisoryHarmonyStack(finding))
            {
                if (!string.IsNullOrWhiteSpace(target))
                {
                    return $"Shared target: {ToShortSentence(target, 70)}. This looks like a stack to validate first, not a likely hard conflict.";
                }

                return "Shared Harmony target looks more like a stack to validate first than a likely hard conflict.";
            }

            if (HasHarmonyOrderingRisk(finding))
            {
                if (!string.IsNullOrWhiteSpace(target))
                {
                    return $"Shared target: {ToShortSentence(target, 70)}. Patch order is not clearly stable on this method.";
                }

                return "Patch order is not clearly stable on this shared Harmony target.";
            }

            if (HasHarmonyOwnershipRisk(finding))
            {
                if (!string.IsNullOrWhiteSpace(target))
                {
                    return $"Shared target: {ToShortSentence(target, 70)}. More than one mod appears to be trying to control the same method path.";
                }

                return "More than one mod appears to be trying to control the same Harmony method path.";
            }

            if (!string.IsNullOrWhiteSpace(target))
            {
                return $"Shared Harmony target: {ToShortSentence(target, 70)}. Validate the gameplay path this method affects.";
            }

            return "Multiple patches target the same method family. Validate the gameplay path this method affects.";
        }

        if (finding.Category is ConflictCategory.LifecycleRegistrationOverlap
            or ConflictCategory.GameModelOverlap
            or ConflictCategory.BehaviorEventOverlap
            or ConflictCategory.MissionBehaviorOverlap)
        {
            return IsRuntimeConfirmed(finding)
                ? "Runtime logs corroborate overlapping registration/model chains between these modules."
                : "Overlapping behavior/model registrations can produce non-deterministic handler precedence.";
        }

        if (finding.Category == ConflictCategory.LoadOrderViolation)
        {
            return "Current load order violates dependency/precedence expectations for these modules.";
        }

        if (finding.Category == ConflictCategory.SaveFileRisk)
        {
            return $"Affected saves: {BuildSaveRiskSaveSummary(finding, 6)}. This warning is about missing mods referenced by those saves, not the current slot order by itself.";
        }

        if (finding.Category == ConflictCategory.MissingDependency)
        {
            return "Missing dependency can prevent stable initialization of dependent modules.";
        }

        string confidence = finding.Confidence >= 0.9 ? "very high" : finding.Confidence >= 0.75 ? "high" : "moderate";
        string evidence = BuildEvidenceStrength(finding);
        return $"Ranked by {confidence} confidence and {evidence.ToLowerInvariant()} evidence for a high-impact category.";
    }

    private static string BuildPlayerSymptom(ConflictFinding finding)
    {
        string source = string.IsNullOrWhiteSpace(finding.LikelyInGameOutcome)
            ? BuildFallbackOutcome(finding)
            : finding.LikelyInGameOutcome!;

        return ToShortSentence(source, 140);
    }

    private static string BuildQuickFix(ConflictFinding finding)
    {
        string source = string.IsNullOrWhiteSpace(finding.Recommendation)
            ? BuildFallbackRecommendation(finding)
            : finding.Recommendation!;

        return ToShortSentence(source, 140);
    }

    private static string BuildExecutionChain(
        ConflictFinding finding,
        IReadOnlyDictionary<string, int> currentOrderByModule,
        IReadOnlyDictionary<string, int> suggestedOrderByModule
    )
    {
        List<(string ModuleId, bool HasCurrent, int CurrentIndex, bool HasSuggested, int SuggestedIndex)> orderedModules = finding.ModuleIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(moduleId =>
            {
                bool hasCurrent = currentOrderByModule.TryGetValue(moduleId, out int currentIndex);
                bool hasSuggested = suggestedOrderByModule.TryGetValue(moduleId, out int suggestedIndex);
                return (moduleId, hasCurrent, currentIndex, hasSuggested, suggestedIndex);
            })
            .OrderBy(x => x.hasCurrent ? 0 : x.hasSuggested ? 1 : 2)
            .ThenBy(x => x.hasCurrent ? x.currentIndex : x.hasSuggested ? x.suggestedIndex : int.MaxValue)
            .ThenBy(x => x.moduleId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (orderedModules.Count == 0)
        {
            return "No module chain was attached to this finding.";
        }

        string chain = string.Join(" -> ", orderedModules.Select(x =>
            x.HasCurrent
                ? $"{x.ModuleId}(#{x.CurrentIndex + 1})"
                : x.HasSuggested
                    ? $"{x.ModuleId}(~#{x.SuggestedIndex + 1})"
                    : $"{x.ModuleId}(?)"));

        string effect = finding.Category switch
        {
            ConflictCategory.LoadOrderViolation => "Dependency metadata requires this precedence.",
            ConflictCategory.HarmonyPatchConflict or ConflictCategory.HarmonyPatchStack =>
                BuildHarmonyExecutionEffect(finding),
            ConflictCategory.GameModelOverlap =>
                "Later model providers can become dominant in runtime resolution.",
            ConflictCategory.BehaviorEventOverlap =>
                "Handler registration order can duplicate or reorder event effects.",
            ConflictCategory.MissionBehaviorOverlap =>
                "Mission init handler order can alter AI/UI startup sequence.",
            ConflictCategory.LifecycleRegistrationOverlap =>
                "Lifecycle hook timing and load order can change which registration wins.",
            ConflictCategory.SaveFileRisk =>
                "This is a save provenance warning. These modules were present when the listed saves were created, but they are missing from the current profile.",
            ConflictCategory.RuntimeModuleSetMismatch =>
                "Runtime-active modules diverged from launcher order, so this chain can differ in practice.",
            ConflictCategory.RuntimeCrashSession =>
                "This is the active custom-mod list captured for the logged crash session. It is forensic context, not a proven causal chain.",
            ConflictCategory.RuntimeLoaderFailure =>
                "This is the active custom-mod list captured for the logged failure session. It is forensic context, not proof of the culprit.",
            _ =>
                "Relative module order influences which implementation takes effect when systems overlap.",
        };

        if (finding.Category == ConflictCategory.LifecycleRegistrationOverlap)
        {
            return $"{chain}. {effect} {BuildLifecycleSignalSummary(finding)}";
        }

        bool hasUnknownSlots = orderedModules.Any(x => !x.HasCurrent && !x.HasSuggested);
        return hasUnknownSlots
            ? $"{chain}. {effect} Some modules have unknown slot positions."
            : $"{chain}. {effect}";
    }

    private static string BuildHarmonyExecutionEffect(ConflictFinding finding)
    {
        string? target = TryExtractHarmonyTarget(finding);
        string familyHint = BuildHarmonyFamilyHint(finding);
        List<string> details = [];

        if (!string.IsNullOrWhiteSpace(target))
        {
            details.Add($"target={target}");
        }

        if (HasHarmonyOrderingRisk(finding))
        {
            string? graph = finding.Evidence.FirstOrDefault(e =>
                e.StartsWith("harmony-graph:", StringComparison.OrdinalIgnoreCase));
            if (graph is not null)
            {
                details.Add(graph["harmony-graph:".Length..]);
            }
        }

        string? priority = finding.Evidence.FirstOrDefault(e =>
            e.StartsWith("harmony-priority:", StringComparison.OrdinalIgnoreCase));
        if (priority is not null)
        {
            details.Add(priority["harmony-priority:".Length..]);
        }

        string baseText = HasHarmonyOrderingRisk(finding)
            ? "Patch order is not clearly stable, so final Harmony behavior can shift."
            : HasHarmonyOwnershipRisk(finding)
                ? "More than one mod is trying to control the same method path."
                : "This looks like a Harmony stack to validate before changing the profile.";

        return details.Count == 0
            ? $"{baseText} {familyHint}"
            : $"{baseText} {familyHint} Advanced patch/order context: {string.Join(" | ", details)}.";
    }

    private static string ToShortSentence(string text, int maxLength)
    {
        string normalized = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        int sentenceEnd = normalized.IndexOf(". ", StringComparison.Ordinal);
        if (sentenceEnd > 0 && sentenceEnd + 1 <= maxLength)
        {
            return normalized[..(sentenceEnd + 1)];
        }

        return normalized[..Math.Max(0, maxLength - 1)].TrimEnd() + "...";
    }

    private static string BuildLifecycleSignalSummary(ConflictFinding finding)
    {
        List<(string ModuleId, string Phase, string Action)> signals = [];
        foreach (string evidence in finding.Evidence)
        {
            string prefix = evidence;
            int arrowIndex = evidence.IndexOf("->", StringComparison.Ordinal);
            if (arrowIndex >= 0)
            {
                prefix = evidence[..arrowIndex].Trim();
            }

            string[] parts = prefix.Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            string moduleId = parts[0];
            string phase = parts[1];
            string action = parts[2];
            if (string.IsNullOrWhiteSpace(moduleId) || string.IsNullOrWhiteSpace(phase) || string.IsNullOrWhiteSpace(action))
            {
                continue;
            }

            signals.Add((moduleId, phase, action));
        }

        if (signals.Count == 0)
        {
            return "Lifecycle phase evidence was not extracted from this finding.";
        }

        List<string> phaseSummaries = signals
            .GroupBy(x => x.Phase, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => GetLifecyclePhaseSortOrder(g.Key))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(g =>
            {
                string moduleAction = string.Join(", ", g
                    .Select(x => $"{x.ModuleId}/{x.Action}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3));
                return $"{g.Key}: {moduleAction}";
            })
            .ToList();

        return $"Lifecycle signals: {string.Join(" | ", phaseSummaries)}.";
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

    private static string BuildFallbackOutcome(ConflictFinding finding) => finding.Category switch
    {
        ConflictCategory.HarmonyPatchConflict or ConflictCategory.HarmonyPatchStack => BuildHarmonyFallbackOutcome(finding),
        ConflictCategory.LoadOrderViolation => "Startup issues or subtle behavior changes because modules initialized in the wrong order.",
        ConflictCategory.MissingDependency => "Mod may fail to initialize or disable parts of its functionality.",
        ConflictCategory.ExplicitIncompatibility => "High chance of crashes or severe campaign instability.",
        ConflictCategory.DependencyVersionMismatch => "Subtle bugs, missing features, or intermittent crashes.",
        ConflictCategory.DllCollision => "Type loader conflicts and hard-to-debug runtime failures.",
        ConflictCategory.XmlEntityCollision => "Gameplay balance or progression may differ from intended design.",
        ConflictCategory.ModuleDataFileCollision => "Transform or data ownership order can produce unstable or non-deterministic behavior.",
        ConflictCategory.AssemblyReferenceMismatch => "API mismatches can trigger method/type load failures during play.",
        ConflictCategory.BehaviorEventOverlap => "Campaign events can fire in a different order or produce doubled effects.",
        ConflictCategory.GameModelOverlap => "The final gameplay calculation can come from a different mod than you expect.",
        ConflictCategory.MissionBehaviorOverlap => "Battle or mission behavior can differ between runs even without a hard crash.",
        ConflictCategory.LifecycleRegistrationOverlap => "Startup registration order can change which behavior or model ends up active.",
        ConflictCategory.SaveFileRisk => "Those saves can fail to load cleanly or behave incorrectly because required campaign-changing mods are missing.",
        ConflictCategory.AnalyzerWarning => "The app could not fully see through some dynamic patching, so this is incomplete visibility rather than proof.",
        ConflictCategory.RuntimeModuleSetMismatch => "The game actually ran with a different profile than the launcher shows, so your live behavior may not match the planned order.",
        ConflictCategory.RuntimeLoaderFailure => IsRecurringRuntimeCluster(finding)
            ? "The same loader/runtime fault signature keeps repeating across sessions, indicating a persistent incompatibility pattern."
            : "Launcher/runtime logs show assembly or managed runtime faults that can block or destabilize sessions.",
        ConflictCategory.RuntimeCrashSession => IsRecurringRuntimeCluster(finding)
            ? "Crash/failure signature is repeating across sessions, indicating a persistent interaction fault rather than a one-off run."
            : "Crash telemetry indicates this module profile has already produced a runtime crash.",
        _ => "No direct in-game outcome was inferred for this finding.",
    };

    private static string BuildFallbackRecommendation(ConflictFinding finding) => finding.Category switch
    {
        ConflictCategory.HarmonyPatchConflict or ConflictCategory.HarmonyPatchStack => BuildHarmonyFallbackRecommendation(finding),
        ConflictCategory.LoadOrderViolation => "Follow the suggested load order move for these modules.",
        ConflictCategory.MissingDependency => "Install the dependency or remove the dependent module.",
        ConflictCategory.ExplicitIncompatibility => "Choose one of the two modules for this profile.",
        ConflictCategory.DependencyVersionMismatch => "Align dependency versions with mod author requirements.",
        ConflictCategory.DllCollision => "Keep only one canonical DLL provider or install compatibility patch.",
        ConflictCategory.XmlEntityCollision => "Prioritize one data owner or use explicit compatibility XML patch.",
        ConflictCategory.ModuleDataFileCollision => "Keep one source for the conflicting ModuleData file or merge changes in a compatibility patch.",
        ConflictCategory.AssemblyReferenceMismatch => "Align all mod builds to the same game patch/API baseline.",
        ConflictCategory.BehaviorEventOverlap => "If you notice duplicate campaign effects, test once with one overlapping behavior mod disabled.",
        ConflictCategory.GameModelOverlap => "If the gameplay system feels wrong, pick one primary mod for that system and retest.",
        ConflictCategory.MissionBehaviorOverlap => "Run one short battle test. If the symptom appears, disable one overlapping mission mod and compare.",
        ConflictCategory.LifecycleRegistrationOverlap => "If startup behavior changes between runs, align load order and reduce duplicate registration mods.",
        ConflictCategory.SaveFileRisk => "Use a mod profile matching the save's original module set.",
        ConflictCategory.AnalyzerWarning => "Collect runtime logs and re-scan to improve conflict coverage for dynamic patches.",
        ConflictCategory.RuntimeModuleSetMismatch => "Align launcher module order with the runtime-active module set and re-scan.",
        ConflictCategory.RuntimeLoaderFailure => IsRecurringRuntimeCluster(finding)
            ? "Treat this as recurring: keep one baseline profile and disable modules in cohorts until the repeated signature disappears."
            : "Fix runtime loader errors first, then retest with the same module set.",
        ConflictCategory.RuntimeCrashSession => IsRecurringRuntimeCluster(finding)
            ? "Treat this as recurring: keep one baseline profile and disable modules in cohorts until the repeated signature disappears."
            : "Use crash-session module list as baseline and disable modules in cohorts to isolate the offender.",
        _ => "Review this finding and test with an isolated mod subset.",
    };

    private static IReadOnlyList<string> BuildImmediateSteps(ConflictFinding finding)
    {
        List<string> steps = [];

        if (finding.ModuleIds.Count > 0)
        {
            steps.Add($"Focus module pair/group: {string.Join(", ", finding.ModuleIds.Take(4))}{(finding.ModuleIds.Count > 4 ? " ..." : string.Empty)}");
        }

        if (finding.Category == ConflictCategory.SaveFileRisk)
        {
            steps.Add($"Affected saves: {BuildSaveRiskSaveSummary(finding, 6)}.");
        }

        if (!string.IsNullOrWhiteSpace(finding.Recommendation))
        {
            steps.Add(finding.Recommendation!);
        }
        else
        {
            steps.Add(BuildFallbackRecommendation(finding));
        }

        steps.Add("Run one short campaign/battle validation after applying the fix.");
        if (finding.Category is ConflictCategory.HarmonyPatchConflict or ConflictCategory.HarmonyPatchStack)
        {
            steps.Add(BuildHarmonyValidationStep(finding));
        }

        if (IsRecurringRuntimeCluster(finding))
        {
            int clusterSessions = GetRuntimeClusterSessionCount(finding) ?? 2;
            steps.Add($"Recurring incident cluster seen in {clusterSessions} session(s). Keep load order fixed while isolating module cohorts.");
        }

        return steps.Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
    }

    private static string BuildCopyHeadline(FindingRow row)
    {
        return row.PlayerHeadline;
    }

    private static bool IsRecurringRuntimeCluster(ConflictFinding finding)
    {
        return finding.Evidence.Any(e =>
            e.StartsWith("cluster-signature:", StringComparison.OrdinalIgnoreCase)
            || e.StartsWith("cluster-session-count:", StringComparison.OrdinalIgnoreCase));
    }

    private static int? GetRuntimeClusterSessionCount(ConflictFinding finding)
    {
        string? token = finding.Evidence.FirstOrDefault(e =>
            e.StartsWith("cluster-session-count:", StringComparison.OrdinalIgnoreCase));
        if (token is null)
        {
            return null;
        }

        string raw = token["cluster-session-count:".Length..].Trim();
        return int.TryParse(raw, out int value) && value > 0
            ? value
            : null;
    }

    private static string BuildRuntimeLoaderHeadline(ConflictFinding finding)
    {
        string label = GetRuntimeIssueLabel(finding);
        return IsRecurringRuntimeCluster(finding)
            ? $"The same logged {label} keeps repeating"
            : $"Logs show a {label}";
    }

    private static string BuildRuntimeLoaderImpactSummary(ConflictFinding finding)
    {
        string summary = GetRuntimeIssueSummary(finding);
        return IsRecurringRuntimeCluster(finding)
            ? $"{summary} keeps repeating across runs. This is observed evidence from multiple sessions, but it still does not identify one guilty mod by itself."
            : $"{summary} was seen in Bannerlord logs for this profile. This is observed evidence, but it still does not identify one guilty mod by itself.";
    }

    private static string BuildRuntimeLoaderRiskLabel(ConflictFinding finding)
    {
        return GetRuntimeIssueKindToken(finding) switch
        {
            "missing-method" => IsRecurringRuntimeCluster(finding) ? "Repeated API Mismatch" : "Logged API Mismatch",
            "type-load" => IsRecurringRuntimeCluster(finding) ? "Repeated Type Load Failure" : "Logged Type Load Failure",
            "loader-failure" => IsRecurringRuntimeCluster(finding) ? "Repeated DLL Load Failure" : "Logged DLL Load Failure",
            "null-reference" => IsRecurringRuntimeCluster(finding) ? "Repeated Runtime Exception" : "Logged Runtime Exception",
            "assertion" => IsRecurringRuntimeCluster(finding) ? "Repeated Assertion" : "Logged Assertion",
            "unhandled-exception" => IsRecurringRuntimeCluster(finding) ? "Repeated Unhandled Exception" : "Logged Unhandled Exception",
            _ => IsRecurringRuntimeCluster(finding) ? "Repeated Logged Failure" : "Logged Failure",
        };
    }

    private static string GetRuntimeIssueLabel(ConflictFinding finding)
    {
        return GetRuntimeIssueKindToken(finding) switch
        {
            "missing-method" => "missing-method or API mismatch",
            "type-load" => "type-load failure",
            "loader-failure" => "DLL or assembly load failure",
            "null-reference" => "runtime exception",
            "assertion" => "assertion failure",
            "unhandled-exception" => "unhandled exception",
            _ => "runtime issue",
        };
    }

    private static string GetRuntimeIssueSummary(ConflictFinding finding)
    {
        string? summary = GetStructuredEvidenceDetail(finding, "issue-summary");
        return string.IsNullOrWhiteSpace(summary)
            ? "A concrete runtime issue"
            : summary;
    }

    private static string GetRuntimeIssueKindToken(ConflictFinding finding)
    {
        return GetStructuredEvidenceDetail(finding, "issue-kind") ?? string.Empty;
    }

    private static string? GetStructuredEvidenceDetail(ConflictFinding finding, string key)
    {
        if (finding.StructuredEvidence?.Details is null)
        {
            return null;
        }

        return finding.StructuredEvidence.Details.TryGetValue(key, out string? value)
            ? value
            : null;
    }

    private static IReadOnlyList<string> BuildEvidenceDisplayItems(ConflictFinding finding)
    {
        if (finding.Evidence.Count == 0)
        {
            return ["No direct evidence paths were attached for this finding."];
        }

        List<string> items = [];
        foreach (string evidence in finding.Evidence)
        {
            string? formatted = FormatEvidenceItemForDisplay(finding, evidence);
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                items.Add(formatted);
            }
        }

        return items.Count == 0
            ? finding.Evidence
            : items;
    }

    private static IReadOnlyList<string> BuildAffectedSaveDisplayItems(ConflictFinding finding)
    {
        List<SaveRiskEvidenceEntry> entries = GetSaveRiskEntries(finding);
        if (entries.Count == 0)
        {
            return [];
        }

        return entries
            .Select(entry => $"{entry.DisplayLabel}: missing {string.Join(", ", entry.MissingMods)}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FormatEvidenceItemForDisplay(ConflictFinding finding, string evidence)
    {
        if (finding.Category == ConflictCategory.SaveFileRisk
            && TryParseSaveRiskEntry(evidence) is SaveRiskEvidenceEntry saveEntry)
        {
            return $"{saveEntry.DisplayLabel} missing: {string.Join(", ", saveEntry.MissingMods)}";
        }

        if (evidence.StartsWith("harmony-target:", StringComparison.OrdinalIgnoreCase))
        {
            return $"Target method: {evidence["harmony-target:".Length..]}";
        }

        if (evidence.StartsWith("harmony-kinds:", StringComparison.OrdinalIgnoreCase))
        {
            return $"Patch kinds: {evidence["harmony-kinds:".Length..]}";
        }

        if (evidence.StartsWith("harmony-source:", StringComparison.OrdinalIgnoreCase))
        {
            return $"Harmony source: {ToDisplayHarmonySource(evidence["harmony-source:".Length..])}.";
        }

        if (evidence.StartsWith("harmony-profile:postfix-only", StringComparison.OrdinalIgnoreCase))
        {
            return "Harmony profile: postfix-only stack.";
        }

        if (evidence.StartsWith("harmony-profile:patch-shape=", StringComparison.OrdinalIgnoreCase))
        {
            return $"Harmony patch shape: {ToDisplayHarmonyToken(evidence["harmony-profile:patch-shape=".Length..])}.";
        }

        if (evidence.StartsWith("harmony-profile:order-state=", StringComparison.OrdinalIgnoreCase))
        {
            return $"Harmony order state: {ToDisplayHarmonyToken(evidence["harmony-profile:order-state=".Length..])}.";
        }

        if (evidence.StartsWith("harmony-profile:ownership-shape=", StringComparison.OrdinalIgnoreCase))
        {
            return $"Harmony ownership shape: {ToDisplayHarmonyToken(evidence["harmony-profile:ownership-shape=".Length..])}.";
        }

        if (evidence.StartsWith("harmony-profile:target-family=", StringComparison.OrdinalIgnoreCase))
        {
            return $"Harmony target family: {ToDisplayHarmonyToken(evidence["harmony-profile:target-family=".Length..])}.";
        }

        if (evidence.StartsWith("harmony-graph:", StringComparison.OrdinalIgnoreCase))
        {
            return $"Harmony graph: {evidence["harmony-graph:".Length..].Replace(';', ',')}";
        }

        if (evidence.StartsWith("cluster-session-count:", StringComparison.OrdinalIgnoreCase))
        {
            return $"Recurring session count: {evidence["cluster-session-count:".Length..]}";
        }

        if (evidence.StartsWith("cluster-signature:", StringComparison.OrdinalIgnoreCase))
        {
            return $"Recurring signature: {evidence["cluster-signature:".Length..]}";
        }

        if (evidence.StartsWith("cluster-sessions:", StringComparison.OrdinalIgnoreCase))
        {
            return $"Recurring sessions: {evidence["cluster-sessions:".Length..]}";
        }

        if (evidence.StartsWith("runtime-correlation:", StringComparison.OrdinalIgnoreCase))
        {
            return $"Runtime correlation: {evidence["runtime-correlation:".Length..]}";
        }

        return evidence;
    }

    private static List<SaveRiskEvidenceEntry> GetSaveRiskEntries(ConflictFinding finding)
    {
        return finding.Evidence
            .Select(TryParseSaveRiskEntry)
            .Where(entry => entry is not null)
            .Cast<SaveRiskEvidenceEntry>()
            .ToList();
    }

    private static SaveRiskEvidenceEntry? TryParseSaveRiskEntry(string evidence)
    {
        const string prefix = "save-risk:";
        const string missingDelimiter = "|missing:";
        if (!evidence.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int delimiterIndex = evidence.IndexOf(missingDelimiter, StringComparison.OrdinalIgnoreCase);
        if (delimiterIndex < 0)
        {
            return null;
        }

        string savePath = evidence[prefix.Length..delimiterIndex].Trim();
        string rawMods = evidence[(delimiterIndex + missingDelimiter.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return null;
        }

        string fileName = Path.GetFileName(savePath);
        string displayLabel = string.IsNullOrWhiteSpace(fileName)
            ? savePath
            : fileName;

        List<string> missingMods = rawMods
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SaveRiskEvidenceEntry(savePath, displayLabel, missingMods);
    }

    private static string BuildSaveRiskSaveSummary(ConflictFinding finding, int limit)
    {
        List<SaveRiskEvidenceEntry> entries = GetSaveRiskEntries(finding);
        if (entries.Count == 0)
        {
            return "no parsed save list";
        }

        List<string> labels = entries
            .Select(entry => entry.DisplayLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return labels.Count <= limit
            ? string.Join(", ", labels)
            : string.Join(", ", labels.Take(limit)) + $" (+{labels.Count - limit} more)";
    }

    private enum ValidatePlayerStage
    {
        ChooseTarget,
        TestTogether,
        Compatible,
        Isolating,
    }

    private readonly record struct ValidatePlayerSurfaceState(
        ValidatePlayerStage Stage,
        string BannerText,
        string StageSummary,
        string ProfileHint,
        string CompatibleSummary,
        string RuntimeSectionTitle,
        string RuntimeSectionHint,
        bool ShowUseTopRuntime,
        bool ShowReset,
        bool ShowRuntimeEvidence,
        bool ShowRuntimeEmptyState,
        bool ShowRuntimeActionPanel,
        bool ShowRuntimeWorkbench,
        bool ShowIsolationSection)
    {
        public bool ShowChooseTarget => Stage == ValidatePlayerStage.ChooseTarget;
        public bool ShowTestTogether => Stage == ValidatePlayerStage.TestTogether;
        public bool ShowCompatible => Stage == ValidatePlayerStage.Compatible;
        public bool ShowIsolating => Stage == ValidatePlayerStage.Isolating;
    }

    private ValidatePlayerSurfaceState BuildValidatePlayerSurfaceState(bool validateTabSelected, int playerLoadOrderMoves)
    {
        bool hasScan = _lastReport is not null;
        bool hasRuntimeEvidence = HasRuntimeEvidence();
        bool hasValidationHistory = _isolationStepRows.Count > 0;
        bool hasIsolationSection = ShouldShowValidateIsolationSection();
        bool pendingCompatibility = _isolationPendingStep?.Mode == IsolationStepMode.CompatibilityValidation;
        bool lastOutcomeCompatible = !pendingCompatibility
            && _isolationPendingStep is null
            && _isolationStepRows.LastOrDefault()?.Outcome.Equals("Compatible In Practice", StringComparison.OrdinalIgnoreCase) == true;

        ValidatePlayerStage stage = hasIsolationSection
            ? ValidatePlayerStage.Isolating
            : pendingCompatibility
                ? ValidatePlayerStage.TestTogether
                : lastOutcomeCompatible
                    ? ValidatePlayerStage.Compatible
                    : ValidatePlayerStage.ChooseTarget;

        string profileHint = !hasScan
            ? "Run a scan first, then try the current stack in game."
            : playerLoadOrderMoves == 0
                ? "Your current order looks fine enough to test in game. Pick one warning and try the stack as-is first."
                : "Apply the order changes first, then try the current stack in game.";

        string stageSummary = stage switch
        {
            ValidatePlayerStage.TestTogether => "Try one real gameplay check before changing the stack.",
            ValidatePlayerStage.Compatible => "This stack looked fine in game for the test you ran.",
            ValidatePlayerStage.Isolating => "The problem showed up. Now split the suspect mods into small groups.",
            _ => "Try the current stack in game first.",
        };

        string compatibleSummary = string.IsNullOrWhiteSpace(_lastValidationGoalText) || _lastValidationGoalText == "-"
            ? "This stack looked fine in game for the test you ran."
            : $"This stack looked fine in game for the test you ran: {_lastValidationGoalText}";

        string runtimeSectionTitle = "Game Runs";
        string runtimeSectionHint = hasRuntimeEvidence
            ? $"{_allRuntimeSessionRows.Count} run(s) and {_allRuntimeChainRows.Count} log warning(s) are ready. Open this only if you need help from logs."
            : "No runtime evidence yet. Play once, then open this section only if you need logs.";

        bool showRuntimeEvidence = _playerValidateRuntimeEvidenceExpanded;
        bool showRuntimeEmptyState = showRuntimeEvidence && !hasRuntimeEvidence;
        bool showRuntimeWorkbench = showRuntimeEvidence && hasRuntimeEvidence;
        bool showRuntimeActionPanel = showRuntimeEvidence && hasRuntimeEvidence;

        return new ValidatePlayerSurfaceState(
            stage,
            BannerText: "Validate In Game",
            StageSummary: stageSummary,
            ProfileHint: profileHint,
            CompatibleSummary: compatibleSummary,
            RuntimeSectionTitle: runtimeSectionTitle,
            RuntimeSectionHint: runtimeSectionHint,
            ShowUseTopRuntime: hasRuntimeEvidence,
            ShowReset: hasValidationHistory,
            ShowRuntimeEvidence: showRuntimeEvidence,
            ShowRuntimeEmptyState: showRuntimeEmptyState,
            ShowRuntimeActionPanel: showRuntimeActionPanel,
            ShowRuntimeWorkbench: showRuntimeWorkbench,
            ShowIsolationSection: validateTabSelected && hasIsolationSection);
    }

    private static string BuildHarmonyPlayerHeadline(ConflictFinding finding)
    {
        if (IsAdvisoryHarmonyStack(finding))
        {
            return "These mods patch the same method, but this looks like a stack to validate first";
        }

        if (HasHarmonyOrderingRisk(finding))
        {
            return "These mods patch the same method and their patch order is not clearly stable";
        }

        if (HasHarmonyOwnershipRisk(finding))
        {
            return "These mods both try to control the same method path";
        }

        return "These mods patch the same game method";
    }

    private static string BuildHarmonyImpactSummary(ConflictFinding finding)
    {
        string familyOutcome = BuildHarmonyFamilyOutcome(finding);
        if (IsAdvisoryHarmonyStack(finding))
        {
            return $"These mods patch the same method, but the current signal looks like a stack to validate, not a likely hard conflict. {familyOutcome}";
        }

        if (HasHarmonyOrderingRisk(finding))
        {
            return $"These mods patch the same method and their patch order is not clearly stable. {familyOutcome}";
        }

        if (HasHarmonyOwnershipRisk(finding))
        {
            return $"These mods both try to control the same method path. {familyOutcome}";
        }

        return $"These mods patch the same method. Validate the affected gameplay path before treating this as a hard conflict. {familyOutcome}";
    }

    private static string BuildHarmonyEvidenceStrength(ConflictFinding finding)
    {
        bool hasStatic = HasHarmonySource(finding, "static");
        bool hasDuplicate = HasHarmonySource(finding, "duplicate-block");
        bool hasGraph = HasHarmonySource(finding, "log-graph");
        bool hasRuntime = HasHarmonyRuntimeCorrelationEvidence(finding);

        return (hasStatic, hasDuplicate, hasGraph, hasRuntime) switch
        {
            (_, _, _, true) when hasGraph || hasDuplicate => "Harmony logs + runtime logs",
            (_, _, _, true) => "Mod scan + runtime logs",
            (_, true, true, false) => "Harmony duplicate scan + graph log",
            (true, false, true, false) => "Mod scan + Harmony graph log",
            (_, true, false, false) => "Harmony duplicate scan",
            (_, false, true, false) => "Harmony graph log",
            (true, false, false, false) => "Direct Mod Scan",
            _ => "Likely From Mod Scan",
        };
    }

    private static string BuildHarmonyImpactRiskLabel(ConflictFinding finding)
    {
        if (IsAdvisoryHarmonyStack(finding))
        {
            return "Validate Stack";
        }

        if (HasHarmonyOrderingRisk(finding))
        {
            return "Order May Not Be Stable";
        }

        if (HasHarmonyOwnershipRisk(finding))
        {
            return "Two Mods Control Same Path";
        }

        return "Needs Validation";
    }

    private static string BuildHarmonyFallbackOutcome(ConflictFinding finding)
    {
        string familyOutcome = BuildHarmonyFamilyOutcome(finding);
        if (IsAdvisoryHarmonyStack(finding))
        {
            return $"This looks like a stack to validate, not a likely hard conflict. {familyOutcome}";
        }

        if (HasHarmonyOrderingRisk(finding))
        {
            return $"Patch order is not clearly stable. {familyOutcome}";
        }

        if (HasHarmonyOwnershipRisk(finding))
        {
            return $"More than one mod appears to control the same method path. {familyOutcome}";
        }

        return familyOutcome;
    }

    private static string BuildHarmonyFallbackRecommendation(ConflictFinding finding)
    {
        string testHint = BuildHarmonyValidationStep(finding);
        if (IsAdvisoryHarmonyStack(finding))
        {
            return $"Keep the stack together and validate the affected gameplay path first. {testHint}";
        }

        if (HasHarmonyOrderingRisk(finding))
        {
            return $"Add explicit Harmony ordering only if the issue reproduces. {testHint}";
        }

        if (HasHarmonyOwnershipRisk(finding))
        {
            return $"Treat one mod as the primary owner for this method path or ship a compatibility patch. {testHint}";
        }

        return testHint;
    }

    private static string BuildHarmonyMeaningCertainty(ConflictFinding finding)
    {
        if (HasHarmonyRuntimeCorrelationEvidence(finding))
        {
            return "This Harmony warning is backed by runtime logs plus patch evidence.";
        }

        if (HasHarmonySource(finding, "log-graph") || HasHarmonySource(finding, "duplicate-block"))
        {
            return "This Harmony warning is backed by Harmony log output.";
        }

        return "This Harmony warning comes from mod metadata and still needs an in-game validation pass.";
    }

    private static string BuildHarmonyValidationStep(ConflictFinding finding)
    {
        return BuildHarmonyFamilyTestHint(finding);
    }

    private static string BuildHarmonyFamilyOutcome(ConflictFinding finding)
    {
        return GetHarmonyTargetFamilyToken(finding) switch
        {
            "settlement-campaign-rule" => "Campaign progression behavior may differ on the affected settlement or campaign rule path.",
            "economic-calculation" => "Final calculation results may differ.",
            "mission-startup" => "Battle or mission behavior may differ.",
            "access-gate" => "Entry conditions or decision results may differ.",
            _ => "Gameplay behavior may differ on this method path.",
        };
    }

    private static string BuildHarmonyFamilyHint(ConflictFinding finding)
    {
        return GetHarmonyTargetFamilyToken(finding) switch
        {
            "settlement-campaign-rule" => "Validate settlement or campaign progression behavior on the affected path.",
            "economic-calculation" => "Validate the affected calculation in a normal campaign flow.",
            "mission-startup" => "Validate one short battle or mission on the affected path.",
            "access-gate" => "Validate the entry or decision path this method controls.",
            _ => "Validate the gameplay path that reaches this method.",
        };
    }

    private static string BuildHarmonyFamilyTestHint(ConflictFinding finding)
    {
        return GetHarmonyTargetFamilyToken(finding) switch
        {
            "settlement-campaign-rule" => "Run a short campaign check and watch the affected settlement values over a few ticks or days.",
            "economic-calculation" => "Run one normal campaign scenario that reaches this calculation and compare the actual result.",
            "mission-startup" => "Run one short battle or mission with the current stack unchanged.",
            "access-gate" => "Check the entry or decision this method controls and confirm the result still matches expectation.",
            _ => "Run one short in-game check that reaches this method before changing the stack.",
        };
    }

    private static bool IsAdvisoryHarmonyStack(ConflictFinding finding)
    {
        string shape = GetHarmonyPatchShapeToken(finding);
        string order = GetHarmonyOrderStateToken(finding);
        return shape == "postfix-only"
            && (order is "explicitly-ordered" or "unknown")
            && !HasHarmonyRuntimeCorrelationEvidence(finding)
            && !HasHarmonyOwnershipRisk(finding);
    }

    private static bool HasHarmonyOrderingRisk(ConflictFinding finding)
    {
        return GetHarmonyOrderStateToken(finding) is "same-priority-ambiguous" or "unordered" or "cycle";
    }

    private static bool HasHarmonyOwnershipRisk(ConflictFinding finding)
    {
        string shape = GetHarmonyPatchShapeToken(finding);
        string ownership = GetHarmonyOwnershipShapeToken(finding);
        return shape is "transpiler-present" or "prefix-mix"
            || ownership == "single-kind-duplicated";
    }

    private static bool HasHarmonyRuntimeCorrelationEvidence(ConflictFinding finding)
    {
        return finding.Evidence.Any(e => e.StartsWith("runtime-correlation:", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasHarmonySource(ConflictFinding finding, string sourceToken)
    {
        return finding.Evidence.Any(e =>
            e.Equals($"harmony-source:{sourceToken}", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetHarmonyPatchShapeToken(ConflictFinding finding)
    {
        return GetHarmonyProfileToken(finding, "patch-shape")
            ?? (IsPostfixOnlyHarmonyFinding(finding) ? "postfix-only" : "unknown");
    }

    private static string GetHarmonyOrderStateToken(ConflictFinding finding)
    {
        return GetHarmonyProfileToken(finding, "order-state")
            ?? "unknown";
    }

    private static string GetHarmonyOwnershipShapeToken(ConflictFinding finding)
    {
        return GetHarmonyProfileToken(finding, "ownership-shape")
            ?? "unknown";
    }

    private static string GetHarmonyTargetFamilyToken(ConflictFinding finding)
    {
        return GetHarmonyProfileToken(finding, "target-family")
            ?? "unknown";
    }

    private static string? GetHarmonyProfileToken(ConflictFinding finding, string key)
    {
        string prefix = $"harmony-profile:{key}=";
        return finding.Evidence.FirstOrDefault(e => e.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?[prefix.Length..]
            .Trim()
            .ToLowerInvariant();
    }

    private static string ToDisplayHarmonyToken(string token)
    {
        return token switch
        {
            "postfix-only" => "postfix-only",
            "prefix-mix" => "prefix mix",
            "transpiler-present" => "transpiler present",
            "finalizer-only" => "finalizer-only",
            "same-priority-ambiguous" => "same-priority ambiguous",
            "explicitly-ordered" => "explicitly ordered",
            "single-kind-duplicated" => "same patch kind duplicated across mods",
            "stacked-mixed-kinds" => "stacked mixed patch kinds",
            "single-module-repeated" => "one module repeats ownership",
            "multi-module" => "multi-module overlap",
            "settlement-campaign-rule" => "settlement or campaign rule",
            "economic-calculation" => "economic or calculation path",
            "mission-startup" => "mission or battle startup",
            "access-gate" => "access or entry gate",
            _ => token.Replace('-', ' '),
        };
    }

    private static string ToDisplayHarmonySource(string token)
    {
        return token switch
        {
            "static" => "mod metadata",
            "duplicate-block" => "Harmony Patch Scanner duplicate block",
            "log-graph" => "Harmony graph log",
            _ => token.Replace('-', ' '),
        };
    }

    private static bool IsPostfixOnlyHarmonyFinding(ConflictFinding finding)
    {
        return finding.Evidence.Any(e =>
                   e.Equals("harmony-profile:postfix-only", StringComparison.OrdinalIgnoreCase))
            || string.Equals(GetHarmonyPatchShapeToken(finding), "postfix-only", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SaveRiskEvidenceEntry(
        string SavePath,
        string DisplayLabel,
        IReadOnlyList<string> MissingMods);

    private static string SanitizeForTsv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
}
