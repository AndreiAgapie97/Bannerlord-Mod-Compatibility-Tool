using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace BannerlordModCompat.Core;

internal sealed class ModuleCapabilityProfiler
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

    private readonly LocalScanCache _cache = new("capability-profile");

    public IReadOnlyList<ModuleCapabilityProfile> BuildProfiles(
        IReadOnlyList<ModuleManifest> modules,
        bool customOnlyFocus,
        List<string> warnings)
    {
        IReadOnlyList<ModuleManifest> scanScope = customOnlyFocus
            ? modules.Where(m => m.IsCustom).ToList()
            : modules.Where(m => !m.IsOfficial).ToList();

        if (scanScope.Count == 0)
        {
            return [];
        }

        List<ModuleCapabilityProfile> profiles = [];
        string hierarchyFingerprint = ComputeHierarchyFingerprint(modules);
        List<(ModuleManifest Module, ModuleCapabilityScanInputs Inputs)> pending = [];

        foreach (ModuleManifest module in scanScope)
        {
            ModuleCapabilityScanInputs inputs = BuildCapabilityScanInputs(module, warnings);
            if (_cache.TryRead(GetCacheScope(module, hierarchyFingerprint), inputs.Fingerprint, out CapabilityProfileCacheEntry? cached)
                && cached is not null)
            {
                ModuleCapabilityProfile cachedProfile = BuildModuleProfileFromCache(module, cached, inputs);
                if (cachedProfile.HasSignals)
                {
                    profiles.Add(cachedProfile);
                }

                continue;
            }

            pending.Add((module, inputs));
        }

        if (pending.Count == 0)
        {
            return profiles;
        }

        TypeHierarchyIndex hierarchy = BuildHierarchy(modules, warnings);
        foreach ((ModuleManifest module, ModuleCapabilityScanInputs inputs) in pending)
        {
            ModuleCapabilityProfile profile = BuildModuleProfile(module, hierarchy, warnings, inputs);
            _cache.Write(GetCacheScope(module, hierarchyFingerprint), inputs.Fingerprint, CapabilityProfileCacheEntry.FromProfile(profile));
            if (!profile.HasSignals)
            {
                continue;
            }

            profiles.Add(profile);
        }

        return profiles;
    }

    private static string GetCacheScope(ModuleManifest module, string hierarchyFingerprint)
    {
        return $"{Path.GetFullPath(module.RootPath)}|hierarchy:{hierarchyFingerprint}";
    }

    private static string ComputeHierarchyFingerprint(IReadOnlyList<ModuleManifest> modules)
    {
        string payload = string.Join('|', modules
            .SelectMany(m => m.Dlls)
            .Select(d => d.Sha256)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(payload))
        {
            return "no-dlls";
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash[..8]);
    }

    private static ModuleCapabilityScanInputs BuildCapabilityScanInputs(ModuleManifest module, List<string> warnings)
    {
        List<string> relevant = [];
        relevant.AddRange(module.Dlls.Select(d => d.Path));

        bool hasGauntletUiXml = false;
        string guiPath = Path.Combine(module.RootPath, "GUI");
        if (Directory.Exists(guiPath))
        {
            string[] guiXmlFiles;
            try
            {
                guiXmlFiles = Directory.EnumerateFiles(guiPath, "*.xml", SearchOption.AllDirectories).ToArray();
            }
            catch (Exception ex)
            {
                AddWarning(warnings, $"Code capability scan warning in '{guiPath}': {ex.Message}");
                guiXmlFiles = [];
            }

            if (guiXmlFiles.Length > 0)
            {
                hasGauntletUiXml = true;
                relevant.AddRange(guiXmlFiles);
            }
        }

        bool hasXsltTransforms = false;
        string moduleDataPath = Path.Combine(module.RootPath, "ModuleData");
        if (Directory.Exists(moduleDataPath))
        {
            string[] xsltFiles;
            try
            {
                xsltFiles = Directory.EnumerateFiles(moduleDataPath, "*.xslt", SearchOption.AllDirectories).ToArray();
            }
            catch (Exception ex)
            {
                AddWarning(warnings, $"Code capability scan warning in '{moduleDataPath}': {ex.Message}");
                xsltFiles = [];
            }

            if (xsltFiles.Length > 0)
            {
                hasXsltTransforms = true;
                relevant.AddRange(xsltFiles);
            }
        }

        return new ModuleCapabilityScanInputs(
            Fingerprint: LocalScanCache.ComputeFingerprint(relevant),
            HasGauntletUiXml: hasGauntletUiXml,
            HasXsltTransforms: hasXsltTransforms
        );
    }

    private static TypeHierarchyIndex BuildHierarchy(
        IReadOnlyList<ModuleManifest> modules,
        List<string> warnings)
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

    private static ModuleCapabilityProfile BuildModuleProfile(
        ModuleManifest module,
        TypeHierarchyIndex hierarchy,
        List<string> warnings,
        ModuleCapabilityScanInputs inputs)
    {
        ModuleCapabilityProfile profile = new(module.Id, module.IsOfficial, module.IsFramework);
        profile.HasUiExtenderDependency = module.Dependencies.Any(d =>
            d.Id.Equals("Bannerlord.UIExtenderEx", StringComparison.OrdinalIgnoreCase)
            || d.Id.Equals("UIExtenderEx", StringComparison.OrdinalIgnoreCase));
        profile.OfficialDependencyTargets.AddRange(module.Dependencies
            .Select(d => d.Id)
            .Where(ModuleTaxonomy.IsOfficial)
            .Distinct(StringComparer.OrdinalIgnoreCase));

        profile.HasGauntletUiXml = inputs.HasGauntletUiXml;

        string moduleDataPath = Path.Combine(module.RootPath, "ModuleData");
        if (Directory.Exists(moduleDataPath))
        {
            profile.HasXsltTransforms = inputs.HasXsltTransforms;
            foreach (string entityType in module.XmlEntities
                         .Select(x => x.EntityType)
                         .Where(x => !string.IsNullOrWhiteSpace(x))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                profile.XmlEntityTypes.Add(entityType);
            }
        }

        profile.HasCodelessGauntletUi = profile.HasGauntletUiXml
            && module.Dlls.All(d => !File.Exists(d.Path) || !IsModCodeAssembly(d));

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
                        profile.HasLifecycleHooks = true;
                        CollectLifecycleRegistrations(
                            profile,
                            typeName,
                            dll.Path,
                            typeDef,
                            peReader,
                            reader,
                            localTypeNames);
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
                AddWarning(warnings, $"Capability analysis warning in '{dll.Path}': {ex.Message}");
            }
        }

        return profile;
    }

    private static ModuleCapabilityProfile BuildModuleProfileFromCache(
        ModuleManifest module,
        CapabilityProfileCacheEntry cached,
        ModuleCapabilityScanInputs inputs)
    {
        ModuleCapabilityProfile profile = cached.ToProfile(module.Id, module.IsOfficial, module.IsFramework);

        // Always prefer live manifest-derived signals.
        profile.HasUiExtenderDependency = module.Dependencies.Any(d =>
            d.Id.Equals("Bannerlord.UIExtenderEx", StringComparison.OrdinalIgnoreCase)
            || d.Id.Equals("UIExtenderEx", StringComparison.OrdinalIgnoreCase));

        profile.OfficialDependencyTargets.Clear();
        profile.OfficialDependencyTargets.AddRange(module.Dependencies
            .Select(d => d.Id)
            .Where(ModuleTaxonomy.IsOfficial)
            .Distinct(StringComparer.OrdinalIgnoreCase));

        profile.HasGauntletUiXml = inputs.HasGauntletUiXml;
        profile.HasXsltTransforms = inputs.HasXsltTransforms;

        profile.XmlEntityTypes.Clear();
        foreach (string entityType in module.XmlEntities
                     .Select(x => x.EntityType)
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            profile.XmlEntityTypes.Add(entityType);
        }

        profile.HasCodelessGauntletUi = profile.HasGauntletUiXml
            && module.Dlls.All(d => !File.Exists(d.Path) || !IsModCodeAssembly(d));

        return profile;
    }

    private static void CollectLifecycleRegistrations(
        ModuleCapabilityProfile profile,
        string typeName,
        string assemblyPath,
        TypeDefinition typeDef,
        PEReader peReader,
        MetadataReader reader,
        IReadOnlyDictionary<TypeDefinitionHandle, string> localTypeNames)
    {
        foreach (MethodDefinitionHandle methodHandle in typeDef.GetMethods())
        {
            MethodDefinition methodDef = reader.GetMethodDefinition(methodHandle);
            string methodName = reader.GetString(methodDef.Name);
            if (!IsLifecycleHookMethod(methodName))
            {
                continue;
            }

            profile.LifecycleHooks.Add(methodName);
            foreach (RegistrationActionKind action in EnumerateRegistrationCalls(peReader, reader, methodDef, localTypeNames))
            {
                profile.LifecycleRegistrations.Add(new LifecycleRegistrationRecord(
                    Phase: ClassifyLifecyclePhase(methodName),
                    HookMethod: methodName,
                    ActionKind: action,
                    EvidencePath: $"{assemblyPath}::{typeName}.{methodName}"));
            }
        }
    }

    private static IReadOnlyList<RegistrationActionKind> EnumerateRegistrationCalls(
        PEReader peReader,
        MetadataReader reader,
        MethodDefinition methodDef,
        IReadOnlyDictionary<TypeDefinitionHandle, string> localTypeNames)
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
        IReadOnlyDictionary<TypeDefinitionHandle, string> localTypeNames)
    {
        return handle.Kind switch
        {
            HandleKind.MemberReference => ResolveRegistrationActionFromMemberReference(
                reader,
                (MemberReferenceHandle)handle,
                localTypeNames),
            HandleKind.MethodDefinition => ResolveRegistrationActionFromMethodDefinition(
                reader,
                (MethodDefinitionHandle)handle),
            _ => null,
        };
    }

    private static RegistrationActionKind? ResolveRegistrationActionFromMemberReference(
        MetadataReader reader,
        MemberReferenceHandle handle,
        IReadOnlyDictionary<TypeDefinitionHandle, string> localTypeNames)
    {
        MemberReference member = reader.GetMemberReference(handle);
        string memberName = reader.GetString(member.Name);
        string? parentType = ResolveTypeName(reader, member.Parent, localTypeNames);
        return MapRegistrationAction(parentType, memberName);
    }

    private static RegistrationActionKind? ResolveRegistrationActionFromMethodDefinition(
        MetadataReader reader,
        MethodDefinitionHandle handle)
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

    private static bool IsLifecycleHookMethod(string methodName) => LifecycleHookMethods.Contains(methodName);

    internal static string ClassifyLifecyclePhase(string hookMethodName)
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

    internal static int GetLifecyclePhaseSortOrder(string phase)
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
        IReadOnlyDictionary<TypeDefinitionHandle, string> localTypeNames)
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
        if (warnings.Count(w => w.StartsWith("Code", StringComparison.OrdinalIgnoreCase)
            || w.StartsWith("Capability", StringComparison.OrdinalIgnoreCase)) > 70)
        {
            return;
        }

        warnings.Add(message);
    }
}

