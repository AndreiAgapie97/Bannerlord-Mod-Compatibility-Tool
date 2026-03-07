using System.Reflection;
using BannerlordModCompat.Core;
using Xunit;

namespace BannerlordModCompat.Tests;

public class CompatibilityAnalyzerBehaviorTests
{
    [Fact]
    public void AnalyzeDllCollisions_DoesNotFlagPairsWithIdenticalBinaries()
    {
        ModuleManifest modA = BuildModule("ModA", dlls:
        [
            new DllArtifact("A/shared.dll", "shared.dll", "HASH_A", "AsmA", "1.0.0.0", false),
        ]);
        ModuleManifest modB = BuildModule("ModB", dlls:
        [
            new DllArtifact("B/shared.dll", "shared.dll", "HASH_A", "AsmB", "1.0.0.0", false),
        ]);
        ModuleManifest modC = BuildModule("ModC", dlls:
        [
            new DllArtifact("C/shared.dll", "shared.dll", "HASH_C", "AsmC", "1.0.0.0", false),
        ]);

        List<ConflictFinding> findings = InvokePrivateFindingsMethod(
            "AnalyzeDllCollisions",
            [modA, modB, modC],
            customOnlyFocus: false
        );

        Assert.Contains(findings, f => IsPair(f, "ModA", "ModC"));
        Assert.Contains(findings, f => IsPair(f, "ModB", "ModC"));
        Assert.DoesNotContain(findings, f => IsPair(f, "ModA", "ModB"));
    }

