using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace BannerlordModCompat.Core;

public sealed class CompatibilityAnalyzer
{
    private const string SaveRiskEvidencePrefix = "save-risk:";
    private const string SaveRiskMissingDelimiter = "|missing:";

    private static readonly HashSet<string> NewCampaignSensitiveMods = new(StringComparer.OrdinalIgnoreCase)
    {
        "RBM",
        "RealisticBattleMod",
        "RealisticBattleAiModule",
        "RealisticBattleCombatModule",
    };

    private static readonly HashSet<string> HighImpactXmlTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "NPCCharacter",
        "Item",
        "Culture",
        "Kingdom",
        "Settlement",
        "Perk",
        "Hero",
        "CraftingPiece",
        "Faction",
    };

    private static readonly HashSet<string> HighImpactModuleDataFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "spnpccharacters.xml",
        "spnpccharacters.xslt",
        "sandboxcore_equipment_sets.xml",
        "sandboxcore_equipment_sets.xslt",
        "crafting_pieces.xml",
        "items.xml",
        "weapons.xml",
    };

    private static readonly string[] TrackedGameAssemblyPrefixes =
    [
        "TaleWorlds.",
    ];

    private static readonly HashSet<ConflictCategory> LikelyIssueCategories =
    [
        ConflictCategory.HarmonyPatchStack,
        ConflictCategory.BehaviorEventOverlap,
        ConflictCategory.GameModelOverlap,
        ConflictCategory.MissionBehaviorOverlap,
        ConflictCategory.LifecycleRegistrationOverlap,
        ConflictCategory.SaveFileRisk,
        ConflictCategory.AnalyzerWarning,
    ];

    private static readonly HashSet<ConflictCategory> DataNoiseCategories =
    [
        ConflictCategory.DependencyVersionMismatch,
        ConflictCategory.DllCollision,
        ConflictCategory.XmlEntityCollision,
        ConflictCategory.ModuleDataFileCollision,
    ];

    private static readonly HashSet<ConflictCategory> HiddenPlayerFacingCategories =
    [
        ConflictCategory.MissingDependency,
        ConflictCategory.LifecycleRegistrationOverlap,
        ConflictCategory.RuntimeCrashSession,
    ];

    private static readonly HashSet<ConflictCategory> MultiCustomModuleFindingCategories =
    [
        ConflictCategory.ExplicitIncompatibility,
        ConflictCategory.DependencyVersionMismatch,
        ConflictCategory.LoadOrderViolation,
        ConflictCategory.HarmonyPatchConflict,
        ConflictCategory.HarmonyPatchStack,
        ConflictCategory.BehaviorEventOverlap,
        ConflictCategory.GameModelOverlap,
        ConflictCategory.MissionBehaviorOverlap,
        ConflictCategory.LifecycleRegistrationOverlap,
        ConflictCategory.DllCollision,
        ConflictCategory.XmlEntityCollision,
        ConflictCategory.ModuleDataFileCollision,
    ];

    private readonly ModuleScanner _moduleScanner = new();
    private readonly LauncherDataService _launcherData = new();
    private readonly LoadOrderPlanner _loadOrderPlanner = new();
    private readonly SaveFileScanner _saveFileScanner = new();
    private readonly HarmonyPatchLogAnalyzer _harmonyPatchAnalyzer = new();
    private readonly HarmonyPatchStaticAnalyzer _harmonyPatchStaticAnalyzer = new();
    private readonly CodeConflictAnalyzer _codeConflictAnalyzer = new();
    private readonly RuntimeSessionLogAnalyzer _runtimeSessionLogAnalyzer = new();
    private readonly RuntimeEvidenceCorrelator _runtimeEvidenceCorrelator = new();

    public ScanReport Analyze(ScanOptions options)
        => Analyze(options, progress: null);

    public ScanReport Analyze(ScanOptions options, IProgress<ScanProgressUpdate>? progress)
    {
        ReportProgress(progress, 2, "Discovering scan paths...");
        DiscoveredPaths discovered = PathDiscovery.Discover(options);
        List<string> warnings = [.. discovered.Warnings];
        ApplyCloudMetadataMode(options, warnings, progress);

        ReportProgress(progress, 8, "Scanning module manifests...");
        IReadOnlyList<ModuleManifest> modules = _moduleScanner.Scan(
            discovered.ModuleRoots,
            discovered.WorkshopRoot,
            warnings
        );

        ReportProgress(progress, 14, "Reading launcher load order...");
        IReadOnlyList<string> currentOrder = _launcherData.ReadCurrentOrder(
            discovered.LauncherDataPath,
            modules,
            warnings
        );

        IReadOnlyList<ModuleManifest> scopedModules = options.CustomModsOnlyFocus
            ? modules.Where(m => m.IsCustom).ToList()
            : modules;
        IReadOnlyList<string> scopedOrder = options.CustomModsOnlyFocus
            ? currentOrder.Where(id => scopedModules.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList()
            : currentOrder;

        List<ConflictFinding> conflicts = [];
        ReportProgress(progress, 22, "Checking dependency and load-order rules...");
        conflicts.AddRange(AnalyzeDependencies(
            modules,
            currentOrder,
            options.CustomModsOnlyFocus,
            options.IncludeDataNoiseFindings
        ));
        ReportProgress(progress, 34, "Running static Harmony patch analysis...");
        conflicts.AddRange(_harmonyPatchStaticAnalyzer.Analyze(modules, options.CustomModsOnlyFocus, warnings));
        ReportProgress(progress, 42, "Reading Harmony runtime logs (if available)...");
        conflicts.AddRange(_harmonyPatchAnalyzer.Analyze(
            discovered.HarmonyLogRoots,
            modules,
            options.CustomModsOnlyFocus,
            warnings
        ));
        ReportProgress(progress, 54, "Analyzing code-level behavior/model overlap...");
        conflicts.AddRange(_codeConflictAnalyzer.Analyze(modules, options.CustomModsOnlyFocus, warnings));
        ReportProgress(progress, 62, "Analyzing runtime session logs (launcher/watchdog/rgl)...");
        conflicts.AddRange(_runtimeSessionLogAnalyzer.Analyze(
            modules,
            currentOrder,
            options.CustomModsOnlyFocus,
            warnings
        ));
        if (options.IncludeDataNoiseFindings)
        {
            ReportProgress(progress, 68, "Checking DLL collisions...");
            conflicts.AddRange(AnalyzeDllCollisions(modules, options.CustomModsOnlyFocus));
            ReportProgress(progress, 74, "Checking XML and ModuleData collisions...");
            conflicts.AddRange(AnalyzeXmlCollisions(modules, options.CustomModsOnlyFocus));
            conflicts.AddRange(AnalyzeModuleDataFileCollisions(modules, options.CustomModsOnlyFocus, warnings));
        }
        else
        {
            ReportProgress(progress, 74, "Skipping optional data-noise collision checks...");
            warnings.Add("Data-noise findings are disabled by default: skipping dependency version mismatch, DLL collision, and XML/ModuleData overlap checks.");
        }
        ReportProgress(progress, 82, "Checking game API assembly reference mismatches...");
        conflicts.AddRange(AnalyzeAssemblyReferenceMismatches(modules, options.CustomModsOnlyFocus, warnings));

        if (options.CustomModsOnlyFocus)
        {
            warnings.Add("Custom-mod focus enabled: official game modules and common framework modules are hidden in findings.");
        }

        IReadOnlyList<SaveFileInsight> saveInsights = [];
        if (options.IncludeSaveFileAnalysis)
        {
            ReportProgress(progress, 82, "Scanning save files for mod-set risks...");
            saveInsights = _saveFileScanner.Scan(
                discovered.SaveRoot,
                scopedModules,
                warnings,
                (localPercent, localStage) =>
                {
                    // Save-file scanning can be long; map local 0-100 to global 82-97.
                    int mappedPercent = 82 + (int)Math.Round(Math.Clamp(localPercent, 0, 100) * 15.0 / 100.0);
                    ReportProgress(progress, mappedPercent, localStage);
                });
            conflicts.AddRange(AnalyzeSaveFileRisk(saveInsights, scopedOrder));
        }

        ReportProgress(progress, 97, "Correlating runtime evidence with structural findings...");
        conflicts = _runtimeEvidenceCorrelator.Correlate(conflicts, warnings).ToList();

        List<ConflictFinding> noiseFilteredConflicts = FilterDataNoiseFindings(
            conflicts,
            options.IncludeDataNoiseFindings,
            warnings
        );
        List<ConflictFinding> filteredConflicts = FilterLikelyIssueFindings(
            noiseFilteredConflicts,
            options.IncludeLikelyIssues,
            warnings
        );

        ReportProgress(progress, 98, "Building recommended load order...");
        LoadOrderRecommendation fullRecommendation = _loadOrderPlanner.Build(
            modules,
            currentOrder,
            options.PinnedMods,
            filteredConflicts
        );
        warnings.AddRange(fullRecommendation.Warnings);

        LoadOrderRecommendation projectedRecommendation = ProjectLoadOrder(fullRecommendation, scopedModules, options.CustomModsOnlyFocus);
        List<ConflictFinding> playerFacingConflicts = ProjectPlayerFacingFindings(filteredConflicts, modules);
        if (options.AutoApplyLoadOrder)
        {
            ReportProgress(progress, 99, "Applying suggested load order...");
            IReadOnlyList<string> applyOrder = BuildApplySuggestedOrder(fullRecommendation.CurrentOrder, projectedRecommendation.SuggestedOrder);
            _launcherData.TryApplySuggestedOrder(discovered.LauncherDataPath, applyOrder, warnings);
        }

        ScanReport report = new()
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            GameVersion = options.GameVersion,
            ScannedModuleRoots = discovered.ModuleRoots,
            ScannedWorkshopRoot = discovered.WorkshopRoot,
            OverallState = InferState(playerFacingConflicts),
            Modules = scopedModules,
            Conflicts = playerFacingConflicts.OrderByDescending(c => c.Severity).ThenByDescending(c => c.Confidence).ToList(),
            LoadOrder = projectedRecommendation,
            SaveFiles = saveInsights,
            Warnings = warnings,
        };
        ReportProgress(progress, 100, "Scan complete.");
        return report;
    }

    private static void ApplyCloudMetadataMode(
        ScanOptions options,
        List<string> warnings,
        IProgress<ScanProgressUpdate>? progress
    )
    {
        if (!options.AllowCloudMetadata)
        {
            return;
        }

        if (options.OfflineMode)
        {
            warnings.Add("Cloud metadata option is enabled, but offline mode is active. Continuing with local-only analysis.");
            return;
        }

        ReportProgress(progress, 4, "Cloud metadata requested (local-only mode in current build)...");
        warnings.Add("Cloud metadata mode requested, but no cloud metadata provider is configured in this build. Continuing with local-only analysis.");
    }

    private static List<ConflictFinding> FilterLikelyIssueFindings(
        IReadOnlyList<ConflictFinding> findings,
        bool includeLikelyIssues,
        List<string> warnings
    )
    {
        if (includeLikelyIssues)
        {
            return findings.ToList();
        }

        List<ConflictFinding> filtered = findings
            .Where(f => !LikelyIssueCategories.Contains(f.Category))
            .ToList();
        int removed = findings.Count - filtered.Count;
        warnings.Add(
            removed > 0
                ? $"Likely-issue filtering enabled: removed {removed} probabilistic finding(s)."
                : "Likely-issue filtering enabled: no probabilistic findings were present."
        );
        return filtered;
    }

    private static List<ConflictFinding> FilterDataNoiseFindings(
        IReadOnlyList<ConflictFinding> findings,
        bool includeDataNoiseFindings,
        List<string> warnings
    )
    {
        if (includeDataNoiseFindings)
        {
            return findings.ToList();
        }

        List<ConflictFinding> filtered = findings
            .Where(f => !DataNoiseCategories.Contains(f.Category))
            .ToList();
        int removed = findings.Count - filtered.Count;
        if (removed > 0)
        {
            warnings.Add($"Data-noise filtering enabled: removed {removed} dependency/XML/DLL collision finding(s).");
        }

        return filtered;
    }

    private static List<ConflictFinding> ProjectPlayerFacingFindings(
        IReadOnlyList<ConflictFinding> findings,
        IReadOnlyList<ModuleManifest> modules
    )
    {
        Dictionary<string, ModuleManifest> modulesById = modules
            .ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<ConflictFinding> projected = [];

        foreach (ConflictFinding finding in findings)
        {
            if (HiddenPlayerFacingCategories.Contains(finding.Category))
            {
                continue;
            }

            List<string> customModuleIds = [];
            HashSet<string> seenModules = new(StringComparer.OrdinalIgnoreCase);
            foreach (string moduleId in finding.ModuleIds)
            {
                if (!IsPlayerFacingCustomModule(moduleId, modulesById) || !seenModules.Add(moduleId))
                {
                    continue;
                }

                customModuleIds.Add(moduleId);
            }

            if (customModuleIds.Count == 0)
            {
                continue;
            }

            if (MultiCustomModuleFindingCategories.Contains(finding.Category) && customModuleIds.Count < 2)
            {
                continue;
            }

            ConflictFinding projectedFinding = customModuleIds.Count == finding.ModuleIds.Count
                ? finding
                : finding with { ModuleIds = customModuleIds };

            string dedupeKey = BuildProjectedFindingKey(projectedFinding);
            if (seen.Add(dedupeKey))
            {
                projected.Add(projectedFinding);
            }
        }

        return projected;
    }

    private static bool IsPlayerFacingCustomModule(
        string moduleId,
        IReadOnlyDictionary<string, ModuleManifest> modulesById
    )
    {
        if (modulesById.TryGetValue(moduleId, out ModuleManifest? manifest))
        {
            return manifest.IsCustom;
        }

        return ModuleTaxonomy.IsCustom(moduleId);
    }

    private static string BuildProjectedFindingKey(ConflictFinding finding)
    {
        return string.Join("||",
            finding.Category,
            finding.Severity,
            Math.Round(finding.Confidence, 4).ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture),
            string.Join("|", finding.ModuleIds),
            finding.Reason,
            finding.LikelyInGameOutcome ?? string.Empty,
            finding.Recommendation ?? string.Empty,
            string.Join("|", finding.Evidence));
    }

    private static void ReportProgress(IProgress<ScanProgressUpdate>? progress, int percent, string stage)
    {
        if (progress is null)
        {
            return;
        }

        int normalized = Math.Clamp(percent, 0, 100);
        progress.Report(new ScanProgressUpdate(normalized, stage));
    }

    private static CompatibilityState InferState(IReadOnlyList<ConflictFinding> conflicts)
    {
        if (conflicts.Any(c => c.Severity == ConflictSeverity.Critical))
        {
            return CompatibilityState.Incompatible;
        }

        if (conflicts.Any(c => c.Severity is ConflictSeverity.High or ConflictSeverity.Medium))
        {
            return CompatibilityState.LikelyIssue;
        }

        return CompatibilityState.Compatible;
    }

    private static IEnumerable<ConflictFinding> AnalyzeDependencies(
        IReadOnlyList<ModuleManifest> modules,
        IReadOnlyList<string> currentOrder,
        bool customOnlyFocus,
        bool includeDependencyVersionMismatches
    )
    {
        Dictionary<string, ModuleManifest> byId = modules.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> orderIndex = currentOrder
            .Select((id, index) => new { id, index })
            .GroupBy(x => x.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);

        List<ConflictFinding> findings = [];
        foreach (ModuleManifest module in modules)
        {
            if (customOnlyFocus && !module.IsCustom)
            {
                continue;
            }

            foreach (ModuleDependency dep in module.Dependencies)
            {
                if (customOnlyFocus && !ModuleTaxonomy.IsCustom(dep.Id))
                {
                    continue;
                }

                if (!byId.TryGetValue(dep.Id, out ModuleManifest? depMod))
                {
                    ConflictSeverity severity = dep.Optional ? ConflictSeverity.Low : ConflictSeverity.Critical;
                    findings.Add(new ConflictFinding
                    {
                        Category = ConflictCategory.MissingDependency,
                        Severity = severity,
                        Confidence = dep.Optional ? 0.60 : 1.0,
                        ModuleIds = [module.Id, dep.Id],
                        Reason = $"Dependency '{dep.Id}' required by '{module.Id}' is not installed.",
                        LikelyInGameOutcome = dep.Optional
                            ? "Optional features may not behave as expected."
                            : "Game may fail to launch or crash when the module initializes.",
                        Recommendation = $"Install '{dep.Id}' or disable '{module.Id}'.",
                        Evidence = [module.SubModulePath],
                    });
                    continue;
                }

                if (customOnlyFocus && !depMod.IsCustom)
                {
                    continue;
                }

                if (includeDependencyVersionMismatches
                    && !string.IsNullOrWhiteSpace(dep.VersionHint)
                    && !string.IsNullOrWhiteSpace(depMod.Version)
                    && !depMod.Version.Contains(dep.VersionHint, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new ConflictFinding
                    {
                        Category = ConflictCategory.DependencyVersionMismatch,
                        Severity = ConflictSeverity.Medium,
                        Confidence = 0.85,
                        ModuleIds = [module.Id, depMod.Id],
                        Reason = $"'{module.Id}' expects '{dep.Id}' version hint '{dep.VersionHint}' but found '{depMod.Version}'.",
                        LikelyInGameOutcome = "Unexpected runtime behavior or missing features in dependent systems.",
                        Recommendation = $"Match '{dep.Id}' version to '{dep.VersionHint}' if available.",
                        Evidence = [module.SubModulePath, depMod.SubModulePath],
                    });
                }

                if (orderIndex.TryGetValue(module.Id, out int moduleIdx)
                    && orderIndex.TryGetValue(dep.Id, out int depIdx)
                    && (
                        (dep.Order == LoadOrderConstraint.LoadAfterThis && depIdx < moduleIdx)
                        || (dep.Order == LoadOrderConstraint.LoadBeforeThis && depIdx > moduleIdx)
                    ))
                {
                    if (customOnlyFocus && (!module.IsCustom || !depMod.IsCustom))
                    {
                        continue;
                    }

                    ConflictSeverity severity = dep.Optional ? ConflictSeverity.Medium : ConflictSeverity.High;
                    double confidence = dep.Optional ? 0.78 : 0.90;
                    bool expectsAfter = dep.Order == LoadOrderConstraint.LoadAfterThis;
                    string relation = expectsAfter ? "after" : "before";
                    string reason = dep.Optional
                        ? $"Optional dependency '{dep.Id}' loads in the wrong position relative to '{module.Id}' (expected {relation})."
                        : $"'{dep.Id}' is in the wrong load-order position relative to '{module.Id}' (expected {relation}).";
                    string recommendation = dep.Optional
                        ? expectsAfter
                            ? $"If '{dep.Id}' is installed, place it after '{module.Id}' in load order."
                            : $"If '{dep.Id}' is installed, place it before '{module.Id}' in load order."
                        : expectsAfter
                            ? $"Place '{dep.Id}' after '{module.Id}' in load order."
                            : $"Place '{dep.Id}' before '{module.Id}' in load order.";

                    findings.Add(new ConflictFinding
                    {
                        Category = ConflictCategory.LoadOrderViolation,
                        Severity = severity,
                        Confidence = confidence,
                        ModuleIds = [dep.Id, module.Id],
                        Reason = reason,
                        LikelyInGameOutcome = "Patch application order can break startup or create silent behavior issues.",
                        Recommendation = recommendation,
                        Evidence = [],
                    });
                }
            }

            foreach (string incompatibleId in module.ExplicitIncompatibilities)
            {
                if (byId.TryGetValue(incompatibleId, out ModuleManifest? incompatible))
                {
                    if (customOnlyFocus && (!module.IsCustom || !incompatible.IsCustom))
                    {
                        continue;
                    }

                    findings.Add(new ConflictFinding
                    {
                        Category = ConflictCategory.ExplicitIncompatibility,
                        Severity = ConflictSeverity.Critical,
                        Confidence = 1.0,
                        ModuleIds = [module.Id, incompatibleId],
                        Reason = $"'{module.Id}' explicitly declares incompatibility with '{incompatibleId}'.",
                        LikelyInGameOutcome = "High chance of startup crashes, hard errors, or unstable campaign behavior.",
                        Recommendation = $"Disable either '{module.Id}' or '{incompatibleId}'.",
                        Evidence = [module.SubModulePath],
                    });
                }
            }
        }

        return findings;
    }

    private static IEnumerable<ConflictFinding> AnalyzeDllCollisions(IReadOnlyList<ModuleManifest> modules, bool customOnlyFocus)
    {
        IReadOnlyList<ModuleManifest> scanScope = customOnlyFocus
            ? modules.Where(m => m.IsCustom).ToList()
            : modules;
        Dictionary<string, bool> isCustomById = scanScope
            .ToDictionary(m => m.Id, m => m.IsCustom, StringComparer.OrdinalIgnoreCase);

        var fileGroups = scanScope
            .SelectMany(m => m.Dlls.Select(dll => new { Module = m, Dll = dll }))
            .GroupBy(x => x.Dll.FileName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Module.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);

        Dictionary<string, PairAggregation> pairAgg = new(StringComparer.OrdinalIgnoreCase);

        foreach (var group in fileGroups)
        {
            string[] hashes = group.Select(x => x.Dll.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (hashes.Length <= 1)
            {
                continue;
            }

            Dictionary<string, HashSet<string>> hashesByModule = group
                .GroupBy(x => x.Module.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => x.Dll.Sha256)
                        .Where(hash => !string.IsNullOrWhiteSpace(hash))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase
                );
            Dictionary<string, List<string>> evidencePathsByModule = group
                .GroupBy(x => x.Module.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => x.Dll.Path)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(4)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase
                );

            string[] ids = group.Select(x => x.Module.Id).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            for (int i = 0; i < ids.Length; i++)
            {
                for (int j = i + 1; j < ids.Length; j++)
                {
                    string left = ids[i];
                    string right = ids[j];
                    bool leftCustom = isCustomById.GetValueOrDefault(left);
                    bool rightCustom = isCustomById.GetValueOrDefault(right);
                    if (!leftCustom && !rightCustom)
                    {
                        continue;
                    }

                    if (!hashesByModule.TryGetValue(left, out HashSet<string>? leftHashes))
                    {
                        leftHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }
                    if (!hashesByModule.TryGetValue(right, out HashSet<string>? rightHashes))
                    {
                        rightHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }
                    bool mismatch = !leftHashes.SetEquals(rightHashes);
                    if (!mismatch)
                    {
                        continue;
                    }

                    string pairKey = $"{left}|{right}";

                    if (!pairAgg.TryGetValue(pairKey, out PairAggregation? agg))
                    {
                        agg = new PairAggregation(left, right);
                        pairAgg[pairKey] = agg;
                    }

                    agg.FileNames.Add(group.Key);
                    if (evidencePathsByModule.TryGetValue(left, out List<string>? leftEvidence))
                    {
                        foreach (string path in leftEvidence)
                        {
                            agg.EvidencePaths.Add(path);
                        }
                    }
                    if (evidencePathsByModule.TryGetValue(right, out List<string>? rightEvidence))
                    {
                        foreach (string path in rightEvidence)
                        {
                            agg.EvidencePaths.Add(path);
                        }
                    }
                }
            }
        }

        List<ConflictFinding> findings = [];
        foreach (PairAggregation agg in pairAgg.Values.OrderByDescending(x => x.FileNames.Count))
        {
            string sample = string.Join(", ", agg.FileNames.Take(5));
            if (agg.FileNames.Count > 5)
            {
                sample += $", +{agg.FileNames.Count - 5} more";
            }

            findings.Add(new ConflictFinding
            {
                Category = ConflictCategory.DllCollision,
                Severity = ConflictSeverity.High,
                Confidence = 0.92,
                ModuleIds = [agg.LeftModuleId, agg.RightModuleId],
                Reason = $"Modules ship {agg.FileNames.Count} DLL(s) with the same name but different binaries ({sample}).",
                LikelyInGameOutcome = "Type resolution collisions, method mismatch errors, or hard crashes.",
                Recommendation = "Use one canonical provider/build or verify pair compatibility from mod authors.",
                Evidence = agg.EvidencePaths.Take(8).ToList(),
            });
        }

        return findings;
    }

    private static IEnumerable<ConflictFinding> AnalyzeXmlCollisions(IReadOnlyList<ModuleManifest> modules, bool customOnlyFocus)
    {
        List<ConflictFinding> findings = [];
        IReadOnlyList<ModuleManifest> scanScope = customOnlyFocus
            ? modules.Where(m => m.IsCustom).ToList()
            : modules;

        var groups = scanScope
            .SelectMany(m => m.XmlEntities.Select(entity => new { Module = m, Entity = entity }))
            .GroupBy(x => x.Entity.EntityKey, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Module.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);

        foreach (var group in groups)
        {
            bool hasNonOfficial = group.Any(x => !x.Module.IsOfficial);
            if (!hasNonOfficial)
            {
                continue;
            }

            string[] ids = group.Select(x => x.Module.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string entityType = group.First().Entity.EntityType;
            string entityId = group.First().Entity.EntityId;
            bool highImpact = HighImpactXmlTypes.Contains(entityType);

            findings.Add(new ConflictFinding
            {
                Category = ConflictCategory.XmlEntityCollision,
                Severity = highImpact ? ConflictSeverity.High : ConflictSeverity.Medium,
                Confidence = highImpact ? 0.80 : 0.62,
                ModuleIds = ids,
                Reason = $"Entity '{entityType}:{entityId}' is defined/overridden by multiple modules.",
                LikelyInGameOutcome = highImpact
                    ? "Data overrides can alter gameplay balance, UI assumptions, or campaign progression."
                    : "Potential data override conflict depending on load order and XML merge behavior.",
                Recommendation = "Review load order and mod docs for explicit patch/compatibility instructions.",
                Evidence = group.Select(x => $"{x.Module.Id}:{x.Entity.SourceFile}").Take(8).ToList(),
            });
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.ModuleIds.Count)
            .ThenByDescending(f => f.Confidence)
            .Take(220)
            .ToList();
    }

    private static IEnumerable<ConflictFinding> AnalyzeModuleDataFileCollisions(
        IReadOnlyList<ModuleManifest> modules,
        bool customOnlyFocus,
        List<string> warnings
    )
    {
        IReadOnlyList<ModuleManifest> scanScope = customOnlyFocus
            ? modules.Where(m => m.IsCustom).ToList()
            : modules.Where(m => !m.IsOfficial).ToList();
        Dictionary<string, bool> isCustomById = scanScope
            .ToDictionary(m => m.Id, m => m.IsCustom, StringComparer.OrdinalIgnoreCase);

        List<ModuleDataFileRecord> records = [];
        foreach (ModuleManifest module in scanScope)
        {
            string moduleDataPath = Path.Combine(module.RootPath, "ModuleData");
            if (!Directory.Exists(moduleDataPath))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(moduleDataPath, "*.*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                warnings.Add($"ModuleData scan warning in '{module.Id}': {ex.Message}");
                continue;
            }

            foreach (string path in files)
            {
                string extension = Path.GetExtension(path);
                if (!extension.Equals(".xslt", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string fileName = Path.GetFileName(path);
                bool monitor = extension.Equals(".xslt", StringComparison.OrdinalIgnoreCase)
                    || HighImpactModuleDataFiles.Contains(fileName);
                if (!monitor)
                {
                    continue;
                }

                string? hash = TryComputeSha256(path, warnings);
                if (hash is null)
                {
                    continue;
                }

                string relative = Path.GetRelativePath(module.RootPath, path);
                records.Add(new ModuleDataFileRecord(module.Id, fileName, extension, relative, hash));
            }
        }

        if (records.Count == 0)
        {
            return [];
        }

        List<ConflictFinding> findings = [];
        foreach (IGrouping<string, ModuleDataFileRecord> fileGroup in records
                     .GroupBy(r => r.FileName, StringComparer.OrdinalIgnoreCase))
        {
            string[] modulesInGroup = fileGroup
                .Select(r => r.ModuleId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (modulesInGroup.Length < 2)
            {
                continue;
            }
            if (!modulesInGroup.Any(id => isCustomById.GetValueOrDefault(id)))
            {
                continue;
            }

            string[] hashes = fileGroup
                .Select(r => r.Sha256)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (hashes.Length <= 1)
            {
                continue;
            }

            bool isTransform = fileGroup.Any(r => r.Extension.Equals(".xslt", StringComparison.OrdinalIgnoreCase));
            bool highImpact = isTransform || HighImpactModuleDataFiles.Contains(fileGroup.Key);
            ConflictSeverity severity = highImpact ? ConflictSeverity.High : ConflictSeverity.Medium;
            double confidence = highImpact ? 0.82 : 0.72;

            findings.Add(new ConflictFinding
            {
                Category = ConflictCategory.ModuleDataFileCollision,
                Severity = severity,
                Confidence = confidence,
                ModuleIds = modulesInGroup,
                Reason = $"Multiple modules ship different versions of ModuleData file '{fileGroup.Key}'.",
                LikelyInGameOutcome = isTransform
                    ? "Competing XML transforms can rewrite base game/mod data in different ways depending on load order."
                    : "Data ownership for this file can drift by load order and alter behavior or balance unexpectedly.",
                Recommendation = "Keep one canonical provider for this file or add an explicit compatibility patch that merges both changes.",
                Evidence = fileGroup
                    .Select(r => $"{r.ModuleId}:{r.RelativePath}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToList(),
            });
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.ModuleIds.Count)
            .Take(120)
            .ToList();
    }

    private static IEnumerable<ConflictFinding> AnalyzeAssemblyReferenceMismatches(
        IReadOnlyList<ModuleManifest> modules,
        bool customOnlyFocus,
        List<string> warnings
    )
    {
        IReadOnlyList<ModuleManifest> scanScope = customOnlyFocus
            ? modules.Where(m => m.IsCustom).ToList()
            : modules.Where(m => !m.IsOfficial).ToList();
        Dictionary<string, bool> isCustomById = scanScope
            .ToDictionary(m => m.Id, m => m.IsCustom, StringComparer.OrdinalIgnoreCase);

        List<AssemblyReferenceRecord> records = [];
        foreach (ModuleManifest module in scanScope)
        {
            foreach (DllArtifact dll in module.Dlls.Where(d => File.Exists(d.Path)))
            {
                try
                {
                    using FileStream stream = File.OpenRead(dll.Path);
                    using PEReader peReader = new(stream);
                    if (!peReader.HasMetadata)
                    {
                        continue;
                    }

                    MetadataReader reader = peReader.GetMetadataReader();
                    foreach (AssemblyReferenceHandle handle in reader.AssemblyReferences)
                    {
                        AssemblyReference reference = reader.GetAssemblyReference(handle);
                        string referenceName = reader.GetString(reference.Name);
                        if (string.IsNullOrWhiteSpace(referenceName) || !IsTrackedGameAssembly(referenceName))
                        {
                            continue;
                        }

                        Version? version = reference.Version;
                        if (version is null)
                        {
                            continue;
                        }

                        records.Add(new AssemblyReferenceRecord(
                            ModuleId: module.Id,
                            DllPath: dll.Path,
                            ReferenceName: referenceName,
                            ReferenceVersion: version
                        ));
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Assembly reference scan warning in '{dll.Path}': {ex.Message}");
                }
            }
        }

        if (records.Count == 0)
        {
            return [];
        }

        List<ConflictFinding> findings = [];
        foreach (IGrouping<string, AssemblyReferenceRecord> referenceGroup in records
                     .GroupBy(r => r.ReferenceName, StringComparer.OrdinalIgnoreCase))
        {
            string[] moduleIds = referenceGroup
                .Select(r => r.ModuleId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (moduleIds.Length < 2)
            {
                continue;
            }
            if (!moduleIds.Any(id => isCustomById.GetValueOrDefault(id)))
            {
                continue;
            }

            Version[] versions = referenceGroup
                .Select(r => r.ReferenceVersion)
                .Distinct()
                .OrderBy(v => v)
                .ToArray();
            if (versions.Length <= 1)
            {
                continue;
            }

            bool majorMinorDrift = referenceGroup
                .Select(r => (r.ReferenceVersion.Major, r.ReferenceVersion.Minor))
                .Distinct()
                .Count() > 1;

            ConflictSeverity severity = majorMinorDrift
                ? ConflictSeverity.High
                : ConflictSeverity.Medium;
            double confidence = majorMinorDrift
                ? 0.88
                : 0.74;

            string versionSummary = string.Join("; ",
                referenceGroup
                    .GroupBy(r => r.ReferenceVersion)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key)
                    .Take(4)
                    .Select(g =>
                    {
                        string owners = string.Join(", ", g.Select(x => x.ModuleId)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                            .Take(4));
                        return $"v{g.Key} ({owners})";
                    }));

            findings.Add(new ConflictFinding
            {
                Category = ConflictCategory.AssemblyReferenceMismatch,
                Severity = severity,
                Confidence = confidence,
                ModuleIds = moduleIds,
                Reason = $"Modules reference different versions of game assembly '{referenceGroup.Key}' ({versionSummary}).",
                LikelyInGameOutcome = "Mixed game API baselines can cause MissingMethod/TypeLoad errors or unstable behavior at runtime.",
                Recommendation = "Use mod builds compiled for the same Bannerlord version and recheck this reference family.",
                Evidence = referenceGroup
                    .Select(r => $"{r.ModuleId}:{Path.GetFileName(r.DllPath)} -> {r.ReferenceName} {r.ReferenceVersion}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToList(),
            });
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.Confidence)
            .Take(80)
            .ToList();
    }

    private static bool IsTrackedGameAssembly(string assemblyName)
    {
        return TrackedGameAssemblyPrefixes.Any(prefix =>
            assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryComputeSha256(string path, List<string> warnings)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            byte[] hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash);
        }
        catch (Exception ex)
        {
            warnings.Add($"File hash warning in '{path}': {ex.Message}");
            return null;
        }
    }

    private static IEnumerable<ConflictFinding> AnalyzeSaveFileRisk(
        IReadOnlyList<SaveFileInsight> saveInsights,
        IReadOnlyList<string> currentOrder
    )
    {
        HashSet<string> current = currentOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> missingMods = new(StringComparer.OrdinalIgnoreCase);
        List<string> affectedSaveNames = [];
        List<string> saveEvidence = [];

        foreach (SaveFileInsight save in saveInsights)
        {
            string[] modsOutsideCurrentOrder = save.ReferencedInstalledMods
                .Where(id => !current.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (modsOutsideCurrentOrder.Length == 0)
            {
                continue;
            }

            foreach (string id in modsOutsideCurrentOrder)
            {
                missingMods.Add(id);
            }

            affectedSaveNames.Add(Path.GetFileName(save.SavePath));
            saveEvidence.Add($"{SaveRiskEvidencePrefix}{save.SavePath}{SaveRiskMissingDelimiter}{string.Join(",", modsOutsideCurrentOrder)}");
        }

        if (affectedSaveNames.Count == 0)
        {
            return [];
        }

        string saveSample = string.Join(", ", affectedSaveNames.Take(6));
        if (affectedSaveNames.Count > 6)
        {
            saveSample += $", +{affectedSaveNames.Count - 6} more";
        }

        bool hasNewCampaignSensitiveMod = missingMods.Any(id => NewCampaignSensitiveMods.Contains(id));
        string recommendation = hasNewCampaignSensitiveMod
            ? "RBM-style campaign-changing mods are missing from this profile. For safety, restore the original mod set or start a new campaign."
            : "Re-enable referenced modules or use a profile matching the save's original mod set.";

        string outcome = hasNewCampaignSensitiveMod
            ? "Affected saves can break progression or produce unstable campaign state when loaded without those campaign-changing mods."
            : "Loading affected saves may fail or drop data tied to missing modules.";

        ConflictFinding aggregate = new()
        {
            Category = ConflictCategory.SaveFileRisk,
            Severity = ConflictSeverity.High,
            Confidence = hasNewCampaignSensitiveMod ? 0.84 : 0.78,
            ModuleIds = missingMods.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            Reason = $"{affectedSaveNames.Count} save(s) reference modules missing from current load order. Affected saves: {saveSample}.",
            LikelyInGameOutcome = outcome,
            Recommendation = recommendation,
            Evidence = saveEvidence
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

        return [aggregate];
    }

    private static LoadOrderRecommendation ProjectLoadOrder(
        LoadOrderRecommendation full,
        IReadOnlyList<ModuleManifest> scopedModules,
        bool customOnlyFocus
    )
    {
        HashSet<string> scopedIds = scopedModules
            .Select(m => m.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool hideSingleplayerIrrelevantOfficialModules = full.CurrentOrder.Any(ModuleTaxonomy.IsSingleplayerHiddenLoadOrderModule)
            || full.SuggestedOrder.Any(ModuleTaxonomy.IsSingleplayerHiddenLoadOrderModule);
        if (!customOnlyFocus && !hideSingleplayerIrrelevantOfficialModules)
        {
            return full;
        }

        bool ShouldInclude(string id)
        {
            if (ModuleTaxonomy.IsSingleplayerHiddenLoadOrderModule(id))
            {
                return false;
            }

            return !customOnlyFocus || scopedIds.Contains(id);
        }

        List<string> current = full.CurrentOrder
            .Where(ShouldInclude)
            .ToList();
        List<string> suggested = full.SuggestedOrder
            .Where(ShouldInclude)
            .ToList();
        List<LoadOrderMove> moves = BuildMoves(current, suggested);
        List<string> warnings = [.. full.Warnings];
        List<string> rationale = [.. full.Rationale];
        if (customOnlyFocus)
        {
            warnings.Add("Load order view is filtered to custom singleplayer mods only.");
            rationale.Add("Filtered view keeps only custom modules; hidden framework/core modules remain anchored and still influence final order.");
        }

        if (hideSingleplayerIrrelevantOfficialModules)
        {
            warnings.Add("Singleplayer load-order view hides Multiplayer and keeps it pinned outside the visible recommendation.");
            rationale.Add("Singleplayer projection excludes Multiplayer from the player-facing move plan and preserves its existing slot on apply.");
        }

        return new LoadOrderRecommendation
        {
            CurrentOrder = current,
            SuggestedOrder = suggested,
            Moves = moves,
            Warnings = warnings,
            Rationale = rationale,
            Confidence = full.Confidence,
        };
    }

    private static IReadOnlyList<string> BuildApplySuggestedOrder(
        IReadOnlyList<string> currentOrder,
        IReadOnlyList<string> visibleSuggestedOrder
    )
    {
        HashSet<string> visibleIds = visibleSuggestedOrder
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Queue<string> visibleQueue = new(visibleSuggestedOrder);
        List<string> result = new(currentOrder.Count + Math.Max(0, visibleSuggestedOrder.Count - currentOrder.Count));

        foreach (string currentId in currentOrder)
        {
            if (!visibleIds.Contains(currentId))
            {
                result.Add(currentId);
                continue;
            }

            if (visibleQueue.Count > 0)
            {
                result.Add(visibleQueue.Dequeue());
            }
        }

        while (visibleQueue.Count > 0)
        {
            result.Add(visibleQueue.Dequeue());
        }

        return result;
    }

    private static List<LoadOrderMove> BuildMoves(IReadOnlyList<string> current, IReadOnlyList<string> suggested)
    {
        Dictionary<string, int> currentIndex = current
            .Select((id, index) => new { id, index })
            .ToDictionary(x => x.id, x => x.index, StringComparer.OrdinalIgnoreCase);

        List<LoadOrderMove> result = [];
        for (int i = 0; i < suggested.Count; i++)
        {
            string id = suggested[i];
            if (!currentIndex.TryGetValue(id, out int from) || from == i)
            {
                continue;
            }

            result.Add(new LoadOrderMove(id, from, i));
        }

        return result;
    }

    private sealed class PairAggregation
    {
        public PairAggregation(string leftModuleId, string rightModuleId)
        {
            LeftModuleId = leftModuleId;
            RightModuleId = rightModuleId;
        }

        public string LeftModuleId { get; }
        public string RightModuleId { get; }
        public HashSet<string> FileNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> EvidencePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record ModuleDataFileRecord(
        string ModuleId,
        string FileName,
        string Extension,
        string RelativePath,
        string Sha256
    );

    private sealed record AssemblyReferenceRecord(
        string ModuleId,
        string DllPath,
        string ReferenceName,
        Version ReferenceVersion
    );
}
