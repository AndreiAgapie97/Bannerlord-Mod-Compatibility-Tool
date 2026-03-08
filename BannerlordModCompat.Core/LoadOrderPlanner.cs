namespace BannerlordModCompat.Core;

public sealed class LoadOrderPlanner
{
    private static readonly Dictionary<string, int> PriorityByAlias = BuildPriorityAliasMap();

    private static readonly HashSet<ConflictCategory> StabilityCategories =
    [
        ConflictCategory.HarmonyPatchConflict,
        ConflictCategory.HarmonyPatchStack,
        ConflictCategory.GameModelOverlap,
        ConflictCategory.BehaviorEventOverlap,
        ConflictCategory.MissionBehaviorOverlap,
        ConflictCategory.LifecycleRegistrationOverlap,
        ConflictCategory.XmlEntityCollision,
    ];

    public LoadOrderRecommendation Build(
        IReadOnlyList<ModuleManifest> modules,
        IReadOnlyList<string> currentOrder,
        IReadOnlyList<string> pinnedMods,
        IReadOnlyList<ConflictFinding>? findings = null
    )
        => Build(modules, currentOrder, pinnedMods, findings, capabilityProfiles: null);

    internal LoadOrderRecommendation Build(
        IReadOnlyList<ModuleManifest> modules,
        IReadOnlyList<string> currentOrder,
        IReadOnlyList<string> pinnedMods,
        IReadOnlyList<ConflictFinding>? findings = null,
        IReadOnlyList<ModuleCapabilityProfile>? capabilityProfiles = null
    )
    {
        HashSet<string> moduleIds = modules
            .Select(m => m.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ModuleManifest> byId = modules
            .ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> currentIndex = currentOrder
            .Select((id, index) => new { id, index })
            .GroupBy(x => x.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, HashSet<string>> edges = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> inDegree = new(StringComparer.OrdinalIgnoreCase);
        foreach (string id in moduleIds)
        {
            edges[id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            inDegree[id] = 0;
        }
        Dictionary<string, HashSet<LoadOrderRationaleKind>> rationaleKindsByModule = moduleIds
            .ToDictionary(
                id => id,
                _ => new HashSet<LoadOrderRationaleKind>(),
                StringComparer.OrdinalIgnoreCase);

        List<string> warnings = [];
        int dependencyEdges = AddDependencyEdges(modules, moduleIds, edges, inDegree, warnings, rationaleKindsByModule);
        int canonicalEdges = AddRuleSetEdges(
            moduleIds,
            currentIndex,
            capabilityProfiles,
            edges,
            inDegree,
            warnings,
            rationaleKindsByModule);
        int stabilityEdges = AddConflictStabilityEdges(moduleIds, currentIndex, findings, edges, inDegree, rationaleKindsByModule);

        PriorityQueue<string, (int tier, int current, string alpha)> queue = new();
        foreach (KeyValuePair<string, int> node in inDegree.Where(x => x.Value == 0))
        {
            queue.Enqueue(
                node.Key,
                (
                    GetPriorityTier(node.Key, byId),
                    currentIndex.GetValueOrDefault(node.Key, int.MaxValue),
                    node.Key
                )
            );
        }

        List<string> result = [];
        while (queue.TryDequeue(out string? id, out _))
        {
            result.Add(id);
            foreach (string dependent in edges[id])
            {
                inDegree[dependent]--;
                if (inDegree[dependent] == 0)
                {
                    queue.Enqueue(
                        dependent,
                        (
                            GetPriorityTier(dependent, byId),
                            currentIndex.GetValueOrDefault(dependent, int.MaxValue),
                            dependent
                        )
                    );
                }
            }
        }

        bool hadCycle = result.Count != moduleIds.Count;
        if (hadCycle)
        {
            warnings.Add("Dependency/order cycle detected. Remaining modules were appended using current load order.");
            foreach (string id in currentOrder)
            {
                if (!result.Contains(id, StringComparer.OrdinalIgnoreCase) && moduleIds.Contains(id))
                {
                    result.Add(id);
                }
            }

            foreach (string id in moduleIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!result.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(id);
                }
            }
        }

        if (pinnedMods.Count > 0)
        {
            ApplyPins(result, currentOrder, pinnedMods, warnings, rationaleKindsByModule);
            ValidatePinnedOrder(result, modules, warnings);
        }

        List<LoadOrderMove> moves = BuildMoves(currentOrder, result);
        List<string> rationale = BuildRationale(dependencyEdges, canonicalEdges, stabilityEdges, pinnedMods.Count, hadCycle);
        List<LoadOrderModuleRationale> moduleRationales = BuildModuleRationales(moduleIds, rationaleKindsByModule);
        double confidence = ComputeConfidence(
            dependencyEdges,
            canonicalEdges,
            stabilityEdges,
            hadCycle,
            moves.Count,
            Math.Max(1, moduleIds.Count)
        );

        return new LoadOrderRecommendation
        {
            CurrentOrder = currentOrder,
            SuggestedOrder = result,
            Moves = moves,
            Warnings = warnings,
            Rationale = rationale,
            Confidence = confidence,
            ModuleRationales = moduleRationales,
        };
    }

    private static int AddDependencyEdges(
        IReadOnlyList<ModuleManifest> modules,
        IReadOnlySet<string> moduleIds,
        IReadOnlyDictionary<string, HashSet<string>> edges,
        IDictionary<string, int> inDegree,
        List<string> warnings,
        IReadOnlyDictionary<string, HashSet<LoadOrderRationaleKind>> rationaleKindsByModule
    )
    {
        int added = 0;
        foreach (ModuleManifest module in modules)
        {
            foreach (ModuleDependency dep in module.Dependencies)
            {
                if (!moduleIds.Contains(dep.Id))
                {
                    continue;
                }

                string from = dep.Order == LoadOrderConstraint.LoadAfterThis
                    ? module.Id
                    : dep.Id;
                string to = dep.Order == LoadOrderConstraint.LoadAfterThis
                    ? dep.Id
                    : module.Id;

                MarkRationale(rationaleKindsByModule, module.Id, LoadOrderRationaleKind.Dependency);
                MarkRationale(rationaleKindsByModule, dep.Id, LoadOrderRationaleKind.Dependency);
                if (TryAddEdge(from, to, dep.Optional, edges, inDegree, warnings))
                {
                    added++;
                }
            }
        }

        return added;
    }

    private static int AddRuleSetEdges(
        IReadOnlySet<string> moduleIds,
        IReadOnlyDictionary<string, int> currentIndex,
        IReadOnlyList<ModuleCapabilityProfile>? capabilityProfiles,
        IReadOnlyDictionary<string, HashSet<string>> edges,
        IDictionary<string, int> inDegree,
        List<string> warnings,
        IReadOnlyDictionary<string, HashSet<LoadOrderRationaleKind>> rationaleKindsByModule
    )
    {
        int added = 0;
        foreach (LoadOrderRuleEdge rule in LoadOrderRuleSet.BuildEdges(moduleIds, currentIndex, capabilityProfiles))
        {
            MarkRationale(rationaleKindsByModule, rule.FromModuleId, rule.RationaleKind);
            MarkRationale(rationaleKindsByModule, rule.ToModuleId, rule.RationaleKind);
            if (TryAddEdge(rule.FromModuleId, rule.ToModuleId, rule.Optional, edges, inDegree, warnings))
            {
                added++;
            }
        }

        return added;
    }

    private static int AddConflictStabilityEdges(
        IReadOnlySet<string> moduleIds,
        IReadOnlyDictionary<string, int> currentIndex,
        IReadOnlyList<ConflictFinding>? findings,
        IReadOnlyDictionary<string, HashSet<string>> edges,
        IDictionary<string, int> inDegree,
        IReadOnlyDictionary<string, HashSet<LoadOrderRationaleKind>> rationaleKindsByModule
    )
    {
        if (findings is null || findings.Count == 0)
        {
            return 0;
        }

        int added = 0;
        foreach (ConflictFinding finding in findings.Where(f => StabilityCategories.Contains(f.Category)))
        {
            string[] participants = finding.ModuleIds
                .Where(id => moduleIds.Contains(id) && currentIndex.ContainsKey(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (string participant in participants)
            {
                MarkRationale(rationaleKindsByModule, participant, LoadOrderRationaleKind.StabilityPreference);
            }

            for (int i = 0; i < participants.Length; i++)
            {
                for (int j = i + 1; j < participants.Length; j++)
                {
                    string a = participants[i];
                    string b = participants[j];
                    string earlier = currentIndex[a] <= currentIndex[b] ? a : b;
                    string later = currentIndex[a] <= currentIndex[b] ? b : a;

                    if (edges[later].Contains(earlier))
                    {
                        continue;
                    }

                    if (edges[earlier].Add(later))
                    {
                        inDegree[later]++;
                        added++;
                    }
                }
            }
        }

        return added;
    }

    private static bool TryAddEdge(
        string from,
        string to,
        bool optional,
        IReadOnlyDictionary<string, HashSet<string>> edges,
        IDictionary<string, int> inDegree,
        List<string> warnings
    )
    {
        if (from.Equals(to, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (edges[to].Contains(from))
        {
            if (!optional)
            {
                warnings.Add($"Conflicting hard order rules detected between '{from}' and '{to}'.");
            }

            return false;
        }

        if (!edges[from].Add(to))
        {
            return false;
        }

        inDegree[to]++;
        return true;
    }

    private static void ApplyPins(
        List<string> suggestedOrder,
        IReadOnlyList<string> currentOrder,
        IReadOnlyList<string> pinnedMods,
        List<string> warnings,
        IReadOnlyDictionary<string, HashSet<LoadOrderRationaleKind>> rationaleKindsByModule
    )
    {
        Dictionary<string, int> currentIndex = currentOrder
            .Select((id, index) => new { id, index })
            .GroupBy(x => x.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);

        foreach (string pin in pinnedMods.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            int existingIndex = suggestedOrder.FindIndex(id => id.Equals(pin, StringComparison.OrdinalIgnoreCase));
            if (existingIndex < 0)
            {
                warnings.Add($"Pinned mod '{pin}' was not found in installed module set.");
                continue;
            }

            MarkRationale(rationaleKindsByModule, pin, LoadOrderRationaleKind.Pin);
            suggestedOrder.RemoveAt(existingIndex);
            int targetIndex = currentIndex.TryGetValue(pin, out int idx) ? idx : suggestedOrder.Count;
            targetIndex = Math.Clamp(targetIndex, 0, suggestedOrder.Count);
            suggestedOrder.Insert(targetIndex, pin);
        }
    }

    private static void ValidatePinnedOrder(
        IReadOnlyList<string> suggestedOrder,
        IReadOnlyList<ModuleManifest> modules,
        List<string> warnings
    )
    {
        Dictionary<string, int> order = suggestedOrder
            .Select((id, idx) => new { id, idx })
            .GroupBy(x => x.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().idx, StringComparer.OrdinalIgnoreCase);

        int violationCount = 0;
        foreach (ModuleManifest module in modules)
        {
            if (!order.TryGetValue(module.Id, out int moduleIdx))
            {
                continue;
            }

            foreach (ModuleDependency dep in module.Dependencies.Where(d => !d.Optional))
            {
                if (!order.TryGetValue(dep.Id, out int depIdx))
                {
                    continue;
                }

                bool expectsAfter = dep.Order == LoadOrderConstraint.LoadAfterThis;
                bool invalid = expectsAfter ? depIdx < moduleIdx : depIdx > moduleIdx;
                if (!invalid)
                {
                    continue;
                }

                string expected = expectsAfter ? "after" : "before";
                warnings.Add($"Pinned/manual order may violate dependency: '{dep.Id}' should load {expected} '{module.Id}'.");
                violationCount++;
                if (violationCount >= 10)
                {
                    warnings.Add("Additional pinned-order dependency violations were truncated.");
                    return;
                }
            }
        }
    }

    private static List<string> BuildRationale(
        int dependencyEdges,
        int canonicalEdges,
        int stabilityEdges,
        int pinCount,
        bool hadCycle
    )
    {
        List<string> lines =
        [
            $"Hard rules from module metadata: {dependencyEdges} dependency/order constraint(s) from SubModule data.",
            $"Bootstrap chain rules: {canonicalEdges} framework/core sequencing constraint(s) (Harmony -> ButterLib -> UIExtenderEx -> MCM -> Native...).",
            "Tie-break strategy: keep current order when rules do not force a move (reduces unnecessary behavior changes).",
        ];

        if (stabilityEdges > 0)
        {
            lines.Add(
                $"Conflict-aware stability: {stabilityEdges} extra constraint(s) preserve current relative order for overlapping Harmony/model/behavior modules."
            );
        }

        if (pinCount > 0)
        {
            lines.Add($"Pinned modules: {pinCount} module(s) were kept near their current slot where possible.");
        }

        if (hadCycle)
        {
            lines.Add("Cycle fallback used: conflicting rules existed, so unresolved modules were appended using current order.");
        }

        return lines;
    }

    private static List<LoadOrderModuleRationale> BuildModuleRationales(
        IReadOnlySet<string> moduleIds,
        IReadOnlyDictionary<string, HashSet<LoadOrderRationaleKind>> rationaleKindsByModule)
    {
        return moduleIds
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(moduleId =>
            {
                List<LoadOrderRationaleKind> kinds = rationaleKindsByModule.TryGetValue(moduleId, out HashSet<LoadOrderRationaleKind>? values)
                    ? values.OrderBy(GetRationalePriority).ToList()
                    : [];
                if (kinds.Count == 0)
                {
                    kinds.Add(LoadOrderRationaleKind.AlreadyGood);
                }

                LoadOrderRationaleKind primaryKind = kinds[0];
                return new LoadOrderModuleRationale
                {
                    ModuleId = moduleId,
                    PrimaryKind = primaryKind,
                    Kinds = kinds,
                    Summary = BuildModuleRationaleSummary(primaryKind, kinds),
                };
            })
            .ToList();
    }

    private static double ComputeConfidence(
        int dependencyEdges,
        int canonicalEdges,
        int stabilityEdges,
        bool hadCycle,
        int moveCount,
        int moduleCount
    )
    {
        double confidence = 0.45;
        confidence += dependencyEdges > 0 ? 0.24 : 0.05;
        confidence += canonicalEdges > 0 ? 0.14 : 0.00;
        confidence += stabilityEdges > 0 ? 0.09 : 0.00;

        double moveRatio = (double)moveCount / moduleCount;
        confidence -= Math.Min(0.08, moveRatio * 0.08);

        if (hadCycle)
        {
            confidence -= 0.28;
        }

        return Math.Clamp(confidence, 0.20, 0.98);
    }

    private static string BuildModuleRationaleSummary(
        LoadOrderRationaleKind primaryKind,
        IReadOnlyList<LoadOrderRationaleKind> kinds)
    {
        bool hasDependency = kinds.Contains(LoadOrderRationaleKind.Dependency);
        bool hasBootstrap = kinds.Contains(LoadOrderRationaleKind.Bootstrap);
        bool hasUiPrecedence = kinds.Contains(LoadOrderRationaleKind.OfficialUiPrecedence);
        bool hasStability = kinds.Contains(LoadOrderRationaleKind.StabilityPreference);

        if (primaryKind == LoadOrderRationaleKind.Pin)
        {
            return "Pinned near the current slot while the planner preserved surrounding rules where possible.";
        }

        if (hasUiPrecedence)
        {
            return "Position is shaped by a known Bannerlord UI precedence rule for official-module overrides.";
        }

        if (hasDependency && hasBootstrap)
        {
            return "Position is shaped by both dependency metadata and the core/framework bootstrap sequence.";
        }

        if (hasDependency)
        {
            return "Position is shaped by declared dependency/order constraints from module metadata.";
        }

        if (hasBootstrap)
        {
            return "Position is shaped by the core/framework bootstrap sequence.";
        }

        if (hasStability)
        {
            return "Position is kept stable because this module overlaps with other active modules.";
        }

        return "No stronger dependency or bootstrap rule forced a different slot for this module.";
    }

    private static List<LoadOrderMove> BuildMoves(IReadOnlyList<string> currentOrder, IReadOnlyList<string> suggestedOrder)
    {
        Dictionary<string, int> currentIndex = currentOrder
            .Select((id, index) => new { id, index })
            .GroupBy(x => x.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);

        List<LoadOrderMove> moves = [];
        for (int i = 0; i < suggestedOrder.Count; i++)
        {
            string id = suggestedOrder[i];
            if (!currentIndex.TryGetValue(id, out int from) || from == i)
            {
                continue;
            }

            moves.Add(new LoadOrderMove(id, from, i));
        }

        return moves;
    }

    private static int GetPriorityTier(string moduleId, IReadOnlyDictionary<string, ModuleManifest> modules)
    {
        if (PriorityByAlias.TryGetValue(moduleId, out int tier))
        {
            return tier;
        }

        if (modules.TryGetValue(moduleId, out ModuleManifest? module))
        {
            if (module.IsOfficial)
            {
                return 1_000;
            }

            if (module.IsFramework)
            {
                return 900;
            }

            return 2_000;
        }

        return 3_000;
    }

    private static Dictionary<string, int> BuildPriorityAliasMap()
    {
        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string[]> groups = BannerlordKnowledgeBase.GetCanonicalBootstrapGroups();
        for (int i = 0; i < groups.Count; i++)
        {
            foreach (string alias in groups[i])
            {
                map[alias] = i;
            }
        }

        return map;
    }

    private static void MarkRationale(
        IReadOnlyDictionary<string, HashSet<LoadOrderRationaleKind>> rationaleKindsByModule,
        string moduleId,
        LoadOrderRationaleKind kind)
    {
        if (rationaleKindsByModule.TryGetValue(moduleId, out HashSet<LoadOrderRationaleKind>? kinds))
        {
            kinds.Add(kind);
        }
    }

    private static int GetRationalePriority(LoadOrderRationaleKind kind)
    {
        return kind switch
        {
            LoadOrderRationaleKind.Pin => 0,
            LoadOrderRationaleKind.Dependency => 1,
            LoadOrderRationaleKind.Bootstrap => 2,
            LoadOrderRationaleKind.OfficialUiPrecedence => 3,
            LoadOrderRationaleKind.StabilityPreference => 4,
            LoadOrderRationaleKind.DisabledInstalled => 5,
            LoadOrderRationaleKind.ManualReview => 6,
            _ => 7,
        };
    }
}
