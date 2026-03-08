namespace BannerlordModCompat.Core;

public sealed record PlayerLoadOrderProjection
{
    public required LoadOrderRecommendation Recommendation { get; init; }
    public required IReadOnlyList<string> InactiveInstalledModuleIds { get; init; }
    public required IReadOnlyList<PlayerLoadOrderProjectionRow> Rows { get; init; }
    public required IReadOnlyList<PlayerLoadOrderProjectionRow> InactiveInstalledRows { get; init; }
}

public sealed record PlayerLoadOrderProjectionRow
{
    public required string ModuleId { get; init; }
    public int? CurrentIndex { get; init; }
    public int? SuggestedIndex { get; init; }
    public bool IsChanged { get; init; }
    public bool IsInactiveInstalled { get; init; }
    public bool IsOfficial { get; init; }
    public bool IsFramework { get; init; }
    public bool IsCustom { get; init; }
    public required LoadOrderRationaleKind PrimaryReasonKind { get; init; }
    public IReadOnlyList<LoadOrderRationaleKind> ReasonKinds { get; init; } = [];
    public required string ReasonSummary { get; init; }
}

public static class PlayerLoadOrderProjectionBuilder
{
    public static PlayerLoadOrderProjection BuildEnabledSingleplayerProjection(LoadOrderRecommendation source)
        => BuildEnabledSingleplayerProjection(source, modules: [], findings: []);

