namespace BannerlordModCompat.Core;

internal sealed class ModuleCodeProfile
{
    public ModuleCodeProfile(string moduleId)
    {
        ModuleId = moduleId;
    }

    public string ModuleId { get; }
    public bool CallsAddBehavior { get; set; }
    public bool CallsAddModel { get; set; }
    public bool CallsAddMissionBehavior { get; set; }
    public HashSet<string> CampaignBehaviorTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> GameModelTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> MissionBehaviorTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CampaignEventHooks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, HashSet<string>> ModelFamilies { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, HashSet<string>> MissionFamilies { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<LifecycleRegistrationRecord> LifecycleRegistrations { get; } = [];

    public bool HasSignals =>
        CampaignBehaviorTypes.Count > 0
        || GameModelTypes.Count > 0
        || MissionBehaviorTypes.Count > 0
        || CampaignEventHooks.Count > 0
        || LifecycleRegistrations.Count > 0;

    public void AddModelFamily(string family, string typeName)
    {
        if (!ModelFamilies.TryGetValue(family, out HashSet<string>? set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ModelFamilies[family] = set;
        }

        set.Add(typeName);
    }

    public void AddMissionFamily(string family, string typeName)
    {
        if (!MissionFamilies.TryGetValue(family, out HashSet<string>? set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            MissionFamilies[family] = set;
        }

        set.Add(typeName);
    }
}
