using System.Globalization;
using System.Text.RegularExpressions;

namespace BannerlordModCompat.Core;

public sealed class HarmonyPatchLogAnalyzer
{
    private const int HarmonyDefaultPriority = 400;

    private static readonly Regex PatchTypeRegex = new(
        @"\b(prefix|postfix|transpiler|finalizer)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex MethodSignatureRegex = new(
        @"(?<sig>[A-Za-z_][A-Za-z0-9_`.\+\[\]<>]*(?::|::|\.)[A-Za-z_][A-Za-z0-9_`.\+\[\]<>]*\s*\([^)]*\))",
        RegexOptions.Compiled
    );

    private static readonly Regex PatchLaneRegex = new(
        @"(?<kind>\b(?:pre|prefix|post|postfix|trans|transpiler|final|finalizer)\b)\s*[:=]\s*(?<payload>.*?)(?=(?:\b(?:pre|prefix|post|postfix|trans|transpiler|final|finalizer)\b)\s*[:=]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex PriorityRegex = new(
        @"(?:\bpriority\s*[:=]\s*(?<p>-?\d+)|\[(?<p2>-?\d+)\])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex BeforeRegex = new(
        @"\bbefore\s*[:=]\s*(?<mods>[^;\]\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex AfterRegex = new(
        @"\bafter\s*[:=]\s*(?<mods>[^;\]\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    public IReadOnlyList<ConflictFinding> Analyze(
        IReadOnlyList<string> harmonyLogRoots,
        IReadOnlyList<ModuleManifest> modules,
        bool customOnlyFocus,
        List<string> warnings
    )
    {
        List<string> allPatchFiles = [];
        List<string> duplicatePatchFiles = [];

        foreach (string root in harmonyLogRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            EnumerateHarmonyLogs(root, "AllHarmonyPatches.txt", allPatchFiles, warnings);
            EnumerateHarmonyLogs(root, "DuplicateHarmonyPatches.txt", duplicatePatchFiles, warnings);
        }

        bool hasHarmonyMods = modules.Any(m =>
            m.Id.Contains("Harmony", StringComparison.OrdinalIgnoreCase)
            || m.Dlls.Any(d => d.ReferencesHarmony)
            || m.Dependencies.Any(d => d.Id.Contains("Harmony", StringComparison.OrdinalIgnoreCase)));

        if (allPatchFiles.Count == 0 && duplicatePatchFiles.Count == 0)
        {
            if (hasHarmonyMods)
            {
                warnings.Add(
                    "No Harmony Patch Scanner logs found (AllHarmonyPatches.txt / DuplicateHarmonyPatches.txt). "
                    + "Built-in static Harmony scanning is still active; scanner logs are optional for higher-confidence runtime patch overlap data."
                );
            }

            return [];
        }

        Dictionary<string, ModuleManifest> byId = modules.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        List<ModuleToken> tokens = BuildModuleTokens(modules);
        List<ConflictFinding> findings = [];
        HashSet<string> dedupeKeys = new(StringComparer.OrdinalIgnoreCase);

        findings.AddRange(ParseDuplicatePatchFiles(
            duplicatePatchFiles,
            tokens,
            byId,
            customOnlyFocus,
            dedupeKeys,
            warnings
        ));

        findings.AddRange(ParseAllPatchFiles(
            allPatchFiles,
            tokens,
            byId,
            customOnlyFocus,
            dedupeKeys,
            warnings
        ));

        if (allPatchFiles.Count > 0 && findings.Count == 0)
        {
            warnings.Add(
                "Harmony logs were found, but no multi-module Harmony targets were parsed from them."
            );
        }

        return findings;
    }