    [Fact]
    public void AnalyzeXmlCollisions_PrioritizesHighImpactBeforeTakeLimit()
    {
        List<XmlEntityReference> first = [];
        List<XmlEntityReference> second = [];

        for (int i = 0; i < 221; i++)
        {
            string id = $"low_{i}";
            string key = $"lowtype:{id}";
            first.Add(new XmlEntityReference(key, "LowType", id, $"First/low_{i}.xml"));
            second.Add(new XmlEntityReference(key, "LowType", id, $"Second/low_{i}.xml"));
        }

        const string highId = "legendary_item";
        first.Add(new XmlEntityReference($"item:{highId}", "Item", highId, "First/items.xml"));
        second.Add(new XmlEntityReference($"item:{highId}", "Item", highId, "Second/items.xml"));

        ModuleManifest modA = BuildModule("First", xmlEntities: first);
        ModuleManifest modB = BuildModule("Second", xmlEntities: second);

        List<ConflictFinding> findings = InvokePrivateFindingsMethod(
            "AnalyzeXmlCollisions",
            [modA, modB],
            customOnlyFocus: false
        );

        Assert.Equal(220, findings.Count);
        Assert.Contains(findings, f =>
            f.Category == ConflictCategory.XmlEntityCollision
            && f.Reason.Contains("Item:legendary_item", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ConflictSeverity.High, findings.First().Severity);
    }

    [Fact]
    public void FilterLikelyIssueFindings_RemovesLikelyCategoriesWhenDisabled()
    {
        List<ConflictFinding> input =
        [
            NewFinding(ConflictCategory.MissingDependency, ConflictSeverity.Critical),
            NewFinding(ConflictCategory.HarmonyPatchStack, ConflictSeverity.Medium),
        ];
        List<string> warnings = [];

        MethodInfo method = GetPrivateStaticMethod("FilterLikelyIssueFindings");
        List<ConflictFinding> filtered = (List<ConflictFinding>)method.Invoke(
            null,
            [input, false, warnings]
        )!;

        Assert.Single(filtered);
        Assert.Equal(ConflictCategory.MissingDependency, filtered[0].Category);
        Assert.Contains(warnings, w => w.Contains("removed 1 probabilistic finding", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnalyzeDependencies_SkipsVersionMismatchWhenDataNoiseDisabled()
    {
        ModuleManifest host = BuildModule(
            "Host",
            dependencies:
            [
                new ModuleDependency("Dep", Optional: false, VersionHint: "2.")
            ]);
        ModuleManifest dep = BuildModule("Dep", version: "1.0.0");
        List<ModuleManifest> modules = [host, dep];
        List<string> currentOrder = ["Dep", "Host"];

        MethodInfo method = GetPrivateStaticMethod("AnalyzeDependencies");
        List<ConflictFinding> disabled = ((IEnumerable<ConflictFinding>)method.Invoke(
            null,
            [modules, currentOrder, false, false]
        )!).ToList();
        List<ConflictFinding> enabled = ((IEnumerable<ConflictFinding>)method.Invoke(
            null,
            [modules, currentOrder, false, true]
        )!).ToList();

        Assert.DoesNotContain(disabled, f => f.Category == ConflictCategory.DependencyVersionMismatch);
        Assert.Contains(enabled, f => f.Category == ConflictCategory.DependencyVersionMismatch);
    }

    [Fact]
    public void FilterDataNoiseFindings_RemovesDataNoiseCategoriesWhenDisabled()
    {
        List<ConflictFinding> input =
        [
            NewFinding(ConflictCategory.MissingDependency, ConflictSeverity.Critical),
            NewFinding(ConflictCategory.DllCollision, ConflictSeverity.High),
            NewFinding(ConflictCategory.XmlEntityCollision, ConflictSeverity.Medium),
        ];
        List<string> warnings = [];

        MethodInfo method = GetPrivateStaticMethod("FilterDataNoiseFindings");
        List<ConflictFinding> filtered = (List<ConflictFinding>)method.Invoke(
            null,
            [input, false, warnings]
        )!;

        Assert.Single(filtered);
        Assert.Equal(ConflictCategory.MissingDependency, filtered[0].Category);
        Assert.Contains(warnings, w => w.Contains("Data-noise filtering enabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProjectPlayerFacingFindings_RemovesNoiseCategoriesAndNonCustomModules()
    {
        List<ModuleManifest> modules =
        [
            BuildModule("Native", official: true),
            BuildModule("Bannerlord.ButterLib", framework: true),
            BuildModule("CustomA"),
            BuildModule("CustomB"),
        ];

        List<ConflictFinding> input =
        [
            NewFinding(
                ConflictCategory.MissingDependency,
                ConflictSeverity.Critical,
                ["CustomA", "Bannerlord.ButterLib"]),
            NewFinding(
                ConflictCategory.LifecycleRegistrationOverlap,
                ConflictSeverity.Critical,
                ["CustomA", "CustomB"]),
            NewFinding(
                ConflictCategory.HarmonyPatchConflict,
                ConflictSeverity.High,
                ["CustomA", "Bannerlord.ButterLib", "CustomB"]),
        ];

        MethodInfo method = GetPrivateStaticMethod("ProjectPlayerFacingFindings", [typeof(IReadOnlyList<ConflictFinding>), typeof(IReadOnlyList<ModuleManifest>)]);
        List<ConflictFinding> projected = ((IEnumerable<ConflictFinding>)method.Invoke(null, [input, modules])!).ToList();

        Assert.Single(projected);
        Assert.Equal(ConflictCategory.HarmonyPatchConflict, projected[0].Category);
        Assert.Equal(["CustomA", "CustomB"], projected[0].ModuleIds);
    }

    [Fact]
    public void ProjectPlayerFacingFindings_DropsBaseGameOnlyOverlapAfterProjection()
    {
        List<ModuleManifest> modules =
        [
            BuildModule("Sandbox", official: true),
            BuildModule("CustomA"),
        ];

        List<ConflictFinding> input =
        [
            NewFinding(
                ConflictCategory.HarmonyPatchConflict,
                ConflictSeverity.High,
                ["CustomA", "Sandbox"]),
        ];

        MethodInfo method = GetPrivateStaticMethod("ProjectPlayerFacingFindings", [typeof(IReadOnlyList<ConflictFinding>), typeof(IReadOnlyList<ModuleManifest>)]);
        List<ConflictFinding> projected = ((IEnumerable<ConflictFinding>)method.Invoke(null, [input, modules])!).ToList();

        Assert.Empty(projected);
    }

    [Fact]
    public void ProjectPlayerFacingFindings_KeepsSingleCustomRuntimeSignals()
    {
        List<ModuleManifest> modules =
        [
            BuildModule("Native", official: true),
            BuildModule("Bannerlord.ButterLib", framework: true),
            BuildModule("CustomA"),
        ];

        List<ConflictFinding> input =
        [
            NewFinding(
                ConflictCategory.RuntimeLoaderFailure,
                ConflictSeverity.Critical,
                ["Native", "Bannerlord.ButterLib", "CustomA"]),
        ];

        MethodInfo method = GetPrivateStaticMethod("ProjectPlayerFacingFindings", [typeof(IReadOnlyList<ConflictFinding>), typeof(IReadOnlyList<ModuleManifest>)]);
        List<ConflictFinding> projected = ((IEnumerable<ConflictFinding>)method.Invoke(null, [input, modules])!).ToList();

        Assert.Single(projected);
        Assert.Equal(["CustomA"], projected[0].ModuleIds);
    }

    [Fact]
    public void ProjectPlayerFacingFindings_RemovesRuntimeCrashSessionFromVisibleFindings()
    {
        List<ModuleManifest> modules =
        [
            BuildModule("CustomA"),
            BuildModule("CustomB"),
        ];

        List<ConflictFinding> input =
        [
            NewFinding(
                ConflictCategory.RuntimeCrashSession,
                ConflictSeverity.High,
                ["CustomA", "CustomB"]),
        ];

        MethodInfo method = GetPrivateStaticMethod("ProjectPlayerFacingFindings", [typeof(IReadOnlyList<ConflictFinding>), typeof(IReadOnlyList<ModuleManifest>)]);
        List<ConflictFinding> projected = ((IEnumerable<ConflictFinding>)method.Invoke(null, [input, modules])!).ToList();

        Assert.Empty(projected);
    }

    [Fact]
    public void Discover_WithExplicitModuleRoots_DoesNotAutoExpandScope()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BannerlordModCompatTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            ScanOptions options = new()
            {
                ModuleRoots = [tempRoot],
                IncludeSaveFileAnalysis = false,
            };

            DiscoveredPaths discovered = PathDiscovery.Discover(options);
            string normalized = Path.GetFullPath(tempRoot);

            Assert.Single(discovered.ModuleRoots);
            Assert.Equal(normalized, discovered.ModuleRoots[0], ignoreCase: true);
            Assert.Null(discovered.WorkshopRoot);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void AnalyzeSaveFileRisk_AttachesExactSaveToMissingModMappings()
    {
        MethodInfo method = GetPrivateStaticMethod("AnalyzeSaveFileRisk");
        List<SaveFileInsight> saves =
        [
            new()
            {
                SavePath = @"C:\Saves\Byz1071Test.sav",
                FileSizeBytes = 1024,
                ReferencedInstalledMods = ["AIInfluence", "RBM"],
            },
            new()
            {
                SavePath = @"C:\Saves\Campaign\Ironman6d9dd718e5d7.sav",
                FileSizeBytes = 2048,
                ReferencedInstalledMods = ["RBM"],
            },
        ];

        List<ConflictFinding> findings = ((IEnumerable<ConflictFinding>)method.Invoke(
            null,
            [saves, new List<string>()])!).ToList();

        ConflictFinding finding = Assert.Single(findings);
        Assert.Equal(ConflictCategory.SaveFileRisk, finding.Category);
        Assert.Contains(finding.Evidence, e => e.Contains("Byz1071Test.sav|missing:AIInfluence,RBM", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(finding.Evidence, e => e.Contains("Ironman6d9dd718e5d7.sav|missing:RBM", StringComparison.OrdinalIgnoreCase));
    }

    private static ModuleManifest BuildModule(
        string id,
        string version = "1.0.0",
        IReadOnlyList<ModuleDependency>? dependencies = null,
        IReadOnlyList<DllArtifact>? dlls = null,
        IReadOnlyList<XmlEntityReference>? xmlEntities = null,
        bool official = false,
        bool framework = false
    )
    {
        return new ModuleManifest
        {
            Id = id,
            Name = id,
            Version = version,
            RootPath = $@"C:\Test\{id}",
            SubModulePath = $@"C:\Test\{id}\SubModule.xml",
            SourceType = ModSourceType.Local,
            IsOfficial = official,
            IsFramework = framework,
            Dependencies = dependencies?.ToList() ?? [],
            ExplicitIncompatibilities = [],
            XmlEntities = xmlEntities?.ToList() ?? [],
            Dlls = dlls?.ToList() ?? [],
        };
    }

    private static ConflictFinding NewFinding(
        ConflictCategory category,
        ConflictSeverity severity,
        IReadOnlyList<string>? moduleIds = null
    )
    {
        return new ConflictFinding
        {
            Category = category,
            Severity = severity,
            Confidence = 1.0,
            ModuleIds = moduleIds ?? ["A", "B"],
            Reason = $"{category} test",
            Evidence = [],
        };
    }

    private static bool IsPair(ConflictFinding finding, string left, string right)
    {
        HashSet<string> ids = finding.ModuleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ids.Count == 2
            && ids.Contains(left)
            && ids.Contains(right);
    }

    private static List<ConflictFinding> InvokePrivateFindingsMethod(
        string methodName,
        IReadOnlyList<ModuleManifest> modules,
        bool customOnlyFocus
    )
    {
        MethodInfo method = GetPrivateStaticMethod(methodName);
        IEnumerable<ConflictFinding> findings = (IEnumerable<ConflictFinding>)method.Invoke(
            null,
            [modules, customOnlyFocus]
        )!;
        return findings.ToList();
    }

    private static MethodInfo GetPrivateStaticMethod(string name)
    {
        MethodInfo? method = typeof(CompatibilityAnalyzer).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!;
    }

    private static MethodInfo GetPrivateStaticMethod(string name, Type[] parameterTypes)
    {
        MethodInfo? method = typeof(CompatibilityAnalyzer).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            parameterTypes,
            modifiers: null);
        Assert.NotNull(method);
        return method!;
    }
}
