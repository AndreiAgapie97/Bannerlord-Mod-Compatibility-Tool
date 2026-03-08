using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace BannerlordModCompat.Core;

public sealed class HarmonyPatchStaticAnalyzer
{
    private const string HarmonyPatchAttribute = "HarmonyPatch";
    private const string HarmonyPrefixAttribute = "HarmonyPrefix";
    private const string HarmonyPostfixAttribute = "HarmonyPostfix";
    private const string HarmonyTranspilerAttribute = "HarmonyTranspiler";
    private const string HarmonyFinalizerAttribute = "HarmonyFinalizer";
    private const string HarmonyPriorityAttribute = "HarmonyPriority";
    private const string HarmonyTargetMethodAttribute = "HarmonyTargetMethod";
    private const string HarmonyTargetMethodsAttribute = "HarmonyTargetMethods";
    private const string UnknownTarget = "<unknown>";

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
    };

    private static readonly MetadataTypeNameProvider TypeProvider = new();
    private readonly LocalScanCache _cache = new("harmony-static");

    public IReadOnlyList<ConflictFinding> Analyze(
        IReadOnlyList<ModuleManifest> modules,
        bool customOnlyFocus,
        List<string> warnings
    )
    {
        IReadOnlyList<ModuleManifest> scanScope = customOnlyFocus
            ? modules.Where(m => m.IsCustom).ToList()
            : modules.Where(m => !m.IsOfficial).ToList();

        List<StaticPatchRecord> records = [];
        int unresolvedTargets = 0;
        Dictionary<string, int> unresolvedByModule = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModuleManifest module in scanScope)
        {
            foreach (DllArtifact dll in module.Dlls)
            {
                if (!File.Exists(dll.Path) || !ShouldScanAssembly(dll))
                {
                    continue;
                }

                try
                {
                    string fingerprint = LocalScanCache.ComputeFingerprint([dll.Path]);
                    StaticPatchCacheEntry? cached;
                    if (_cache.TryRead(dll.Path, fingerprint, out cached) && cached is not null)
                    {
                        records.AddRange(cached.Records);
                        unresolvedTargets += cached.UnresolvedTargetCount;
                        if (cached.UnresolvedTargetCount > 0)
                        {
                            unresolvedByModule[module.Id] = unresolvedByModule.TryGetValue(module.Id, out int existing)
                                ? existing + cached.UnresolvedTargetCount
                                : cached.UnresolvedTargetCount;
                        }
                    }
                    else
                    {
                        int unresolvedBefore = unresolvedByModule.TryGetValue(module.Id, out int existingCount)
                            ? existingCount
                            : 0;
                        List<StaticPatchRecord> parsedRecords = ParseAssembly(module, dll.Path, ref unresolvedTargets, unresolvedByModule).ToList();
                        records.AddRange(parsedRecords);
                        int unresolvedAfter = unresolvedByModule.TryGetValue(module.Id, out int count)
                            ? count
                            : 0;
                        int unresolvedForDll = Math.Max(0, unresolvedAfter - unresolvedBefore);
                        _cache.Write(dll.Path, fingerprint, new StaticPatchCacheEntry(parsedRecords, unresolvedForDll));
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Static Harmony scan warning in '{dll.Path}': {ex.Message}");
                }
            }
        }

        if (unresolvedTargets > 0)
        {
            warnings.Add(
                $"Static Harmony scan: {unresolvedTargets} patch method(s) had dynamic/unresolved targets. "
                + "These are not included in deterministic conflict grouping."
            );
        }

        List<ConflictFinding> findings = BuildFindings(records);
        findings.AddRange(BuildUnresolvedTargetFindings(unresolvedByModule));
        return findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.Confidence)
            .Take(220)
            .ToList();
    }

    private sealed record StaticPatchCacheEntry(
        IReadOnlyList<StaticPatchRecord> Records,
        int UnresolvedTargetCount
    );

    private static bool ShouldScanAssembly(DllArtifact dll)
    {
        string asmName = !string.IsNullOrWhiteSpace(dll.AssemblyName)
            ? dll.AssemblyName!
            : Path.GetFileNameWithoutExtension(dll.FileName);

        if (AssemblyIgnoreNames.Contains(asmName))
        {
            return false;
        }

        if (AssemblyIgnorePrefixes.Any(p => asmName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private static IEnumerable<StaticPatchRecord> ParseAssembly(
        ModuleManifest module,
        string assemblyPath,
        ref int unresolvedTargets,
        IDictionary<string, int> unresolvedByModule
    )
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader peReader = new(stream);
        if (!peReader.HasMetadata)
        {
            return [];
        }

        MetadataReader reader = peReader.GetMetadataReader();
        List<StaticPatchRecord> records = [];

        foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
        {
            TypeDefinition typeDef = reader.GetTypeDefinition(typeHandle);
            string patchTypeName = GetTypeFullName(reader, typeHandle);

            List<PatchTargetHint> typeHints = ReadPatchHints(reader, typeDef.GetCustomAttributes());
            int? typePriority = ReadHarmonyPriority(reader, typeDef.GetCustomAttributes());
            bool typeDeclaresDynamicTarget = HasHarmonyAttribute(reader, typeDef.GetCustomAttributes(), HarmonyTargetMethodAttribute)
                || HasHarmonyAttribute(reader, typeDef.GetCustomAttributes(), HarmonyTargetMethodsAttribute);

            foreach (MethodDefinitionHandle methodHandle in typeDef.GetMethods())
            {
                MethodDefinition methodDef = reader.GetMethodDefinition(methodHandle);
                string methodName = reader.GetString(methodDef.Name);
                List<string> patchKinds = ReadPatchKinds(reader, methodDef.GetCustomAttributes());
                if (patchKinds.Count == 0)
                {
                    continue;
                }

                List<PatchTargetHint> methodHints = ReadPatchHints(reader, methodDef.GetCustomAttributes());
                int? methodPriority = ReadHarmonyPriority(reader, methodDef.GetCustomAttributes()) ?? typePriority;
                bool methodDeclaresDynamicTarget = HasHarmonyAttribute(reader, methodDef.GetCustomAttributes(), HarmonyTargetMethodAttribute)
                    || HasHarmonyAttribute(reader, methodDef.GetCustomAttributes(), HarmonyTargetMethodsAttribute);

                List<PatchTargetHint> effectiveHints = BuildEffectiveHints(typeHints, methodHints);
                if (effectiveHints.Count == 0 || effectiveHints.All(h => h.TargetKey == UnknownTarget))
                {
                    if (typeDeclaresDynamicTarget || methodDeclaresDynamicTarget || typeHints.Count > 0 || methodHints.Count > 0)
                    {
                        unresolvedTargets++;
                        unresolvedByModule[module.Id] = unresolvedByModule.TryGetValue(module.Id, out int existing)
                            ? existing + 1
                            : 1;
                    }

                    continue;
                }

                string patchMethodName = $"{patchTypeName}.{methodName}";
                foreach (PatchTargetHint hint in effectiveHints.Where(h => h.TargetKey != UnknownTarget))
                {
                    foreach (string kind in patchKinds)
                    {
                        records.Add(new StaticPatchRecord(
                            ModuleId: module.Id,
                            TargetKey: hint.TargetKey,
                            PatchKind: kind,
                            PatchMethod: patchMethodName,
                            EvidencePath: $"{assemblyPath}::{patchMethodName}",
                            Priority: methodPriority
                        ));
                    }
                }
            }
        }

        return records;
    }

    private static IEnumerable<ConflictFinding> BuildUnresolvedTargetFindings(
        IReadOnlyDictionary<string, int> unresolvedByModule
    )
    {
        if (unresolvedByModule.Count == 0)
        {
            return [];
        }

        List<ConflictFinding> findings = [];
        foreach ((string moduleId, int unresolvedCount) in unresolvedByModule
                     .OrderByDescending(x => x.Value)
                     .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                     .Take(20))
        {
            ConflictSeverity severity = unresolvedCount >= 8
                ? ConflictSeverity.Medium
                : ConflictSeverity.Low;
            double confidence = unresolvedCount >= 8
                ? 0.74
                : 0.66;

            findings.Add(new ConflictFinding
            {
                Category = ConflictCategory.AnalyzerWarning,
                Severity = severity,
                Confidence = confidence,
                ModuleIds = [moduleId],
                Reason = $"Static Harmony scan could not resolve {unresolvedCount} dynamic patch target(s) in '{moduleId}'.",
                LikelyInGameOutcome = "Additional Harmony overlap conflicts may exist but cannot be deterministically grouped from static metadata alone.",
                Recommendation = "Generate runtime Harmony patch logs and re-scan to increase conflict coverage for this module.",
                Evidence = [],
            });
        }

        return findings;
    }

    private static List<ConflictFinding> BuildFindings(IReadOnlyList<StaticPatchRecord> records)
    {
        List<ConflictFinding> findings = [];
        foreach (IGrouping<string, StaticPatchRecord> targetGroup in records.GroupBy(r => r.TargetKey, StringComparer.OrdinalIgnoreCase))
        {
            string[] modules = targetGroup
                .Select(r => r.ModuleId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (modules.Length < 2)
            {
                continue;
            }

            List<string> patchKinds = targetGroup
                .Select(r => r.PatchKind)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            HarmonyOrderState orderState = DetermineStaticOrderState(targetGroup, modules);
            HarmonyOwnershipShape ownershipShape = HarmonyFindingProfiles.DetermineOwnershipShape(
                targetGroup.Select(record => (record.ModuleId, record.PatchKind)));
            HarmonyRiskProfile profile = HarmonyFindingProfiles.Create(
                targetGroup.Key,
                patchKinds,
                orderState,
                ownershipShape,
                hasStaticMetadataEvidence: true,
                hasDuplicateScannerEvidence: false,
                hasOrderingGraphEvidence: false,
                hasRuntimeCorrelationEvidence: false,
                moduleCount: modules.Length);
            HarmonyRiskAssessment assessment = HarmonyFindingProfiles.Assess(profile);

            string kindText = patchKinds.Count == 0
                ? "unknown patch kinds"
                : string.Join(", ", patchKinds);

            List<string> evidence = targetGroup
                .Select(r => r.EvidencePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            evidence.Add($"{HarmonyFindingProfiles.TargetPrefix}{targetGroup.Key}");
            evidence.Add($"{HarmonyFindingProfiles.KindsPrefix}{kindText}");
            List<string> priorityTokens = targetGroup
                .Where(r => r.Priority.HasValue)
                .GroupBy(r => r.ModuleId, StringComparer.OrdinalIgnoreCase)
                .Select(group => $"{group.Key}:{group.Select(r => r.Priority!.Value).Distinct().OrderBy(x => x).First()}")
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (priorityTokens.Count > 0)
            {
                evidence.Add($"{HarmonyFindingProfiles.PriorityPrefix}{string.Join(", ", priorityTokens)}");
            }
            evidence.AddRange(HarmonyFindingProfiles.BuildEvidenceTags(profile));

            findings.Add(new ConflictFinding
            {
                Category = assessment.Category,
                Severity = assessment.Severity,
                Confidence = assessment.Confidence,
                ModuleIds = modules,
                Reason = assessment.Reason,
                LikelyInGameOutcome = assessment.LikelyOutcome,
                Recommendation = assessment.Recommendation,
                Evidence = evidence,
                StructuredEvidence = HarmonyFindingProfiles.BuildStructuredEvidence(profile),
            });
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.ModuleIds.Count)
            .Take(160)
            .ToList();
    }

    private static HarmonyOrderState DetermineStaticOrderState(
        IGrouping<string, StaticPatchRecord> targetGroup,
        IReadOnlyList<string> modules
    )
    {
        Dictionary<string, int?> priorityByModule = modules.ToDictionary(moduleId => moduleId, _ => (int?)null, StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, StaticPatchRecord> moduleGroup in targetGroup.GroupBy(r => r.ModuleId, StringComparer.OrdinalIgnoreCase))
        {
            List<int> priorities = moduleGroup
                .Where(record => record.Priority.HasValue)
                .Select(record => record.Priority!.Value)
                .Distinct()
                .ToList();
            if (priorities.Count == 1)
            {
                priorityByModule[moduleGroup.Key] = priorities[0];
            }
        }

        if (priorityByModule.Values.All(priority => priority.HasValue)
            && priorityByModule.Values.Select(priority => priority!.Value).Distinct().Count() == modules.Count)
        {
            return HarmonyOrderState.ExplicitlyOrdered;
        }

        return HarmonyOrderState.Unknown;
    }

    private static List<PatchTargetHint> BuildEffectiveHints(
        IReadOnlyList<PatchTargetHint> typeHints,
        IReadOnlyList<PatchTargetHint> methodHints
    )
    {
        if (typeHints.Count == 0 && methodHints.Count == 0)
        {
            return [];
        }

        if (methodHints.Count == 0)
        {
            return typeHints.ToList();
        }

        if (typeHints.Count == 0)
        {
            return methodHints.ToList();
        }

        PatchTargetHint baseHint = typeHints[0];
        return methodHints.Select(h => MergeHints(baseHint, h)).ToList();
    }

    private static PatchTargetHint MergeHints(PatchTargetHint left, PatchTargetHint right)
    {
        string? declaringType = !string.IsNullOrWhiteSpace(right.DeclaringType)
            ? right.DeclaringType
            : left.DeclaringType;
        string? methodName = !string.IsNullOrWhiteSpace(right.MethodName)
            ? right.MethodName
            : left.MethodName;
        IReadOnlyList<string> args = right.ArgumentTypes.Count > 0
            ? right.ArgumentTypes
            : left.ArgumentTypes;
        return new PatchTargetHint(declaringType, methodName, args);
    }

    private static List<PatchTargetHint> ReadPatchHints(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        List<PatchTargetHint> hints = [];
        foreach (CustomAttributeHandle attrHandle in attributes)
        {
            CustomAttribute attr = reader.GetCustomAttribute(attrHandle);
            string? attrType = GetAttributeTypeName(reader, attr);
            if (!IsHarmonyAttribute(attrType, HarmonyPatchAttribute))
            {
                continue;
            }

            PatchTargetHint hint = DecodeHarmonyPatchHint(reader, attr);
            hints.Add(hint);
        }

        return hints;
    }

    private static List<string> ReadPatchKinds(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        HashSet<string> kinds = new(StringComparer.OrdinalIgnoreCase);
        foreach (CustomAttributeHandle attrHandle in attributes)
        {
            CustomAttribute attr = reader.GetCustomAttribute(attrHandle);
            string? attrType = GetAttributeTypeName(reader, attr);
            if (IsHarmonyAttribute(attrType, HarmonyPrefixAttribute))
            {
                kinds.Add("Prefix");
            }
            else if (IsHarmonyAttribute(attrType, HarmonyPostfixAttribute))
            {
                kinds.Add("Postfix");
            }
            else if (IsHarmonyAttribute(attrType, HarmonyTranspilerAttribute))
            {
                kinds.Add("Transpiler");
            }
            else if (IsHarmonyAttribute(attrType, HarmonyFinalizerAttribute))
            {
                kinds.Add("Finalizer");
            }
        }

        return kinds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int? ReadHarmonyPriority(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        foreach (CustomAttributeHandle attrHandle in attributes)
        {
            CustomAttribute attr = reader.GetCustomAttribute(attrHandle);
            string? attrType = GetAttributeTypeName(reader, attr);
            if (!IsHarmonyAttribute(attrType, HarmonyPriorityAttribute))
            {
                continue;
            }

            int? priority = TryDecodePriorityAttribute(attr);
            if (priority.HasValue)
            {
                return priority.Value;
            }
        }

        return null;
    }

    private static int? TryDecodePriorityAttribute(CustomAttribute attr)
    {
        try
        {
            CustomAttributeValue<string> value = attr.DecodeValue(TypeProvider);
            foreach (CustomAttributeTypedArgument<string> fixedArg in value.FixedArguments)
            {
                if (fixedArg.Type.Equals("System.Int32", StringComparison.OrdinalIgnoreCase)
                    && fixedArg.Value is int priority)
                {
                    return priority;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static PatchTargetHint DecodeHarmonyPatchHint(MetadataReader reader, CustomAttribute attr)
    {
        try
        {
            CustomAttributeValue<string> value = attr.DecodeValue(TypeProvider);
            string? declaringType = null;
            string? methodName = null;
            List<string> argTypes = [];

            foreach (CustomAttributeTypedArgument<string> fixedArg in value.FixedArguments)
            {
                if (IsSystemTypeToken(fixedArg.Type))
                {
                    string? typeName = fixedArg.Value as string;
                    if (!string.IsNullOrWhiteSpace(typeName))
                    {
                        if (declaringType is null)
                        {
                            declaringType = NormalizeTypeName(typeName);
                        }
                        else
                        {
                            argTypes.Add(NormalizeTypeName(typeName));
                        }
                    }
                }
                else if (IsStringTypeToken(fixedArg.Type))
                {
                    string? str = fixedArg.Value as string;
                    if (!string.IsNullOrWhiteSpace(str))
                    {
                        if (methodName is null && LooksLikeMethodName(str))
                        {
                            methodName = str.Trim();
                        }
                    }
                }
                else if (IsTypeArrayToken(fixedArg.Type) && fixedArg.Value is ImmutableArray<CustomAttributeTypedArgument<string>> typeArray)
                {
                    foreach (CustomAttributeTypedArgument<string> item in typeArray)
                    {
                        string? typeName = item.Value as string;
                        if (!string.IsNullOrWhiteSpace(typeName))
                        {
                            argTypes.Add(NormalizeTypeName(typeName));
                        }
                    }
                }
            }

            foreach (CustomAttributeNamedArgument<string> namedArg in value.NamedArguments)
            {
                if (!string.Equals(namedArg.Name, "methodName", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (namedArg.Value is string namedMethod && !string.IsNullOrWhiteSpace(namedMethod))
                {
                    methodName = namedMethod.Trim();
                }
            }

            return new PatchTargetHint(declaringType, methodName, argTypes);
        }
        catch
        {
            return new PatchTargetHint(null, null, []);
        }
    }

    private static bool HasHarmonyAttribute(
        MetadataReader reader,
        CustomAttributeHandleCollection attributes,
        string attributeSimpleName
    )
    {
        foreach (CustomAttributeHandle attrHandle in attributes)
        {
            CustomAttribute attr = reader.GetCustomAttribute(attrHandle);
            string? attrType = GetAttributeTypeName(reader, attr);
            if (IsHarmonyAttribute(attrType, attributeSimpleName))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetAttributeTypeName(MetadataReader reader, CustomAttribute attr)
    {
        EntityHandle ctor = attr.Constructor;
        return ctor.Kind switch
        {
            HandleKind.MemberReference => GetAttributeTypeFromMemberRef(reader, (MemberReferenceHandle)ctor),
            HandleKind.MethodDefinition => GetAttributeTypeFromMethodDef(reader, (MethodDefinitionHandle)ctor),
            _ => null,
        };
    }

    private static string? GetAttributeTypeFromMemberRef(MetadataReader reader, MemberReferenceHandle handle)
    {
        MemberReference memberRef = reader.GetMemberReference(handle);
        return memberRef.Parent.Kind switch
        {
            HandleKind.TypeReference => GetTypeReferenceFullName(reader, (TypeReferenceHandle)memberRef.Parent),
            HandleKind.TypeDefinition => GetTypeFullName(reader, (TypeDefinitionHandle)memberRef.Parent),
            _ => null,
        };
    }

    private static string? GetAttributeTypeFromMethodDef(MetadataReader reader, MethodDefinitionHandle handle)
    {
        MethodDefinition methodDef = reader.GetMethodDefinition(handle);
        return GetTypeFullName(reader, methodDef.GetDeclaringType());
    }

    private static string GetTypeFullName(MetadataReader reader, TypeDefinitionHandle typeHandle)
    {
        TypeDefinition typeDef = reader.GetTypeDefinition(typeHandle);
        string ns = reader.GetString(typeDef.Namespace);
        string name = reader.GetString(typeDef.Name);
        return string.IsNullOrWhiteSpace(ns) ? name : $"{ns}.{name}";
    }

    private static string GetTypeReferenceFullName(MetadataReader reader, TypeReferenceHandle typeHandle)
    {
        TypeReference typeRef = reader.GetTypeReference(typeHandle);
        string ns = reader.GetString(typeRef.Namespace);
        string name = reader.GetString(typeRef.Name);
        return string.IsNullOrWhiteSpace(ns) ? name : $"{ns}.{name}";
    }

    private static bool IsHarmonyAttribute(string? attributeTypeName, string expectedSimpleName)
    {
        if (string.IsNullOrWhiteSpace(attributeTypeName))
        {
            return false;
        }

        if (attributeTypeName.EndsWith($".{expectedSimpleName}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return attributeTypeName.Equals(expectedSimpleName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemTypeToken(string token)
    {
        return token.Equals("System.Type", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStringTypeToken(string token)
    {
        return token.Equals("System.String", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTypeArrayToken(string token)
    {
        return token.Equals("System.Type[]", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeMethodName(string value)
    {
        if (value.Contains(' ') || value.Contains(':'))
        {
            return false;
        }

        if (value.Contains('.') && value.Contains('('))
        {
            return false;
        }

        return true;
    }

    private static string NormalizeTypeName(string value)
    {
        string normalized = value.Trim().Replace("/", ".", StringComparison.Ordinal);
        int commaIdx = normalized.IndexOf(',');
        if (commaIdx > 0)
        {
            normalized = normalized[..commaIdx].Trim();
        }

        return normalized;
    }

    private sealed record PatchTargetHint(string? DeclaringType, string? MethodName, IReadOnlyList<string> ArgumentTypes)
    {
        public string TargetKey
        {
            get
            {
                if (string.IsNullOrWhiteSpace(DeclaringType) || string.IsNullOrWhiteSpace(MethodName))
                {
                    return UnknownTarget;
                }

                string args = ArgumentTypes.Count == 0
                    ? string.Empty
                    : $"({string.Join(", ", ArgumentTypes)})";
                return $"{DeclaringType}::{MethodName}{args}";
            }
        }
    }

    private sealed record StaticPatchRecord(
        string ModuleId,
        string TargetKey,
        string PatchKind,
        string PatchMethod,
        string EvidencePath,
        int? Priority
    );

    private sealed class MetadataTypeNameProvider : ICustomAttributeTypeProvider<string>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Object => "System.Object",
            _ => typeCode.ToString(),
        };

        public string GetSystemType() => "System.Type";

        public bool IsSystemType(string type) => type.Equals("System.Type", StringComparison.OrdinalIgnoreCase);

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            string ns = reader.GetString(type.Namespace);
            string name = reader.GetString(type.Name);
            return string.IsNullOrWhiteSpace(ns) ? name : $"{ns}.{name}";
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference type = reader.GetTypeReference(handle);
            string ns = reader.GetString(type.Namespace);
            string name = reader.GetString(type.Name);
            return string.IsNullOrWhiteSpace(ns) ? name : $"{ns}.{name}";
        }

        public string GetSZArrayType(string elementType) => $"{elementType}[]";

        public string GetTypeFromSerializedName(string name) => name;

        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;

        public string GetTypeFromSpecification(MetadataReader reader, object context, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            return "TypeSpec";
        }

        public string GetGenericTypeParameter(object genericContext, int index) => $"!{index}";

        public string GetGenericMethodParameter(object genericContext, int index) => $"!!{index}";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetByReferenceType(string elementType) => elementType;

        public string GetFunctionPointerType(MethodSignature<string> signature) => "System.IntPtr";
    }
}
