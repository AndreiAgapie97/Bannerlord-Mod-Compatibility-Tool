using BannerlordModCompat.Core;
using Xunit;

namespace BannerlordModCompat.Tests;

public class HarmonyPatchLogAnalyzerTests
{
    [Fact]
    public void Analyze_AllPatchLog_AmbiguousTranspilerOrdering_IsCriticalConflict()
    {
        string tempRoot = CreateTempRoot();
        try
        {
            string harmonyLog = Path.Combine(tempRoot, "AllHarmonyPatches.txt");
            File.WriteAllText(harmonyLog,
                "TaleWorlds.CampaignSystem.CampaignBehaviors.DefaultClanPoliticsModel::CalculateInfluence(Clan) "
                + "Transpiler: [400] ModA.Patches.ClanInfluenceTranspiler; [400] ModB.Patches.ClanInfluenceTranspiler");

            HarmonyPatchLogAnalyzer analyzer = new();
            List<string> warnings = [];
            List<ConflictFinding> findings = analyzer.Analyze(
                [tempRoot],
                [BuildModule("ModA"), BuildModule("ModB")],
                customOnlyFocus: false,
                warnings: warnings
            ).ToList();

            ConflictFinding finding = Assert.Single(findings, f => f.Category == ConflictCategory.HarmonyPatchConflict);
            Assert.Equal(ConflictSeverity.Critical, finding.Severity);
            Assert.Contains("ambiguous-same-priority", finding.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(finding.Evidence, e => e.StartsWith("harmony-graph:", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void Analyze_AllPatchLog_CircularBeforeAfter_IsCriticalConflict()
    {
        string tempRoot = CreateTempRoot();
        try
        {
            string harmonyLog = Path.Combine(tempRoot, "AllHarmonyPatches.txt");
            File.WriteAllText(harmonyLog,
                "TaleWorlds.CampaignSystem.SandBox.CampaignBehaviors.DefaultBehavior::OnDailyTick() "
                + "Prefix: [400] ModA.Patches.TickPrefix before=ModB; [400] ModB.Patches.TickPrefix before=ModA");

            HarmonyPatchLogAnalyzer analyzer = new();
            List<string> warnings = [];
            List<ConflictFinding> findings = analyzer.Analyze(
                [tempRoot],
                [BuildModule("ModA"), BuildModule("ModB")],
                customOnlyFocus: false,
                warnings: warnings
            ).ToList();

            ConflictFinding finding = Assert.Single(findings, f => f.Category == ConflictCategory.HarmonyPatchConflict);
            Assert.Equal(ConflictSeverity.Critical, finding.Severity);
            Assert.Contains("cycle=yes", finding.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("before/after", finding.Recommendation ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void Analyze_AllPatchLog_PostfixOnlyOverlap_IsStackNotConflict()
    {
        string tempRoot = CreateTempRoot();
        try
        {
            string harmonyLog = Path.Combine(tempRoot, "AllHarmonyPatches.txt");
            File.WriteAllText(harmonyLog,
                "TaleWorlds.CampaignSystem.GameComponents.DefaultSettlementFoodModel::CalculateTownFoodStocksChange(Town) "
                + "Postfix: [400] AIInfluence.Patches.FoodPatch; [400] Byzantium1071.Patches.FoodPatch");

            HarmonyPatchLogAnalyzer analyzer = new();
            List<string> warnings = [];
            List<ConflictFinding> findings = analyzer.Analyze(
                [tempRoot],
                [BuildModule("AIInfluence"), BuildModule("Byzantium1071")],
                customOnlyFocus: false,
                warnings: warnings
            ).ToList();

            ConflictFinding finding = Assert.Single(findings, f => f.ModuleIds.SequenceEqual(["AIInfluence", "Byzantium1071"]));
            Assert.Equal(ConflictCategory.HarmonyPatchStack, finding.Category);
            Assert.Equal(ConflictSeverity.Low, finding.Severity);
            Assert.Contains(finding.Evidence, e => e.Equals("harmony-profile:postfix-only", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    private static ModuleManifest BuildModule(string id)
    {
        return new ModuleManifest
        {
            Id = id,
            Name = id,
            Version = "1.0.0",
            RootPath = $@"C:\Test\{id}",
            SubModulePath = $@"C:\Test\{id}\SubModule.xml",
            SourceType = ModSourceType.Local,
            IsOfficial = false,
            IsFramework = false,
            Dependencies = [],
            ExplicitIncompatibilities = [],
            XmlEntities = [],
            Dlls = [],
        };
    }

    private static string CreateTempRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "BannerlordModCompatTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempRoot(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Directory.Delete(path, recursive: true);
    }
}