    private static IEnumerable<ConflictFinding> ParseDuplicatePatchFiles(
        IReadOnlyList<string> duplicatePatchFiles,
        IReadOnlyList<ModuleToken> tokens,
        IReadOnlyDictionary<string, ModuleManifest> byId,
        bool customOnlyFocus,
        HashSet<string> dedupeKeys,
        List<string> warnings
    )
    {
        List<ConflictFinding> findings = [];

        foreach (string file in duplicatePatchFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? text = ReadAllTextSafe(file, warnings);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            string[] blocks = Regex.Split(text, @"(?:\r?\n){2,}");
            foreach (string block in blocks)
            {
                List<string> moduleIds = ResolveModules(block, tokens, byId, customOnlyFocus);
                if (moduleIds.Count < 2)
                {
                    continue;
                }

                string target = ExtractTargetSignature(block) ?? "Unknown target method";
                List<string> patchTypes = ExtractPatchTypes(block);
                string key = $"duplicate|{target}|{string.Join("|", moduleIds)}";
                if (!dedupeKeys.Add(key))
                {
                    continue;
                }

                string patchSummary = patchTypes.Count > 0
                    ? string.Join(", ", patchTypes)
                    : "unknown patch kinds";
                HarmonyOwnershipShape ownershipShape = HarmonyFindingProfiles.DetermineOwnershipShape(
                    moduleIds.SelectMany(moduleId => patchTypes.Select(patchType => (moduleId, patchType))));
                HarmonyRiskProfile profile = HarmonyFindingProfiles.Create(
                    target,
                    patchTypes,
                    HarmonyOrderState.Unknown,
                    ownershipShape,
                    hasStaticMetadataEvidence: false,
                    hasDuplicateScannerEvidence: true,
                    hasOrderingGraphEvidence: false,
                    hasRuntimeCorrelationEvidence: false,
                    moduleCount: moduleIds.Count);
                HarmonyRiskAssessment assessment = HarmonyFindingProfiles.Assess(profile);

                findings.Add(new ConflictFinding
                {
                    Category = assessment.Category,
                    Severity = assessment.Severity,
                    Confidence = assessment.Confidence,
                    ModuleIds = moduleIds,
                    Reason = assessment.Reason,
                    LikelyInGameOutcome = assessment.LikelyOutcome,
                    Recommendation = assessment.Recommendation,
                    Evidence = BuildDuplicatePatchEvidence(file, profile, patchSummary),
                    StructuredEvidence = HarmonyFindingProfiles.BuildStructuredEvidence(profile),
                });
            }
        }

        return findings;
    }

