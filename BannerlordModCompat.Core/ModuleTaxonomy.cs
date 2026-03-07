namespace BannerlordModCompat.Core;

public static class ModuleTaxonomy
{
    private static readonly HashSet<string> OfficialModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "Native",
        "SandBoxCore",
        "SandboxCore",
        "Sandbox",
        "StoryMode",
        "CustomBattle",
        "BirthAndDeath",
        "Multiplayer",
        "NavalDLC",
        "FastMode",
    };

    private static readonly HashSet<string> FrameworkModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bannerlord.Harmony",
        "Harmony",
        "HarmonyLib",
        "Bannerlord.ButterLib",
        "ButterLib",
        "Bannerlord.UIExtenderEx",
        "UIExtenderEx",
        "Bannerlord.MBOptionScreen",
        "MBOptionScreen",
        "MCMv5",
        "MCMv4",
        "MCM",
        "ModConfigurationMenu",
        "BUTRLoader",
        "Bannerlord.BLSE",
        "BLSE",
    };

    private static readonly HashSet<string> SingleplayerHiddenLoadOrderModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "Multiplayer",
    };

    public static bool IsOfficial(string moduleId) => OfficialModules.Contains(moduleId);

    public static bool IsFramework(string moduleId) => FrameworkModules.Contains(moduleId);

    public static bool IsCustom(string moduleId) => !IsOfficial(moduleId) && !IsFramework(moduleId);

    public static bool IsSingleplayerHiddenLoadOrderModule(string moduleId) => SingleplayerHiddenLoadOrderModules.Contains(moduleId);

    public static bool IsIsolationFoundation(string moduleId) => IsOfficial(moduleId) || IsFramework(moduleId);
}
