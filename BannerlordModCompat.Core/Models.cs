using System.Text.Json.Serialization;

namespace BannerlordModCompat.Core;

public enum CompatibilityState
{
    Compatible,
    LikelyIssue,
    Incompatible,
}

public enum ConflictSeverity
{
    Critical = 4,
    High = 3,
    Medium = 2,
    Low = 1,
    Info = 0,
}

public enum ConflictCategory
{
    MissingDependency,
    ExplicitIncompatibility,
    DependencyVersionMismatch,
    LoadOrderViolation,
    HarmonyPatchConflict,
    HarmonyPatchStack,
    BehaviorEventOverlap,
    GameModelOverlap,
    MissionBehaviorOverlap,
    LifecycleRegistrationOverlap,
    DllCollision,
    XmlEntityCollision,
    ModuleDataFileCollision,
    AssemblyReferenceMismatch,
    SaveFileRisk,
    AnalyzerWarning,
    RuntimeModuleSetMismatch,
    RuntimeLoaderFailure,
    RuntimeCrashSession,
}

public enum ModSourceType
{
    Local,
    SteamWorkshop,
}

public enum FindingEvidenceSource
{
    Unknown,
    StaticMetadata,
    RuntimeLog,
    SaveScan,
    LauncherOrder,
    HarmonyGraph,
    RuntimeCluster,
    DuplicateScanner,
}

public enum FindingEvidenceKind
{
    Unknown,
    Advisory,
    OrderAmbiguity,
    OwnershipClash,
    MissingSaveMod,
    RuntimeLoaderIssue,
    DependencyRule,
    BootstrapRule,
    StabilityPreference,
    Pin,
    RuntimeModuleDrift,
}

public enum FindingEvidenceScope
{
    Unknown,
    Module,
    Method,
    Save,
    Session,
}

public enum LoadOrderRationaleKind
{
    Unknown,
    Dependency,
    Bootstrap,
    StabilityPreference,
    Pin,
    DisabledInstalled,
    AlreadyGood,
    ManualReview,
}

public enum LoadOrderConstraint
{
    LoadBeforeThis,
    LoadAfterThis,
}

public sealed record ScanOptions
{
    public string GameVersion { get; init; } = "1.3.15";
    public List<string> ModuleRoots { get; init; } = [];
    public string? WorkshopRoot { get; init; }
    public string? LauncherDataPath { get; init; }
    public string? SaveRoot { get; init; }
    public string? HarmonyLogsRoot { get; init; }
    public bool IncludeLikelyIssues { get; init; } = true;
    public bool IncludeDataNoiseFindings { get; init; } = false;
    public bool IncludeSaveFileAnalysis { get; init; } = true;
    public bool OfflineMode { get; init; } = true;
    public bool AllowCloudMetadata { get; init; } = false;
    public bool AutoApplyLoadOrder { get; init; }
    public bool CustomModsOnlyFocus { get; init; } = false;
    public List<string> PinnedMods { get; init; } = [];
}

public sealed record ScanProgressUpdate(
    int Percent,
    string Stage
);

public sealed record DiscoveredPaths(
    IReadOnlyList<string> ModuleRoots,
    string? WorkshopRoot,
    string? LauncherDataPath,
    string? SaveRoot,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> HarmonyLogRoots
);

public sealed record ModuleDependency(
    string Id,
    bool Optional,
    string? VersionHint,
    LoadOrderConstraint Order = LoadOrderConstraint.LoadBeforeThis
);

public sealed record XmlEntityReference(
    string EntityKey,
    string EntityType,
    string EntityId,
    string SourceFile
);

public sealed record DllArtifact(
    string Path,
    string FileName,
    string Sha256,
    string? AssemblyName,
    string? AssemblyVersion,
    bool ReferencesHarmony
);

public sealed record ModuleManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public required string RootPath { get; init; }
    public required string SubModulePath { get; init; }
    public required ModSourceType SourceType { get; init; }
    public bool IsOfficial { get; init; }
    public bool IsFramework { get; init; }
    [JsonIgnore]
    public bool IsCustom => !IsOfficial && !IsFramework;
    public List<ModuleDependency> Dependencies { get; init; } = [];
    public List<string> ExplicitIncompatibilities { get; init; } = [];
    public List<XmlEntityReference> XmlEntities { get; init; } = [];
    public List<DllArtifact> Dlls { get; init; } = [];
}

public sealed record ConflictFinding
{
    public required ConflictCategory Category { get; init; }
    public required ConflictSeverity Severity { get; init; }
    public required double Confidence { get; init; }
    public required IReadOnlyList<string> ModuleIds { get; init; }
    public required string Reason { get; init; }
    public string? LikelyInGameOutcome { get; init; }
    public string? Recommendation { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    [JsonIgnore]
    public FindingEvidenceDescriptor? StructuredEvidence { get; init; }
}

public sealed record LoadOrderMove(
    string ModuleId,
    int FromIndex,
    int ToIndex
);

public sealed record LoadOrderRecommendation
{
    public required IReadOnlyList<string> CurrentOrder { get; init; }
    public required IReadOnlyList<string> SuggestedOrder { get; init; }
    public IReadOnlyList<LoadOrderMove> Moves { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> Rationale { get; init; } = [];
    public double Confidence { get; init; } = 0.50;
    [JsonIgnore]
    public IReadOnlyList<LoadOrderModuleRationale> ModuleRationales { get; init; } = [];
}

public sealed record FindingEvidenceDescriptor
{
    public required FindingEvidenceScope Scope { get; init; }
    public IReadOnlyList<FindingEvidenceSource> Sources { get; init; } = [];
    public IReadOnlyList<FindingEvidenceKind> Kinds { get; init; } = [];
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();
}

public sealed record LoadOrderModuleRationale
{
    public required string ModuleId { get; init; }
    public required LoadOrderRationaleKind PrimaryKind { get; init; }
    public IReadOnlyList<LoadOrderRationaleKind> Kinds { get; init; } = [];
    public required string Summary { get; init; }
}

public sealed record SaveFileInsight
{
    public required string SavePath { get; init; }
    public required long FileSizeBytes { get; init; }
    public required IReadOnlyList<string> ReferencedInstalledMods { get; init; }
}

public sealed record ScanReport
{
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required string GameVersion { get; init; }
    public required IReadOnlyList<string> ScannedModuleRoots { get; init; }
    public string? ScannedWorkshopRoot { get; init; }
    public required CompatibilityState OverallState { get; init; }
    public required IReadOnlyList<ModuleManifest> Modules { get; init; }
    public required IReadOnlyList<ConflictFinding> Conflicts { get; init; }
    public required LoadOrderRecommendation LoadOrder { get; init; }
    public required IReadOnlyList<SaveFileInsight> SaveFiles { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    [JsonIgnore]
    public bool HasCritical => Conflicts.Any(c => c.Severity == ConflictSeverity.Critical);
}
