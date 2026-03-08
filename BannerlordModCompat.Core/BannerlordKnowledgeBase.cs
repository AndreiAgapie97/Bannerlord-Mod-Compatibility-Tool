namespace BannerlordModCompat.Core;

internal enum KnownRuleScope
{
    Module,
    Pair,
    Family,
    Ui,
    Save,
}

internal sealed record KnownCompatibilityRule
{
    public required string Id { get; init; }
    public required KnownRuleScope Scope { get; init; }
    public IReadOnlyList<string> ModuleIds { get; init; } = [];
    public ConflictSeverity? SeverityFloor { get; init; }
    public ConflictSeverity? SeverityCeiling { get; init; }
    public FindingEvidenceKind EvidenceKind { get; init; } = FindingEvidenceKind.KnownRule;
    public LoadOrderRationaleKind? LoadOrderRationaleKind { get; init; }
    public required string Summary { get; init; }
    public required string PlayerSummary { get; init; }
}

internal static class BannerlordKnowledgeBase
{
    private static readonly string[][] CanonicalBootstrapGroups =
    [
        ["Bannerlord.Harmony", "Harmony", "HarmonyLib"],
        ["Bannerlord.ButterLib", "ButterLib"],
        ["Bannerlord.UIExtenderEx", "UIExtenderEx"],
        ["Bannerlord.MBOptionScreen", "MBOptionScreen"],
        ["ModConfigurationMenu", "MCM", "MCMv5", "MCMv4"],
        ["Bannerlord.BLSE", "BLSE", "BUTRLoader"],
        ["Native"],
        ["SandBoxCore", "SandboxCore"],
        ["BirthAndDeath"],
        ["CustomBattle"],
        ["Sandbox"],
        ["StoryMode"],
        ["Multiplayer"],
        ["NavalDLC"],
        ["FastMode"],
    ];

    private static readonly HashSet<string> SaveSensitiveModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "RBM",
        "RealisticBattleMod",
        "RealisticBattleAiModule",
        "RealisticBattleCombatModule",
        "Bannerlord.EconomyOverhaul",
        "AIInfluence",
        "Diplomacy",
        "Bannerlord.Diplomacy",
        "Bannerlord.EconomyOverhaul",
        "ImprovedGarrisons",
    };

    private static readonly Dictionary<string, string> FrameworkVersionAnchors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bannerlord.Harmony"] = "Framework builds should match the active Bannerlord branch before gameplay mods are validated.",
        ["Harmony"] = "Framework builds should match the active Bannerlord branch before gameplay mods are validated.",
        ["HarmonyLib"] = "Framework builds should match the active Bannerlord branch before gameplay mods are validated.",
        ["Bannerlord.ButterLib"] = "Framework builds should match the active Bannerlord branch before gameplay mods are validated.",
        ["ButterLib"] = "Framework builds should match the active Bannerlord branch before gameplay mods are validated.",
        ["Bannerlord.UIExtenderEx"] = "UI framework builds should match the active Bannerlord branch before UI mods are validated.",
        ["UIExtenderEx"] = "UI framework builds should match the active Bannerlord branch before UI mods are validated.",
        ["Bannerlord.MBOptionScreen"] = "UI framework builds should match the active Bannerlord branch before MCM-driven mods are validated.",
        ["MBOptionScreen"] = "UI framework builds should match the active Bannerlord branch before MCM-driven mods are validated.",
        ["MCM"] = "Configuration frameworks should match the active Bannerlord branch before gameplay mods are validated.",
        ["MCMv5"] = "Configuration frameworks should match the active Bannerlord branch before gameplay mods are validated.",
        ["MCMv4"] = "Configuration frameworks should match the active Bannerlord branch before gameplay mods are validated.",
    };

    public static IReadOnlyList<string[]> GetCanonicalBootstrapGroups() => CanonicalBootstrapGroups;

    public static bool IsSaveSensitiveModule(string moduleId) => SaveSensitiveModules.Contains(moduleId);

    public static bool TryGetFrameworkVersionAnchor(string moduleId, out string note)
        => FrameworkVersionAnchors.TryGetValue(moduleId, out note!);

    public static IReadOnlyList<KnownCompatibilityRule> BuildUiPrecedenceRules(
        IReadOnlyList<ModuleCapabilityProfile> capabilityProfiles)
    {
        List<KnownCompatibilityRule> rules = [];
        foreach (ModuleCapabilityProfile profile in capabilityProfiles)
        {
            if (!profile.IsCustom
                || !profile.HasCodelessGauntletUi
                || profile.OfficialDependencyTargets.Count == 0)
            {
                continue;
            }

            foreach (string officialTarget in profile.OfficialDependencyTargets
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                rules.Add(new KnownCompatibilityRule
                {
                    Id = $"ui-precedence:{profile.ModuleId}:{officialTarget}",
                    Scope = KnownRuleScope.Ui,
                    ModuleIds = [profile.ModuleId, officialTarget],
                    SeverityFloor = ConflictSeverity.Medium,
                    SeverityCeiling = ConflictSeverity.High,
                    EvidenceKind = FindingEvidenceKind.UiPrecedence,
                    LoadOrderRationaleKind = LoadOrderRationaleKind.OfficialUiPrecedence,
                    Summary = $"Codeless Gauntlet UI overrides from '{profile.ModuleId}' should load before official module '{officialTarget}'.",
                    PlayerSummary = "This UI mod overrides official Bannerlord UI files and should load before the official module it extends.",
                });
            }
        }

        return rules;
    }
}
