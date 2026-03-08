using BannerlordModCompat.Core;
using Xunit;

namespace BannerlordModCompat.Tests;

public class UiOverrideAnalyzerTests
{
    [Fact]
    public void Analyze_EmitsKnownUiPrecedenceFindingWhenCodelessOverrideLoadsAfterOfficialModule()
    {
        UiOverrideAnalyzer analyzer = new();
        ModuleCapabilityProfile customUi = new("CustomUi", isOfficial: false, isFramework: false)
        {
            HasGauntletUiXml = true,
            HasCodelessGauntletUi = true,
        };
        customUi.OfficialDependencyTargets.Add("SandBox");

        List<ConflictFinding> findings = analyzer.Analyze(
            currentOrder: ["SandBox", "CustomUi"],
            capabilityProfiles: [customUi]).ToList();

        ConflictFinding finding = Assert.Single(findings);
        Assert.Equal(ConflictCategory.LoadOrderViolation, finding.Category);
        Assert.Contains("CustomUi", finding.ModuleIds, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("SandBox", finding.ModuleIds, StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(finding.StructuredEvidence);
        Assert.Contains(FindingEvidenceKind.KnownRule, finding.StructuredEvidence!.Kinds);
        Assert.Contains(FindingEvidenceKind.UiPrecedence, finding.StructuredEvidence.Kinds);
        Assert.Equal("ui-precedence:CustomUi:SandBox", finding.StructuredEvidence.Details["rule-id"]);
    }

    [Fact]
    public void Analyze_DoesNotEmitFindingWhenCodelessOverrideLoadsBeforeOfficialModule()
    {
        UiOverrideAnalyzer analyzer = new();
        ModuleCapabilityProfile customUi = new("CustomUi", isOfficial: false, isFramework: false)
        {
            HasGauntletUiXml = true,
            HasCodelessGauntletUi = true,
        };
        customUi.OfficialDependencyTargets.Add("SandBox");

        IReadOnlyList<ConflictFinding> findings = analyzer.Analyze(
            currentOrder: ["CustomUi", "SandBox"],
            capabilityProfiles: [customUi]);

        Assert.Empty(findings);
    }
}
