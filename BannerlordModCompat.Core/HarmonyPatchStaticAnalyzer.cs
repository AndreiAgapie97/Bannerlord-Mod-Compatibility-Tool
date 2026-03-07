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
                    records.AddRange(ParseAssembly(module, dll.Path, ref unresolvedTargets, unresolvedByModule));
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
                            EvidencePath: $"{assemblyPath}::{patchMethodName}"
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

            string[] patchKinds = targetGroup
                .Select(r => r.PatchKind)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            bool hasTranspiler = patchKinds.Any(x => x.Equals("Transpiler", StringComparison.OrdinalIgnoreCase));
            bool postfixOnly = IsPostfixOnlyPatchSet(patchKinds);
            bool duplicateKindAcrossMods = targetGroup
                .GroupBy(r => r.PatchKind, StringComparer.OrdinalIgnoreCase)
                .Any(g => g.Select(x => x.ModuleId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);

            ConflictCategory category = postfixOnly
                ? ConflictCategory.HarmonyPatchStack
                : (hasTranspiler || duplicateKindAcrossMods)
                ? ConflictCategory.HarmonyPatchConflict
                : ConflictCategory.HarmonyPatchStack;
            ConflictSeverity severity = postfixOnly
                ? ConflictSeverity.Low
                : hasTranspiler
                ? ConflictSeverity.Critical
                : duplicateKindAcrossMods
                    ? ConflictSeverity.High
                    : ConflictSeverity.Medium;
            double confidence = postfixOnly
                ? 0.62
                : hasTranspiler
                ? 0.84
                : duplicateKindAcrossMods
                    ? 0.76
                    : 0.66;

            string kindText = string.Join(", ", patchKinds);
            string reason = postfixOnly
                ? $"Static Harmony scan found postfix stack on '{targetGroup.Key}' ({kindText})."
                : category == ConflictCategory.HarmonyPatchConflict
                ? $"Static Harmony scan found potentially conflicting patches on '{targetGroup.Key}' ({kindText})."
                : $"Static Harmony scan found patch stack on '{targetGroup.Key}' ({kindText}).";
            string outcome = postfixOnly
                ? "These are postfix patches. They usually stack safely, but final values or side effects can still depend on patch order."
                : category == ConflictCategory.HarmonyPatchConflict
                ? "Execution order or IL rewriting can change core logic and trigger runtime instability."
                : "Patch stacking may be valid but can still alter behavior based on ordering and side effects.";
            string recommendation = postfixOnly
                ? "Keep the current mod stack together and validate the affected gameplay path first. Only isolate one patch source if you can reproduce a real symptom on this method."
                : category == ConflictCategory.HarmonyPatchConflict
                ? "Set explicit Harmony priority/before/after rules or disable one patch source for this method."
                : "Verify this patched method path in gameplay and keep explicit patch ordering where possible.";

            List<string> evidence = targetGroup
                .Select(r => r.EvidencePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            evidence.Add($"harmony-target:{targetGroup.Key}");
            evidence.Add($"harmony-kinds:{kindText}");
            if (postfixOnly)
            {
                evidence.Add("harmony-profile:postfix-only");
            }

            findings.Add(new ConflictFinding
            {
                Category = category,
                Severity = severity,
                Confidence = confidence,
                ModuleIds = modules,
                Reason = reason,
                LikelyInGameOutcome = outcome,
                Recommendation = recommendation,
                Evidence = evidence,
            });
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.ModuleIds.Count)
            .Take(160)
            .ToList();
    }

    private static bool IsPostfixOnlyPatchSet(IReadOnlyCollection<string> patchKinds)
    {
        return patchKinds.Count > 0
            && patchKinds.All(kind => kind.Equals("Postfix", StringComparison.OrdinalIgnoreCase));
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
        string EvidencePath
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
