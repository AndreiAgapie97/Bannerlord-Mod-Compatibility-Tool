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
}
