using System.Xml.Linq;
using BannerlordModCompat.Core;
using Xunit;

namespace BannerlordModCompat.Tests;

public class LoadOrderProjectionBuilderTests
{
    [Fact]
    public void BuildEnabledSingleplayerProjection_ExcludesInactiveInstalledModulesFromActionPlan()
    {
        LoadOrderRecommendation source = new()
        {
            CurrentOrder = ["Native", "Sandbox", "StoryMode", "Bannerlord.Diplomacy"],
            SuggestedOrder = ["Native", "Sandbox", "StoryMode", "FastMode", "Bannerlord.Diplomacy"],
            Moves = [new LoadOrderMove("FastMode", 99, 3)],
            Warnings = [],
            Rationale = [],
            Confidence = 0.90,
        };

        PlayerLoadOrderProjection projection = PlayerLoadOrderProjectionBuilder.BuildEnabledSingleplayerProjection(source);

        Assert.Equal(["Native", "Sandbox", "StoryMode", "Bannerlord.Diplomacy"], projection.Recommendation.CurrentOrder);
        Assert.Equal(["Native", "Sandbox", "StoryMode", "Bannerlord.Diplomacy"], projection.Recommendation.SuggestedOrder);
        Assert.Empty(projection.Recommendation.Moves);
        Assert.Equal(["FastMode"], projection.InactiveInstalledModuleIds);
    }

    [Fact]
    public void BuildEnabledSingleplayerProjection_HidesMultiplayerFromPlayerFacingOrder()
    {
        LoadOrderRecommendation source = new()
        {
            CurrentOrder = ["Native", "Sandbox", "Multiplayer", "StoryMode", "CustomA"],
            SuggestedOrder = ["Native", "Sandbox", "Multiplayer", "StoryMode", "CustomA"],
            Moves = [],
            Warnings = [],
            Rationale = [],
            Confidence = 0.75,
        };

        PlayerLoadOrderProjection projection = PlayerLoadOrderProjectionBuilder.BuildEnabledSingleplayerProjection(source);

        Assert.DoesNotContain("Multiplayer", projection.Recommendation.CurrentOrder, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Multiplayer", projection.Recommendation.SuggestedOrder, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Multiplayer", projection.InactiveInstalledModuleIds, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildEnabledSingleplayerProjection_PreservesPlannerRationaleOnVisibleRows()
    {
        LoadOrderRecommendation source = new()
        {
            CurrentOrder = ["Native", "Bannerlord.Diplomacy"],
            SuggestedOrder = ["Native", "Bannerlord.Diplomacy"],
            Moves = [],
            Warnings = [],
            Rationale = [],
            Confidence = 0.75,
            ModuleRationales =
            [
                new LoadOrderModuleRationale
                {
                    ModuleId = "Native",
                    PrimaryKind = LoadOrderRationaleKind.Bootstrap,
                    Kinds = [LoadOrderRationaleKind.Bootstrap],
                    Summary = "Position is shaped by the core/framework bootstrap sequence.",
                },
                new LoadOrderModuleRationale
                {
                    ModuleId = "Bannerlord.Diplomacy",
                    PrimaryKind = LoadOrderRationaleKind.StabilityPreference,
                    Kinds = [LoadOrderRationaleKind.StabilityPreference],
                    Summary = "Position is kept stable because this module overlaps with other active modules.",
                },
            ],
        };

        PlayerLoadOrderProjection projection = PlayerLoadOrderProjectionBuilder.BuildEnabledSingleplayerProjection(
            source,
            modules:
            [
                BuildModule("Native", official: true),
                BuildModule("Bannerlord.Diplomacy"),
            ],
            findings: []);

        PlayerLoadOrderProjectionRow row = Assert.Single(projection.Rows, r => r.ModuleId == "Bannerlord.Diplomacy");
        Assert.Equal(LoadOrderRationaleKind.StabilityPreference, row.PrimaryReasonKind);
        Assert.Contains("overlaps", row.ReasonSummary, StringComparison.OrdinalIgnoreCase);
        Assert.True(row.IsCustom);
    }

    [Fact]
    public void BuildEnabledSingleplayerProjection_MarksInactiveRowsAsDisabledInstalled()
    {
        LoadOrderRecommendation source = new()
        {
            CurrentOrder = ["Native", "Sandbox", "StoryMode"],
            SuggestedOrder = ["Native", "Sandbox", "StoryMode", "FastMode"],
            Moves = [],
            Warnings = [],
            Rationale = [],
            Confidence = 0.80,
            ModuleRationales =
            [
                new LoadOrderModuleRationale
                {
                    ModuleId = "FastMode",
                    PrimaryKind = LoadOrderRationaleKind.Bootstrap,
                    Kinds = [LoadOrderRationaleKind.Bootstrap],
                    Summary = "Position is shaped by the core/framework bootstrap sequence.",
                },
            ],
        };

        PlayerLoadOrderProjection projection = PlayerLoadOrderProjectionBuilder.BuildEnabledSingleplayerProjection(
            source,
            modules:
            [
                BuildModule("Native", official: true),
                BuildModule("Sandbox", official: true),
                BuildModule("StoryMode", official: true),
                BuildModule("FastMode", official: true),
            ],
            findings: []);

        PlayerLoadOrderProjectionRow row = Assert.Single(projection.InactiveInstalledRows);
        Assert.Equal("FastMode", row.ModuleId);
        Assert.True(row.IsInactiveInstalled);
        Assert.Equal(LoadOrderRationaleKind.DisabledInstalled, row.PrimaryReasonKind);
    }

    [Fact]
    public void TryApplySuggestedOrder_PreservesDisabledModulePositions_WhenApplyingEnabledOnlyOrder()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BannerlordModCompatTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string launcherPath = Path.Combine(tempRoot, "LauncherData.xml");

        try
        {
            XDocument doc = XDocument.Parse("""
                <Root>
                  <ModDatas>
                    <UserModData><Id>Native</Id><IsSelected>true</IsSelected></UserModData>
                    <UserModData><Id>FastMode</Id><IsSelected>false</IsSelected></UserModData>
                    <UserModData><Id>Sandbox</Id><IsSelected>true</IsSelected></UserModData>
                    <UserModData><Id>StoryMode</Id><IsSelected>true</IsSelected></UserModData>
                  </ModDatas>
                </Root>
                """);
            doc.Save(launcherPath);

            LauncherDataService service = new();
            List<string> warnings = [];

            bool applied = service.TryApplySuggestedOrder(
                launcherPath,
                ["Sandbox", "Native", "StoryMode"],
                warnings);

            Assert.True(applied);

            XDocument saved = XDocument.Load(launcherPath);
            string[] ids = saved
                .Descendants()
                .Where(e => e.Name.LocalName.Equals("UserModData", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Elements().First(x => x.Name.LocalName.Equals("Id", StringComparison.OrdinalIgnoreCase)).Value.Trim())
                .ToArray();

            Assert.Equal(["Sandbox", "FastMode", "Native", "StoryMode"], ids);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static ModuleManifest BuildModule(string id, bool official = false, bool framework = false)
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
            Dependencies = [],
            ExplicitIncompatibilities = [],
            XmlEntities = [],
            Dlls = [],
        };
    }
}