    public static PlayerLoadOrderProjection BuildEnabledSingleplayerProjection(
        LoadOrderRecommendation source,
        IReadOnlyList<ModuleManifest> modules,
        IReadOnlyList<ConflictFinding> findings)
    {
        List<string> current = source.CurrentOrder
            .Where(ShouldIncludeInSingleplayerPlayerView)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        HashSet<string> enabledIds = current.ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> suggested = source.SuggestedOrder
            .Where(id => enabledIds.Contains(id) && ShouldIncludeInSingleplayerPlayerView(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string moduleId in current)
        {
            if (!suggested.Contains(moduleId, StringComparer.OrdinalIgnoreCase))
            {
                suggested.Add(moduleId);
            }
        }

        List<string> inactiveInstalled = source.SuggestedOrder
            .Where(id => !enabledIds.Contains(id) && ShouldIncludeInSingleplayerPlayerView(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<string> warnings = [.. source.Warnings];
        List<string> rationale = [.. source.Rationale];
        warnings.Add("Player load-order view is limited to currently enabled singleplayer modules.");
        rationale.Add("Installed but disabled modules are listed separately so the player-facing move plan stays focused on the active profile.");

        Dictionary<string, ModuleManifest> modulesById = modules
            .ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, LoadOrderModuleRationale> rationaleByModule = source.ModuleRationales
            .GroupBy(r => r.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        List<PlayerLoadOrderProjectionRow> rows = BuildRows(
            current,
            suggested,
            modulesById,
            rationaleByModule);
        List<PlayerLoadOrderProjectionRow> inactiveRows = BuildInactiveRows(
            inactiveInstalled,
            modulesById,
            rationaleByModule);

        return new PlayerLoadOrderProjection
        {
            Recommendation = new LoadOrderRecommendation
            {
                CurrentOrder = current,
                SuggestedOrder = suggested,
                Moves = BuildMoves(current, suggested),
                Warnings = warnings,
                Rationale = rationale,
                Confidence = source.Confidence,
                ModuleRationales = source.ModuleRationales
                    .Where(r => current.Contains(r.ModuleId, StringComparer.OrdinalIgnoreCase))
                    .ToList(),
            },
            InactiveInstalledModuleIds = inactiveInstalled,
            Rows = rows,
            InactiveInstalledRows = inactiveRows,
        };
    }

    private static bool ShouldIncludeInSingleplayerPlayerView(string moduleId)
    {
        return !ModuleTaxonomy.IsSingleplayerHiddenLoadOrderModule(moduleId);
    }

    private static IReadOnlyList<LoadOrderMove> BuildMoves(
        IReadOnlyList<string> currentOrder,
        IReadOnlyList<string> suggestedOrder)
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

    private static List<PlayerLoadOrderProjectionRow> BuildRows(
        IReadOnlyList<string> currentOrder,
        IReadOnlyList<string> suggestedOrder,
        IReadOnlyDictionary<string, ModuleManifest> modulesById,
        IReadOnlyDictionary<string, LoadOrderModuleRationale> rationaleByModule)
    {
        Dictionary<string, int> currentIndex = currentOrder
            .Select((id, index) => new { id, index })
            .GroupBy(x => x.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> suggestedIndex = suggestedOrder
            .Select((id, index) => new { id, index })
            .GroupBy(x => x.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);

        List<PlayerLoadOrderProjectionRow> rows = [];
        foreach (string moduleId in suggestedOrder)
        {
            currentIndex.TryGetValue(moduleId, out int current);
            suggestedIndex.TryGetValue(moduleId, out int suggested);
            bool hasCurrent = currentIndex.ContainsKey(moduleId);
            LoadOrderModuleRationale rationale = rationaleByModule.TryGetValue(moduleId, out LoadOrderModuleRationale? existing)
                ? existing
                : new LoadOrderModuleRationale
                {
                    ModuleId = moduleId,
                    PrimaryKind = LoadOrderRationaleKind.AlreadyGood,
                    Kinds = [LoadOrderRationaleKind.AlreadyGood],
                    Summary = "No stronger dependency or bootstrap rule forced a different slot for this module.",
                };
            rows.Add(BuildProjectionRow(
                moduleId,
                hasCurrent ? current : null,
                suggested,
                isInactiveInstalled: false,
                modulesById,
                rationale));
        }

        return rows;
    }

    private static List<PlayerLoadOrderProjectionRow> BuildInactiveRows(
        IReadOnlyList<string> inactiveInstalled,
        IReadOnlyDictionary<string, ModuleManifest> modulesById,
        IReadOnlyDictionary<string, LoadOrderModuleRationale> rationaleByModule)
    {
        List<PlayerLoadOrderProjectionRow> rows = [];
        foreach (string moduleId in inactiveInstalled)
        {
            LoadOrderModuleRationale rationale = rationaleByModule.TryGetValue(moduleId, out LoadOrderModuleRationale? existing)
                ? existing with
                {
                    PrimaryKind = LoadOrderRationaleKind.DisabledInstalled,
                    Kinds = existing.Kinds
                        .Concat([LoadOrderRationaleKind.DisabledInstalled])
                        .Distinct()
                        .ToList(),
                    Summary = "Installed but currently disabled, so it is not part of the player-facing order recommendation.",
                }
                : new LoadOrderModuleRationale
                {
                    ModuleId = moduleId,
                    PrimaryKind = LoadOrderRationaleKind.DisabledInstalled,
                    Kinds = [LoadOrderRationaleKind.DisabledInstalled],
                    Summary = "Installed but currently disabled, so it is not part of the player-facing order recommendation.",
                };
            rows.Add(BuildProjectionRow(
                moduleId,
                currentIndex: null,
                suggestedIndex: null,
                isInactiveInstalled: true,
                modulesById,
                rationale));
        }

        return rows;
    }

    private static PlayerLoadOrderProjectionRow BuildProjectionRow(
        string moduleId,
        int? currentIndex,
        int? suggestedIndex,
        bool isInactiveInstalled,
        IReadOnlyDictionary<string, ModuleManifest> modulesById,
        LoadOrderModuleRationale rationale)
    {
        modulesById.TryGetValue(moduleId, out ModuleManifest? manifest);
        bool isOfficial = manifest?.IsOfficial == true || ModuleTaxonomy.IsOfficial(moduleId);
        bool isFramework = manifest?.IsFramework == true || ModuleTaxonomy.IsFramework(moduleId);
        bool isCustom = manifest?.IsCustom ?? ModuleTaxonomy.IsCustom(moduleId);
        bool isChanged = !isInactiveInstalled
            && currentIndex.HasValue
            && suggestedIndex.HasValue
            && currentIndex.Value != suggestedIndex.Value;

        return new PlayerLoadOrderProjectionRow
        {
            ModuleId = moduleId,
            CurrentIndex = currentIndex,
            SuggestedIndex = suggestedIndex,
            IsChanged = isChanged,
            IsInactiveInstalled = isInactiveInstalled,
            IsOfficial = isOfficial,
            IsFramework = isFramework,
            IsCustom = isCustom,
            PrimaryReasonKind = rationale.PrimaryKind,
            ReasonKinds = rationale.Kinds,
            ReasonSummary = rationale.Summary,
        };
    }
}
