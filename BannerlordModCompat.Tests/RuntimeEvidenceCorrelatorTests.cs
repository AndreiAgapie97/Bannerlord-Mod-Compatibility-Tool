using BannerlordModCompat.Core;
using Xunit;

namespace BannerlordModCompat.Tests;

public class RuntimeEvidenceCorrelatorTests
{
    [Fact]
    public void Correlate_PromotesLoadOrderFinding_WithRuntimeMismatchSignal()
    {
        RuntimeEvidenceCorrelator correlator = new();
        List<string> warnings = [];
        List<ConflictFinding> findings =
        [
            NewFinding(
                category: ConflictCategory.LoadOrderViolation,
                severity: ConflictSeverity.High,
                confidence: 0.82,
                moduleIds: ["ModA", "ModB"],
                reason: "Load order is invalid for dependency chain."),
            NewFinding(
                category: ConflictCategory.RuntimeModuleSetMismatch,
                severity: ConflictSeverity.High,
                confidence: 0.95,
                moduleIds: ["ModA", "ModB"],
                reason: "Runtime module set differs from launcher order.",
                evidence: [@"C:\ProgramData\Mount and Blade II Bannerlord\logs\watchdog_log_111.txt"]),
        ];

        List<ConflictFinding> result = correlator.Correlate(findings, warnings).ToList();
        ConflictFinding promoted = Assert.Single(result, f => f.Category == ConflictCategory.LoadOrderViolation);
        Assert.True(promoted.Confidence > 0.82);
        Assert.Contains(promoted.Evidence, e =>
            e.Contains("watchdog_log_111.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result, f => f.Category == ConflictCategory.RuntimeModuleSetMismatch);
        Assert.Contains(warnings, w => w.Contains("promoted 1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Correlate_LeavesHarmonyStackSeverity_WhenNoHarmonyRuntimeProof()
    {
        RuntimeEvidenceCorrelator correlator = new();
        List<string> warnings = [];
        List<ConflictFinding> findings =
        [
            NewFinding(
                category: ConflictCategory.HarmonyPatchStack,
                severity: ConflictSeverity.Medium,
                confidence: 0.66,
                moduleIds: ["ModA", "ModB"],
                reason: "Patch stack detected for a shared target method."),
            NewFinding(
                category: ConflictCategory.RuntimeLoaderFailure,
                severity: ConflictSeverity.High,
                confidence: 0.92,
                moduleIds: ["ModA", "ModB"],
                reason: "Runtime loader failure signatures detected.",
                evidence:
                [
                    @"C:\ProgramData\Mount and Blade II Bannerlord\logs\launcher_log_222.txt",
                    "issue: Could not load file or assembly 'ModA.dll'"
                ]),
        ];

        List<ConflictFinding> result = correlator.Correlate(findings, warnings).ToList();
        ConflictFinding promoted = Assert.Single(result, f => f.Category == ConflictCategory.HarmonyPatchStack);
        Assert.Equal(ConflictSeverity.Medium, promoted.Severity);
        Assert.True(promoted.Confidence > 0.66);
        Assert.Contains("Runtime evidence correlation", promoted.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result, f => f.Category == ConflictCategory.RuntimeLoaderFailure);
    }

    [Fact]
    public void Correlate_PromotesHarmonyConflictSeverity_WhenHarmonyRuntimeProofExists()
    {
        RuntimeEvidenceCorrelator correlator = new();
        List<string> warnings = [];
        List<ConflictFinding> findings =
        [
            NewFinding(
                category: ConflictCategory.HarmonyPatchConflict,
                severity: ConflictSeverity.High,
                confidence: 0.76,
                moduleIds: ["ModA", "ModB"],
                reason: "Static Harmony conflict on shared target.",
                evidence:
                [
                    @"C:\MountAndBladeIIBannerlord\Configs\Harmony\AllHarmonyPatches.txt",
                    "harmony-target:TaleWorlds.Example::Method"
                ]),
            NewFinding(
                category: ConflictCategory.RuntimeCrashSession,
                severity: ConflictSeverity.High,
                confidence: 0.89,
                moduleIds: ["ModA", "ModB", "ModC"],
                reason: "Crash telemetry captured for runtime session.",
                evidence:
                [
                    @"C:\ProgramData\Mount and Blade II Bannerlord\logs\rgl_log_444.txt",
                    "issue: Harmony exception while patching"
                ]),
        ];

        List<ConflictFinding> result = correlator.Correlate(findings, warnings).ToList();
        ConflictFinding promoted = Assert.Single(result, f => f.Category == ConflictCategory.HarmonyPatchConflict);
        Assert.Equal(ConflictSeverity.Critical, promoted.Severity);
        Assert.True(promoted.Confidence > 0.76);
        Assert.Contains("Runtime evidence correlation", promoted.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result, f => f.Category == ConflictCategory.RuntimeCrashSession);
    }

    [Fact]
    public void Correlate_LeavesLifecycleSeverity_WhenRuntimeSignalIsNotRecurring()
    {
        RuntimeEvidenceCorrelator correlator = new();
        List<string> warnings = [];
        List<ConflictFinding> findings =
        [
            NewFinding(
                category: ConflictCategory.LifecycleRegistrationOverlap,
                severity: ConflictSeverity.High,
                confidence: 0.80,
                moduleIds: ["ModA", "ModB", "ModC"],
                reason: "Multiple modules register handlers in GameStart lifecycle phase.",
                evidence:
                [
                    "ModA:GameStart:AddBehavior",
                    "ModB:GameStart:AddBehavior"
                ]),
            NewFinding(
                category: ConflictCategory.RuntimeCrashSession,
                severity: ConflictSeverity.High,
                confidence: 0.89,
                moduleIds: ["ModA", "ModB", "ModC", "ModD"],
                reason: "Crash telemetry captured for runtime session.",
                evidence:
                [
                    @"C:\ProgramData\Mount and Blade II Bannerlord\logs\rgl_log_551.txt",
                    "issue: access violation"
                ]),
        ];

        List<ConflictFinding> result = correlator.Correlate(findings, warnings).ToList();
        ConflictFinding promoted = Assert.Single(result, f => f.Category == ConflictCategory.LifecycleRegistrationOverlap);
        Assert.Equal(ConflictSeverity.High, promoted.Severity);
        Assert.True(promoted.Confidence > 0.80);
    }

    [Fact]
    public void Correlate_PromotesLifecycleSeverity_WhenRecurringRuntimeClusterCorrelates()
    {
        RuntimeEvidenceCorrelator correlator = new();
        List<string> warnings = [];
        List<ConflictFinding> findings =
        [
            NewFinding(
                category: ConflictCategory.LifecycleRegistrationOverlap,
                severity: ConflictSeverity.High,
                confidence: 0.80,
                moduleIds: ["ModA", "ModB", "ModC"],
                reason: "Multiple modules register handlers in GameStart lifecycle phase.",
                evidence:
                [
                    "ModA:GameStart:AddBehavior",
                    "ModB:GameStart:AddBehavior",
                    "ModC:GameStart:AddModel"
                ]),
            NewFinding(
                category: ConflictCategory.RuntimeCrashSession,
                severity: ConflictSeverity.High,
                confidence: 0.89,
                moduleIds: ["ModA", "ModB", "ModC", "ModD"],
                reason: "Recurring runtime cluster detected.",
                evidence:
                [
                    @"C:\ProgramData\Mount and Blade II Bannerlord\logs\rgl_log_552.txt",
                    "cluster-signature:access-violation-at-startup",
                    "cluster-session-count:3"
                ]),
        ];

        List<ConflictFinding> result = correlator.Correlate(findings, warnings).ToList();
        ConflictFinding promoted = Assert.Single(result, f => f.Category == ConflictCategory.LifecycleRegistrationOverlap);
        Assert.Equal(ConflictSeverity.Critical, promoted.Severity);
        Assert.True(promoted.Confidence > 0.80);
    }

    [Fact]
    public void Correlate_DoesNotOverCorrelateBroadCrashSet_OnSingleModuleOverlap()
    {
        RuntimeEvidenceCorrelator correlator = new();
        List<string> warnings = [];
        List<ConflictFinding> findings =
        [
            NewFinding(
                category: ConflictCategory.GameModelOverlap,
                severity: ConflictSeverity.Medium,
                confidence: 0.70,
                moduleIds: ["ModA"],
                reason: "Multiple game model providers are active."),
            NewFinding(
                category: ConflictCategory.RuntimeCrashSession,
                severity: ConflictSeverity.High,
                confidence: 0.89,
                moduleIds: ["ModA", "ModB", "ModC", "ModD", "ModE", "ModF", "ModG", "ModH", "ModI", "ModJ", "ModK", "ModL"],
                reason: "Crash telemetry captured for runtime session.",
                evidence: [@"C:\ProgramData\Mount and Blade II Bannerlord\logs\rgl_log_333.txt"]),
        ];

        List<ConflictFinding> result = correlator.Correlate(findings, warnings).ToList();
        ConflictFinding unchanged = Assert.Single(result, f => f.Category == ConflictCategory.GameModelOverlap);
        Assert.Equal(0.70, unchanged.Confidence, precision: 6);
        Assert.DoesNotContain(unchanged.Evidence, e =>
            e.Contains("rgl_log_333.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result, f => f.Category == ConflictCategory.RuntimeCrashSession);
        Assert.Empty(warnings);
    }

    private static ConflictFinding NewFinding(
        ConflictCategory category,
        ConflictSeverity severity,
        double confidence,
        IReadOnlyList<string> moduleIds,
        string reason,
        IReadOnlyList<string>? evidence = null
    )
    {
        return new ConflictFinding
        {
            Category = category,
            Severity = severity,
            Confidence = confidence,
            ModuleIds = moduleIds,
            Reason = reason,
            Evidence = evidence ?? [],
        };
    }
}