    private static IEnumerable<ConflictFinding> ParseAllPatchFiles(
        IReadOnlyList<string> allPatchFiles,
        IReadOnlyList<ModuleToken> tokens,
        IReadOnlyDictionary<string, ModuleManifest> byId,
        bool customOnlyFocus,
        HashSet<string> dedupeKeys,
        List<string> warnings
    )
    {
        List<HarmonyPatchRecord> records = [];
        Dictionary<string, HarmonyGraphAccumulator> graphByTarget = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in allPatchFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? text = ReadAllTextSafe(file, warnings);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            bool parsedFromLines = false;
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                List<string> patchTypes = ExtractPatchTypes(line);
                if (patchTypes.Count == 0)
                {
                    continue;
                }

                string? target = ExtractTargetSignature(line);
                if (string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                List<string> moduleIds = ResolveModules(line, tokens, byId, customOnlyFocus);
                if (moduleIds.Count == 0)
                {
                    continue;
                }

                AddGraphSignals(graphByTarget, target, line, patchTypes, moduleIds, tokens, byId, customOnlyFocus);
                foreach (string moduleId in moduleIds)
                {
                    foreach (string patchType in patchTypes)
                    {
                        records.Add(new HarmonyPatchRecord(moduleId, target, patchType, file));
                    }
                }

                parsedFromLines = true;
            }

            if (parsedFromLines)
            {
                continue;
            }

            foreach (string block in Regex.Split(text, @"(?:\r?\n){2,}"))
            {
                List<string> patchTypes = ExtractPatchTypes(block);
                if (patchTypes.Count == 0)
                {
                    continue;
                }

                string? target = ExtractTargetSignature(block);
                if (string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                List<string> moduleIds = ResolveModules(block, tokens, byId, customOnlyFocus);
                if (moduleIds.Count < 2)
                {
                    continue;
                }

                AddGraphSignals(graphByTarget, target, block, patchTypes, moduleIds, tokens, byId, customOnlyFocus);
                foreach (string moduleId in moduleIds)
                {
                    foreach (string patchType in patchTypes)
                    {
                        records.Add(new HarmonyPatchRecord(moduleId, target, patchType, file));
                    }
                }
            }
        }

        List<ConflictFinding> findings = [];
        foreach (IGrouping<string, HarmonyPatchRecord> targetGroup in records.GroupBy(r => r.TargetMethod, StringComparer.OrdinalIgnoreCase))
        {
            string[] moduleIds = targetGroup.Select(r => r.ModuleId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (moduleIds.Length < 2)
            {
                continue;
            }

            string key = $"graph|{targetGroup.Key}|{string.Join("|", moduleIds)}";
            if (!dedupeKeys.Add(key))
            {
                continue;
            }

            List<string> patchTypes = targetGroup.Select(r => r.PatchType)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            HarmonyGraphMetrics metrics = graphByTarget.TryGetValue(targetGroup.Key, out HarmonyGraphAccumulator? graph)
                ? graph.Build(targetGroup.Key, moduleIds)
                : HarmonyGraphMetrics.Empty(targetGroup.Key, moduleIds);
            HarmonyOrderState orderState = DetermineGraphOrderState(metrics);
            HarmonyOwnershipShape ownershipShape = HarmonyFindingProfiles.DetermineOwnershipShape(
                targetGroup.Select(record => (record.ModuleId, record.PatchType)));
            HarmonyRiskProfile profile = HarmonyFindingProfiles.Create(
                targetGroup.Key,
                patchTypes,
                orderState,
                ownershipShape,
                hasStaticMetadataEvidence: false,
                hasDuplicateScannerEvidence: false,
                hasOrderingGraphEvidence: true,
                hasRuntimeCorrelationEvidence: false,
                moduleCount: moduleIds.Length,
                unorderedPairs: metrics.UnorderedModulePairs,
                ambiguousPairs: metrics.AmbiguousSamePriorityPairs,
                explicitEdgeCount: metrics.ExplicitEdgeCount,
                hasOrderingCycle: metrics.HasOrderingCycle);
            HarmonyRiskAssessment assessment = HarmonyFindingProfiles.Assess(profile);

            findings.Add(new ConflictFinding
            {
                Category = assessment.Category,
                Severity = assessment.Severity,
                Confidence = assessment.Confidence,
                ModuleIds = moduleIds,
                Reason = assessment.Reason,
                LikelyInGameOutcome = assessment.LikelyOutcome,
                Recommendation = assessment.Recommendation,
                Evidence = BuildGraphEvidence(targetGroup, metrics, profile),
                StructuredEvidence = HarmonyFindingProfiles.BuildStructuredEvidence(profile),
            });
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.Confidence)
            .ThenByDescending(f => f.ModuleIds.Count)
            .Take(120)
            .ToList();
    }

    private static ConflictSeverity ComputeGraphDrivenSeverity(
        bool hasTranspiler,
        bool duplicateKindAcrossMods,
        HarmonyGraphMetrics metrics,
        ConflictCategory category,
        bool postfixOnly
    )
    {
        if (postfixOnly)
        {
            return metrics.HasOrderingCycle
                || metrics.AmbiguousSamePriorityPairs > 1
                || metrics.UnorderedModulePairs > 2
                ? ConflictSeverity.Medium
                : ConflictSeverity.Low;
        }

        if (metrics.HasOrderingCycle)
        {
            return ConflictSeverity.Critical;
        }

        if (hasTranspiler && metrics.AmbiguousSamePriorityPairs > 0)
        {
            return ConflictSeverity.Critical;
        }

        if (hasTranspiler
            || metrics.AmbiguousSamePriorityPairs > 1
            || metrics.UnorderedModulePairs > 1
            || duplicateKindAcrossMods)
        {
            return ConflictSeverity.High;
        }

        if (category == ConflictCategory.HarmonyPatchConflict)
        {
            return ConflictSeverity.Medium;
        }

        return ConflictSeverity.Medium;
    }

    private static double ComputeGraphDrivenConfidence(HarmonyGraphMetrics metrics, ConflictCategory category, bool postfixOnly)
    {
        double baseConfidence = postfixOnly
            ? 0.64
            : category == ConflictCategory.HarmonyPatchConflict ? 0.82 : 0.72;
        if (metrics.ExplicitEdgeCount > 0)
        {
            baseConfidence += 0.06;
        }

        if (metrics.UnorderedModulePairs > 0)
        {
            baseConfidence += 0.04;
        }

        if (metrics.AmbiguousSamePriorityPairs > 0)
        {
            baseConfidence += 0.06;
        }

        if (metrics.HasOrderingCycle)
        {
            baseConfidence += 0.05;
        }

        return Math.Clamp(baseConfidence, 0.66, 0.97);
    }

    private static IReadOnlyList<string> BuildGraphEvidence(
        IGrouping<string, HarmonyPatchRecord> targetGroup,
        HarmonyGraphMetrics metrics,
        HarmonyRiskProfile profile
    )
    {
        List<string> evidence = targetGroup.Select(r => r.SourcePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
        evidence.Add($"{HarmonyFindingProfiles.TargetPrefix}{metrics.TargetMethod}");
        evidence.Add($"{HarmonyFindingProfiles.GraphPrefix}modules={metrics.ModuleCount};ops={metrics.OperationCount};edges={metrics.ExplicitEdgeCount};unordered={metrics.UnorderedModulePairs};ambiguous={metrics.AmbiguousSamePriorityPairs};cycle={(metrics.HasOrderingCycle ? 1 : 0)}");
        if (metrics.KindSummary.Length > 0)
        {
            evidence.Add($"{HarmonyFindingProfiles.KindsPrefix}{string.Join(", ", profile.PatchKinds)}");
        }
        if (metrics.PrioritySummary.Length > 0)
        {
            evidence.Add($"{HarmonyFindingProfiles.PriorityPrefix}{metrics.PrioritySummary}");
        }
        evidence.AddRange(HarmonyFindingProfiles.BuildEvidenceTags(profile));

        return evidence.Take(16).ToList();
    }

    private static IReadOnlyList<string> BuildDuplicatePatchEvidence(
        string file,
        HarmonyRiskProfile profile,
        string patchSummary
    )
    {
        List<string> evidence =
        [
            file,
            $"{HarmonyFindingProfiles.TargetPrefix}{profile.TargetMethod}",
            $"{HarmonyFindingProfiles.KindsPrefix}{patchSummary}",
        ];
        evidence.AddRange(HarmonyFindingProfiles.BuildEvidenceTags(profile));

        return evidence;
    }

    private static HarmonyOrderState DetermineGraphOrderState(HarmonyGraphMetrics metrics)
    {
        if (metrics.HasOrderingCycle)
        {
            return HarmonyOrderState.Cycle;
        }

        if (metrics.AmbiguousSamePriorityPairs > 0)
        {
            return HarmonyOrderState.SamePriorityAmbiguous;
        }

        if (metrics.UnorderedModulePairs > 0)
        {
            return HarmonyOrderState.Unordered;
        }

        return metrics.ExplicitEdgeCount > 0
            ? HarmonyOrderState.ExplicitlyOrdered
            : HarmonyOrderState.Unknown;
    }

    private static bool IsPostfixOnlyPatchSet(IEnumerable<string> patchKinds)
    {
        bool hasAny = false;
        foreach (string kind in patchKinds)
        {
            hasAny = true;
            if (!kind.Equals("Postfix", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return hasAny;
    }

    private static void AddGraphSignals(
        IDictionary<string, HarmonyGraphAccumulator> graphByTarget,
        string targetMethod,
        string sourceText,
        IReadOnlyList<string> patchTypes,
        IReadOnlyList<string> fallbackModuleIds,
        IReadOnlyList<ModuleToken> tokens,
        IReadOnlyDictionary<string, ModuleManifest> byId,
        bool customOnlyFocus
    )
    {
        if (!graphByTarget.TryGetValue(targetMethod, out HarmonyGraphAccumulator? accumulator))
        {
            accumulator = new HarmonyGraphAccumulator(targetMethod);
            graphByTarget[targetMethod] = accumulator;
        }

        accumulator.AddSourceLine(sourceText, patchTypes, fallbackModuleIds, tokens, byId, customOnlyFocus);
    }

    private static void EnumerateHarmonyLogs(
        string root,
        string fileName,
        List<string> destination,
        List<string> warnings
    )
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (string path in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
            {
                destination.Add(Path.GetFullPath(path));
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Harmony log search warning in '{root}': {ex.Message}");
        }
    }

    private static string? ReadAllTextSafe(string path, List<string> warnings)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to read Harmony log '{path}': {ex.Message}");
            return null;
        }
    }

    private static List<ModuleToken> BuildModuleTokens(IReadOnlyList<ModuleManifest> modules)
    {
        HashSet<(string moduleId, string token)> raw = new();
        Dictionary<string, HashSet<string>> assemblyTokenOwners = new(StringComparer.OrdinalIgnoreCase);

        foreach (ModuleManifest module in modules)
        {
            AddToken(raw, module.Id, module.Id);
            AddToken(raw, module.Id, module.Name);

            foreach (DllArtifact dll in module.Dlls)
            {
                string? asmToken = !string.IsNullOrWhiteSpace(dll.AssemblyName)
                    ? dll.AssemblyName
                    : Path.GetFileNameWithoutExtension(dll.FileName);
                if (string.IsNullOrWhiteSpace(asmToken) || IsNoisyAssemblyToken(asmToken))
                {
                    continue;
                }

                if (!assemblyTokenOwners.TryGetValue(asmToken, out HashSet<string>? owners))
                {
                    owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    assemblyTokenOwners[asmToken] = owners;
                }

                owners.Add(module.Id);
            }
        }

        foreach ((string token, HashSet<string> owners) in assemblyTokenOwners)
        {
            if (owners.Count != 1)
            {
                continue;
            }

            string ownerModuleId = owners.First();
            AddToken(raw, ownerModuleId, token);
        }

        return raw
            .Select(x => new ModuleToken(x.moduleId, x.token))
            .Where(x => x.Token.Length >= 3)
            .OrderByDescending(x => x.Token.Length)
            .ThenBy(x => x.Token, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddToken(HashSet<(string moduleId, string token)> destination, string moduleId, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        destination.Add((moduleId, token.Trim()));
    }

    private static bool IsNoisyAssemblyToken(string token)
    {
        return token.StartsWith("TaleWorlds.", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase)
            || token.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
            || token.Equals("netstandard", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ResolveModules(
        string text,
        IReadOnlyList<ModuleToken> tokens,
        IReadOnlyDictionary<string, ModuleManifest> byId,
        bool customOnlyFocus
    )
    {
        HashSet<string> matches = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModuleToken token in tokens)
        {
            if (ContainsToken(text, token.Token))
            {
                if (!customOnlyFocus || (byId.TryGetValue(token.ModuleId, out ModuleManifest? mod) && mod.IsCustom))
                {
                    matches.Add(token.ModuleId);
                }
            }
        }

        return matches.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<LanePayload> ExtractLanePayloads(string text, IReadOnlyList<string> fallbackPatchTypes)
    {
        List<LanePayload> lanes = [];
        foreach (Match match in PatchLaneRegex.Matches(text))
        {
            string kind = NormalizePatchKind(match.Groups["kind"].Value);
            if (kind.Length == 0)
            {
                continue;
            }

            string payload = match.Groups["payload"].Value.Trim();
            if (payload.Length == 0)
            {
                continue;
            }

            lanes.Add(new LanePayload(kind, payload));
        }

        if (lanes.Count > 0)
        {
            return lanes;
        }

        // Fallback for compact/irregular formats where lane tokenization fails.
        return fallbackPatchTypes
            .Select(kind => new LanePayload(kind, text))
            .ToList();
    }

    private static string NormalizePatchKind(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "pre" or "prefix" => "Prefix",
            "post" or "postfix" => "Postfix",
            "trans" or "transpiler" => "Transpiler",
            "final" or "finalizer" => "Finalizer",
            _ => string.Empty,
        };
    }

    private static IReadOnlyList<string> SplitPatchEntrySnippets(string payload)
    {
        List<string> snippets = [];
        foreach (string semi in payload.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (semi.Contains("],", StringComparison.Ordinal))
            {
                string[] parts = semi.Split("],", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    string text = i < parts.Length - 1 ? parts[i] + "]" : parts[i];
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        snippets.Add(text.Trim());
                    }
                }

                continue;
            }

            snippets.Add(semi.Trim());
        }

        return snippets.Count == 0 ? [payload.Trim()] : snippets;
    }

    private static IReadOnlyList<int> ExtractPriorities(string text)
    {
        List<int> values = [];
        foreach (Match match in PriorityRegex.Matches(text))
        {
            string value = !string.IsNullOrWhiteSpace(match.Groups["p"].Value)
                ? match.Groups["p"].Value
                : match.Groups["p2"].Value;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int priority))
            {
                continue;
            }

            if (priority is < -20000 or > 20000)
            {
                continue;
            }

            values.Add(priority);
        }

        return values;
    }

    private static IReadOnlyList<string> ExtractConstraintModules(
        string text,
        Regex regex,
        IReadOnlyList<ModuleToken> tokens,
        IReadOnlyDictionary<string, ModuleManifest> byId,
        bool customOnlyFocus
    )
    {
        HashSet<string> modules = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in regex.Matches(text))
        {
            string raw = match.Groups["mods"].Value;
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            foreach (string moduleId in ResolveModules(raw, tokens, byId, customOnlyFocus))
            {
                modules.Add(moduleId);
            }
        }

        return modules.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool ContainsToken(string text, string token)
    {
        int start = 0;
        while (true)
        {
            int idx = text.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }

            bool leftBoundary = idx == 0 || IsBoundary(text[idx - 1]);
            int rightIdx = idx + token.Length;
            bool rightBoundary = rightIdx >= text.Length || IsBoundary(text[rightIdx]);
            if (leftBoundary && rightBoundary)
            {
                return true;
            }

            start = idx + token.Length;
        }
    }

    private static bool IsBoundary(char c) => !char.IsLetterOrDigit(c) && c != '_';

    private static List<string> ExtractPatchTypes(string text)
    {
        TextInfo ti = CultureInfo.InvariantCulture.TextInfo;
        return PatchTypeRegex.Matches(text)
            .Select(x => ti.ToTitleCase(x.Value.ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ExtractTargetSignature(string text)
    {
        MatchCollection matches = MethodSignatureRegex.Matches(text);
        if (matches.Count > 0)
        {
            return matches
                .Select(m => m.Groups["sig"].Value.Trim())
                .OrderByDescending(x => x.Length)
                .FirstOrDefault();
        }

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Contains("method", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("target", StringComparison.OrdinalIgnoreCase))
            {
                int idx = trimmed.IndexOf(':');
                if (idx >= 0 && idx < trimmed.Length - 1)
                {
                    return trimmed[(idx + 1)..].Trim();
                }
            }
        }

        return null;
    }

    private sealed record ModuleToken(string ModuleId, string Token);

    private sealed record HarmonyPatchRecord(string ModuleId, string TargetMethod, string PatchType, string SourcePath);

    private sealed record LanePayload(string Kind, string Payload);

    private sealed record GraphPatchNode(
        string ModuleId,
        string PatchKind,
        int? Priority,
        IReadOnlyList<string> BeforeModules,
        IReadOnlyList<string> AfterModules
    );

    private sealed class HarmonyGraphAccumulator
    {
        private readonly string _targetMethod;
        private readonly List<GraphPatchNode> _nodes = [];

        public HarmonyGraphAccumulator(string targetMethod)
        {
            _targetMethod = targetMethod;
        }

        public void AddSourceLine(
            string sourceText,
            IReadOnlyList<string> patchTypes,
            IReadOnlyList<string> fallbackModuleIds,
            IReadOnlyList<ModuleToken> tokens,
            IReadOnlyDictionary<string, ModuleManifest> byId,
            bool customOnlyFocus
        )
        {
            IReadOnlyList<LanePayload> lanes = ExtractLanePayloads(sourceText, patchTypes);
            foreach (LanePayload lane in lanes)
            {
                IReadOnlyList<string> snippets = SplitPatchEntrySnippets(lane.Payload);
                foreach (string snippet in snippets)
                {
                    List<string> owners = ResolveModules(snippet, tokens, byId, customOnlyFocus);
                    if (owners.Count == 0)
                    {
                        owners = fallbackModuleIds.ToList();
                    }

                    if (owners.Count == 0)
                    {
                        continue;
                    }

                    IReadOnlyList<int> snippetPriorities = ExtractPriorities(snippet);
                    IReadOnlyList<string> beforeModules = ExtractConstraintModules(snippet, BeforeRegex, tokens, byId, customOnlyFocus);
                    IReadOnlyList<string> afterModules = ExtractConstraintModules(snippet, AfterRegex, tokens, byId, customOnlyFocus);

                    for (int i = 0; i < owners.Count; i++)
                    {
                        int? priority = snippetPriorities.Count == 0
                            ? null
                            : snippetPriorities[Math.Min(i, snippetPriorities.Count - 1)];
                        string owner = owners[i];
                        List<string> before = beforeModules
                            .Where(m => !m.Equals(owner, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        List<string> after = afterModules
                            .Where(m => !m.Equals(owner, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        _nodes.Add(new GraphPatchNode(owner, lane.Kind, priority, before, after));
                    }
                }
            }
        }

        public HarmonyGraphMetrics Build(string targetMethod, IReadOnlyList<string> modules)
        {
            HashSet<string> moduleSet = modules.ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<GraphPatchNode> relevantNodes = _nodes
                .Where(n => moduleSet.Contains(n.ModuleId))
                .ToList();
            if (relevantNodes.Count == 0)
            {
                return HarmonyGraphMetrics.Empty(targetMethod, modules);
            }

            Dictionary<string, HashSet<string>> adjacency = new(StringComparer.OrdinalIgnoreCase);
            foreach (string module in modules)
            {
                adjacency[module] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            foreach (GraphPatchNode node in relevantNodes)
            {
                foreach (string before in node.BeforeModules.Where(moduleSet.Contains))
                {
                    if (!before.Equals(node.ModuleId, StringComparison.OrdinalIgnoreCase))
                    {
                        adjacency[node.ModuleId].Add(before);
                    }
                }

                foreach (string after in node.AfterModules.Where(moduleSet.Contains))
                {
                    if (!after.Equals(node.ModuleId, StringComparison.OrdinalIgnoreCase))
                    {
                        adjacency[after].Add(node.ModuleId);
                    }
                }
            }

            Dictionary<(string From, string To), bool> reachability = BuildReachability(adjacency);
            int unorderedPairs = 0;
            int ambiguousSamePriorityPairs = 0;

            string[] orderedModules = modules
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            for (int i = 0; i < orderedModules.Length; i++)
            {
                for (int j = i + 1; j < orderedModules.Length; j++)
                {
                    bool aToB = reachability.GetValueOrDefault((orderedModules[i], orderedModules[j]));
                    bool bToA = reachability.GetValueOrDefault((orderedModules[j], orderedModules[i]));
                    if (!aToB && !bToA)
                    {
                        unorderedPairs++;
                    }
                }
            }

            foreach (IGrouping<string, GraphPatchNode> kindGroup in relevantNodes
                         .GroupBy(n => n.PatchKind, StringComparer.OrdinalIgnoreCase))
            {
                foreach (IGrouping<int, GraphPatchNode> priorityGroup in kindGroup
                             .GroupBy(n => n.Priority ?? HarmonyDefaultPriority))
                {
                    string[] bucketModules = priorityGroup
                        .Select(n => n.ModuleId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (bucketModules.Length < 2)
                    {
                        continue;
                    }

                    for (int i = 0; i < bucketModules.Length; i++)
                    {
                        for (int j = i + 1; j < bucketModules.Length; j++)
                        {
                            bool leftToRight = reachability.GetValueOrDefault((bucketModules[i], bucketModules[j]));
                            bool rightToLeft = reachability.GetValueOrDefault((bucketModules[j], bucketModules[i]));
                            if (!leftToRight && !rightToLeft)
                            {
                                ambiguousSamePriorityPairs++;
                            }
                        }
                    }
                }
            }

            bool hasCycle = HasCycle(adjacency);
            int edgeCount = adjacency.Values.Sum(set => set.Count);
            string kindSummary = string.Join(", ", relevantNodes
                .GroupBy(n => n.PatchKind, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"{g.Key}:{g.Count()}"));
            string prioritySummary = string.Join(", ", relevantNodes
                .GroupBy(n => n.Priority ?? HarmonyDefaultPriority)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Key}:{g.Count()}"));

            return new HarmonyGraphMetrics(
                TargetMethod: _targetMethod,
                ModuleCount: orderedModules.Length,
                OperationCount: relevantNodes.Count,
                ExplicitEdgeCount: edgeCount,
                UnorderedModulePairs: unorderedPairs,
                AmbiguousSamePriorityPairs: ambiguousSamePriorityPairs,
                HasOrderingCycle: hasCycle,
                KindSummary: kindSummary,
                PrioritySummary: prioritySummary
            );
        }

        private static Dictionary<(string From, string To), bool> BuildReachability(
            IReadOnlyDictionary<string, HashSet<string>> adjacency
        )
        {
            Dictionary<(string From, string To), bool> reachability = [];
            foreach (string source in adjacency.Keys)
            {
                HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
                Queue<string> queue = new();
                queue.Enqueue(source);
                while (queue.Count > 0)
                {
                    string current = queue.Dequeue();
                    if (!adjacency.TryGetValue(current, out HashSet<string>? nextNodes))
                    {
                        continue;
                    }

                    foreach (string next in nextNodes)
                    {
                        if (!visited.Add(next))
                        {
                            continue;
                        }

                        reachability[(source, next)] = true;
                        queue.Enqueue(next);
                    }
                }
            }

            return reachability;
        }

        private static bool HasCycle(IReadOnlyDictionary<string, HashSet<string>> adjacency)
        {
            HashSet<string> visiting = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
            foreach (string node in adjacency.Keys)
            {
                if (DetectCycle(node, adjacency, visiting, visited))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool DetectCycle(
            string node,
            IReadOnlyDictionary<string, HashSet<string>> adjacency,
            ISet<string> visiting,
            ISet<string> visited
        )
        {
            if (visited.Contains(node))
            {
                return false;
            }

            if (!visiting.Add(node))
            {
                return true;
            }

            if (adjacency.TryGetValue(node, out HashSet<string>? nextNodes))
            {
                foreach (string next in nextNodes)
                {
                    if (DetectCycle(next, adjacency, visiting, visited))
                    {
                        return true;
                    }
                }
            }

            visiting.Remove(node);
            visited.Add(node);
            return false;
        }
    }

    private sealed record HarmonyGraphMetrics(
        string TargetMethod,
        int ModuleCount,
        int OperationCount,
        int ExplicitEdgeCount,
        int UnorderedModulePairs,
        int AmbiguousSamePriorityPairs,
        bool HasOrderingCycle,
        string KindSummary,
        string PrioritySummary
    )
    {
        public static HarmonyGraphMetrics Empty(string targetMethod, IReadOnlyList<string> modules) => new(
            TargetMethod: targetMethod,
            ModuleCount: modules.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            OperationCount: 0,
            ExplicitEdgeCount: 0,
            UnorderedModulePairs: 0,
            AmbiguousSamePriorityPairs: 0,
            HasOrderingCycle: false,
            KindSummary: string.Empty,
            PrioritySummary: string.Empty
        );
    }
}
