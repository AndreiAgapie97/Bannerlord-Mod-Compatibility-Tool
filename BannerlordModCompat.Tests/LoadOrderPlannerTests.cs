using BannerlordModCompat.Core;
using Xunit;

namespace BannerlordModCompat.Tests;

public class LoadOrderPlannerTests
{
    [Fact]
    public void Build_ClassifiesDependencyBootstrapStabilityAndPinRationales()
    {
        LoadOrderPlanner planner = new();
        List<ModuleManifest> modules =
        [
            BuildModule("Bannerlord.Harmony", official: true, framework: true),
            BuildModule("Native", official: true),
            BuildModule(
                "CustomA",
                dependencies:
                [
                    new ModuleDependency("Native", Optional: false, VersionHint: null),
                ]),
            BuildModule("CustomB"),
        ];

        LoadOrderRecommendation recommendation = planner.Build(
            modules,
            currentOrder: ["CustomB", "CustomA", "Bannerlord.Harmony", "Native"],
            pinnedMods: ["CustomB"],
            findings:
            [
                new ConflictFinding
                {
                    Category = ConflictCategory.HarmonyPatchConflict,
                    Severity = ConflictSeverity.High,
                    Confidence = 0.80,
                    ModuleIds = ["CustomA", "CustomB"],
                    Reason = "Shared method ownership",
                    LikelyInGameOutcome = "Behavior may differ.",
                    Recommendation = "Validate order.",
                    Evidence = ["test"],
                },
            ]);

        LoadOrderModuleRationale harmony = Assert.Single(recommendation.ModuleRationales, r => r.ModuleId == "Bannerlord.Harmony");
        Assert.Equal(LoadOrderRationaleKind.Bootstrap, harmony.PrimaryKind);
        Assert.Contains(LoadOrderRationaleKind.Bootstrap, harmony.Kinds);

        LoadOrderModuleRationale native = Assert.Single(recommendation.ModuleRationales, r => r.ModuleId == "Native");
        Assert.Contains(LoadOrderRationaleKind.Dependency, native.Kinds);
        Assert.Contains(LoadOrderRationaleKind.Bootstrap, native.Kinds);

        LoadOrderModuleRationale customA = Assert.Single(recommendation.ModuleRationales, r => r.ModuleId == "CustomA");
        Assert.Contains(LoadOrderRationaleKind.Dependency, customA.Kinds);
        Assert.Contains(LoadOrderRationaleKind.StabilityPreference, customA.Kinds);

        LoadOrderModuleRationale customB = Assert.Single(recommendation.ModuleRationales, r => r.ModuleId == "CustomB");
        Assert.Contains(LoadOrderRationaleKind.StabilityPreference, customB.Kinds);
        Assert.Contains(LoadOrderRationaleKind.Pin, customB.Kinds);
    }

    [Fact]
    public void Build_ClassifiesOfficialUiPrecedenceRationale()
    {
        LoadOrderPlanner planner = new();
        List<ModuleManifest> modules =
        [
            BuildModule("SandBox", official: true),
            BuildModule("CustomUi"),
        ];
        ModuleCapabilityProfile customUiProfile = new("CustomUi", isOfficial: false, isFramework: false)
        {
            HasGauntletUiXml = true,
            HasCodelessGauntletUi = true,
        };
        customUiProfile.OfficialDependencyTargets.Add("SandBox");

        LoadOrderRecommendation recommendation = planner.Build(
            modules,
            currentOrder: ["SandBox", "CustomUi"],
            pinnedMods: [],
            findings: null,
            capabilityProfiles: [customUiProfile]);

        Assert.Equal(["CustomUi", "SandBox"], recommendation.SuggestedOrder);
        LoadOrderModuleRationale uiRationale = Assert.Single(recommendation.ModuleRationales, r => r.ModuleId == "CustomUi");
        Assert.Equal(LoadOrderRationaleKind.OfficialUiPrecedence, uiRationale.PrimaryKind);
        Assert.Contains(LoadOrderRationaleKind.OfficialUiPrecedence, uiRationale.Kinds);
        Assert.Contains("UI precedence", uiRationale.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private static ModuleManifest BuildModule(
        string id,
        IReadOnlyList<ModuleDependency>? dependencies = null,
        bool official = false,
        bool framework = false)
    {
        return new ModuleManifest
        {
            Id = id,
            Name = id,
            Version = "1.0.0",
            RootPath = $@"C:\Test\{id}",
            SubModulePath = $@"C:\Test\{id}\SubModule.xml",
            SourceType = ModSourceType.Local,
            IsOfficial = official,
            IsFramework = framework,
            Dependencies = dependencies?.ToList() ?? [],
            ExplicitIncompatibilities = [],
            XmlEntities = [],
            Dlls = [],
        };
    }
}
