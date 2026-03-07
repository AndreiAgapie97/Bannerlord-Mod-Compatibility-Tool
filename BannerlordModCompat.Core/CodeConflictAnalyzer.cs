using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace BannerlordModCompat.Core;

public sealed class CodeConflictAnalyzer
{
    private const string CampaignBehaviorBaseType = "TaleWorlds.CampaignSystem.CampaignBehaviors.CampaignBehaviorBase";
    private const string GameModelBaseType = "TaleWorlds.Core.GameModel";
    private const string MissionBehaviorBaseType = "TaleWorlds.MountAndBlade.MissionBehavior";
    private const string MissionLogicBaseType = "TaleWorlds.MountAndBlade.MissionLogic";
    private const string CampaignEventsType = "TaleWorlds.CampaignSystem.CampaignEvents";
    private const string CampaignGameStarterType = "TaleWorlds.CampaignSystem.CampaignGameStarter";
    private const string MissionType = "TaleWorlds.MountAndBlade.Mission";
    private const string SubModuleBaseType = "TaleWorlds.MountAndBlade.MBSubModuleBase";

    private static readonly HashSet<string> LifecycleHookMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "OnSubModuleLoad",
        "OnBeforeGameStart",
        "OnGameStart",
        "InitializeGameStarter",
        "OnGameLoaded",
        "OnAfterGameLoaded",
        "OnNewGameCreated",
        "BeginGameStart",
        "OnGameInitializationFinished",
        "OnAfterGameInitializationFinished",
        "OnCampaignStart",
        "OnBeforeMissionBehaviorInitialize",
        "OnMissionBehaviorInitialize",
    };

    private static readonly string[] AssemblyIgnorePrefixes =
    [
        "TaleWorlds.",
        "System.",
        "Microsoft.",
        "Mono.",
    ];

    private static readonly HashSet<string> AssemblyIgnoreNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib",
        "netstandard",
        "0Harmony",
        "HarmonyLib",
        "Bannerlord.Harmony",
        "Bannerlord.ButterLib",
        "Bannerlord.UIExtenderEx",
        "Bannerlord.MBOptionScreen",
        "MCMv5",
        "MCMv4",
    };

    public IReadOnlyList<ConflictFinding> Analyze(
        IReadOnlyList<ModuleManifest> modules,
        bool customOnlyFocus,
        List<string> warnings
    )
    {
        IReadOnlyList<ModuleManifest> scanScope = customOnlyFocus
            ? modules.Where(m => m.IsCustom).ToList()
            : modules.Where(m => !m.IsOfficial).ToList();

        if (scanScope.Count == 0)
        {
            return [];
        }

        TypeHierarchyIndex hierarchy = BuildHierarchy(modules, warnings);
        List<ModuleCodeProfile> profiles = [];
        foreach (ModuleManifest module in scanScope)
        {
            ModuleCodeProfile profile = BuildModuleProfile(module, hierarchy, warnings);
            if (!profile.HasSignals)
            {
                continue;
            }

            profiles.Add(profile);
        }

        if (profiles.Count <= 1)
        {
            return [];
        }

        List<ConflictFinding> findings = [];
        findings.AddRange(AnalyzeModelOverlaps(profiles));
        findings.AddRange(AnalyzeBehaviorEventOverlaps(profiles));
        findings.AddRange(AnalyzeMissionBehaviorOverlaps(profiles));
        findings.AddRange(AnalyzeLifecycleRegistrationOverlaps(profiles));
        return findings;
    }

    private static TypeHierarchyIndex BuildHierarchy(
        IReadOnlyList<ModuleManifest> modules,
        List<string> warnings
    )
    {
        TypeHierarchyIndex index = new();
        IEnumerable<DllArtifact> dlls = modules
            .SelectMany(m => m.Dlls)
            .Where(d => File.Exists(d.Path))
            .GroupBy(d => d.Sha256, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());

        foreach (DllArtifact dll in dlls)
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
                Dictionary<TypeDefinitionHandle, string> localTypeNames = reader.TypeDefinitions
                    .ToDictionary(h => h, h => GetTypeFullName(reader, h));

                foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
                {
                    TypeDefinition typeDef = reader.GetTypeDefinition(handle);
                    string typeName = localTypeNames[handle];
                    string? baseName = ResolveTypeName(reader, typeDef.BaseType, localTypeNames);
                    index.Add(typeName, baseName);
                }
            }
            catch (Exception ex)
            {
                AddWarning(warnings, $"Code hierarchy parse warning in '{dll.Path}': {ex.Message}");
            }
        }

        return index;
    }

    private static ModuleCodeProfile BuildModuleProfile(
        ModuleManifest module,
        TypeHierarchyIndex hierarchy,
        List<string> warnings
    )
    {
        ModuleCodeProfile profile = new(module.Id);
        foreach (DllArtifact dll in module.Dlls)
        {
            if (!File.Exists(dll.Path) || !IsModCodeAssembly(dll))
            {
                continue;
            }

            try
            {
                using FileStream stream = File.OpenRead(dll.Path);
                using PEReader peReader = new(stream);
                if (!peReader.HasMetadata)
                {
                    continue;
                }

                MetadataReader reader = peReader.GetMetadataReader();
                Dictionary<TypeDefinitionHandle, string> localTypeNames = reader.TypeDefinitions
                    .ToDictionary(h => h, h => GetTypeFullName(reader, h));

                foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
                {
                    TypeDefinition typeDef = reader.GetTypeDefinition(handle);
                    string typeName = localTypeNames[handle];
                    if (hierarchy.InheritsFrom(typeName, CampaignBehaviorBaseType))
                    {
                        profile.CampaignBehaviorTypes.Add(typeName);
                    }

                    if (hierarchy.InheritsFrom(typeName, GameModelBaseType))
                    {
                        profile.GameModelTypes.Add(typeName);
                        string family = hierarchy.GetFamilyClosestTo(typeName, GameModelBaseType) ?? typeName;
                        profile.AddModelFamily(family, typeName);
                    }

                    if (hierarchy.InheritsFrom(typeName, MissionBehaviorBaseType)
                        || hierarchy.InheritsFrom(typeName, MissionLogicBaseType))
                    {
                        profile.MissionBehaviorTypes.Add(typeName);
                        string family = hierarchy.GetFamilyClosestTo(typeName, MissionBehaviorBaseType)
                            ?? hierarchy.GetFamilyClosestTo(typeName, MissionLogicBaseType)
                            ?? typeName;
                        profile.AddMissionFamily(family, typeName);
                    }

                    if (hierarchy.InheritsFrom(typeName, SubModuleBaseType))
                    {
                        CollectLifecycleRegistrations(
                            profile,
                            typeName,
                            dll.Path,
                            typeDef,
                            peReader,
                            reader,
                            localTypeNames
                        );
                    }
                }

                foreach (MemberReferenceHandle handle in reader.MemberReferences)
                {
                    MemberReference member = reader.GetMemberReference(handle);
                    string memberName = reader.GetString(member.Name);
                    string? parentType = ResolveTypeName(reader, member.Parent, localTypeNames);
                    if (string.IsNullOrWhiteSpace(parentType))
                    {
                        continue;
                    }

                    if (parentType.Equals(CampaignEventsType, StringComparison.OrdinalIgnoreCase))
                    {
                        string? eventName = NormalizeCampaignEventName(memberName);
                        if (!string.IsNullOrWhiteSpace(eventName))
                        {
                            profile.CampaignEventHooks.Add(eventName);
                        }
                    }
                    else if (parentType.Equals(CampaignGameStarterType, StringComparison.OrdinalIgnoreCase))
                    {
                        if (memberName.Equals("AddBehavior", StringComparison.OrdinalIgnoreCase))
                        {
                            profile.CallsAddBehavior = true;
                        }
                        else if (memberName.Equals("AddModel", StringComparison.OrdinalIgnoreCase))
                        {
                            profile.CallsAddModel = true;
                        }
                    }
                    else if (parentType.Equals(MissionType, StringComparison.OrdinalIgnoreCase)
                             && memberName.Equals("AddMissionBehavior", StringComparison.OrdinalIgnoreCase))
                    {
                        profile.CallsAddMissionBehavior = true;
                    }
                }
            }
            catch (Exception ex)
            {
                AddWarning(warnings, $"Code analysis warning in '{dll.Path}': {ex.Message}");
            }
        }

        return profile;
    }

    private static IEnumerable<ConflictFinding> AnalyzeModelOverlaps(IReadOnlyList<ModuleCodeProfile> profiles)
    {
        Dictionary<string, List<(string ModuleId, string TypeName)>> byFamily = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModuleCodeProfile profile in profiles)
        {
            foreach ((string family, HashSet<string> typeNames) in profile.ModelFamilies)
            {
                if (!byFamily.TryGetValue(family, out List<(string ModuleId, string TypeName)>? list))
                {
                    list = [];
                    byFamily[family] = list;
                }

                foreach (string typeName in typeNames)
                {
                    list.Add((profile.ModuleId, typeName));
                }
            }
        }

        List<ConflictFinding> findings = [];
        foreach ((string family, List<(string ModuleId, string TypeName)> entries) in byFamily)
        {
            string[] modules = entries.Select(x => x.ModuleId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (modules.Length < 2)
            {
                continue;
            }

            bool explicitAddModel = modules
                .Select(id => profiles.First(p => p.ModuleId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                .Any(p => p.CallsAddModel);

            findings.Add(new ConflictFinding
            {
                Category = ConflictCategory.GameModelOverlap,
                Severity = explicitAddModel ? ConflictSeverity.High : ConflictSeverity.Medium,
                Confidence = explicitAddModel ? 0.86 : 0.72,
                ModuleIds = modules,
                Reason = $"Multiple modules provide GameModel implementations in family '{ShortName(family)}'.",
                LikelyInGameOutcome =
                    "Only one effective model path may win for core calculations, causing balance or behavior drift.",
                Recommendation =
                    "Keep one primary model provider for this family or use a dedicated compatibility bridge patch.",
                Evidence = entries.Select(x => $"{x.ModuleId}:{x.TypeName}").Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList(),
            });
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.ModuleIds.Count)
            .Take(80)
            .ToList();
    }

    private static IEnumerable<ConflictFinding> AnalyzeBehaviorEventOverlaps(IReadOnlyList<ModuleCodeProfile> profiles)
    {
        Dictionary<string, HashSet<string>> eventToModules = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModuleCodeProfile profile in profiles.Where(p => p.CampaignBehaviorTypes.Count > 0))
        {
            foreach (string eventName in profile.CampaignEventHooks)
            {
                if (!eventToModules.TryGetValue(eventName, out HashSet<string>? moduleSet))
                {
                    moduleSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    eventToModules[eventName] = moduleSet;
                }

                moduleSet.Add(profile.ModuleId);
            }
        }

        List<ConflictFinding> findings = [];
        foreach ((string eventName, HashSet<string> moduleSet) in eventToModules
                     .OrderByDescending(x => x.Value.Count)
                     .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (moduleSet.Count < 2)
            {
                continue;
            }

            string[] modules = moduleSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            bool highImpact = IsHighImpactCampaignEvent(eventName);
            findings.Add(new ConflictFinding
            {
                Category = ConflictCategory.BehaviorEventOverlap,
                Severity = highImpact ? ConflictSeverity.High : ConflictSeverity.Medium,
                Confidence = highImpact ? 0.76 : 0.64,
                ModuleIds = modules,
                Reason = $"Multiple modules attach campaign behavior handlers to '{eventName}'.",
                LikelyInGameOutcome =
                    "Event listener order can stack side effects, duplicate logic, or alter campaign state progression.",
                Recommendation =
                    "Review behavior registration order and add guard conditions or compatibility event wrappers.",
                Evidence = profiles
                    .Where(p => moduleSet.Contains(p.ModuleId))
                    .SelectMany(p => p.CampaignBehaviorTypes.Take(2).Select(t => $"{p.ModuleId}:{t}"))
                    .Take(10)
                    .ToList(),
            });
        }

        return findings.Take(80).ToList();
    }

    private static IEnumerable<ConflictFinding> AnalyzeMissionBehaviorOverlaps(IReadOnlyList<ModuleCodeProfile> profiles)
    {
        Dictionary<string, List<(string ModuleId, string TypeName)>> byFamily = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModuleCodeProfile profile in profiles)
        {
            foreach ((string family, HashSet<string> typeNames) in profile.MissionFamilies)
            {
                if (!byFamily.TryGetValue(family, out List<(string ModuleId, string TypeName)>? list))
                {
                    list = [];
                    byFamily[family] = list;
                }

                foreach (string typeName in typeNames)
                {
                    list.Add((profile.ModuleId, typeName));
                }
            }
        }

        List<ConflictFinding> findings = [];
        foreach ((string family, List<(string ModuleId, string TypeName)> entries) in byFamily)
        {
            string[] modules = entries.Select(x => x.ModuleId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (modules.Length < 2)
            {
                continue;
            }

            bool explicitMissionAdd = modules
                .Select(id => profiles.First(p => p.ModuleId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                .Any(p => p.CallsAddMissionBehavior);

            findings.Add(new ConflictFinding
            {
                Category = ConflictCategory.MissionBehaviorOverlap,
                Severity = explicitMissionAdd ? ConflictSeverity.Medium : ConflictSeverity.Low,
                Confidence = explicitMissionAdd ? 0.70 : 0.56,
                ModuleIds = modules,
                Reason = $"Multiple modules provide mission behavior logic in family '{ShortName(family)}'.",
                LikelyInGameOutcome =
                    "Mission initialization order can change AI/UI/agent behavior at runtime.",
                Recommendation =
                    "Validate battle/mission scenarios with one module disabled to confirm additive vs conflicting behavior.",
                Evidence = entries.Select(x => $"{x.ModuleId}:{x.TypeName}").Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList(),
            });
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.ModuleIds.Count)
            .Take(50)
            .ToList();
    }

    private static IEnumerable<ConflictFinding> AnalyzeLifecycleRegistrationOverlaps(IReadOnlyList<ModuleCodeProfile> profiles)
    {
        List<(string ModuleId, LifecycleRegistrationRecord Record)> campaignRegistrations = profiles
            .SelectMany(profile => profile.LifecycleRegistrations
                .Where(r => r.ActionKind is RegistrationActionKind.AddBehavior or RegistrationActionKind.AddModel)
                .Select(r => (profile.ModuleId, r)))
            .ToList();

        List<(string ModuleId, LifecycleRegistrationRecord Record)> missionRegistrations = profiles
            .SelectMany(profile => profile.LifecycleRegistrations
                .Where(r => r.ActionKind == RegistrationActionKind.AddMissionBehavior)
                .Select(r => (profile.ModuleId, r)))
            .ToList();

        List<ConflictFinding> findings = [];

        ConflictFinding? campaignFinding = BuildLifecycleFinding(
            campaignRegistrations,
            targetLabel: "campaign behavior/model handlers",
            likelyOutcome: "Hook-phase and load-order precedence can change event listener order and model override chains between runs.",
            recommendation: "Align order of modules that register behavior/model handlers in the same lifecycle phase or add explicit compatibility guards."
        );
        if (campaignFinding is not null)
        {
            findings.Add(campaignFinding);
        }

        ConflictFinding? missionFinding = BuildLifecycleFinding(
            missionRegistrations,
            targetLabel: "mission behavior handlers",
            likelyOutcome: "Mission startup behavior can drift when multiple modules inject handlers in overlapping mission lifecycle phases.",
            recommendation: "Keep mission handler providers in a stable relative order and verify battle initialization after updates."
        );
        if (missionFinding is not null)
        {
            findings.Add(missionFinding);
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.Confidence)
            .Take(20)
            .ToList();
    }

    private static ConflictFinding? BuildLifecycleFinding(
        IReadOnlyList<(string ModuleId, LifecycleRegistrationRecord Record)> records,
        string targetLabel,
        string likelyOutcome,
        string recommendation
    )
    {
        string[] moduleIds = records
            .Select(r => r.ModuleId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (moduleIds.Length < 2)
        {
            return null;
        }

        string[] phaseList = records
            .Select(r => r.Record.Phase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetLifecyclePhaseSortOrder)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] sharedPhases = records
            .GroupBy(r => r.Record.Phase, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.ModuleId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(g => g.Key)
            .OrderBy(GetLifecyclePhaseSortOrder)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        bool hasSharedEarlyPhase = sharedPhases.Any(phase =>
            phase.Equals("Bootstrap", StringComparison.OrdinalIgnoreCase)
            || phase.Equals("GameStart", StringComparison.OrdinalIgnoreCase)
            || phase.Equals("CampaignStart", StringComparison.OrdinalIgnoreCase)
            || phase.Equals("MissionInit", StringComparison.OrdinalIgnoreCase));

        ConflictSeverity severity = hasSharedEarlyPhase
            ? ConflictSeverity.High
            : ConflictSeverity.Medium;
        double confidence = 0.68
            + (sharedPhases.Length > 0 ? 0.10 : 0.00)
            + (moduleIds.Length >= 3 ? 0.05 : 0.00)
            + (hasSharedEarlyPhase ? 0.07 : 0.00);
        confidence = Math.Clamp(confidence, 0.55, 0.92);

        string phaseSummary = phaseList.Length == 0
            ? "unknown phase"
            : string.Join(", ", phaseList.Take(4));
        if (phaseList.Length > 4)
        {
            phaseSummary += $", +{phaseList.Length - 4} more";
        }

        string sharedSummary = sharedPhases.Length == 0
            ? "no explicit shared phase was resolved"
            : string.Join(", ", sharedPhases.Take(3));

        return new ConflictFinding
        {
            Category = ConflictCategory.LifecycleRegistrationOverlap,
            Severity = severity,
            Confidence = confidence,
            ModuleIds = moduleIds,
            Reason = $"Multiple modules register {targetLabel} from MBSubModuleBase lifecycle hooks ({phaseSummary}); shared phase signal: {sharedSummary}.",
            LikelyInGameOutcome = likelyOutcome,
            Recommendation = recommendation,
            Evidence = records
                .Select(r => $"{r.ModuleId}:{r.Record.Phase}:{r.Record.ActionKind} -> {r.Record.EvidencePath}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList(),
        };
    }

    private static void CollectLifecycleRegistrations(
        ModuleCodeProfile profile,
        string typeName,
        string assemblyPath,
        TypeDefinition typeDef,
        PEReader peReader,
        MetadataReader reader,
        IReadOnlyDictionary<TypeDefinitionHandle, string> localTypeNames
    )
    {
        foreach (MethodDefinitionHandle methodHandle in typeDef.GetMethods())
        {
            MethodDefinition methodDef = reader.GetMethodDefinition(methodHandle);
            string methodName = reader.GetString(methodDef.Name);
            if (!IsLifecycleHookMethod(methodName))
            {
                continue;
            }

            foreach (RegistrationActionKind action in EnumerateRegistrationCalls(peReader, reader, methodDef, localTypeNames))
            {
                profile.LifecycleRegistrations.Add(new LifecycleRegistrationRecord(
                    Phase: ClassifyLifecyclePhase(methodName),
                    HookMethod: methodName,
                    ActionKind: action,
                    EvidencePath: $"{assemblyPath}::{typeName}.{methodName}"
                ));
            }
        }
    }

    private static IReadOnlyList<RegistrationActionKind> EnumerateRegistrationCalls(
        PEReader peReader,
        MetadataReader reader,
        MethodDefinition methodDef,
        IReadOnlyDictionary<TypeDefinitionHandle, string> localTypeNames
    )
    {
        if (methodDef.RelativeVirtualAddress <= 0)
        {
            return [];
        }

        MethodBodyBlock body;
        try
        {
            body = peReader.GetMethodBody(methodDef.RelativeVirtualAddress);
        }
        catch
        {
            return [];
        }

        ReadOnlySpan<byte> il = body.GetILBytes();
        HashSet<RegistrationActionKind> actions = [];
        for (int i = 0; i <= il.Length - 5; i++)
        {
            byte opcode = il[i];
            if (opcode is not 0x28 and not 0x6F)
            {
                continue;
            }

            int token = BinaryPrimitives.ReadInt32LittleEndian(il.Slice(i + 1, 4));
            if (token <= 0)
            {
                continue;
            }

            EntityHandle handle;
            try
            {
                handle = MetadataTokens.EntityHandle(token);
            }
            catch
            {
                continue;
            }

            RegistrationActionKind? action = ResolveRegistrationActionKind(reader, handle, localTypeNames);
            if (action is not null)
            {
                actions.Add(action.Value);
            }
        }

        return actions.ToList();
    }

    private static RegistrationActionKind? ResolveRegistrationActionKind(
        MetadataReader reader,
        EntityHandle handle,
        IReadOnlyDictionary<TypeDefinitionHandle, string> localTypeNames
    )
    {
        return handle.Kind switch
        {
            HandleKind.MemberReference => ResolveRegistrationActionFromMemberReference(
                reader,
                (MemberReferenceHandle)handle,
                localTypeNames
            ),
            HandleKind.MethodDefinition => ResolveRegistrationActionFromMethodDefinition(
                reader,
                (MethodDefinitionHandle)handle
            ),
            _ => null,
        };
    }

    private static RegistrationActionKind? ResolveRegistrationActionFromMemberReference(
        MetadataReader reader,
        MemberReferenceHandle handle,
        IReadOnlyDictionary<TypeDefinitionHandle, string> localTypeNames
    )
    {
        MemberReference member = reader.GetMemberReference(handle);
        string memberName = reader.GetString(member.Name);
        string? parentType = ResolveTypeName(reader, member.Parent, localTypeNames);
        return MapRegistrationAction(parentType, memberName);
    }

    private static RegistrationActionKind? ResolveRegistrationActionFromMethodDefinition(
        MetadataReader reader,
        MethodDefinitionHandle handle
    )
    {
        MethodDefinition method = reader.GetMethodDefinition(handle);
        string memberName = reader.GetString(method.Name);
        string parentType = GetTypeFullName(reader, method.GetDeclaringType());
        return MapRegistrationAction(parentType, memberName);
    }

    private static RegistrationActionKind? MapRegistrationAction(string? parentType, string? memberName)
    {
        if (string.IsNullOrWhiteSpace(parentType) || string.IsNullOrWhiteSpace(memberName))
        {
            return null;
        }

        if (parentType.Equals(CampaignGameStarterType, StringComparison.OrdinalIgnoreCase))
        {
            if (memberName.Equals("AddBehavior", StringComparison.OrdinalIgnoreCase))
            {
                return RegistrationActionKind.AddBehavior;
            }

            if (memberName.Equals("AddModel", StringComparison.OrdinalIgnoreCase))
            {
                return RegistrationActionKind.AddModel;
            }
        }

        if (parentType.Equals(MissionType, StringComparison.OrdinalIgnoreCase)
            && memberName.Equals("AddMissionBehavior", StringComparison.OrdinalIgnoreCase))
        {
            return RegistrationActionKind.AddMissionBehavior;
        }

        return null;
    }

    private static bool IsLifecycleHookMethod(string methodName)
    {
        return LifecycleHookMethods.Contains(methodName);
    }

    private static string ClassifyLifecyclePhase(string hookMethodName)
    {
        if (hookMethodName.Equals("OnSubModuleLoad", StringComparison.OrdinalIgnoreCase))
        {
            return "Bootstrap";
        }

        if (hookMethodName.Equals("OnCampaignStart", StringComparison.OrdinalIgnoreCase))
        {
            return "CampaignStart";
        }

        if (hookMethodName.Equals("OnBeforeMissionBehaviorInitialize", StringComparison.OrdinalIgnoreCase)
            || hookMethodName.Equals("OnMissionBehaviorInitialize", StringComparison.OrdinalIgnoreCase))
        {
            return "MissionInit";
        }

        if (hookMethodName.Equals("OnBeforeGameStart", StringComparison.OrdinalIgnoreCase)
            || hookMethodName.Equals("OnGameStart", StringComparison.OrdinalIgnoreCase)
            || hookMethodName.Equals("InitializeGameStarter", StringComparison.OrdinalIgnoreCase)
            || hookMethodName.Equals("OnGameLoaded", StringComparison.OrdinalIgnoreCase)
            || hookMethodName.Equals("OnAfterGameLoaded", StringComparison.OrdinalIgnoreCase)
            || hookMethodName.Equals("OnNewGameCreated", StringComparison.OrdinalIgnoreCase)
            || hookMethodName.Equals("BeginGameStart", StringComparison.OrdinalIgnoreCase)
            || hookMethodName.Equals("OnGameInitializationFinished", StringComparison.OrdinalIgnoreCase)
            || hookMethodName.Equals("OnAfterGameInitializationFinished", StringComparison.OrdinalIgnoreCase))
        {
            return "GameStart";
        }

        return "OtherLifecycle";
    }

    private static int GetLifecyclePhaseSortOrder(string phase)
    {
        return phase switch
        {
            "Bootstrap" => 0,
            "GameStart" => 1,
            "CampaignStart" => 2,
            "MissionInit" => 3,
            _ => 10,
        };
    }

    private static bool IsModCodeAssembly(DllArtifact dll)
    {
        string name = !string.IsNullOrWhiteSpace(dll.AssemblyName)
            ? dll.AssemblyName!
            : Path.GetFileNameWithoutExtension(dll.FileName);

        if (AssemblyIgnoreNames.Contains(name))
        {
            return false;
        }

        return !AssemblyIgnorePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeCampaignEventName(string memberName)
    {
        string eventName = memberName;
        if (eventName.StartsWith("add_", StringComparison.OrdinalIgnoreCase))
        {
            eventName = eventName[4..];
        }
        else if (eventName.StartsWith("remove_", StringComparison.OrdinalIgnoreCase))
        {
            eventName = eventName[7..];
        }

        if (string.IsNullOrWhiteSpace(eventName))
        {
            return null;
        }

        if (eventName.Contains('<') || eventName.Contains('>'))
        {
            return null;
        }

        bool looksLikeEvent = eventName.Contains("Tick", StringComparison.OrdinalIgnoreCase)
            || eventName.StartsWith("On", StringComparison.OrdinalIgnoreCase)
            || eventName.EndsWith("Event", StringComparison.OrdinalIgnoreCase);
        return looksLikeEvent ? eventName : null;
    }

    private static bool IsHighImpactCampaignEvent(string eventName)
    {
        return eventName.Contains("SessionLaunched", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("GameLoaded", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("NewGameCreated", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("DailyTick", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("HourlyTick", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("Settlement", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("Hero", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("Clan", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("Party", StringComparison.OrdinalIgnoreCase);
    }

    private static string ShortName(string fullName)
    {
        int idx = fullName.LastIndexOf('.');
        return idx >= 0 && idx < fullName.Length - 1 ? fullName[(idx + 1)..] : fullName;
    }

    private static string GetTypeFullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        string name = reader.GetString(type.Name);
        string ns = reader.GetString(type.Namespace);
        return string.IsNullOrWhiteSpace(ns) ? name : $"{ns}.{name}";
    }

    private static string? ResolveTypeName(
        MetadataReader reader,
        EntityHandle handle,
        IReadOnlyDictionary<TypeDefinitionHandle, string> localTypeNames
    )
    {
        if (handle.IsNil)
        {
            return null;
        }

        return handle.Kind switch
        {
            HandleKind.TypeReference => GetTypeReferenceFullName(reader, (TypeReferenceHandle)handle),
            HandleKind.TypeDefinition => localTypeNames.TryGetValue((TypeDefinitionHandle)handle, out string? name) ? name : null,
            _ => null,
        };
    }

    private static string GetTypeReferenceFullName(MetadataReader reader, TypeReferenceHandle handle)
    {
        TypeReference typeRef = reader.GetTypeReference(handle);
        string name = reader.GetString(typeRef.Name);
        string ns = reader.GetString(typeRef.Namespace);
        return string.IsNullOrWhiteSpace(ns) ? name : $"{ns}.{name}";
    }

    private static void AddWarning(List<string> warnings, string message)
    {
        if (warnings.Count(w => w.StartsWith("Code", StringComparison.OrdinalIgnoreCase)) > 70)
        {
            return;
        }

        warnings.Add(message);
    }

    private sealed class TypeHierarchyIndex
    {
        private readonly Dictionary<string, string?> _baseByType = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string typeName, string? baseTypeName)
        {
            if (!_baseByType.TryGetValue(typeName, out string? existing) || string.IsNullOrWhiteSpace(existing))
            {
                _baseByType[typeName] = baseTypeName;
            }
        }

        public bool InheritsFrom(string typeName, string ancestorTypeName)
        {
            string? current = typeName;
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { typeName };
            for (int i = 0; i < 48; i++)
            {
                if (current is null || !_baseByType.TryGetValue(current, out string? baseType) || string.IsNullOrWhiteSpace(baseType))
                {
                    return false;
                }

                if (baseType.Equals(ancestorTypeName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!visited.Add(baseType))
                {
                    return false;
                }

                current = baseType;
            }

            return false;
        }

        public string? GetFamilyClosestTo(string typeName, string ancestorTypeName)
        {
            string current = typeName;
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { typeName };
            for (int i = 0; i < 48; i++)
            {
                if (!_baseByType.TryGetValue(current, out string? baseType) || string.IsNullOrWhiteSpace(baseType))
                {
                    return null;
                }

                if (baseType.Equals(ancestorTypeName, StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                if (!visited.Add(baseType))
                {
                    return current;
                }

                current = baseType;
            }

            return null;
        }
    }

    private sealed class ModuleCodeProfile
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

    private enum RegistrationActionKind
    {
        AddBehavior,
        AddModel,
        AddMissionBehavior,
    }

    private sealed record LifecycleRegistrationRecord(
        string Phase,
        string HookMethod,
        RegistrationActionKind ActionKind,
        string EvidencePath
    );
}