internal sealed class TypeHierarchyIndex
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

internal sealed class ModuleCapabilityProfile
{
    public ModuleCapabilityProfile(string moduleId, bool isOfficial, bool isFramework)
    {
        ModuleId = moduleId;
        IsOfficial = isOfficial;
        IsFramework = isFramework;
    }

    public string ModuleId { get; }
    public bool IsOfficial { get; }
    public bool IsFramework { get; }
    public bool IsCustom => !IsOfficial && !IsFramework;
    public bool CallsAddBehavior { get; set; }
    public bool CallsAddModel { get; set; }
    public bool CallsAddMissionBehavior { get; set; }
    public bool HasLifecycleHooks { get; set; }
    public bool HasGauntletUiXml { get; set; }
    public bool HasCodelessGauntletUi { get; set; }
    public bool HasUiExtenderDependency { get; set; }
    public bool HasXsltTransforms { get; set; }
    public HashSet<string> CampaignBehaviorTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> GameModelTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> MissionBehaviorTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CampaignEventHooks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> LifecycleHooks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> OfficialDependencyTargets { get; } = [];
    public HashSet<string> XmlEntityTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, HashSet<string>> ModelFamilies { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, HashSet<string>> MissionFamilies { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<LifecycleRegistrationRecord> LifecycleRegistrations { get; } = [];

    public bool HasSignals =>
        CampaignBehaviorTypes.Count > 0
        || GameModelTypes.Count > 0
        || MissionBehaviorTypes.Count > 0
        || CampaignEventHooks.Count > 0
        || LifecycleRegistrations.Count > 0
        || HasGauntletUiXml
        || HasXsltTransforms;

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

internal enum RegistrationActionKind
{
    AddBehavior,
    AddModel,
    AddMissionBehavior,
}

internal sealed record LifecycleRegistrationRecord(
    string Phase,
    string HookMethod,
    RegistrationActionKind ActionKind,
    string EvidencePath
);

internal sealed record ModuleCapabilityScanInputs(
    string Fingerprint,
    bool HasGauntletUiXml,
    bool HasXsltTransforms
);

internal sealed record CapabilityProfileCacheEntry
{
    public bool CallsAddBehavior { get; init; }
    public bool CallsAddModel { get; init; }
    public bool CallsAddMissionBehavior { get; init; }
    public bool HasLifecycleHooks { get; init; }
    public List<string> CampaignBehaviorTypes { get; init; } = [];
    public List<string> GameModelTypes { get; init; } = [];
    public List<string> MissionBehaviorTypes { get; init; } = [];
    public List<string> CampaignEventHooks { get; init; } = [];
    public List<string> LifecycleHooks { get; init; } = [];
    public Dictionary<string, List<string>> ModelFamilies { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> MissionFamilies { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CapabilityLifecycleRecordCacheEntry> LifecycleRegistrations { get; init; } = [];

    public static CapabilityProfileCacheEntry FromProfile(ModuleCapabilityProfile profile)
    {
        return new CapabilityProfileCacheEntry
        {
            CallsAddBehavior = profile.CallsAddBehavior,
            CallsAddModel = profile.CallsAddModel,
            CallsAddMissionBehavior = profile.CallsAddMissionBehavior,
            HasLifecycleHooks = profile.HasLifecycleHooks,
            CampaignBehaviorTypes = profile.CampaignBehaviorTypes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            GameModelTypes = profile.GameModelTypes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            MissionBehaviorTypes = profile.MissionBehaviorTypes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            CampaignEventHooks = profile.CampaignEventHooks.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            LifecycleHooks = profile.LifecycleHooks.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            ModelFamilies = profile.ModelFamilies.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase),
            MissionFamilies = profile.MissionFamilies.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase),
            LifecycleRegistrations = profile.LifecycleRegistrations
                .Select(r => new CapabilityLifecycleRecordCacheEntry(
                    r.Phase,
                    r.HookMethod,
                    r.ActionKind.ToString(),
                    r.EvidencePath))
                .OrderBy(x => x.Phase, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.HookMethod, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ActionKindToken, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    public ModuleCapabilityProfile ToProfile(string moduleId, bool isOfficial, bool isFramework)
    {
        ModuleCapabilityProfile profile = new(moduleId, isOfficial, isFramework)
        {
            CallsAddBehavior = CallsAddBehavior,
            CallsAddModel = CallsAddModel,
            CallsAddMissionBehavior = CallsAddMissionBehavior,
            HasLifecycleHooks = HasLifecycleHooks,
        };

        foreach (string value in CampaignBehaviorTypes)
        {
            profile.CampaignBehaviorTypes.Add(value);
        }

        foreach (string value in GameModelTypes)
        {
            profile.GameModelTypes.Add(value);
        }

        foreach (string value in MissionBehaviorTypes)
        {
            profile.MissionBehaviorTypes.Add(value);
        }

        foreach (string value in CampaignEventHooks)
        {
            profile.CampaignEventHooks.Add(value);
        }

        foreach (string value in LifecycleHooks)
        {
            profile.LifecycleHooks.Add(value);
        }

        foreach ((string family, List<string> types) in ModelFamilies)
        {
            foreach (string typeName in types)
            {
                profile.AddModelFamily(family, typeName);
            }
        }

        foreach ((string family, List<string> types) in MissionFamilies)
        {
            foreach (string typeName in types)
            {
                profile.AddMissionFamily(family, typeName);
            }
        }

        foreach (CapabilityLifecycleRecordCacheEntry record in LifecycleRegistrations)
        {
            if (!Enum.TryParse(record.ActionKindToken, ignoreCase: true, out RegistrationActionKind actionKind))
            {
                continue;
            }

            profile.LifecycleRegistrations.Add(new LifecycleRegistrationRecord(
                record.Phase,
                record.HookMethod,
                actionKind,
                record.EvidencePath));
        }

        return profile;
    }
}

internal sealed record CapabilityLifecycleRecordCacheEntry(
    string Phase,
    string HookMethod,
    string ActionKindToken,
    string EvidencePath
);
