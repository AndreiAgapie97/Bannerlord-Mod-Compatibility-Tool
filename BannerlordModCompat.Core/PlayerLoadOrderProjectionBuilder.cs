namespace BannerlordModCompat.Core;

public sealed record PlayerLoadOrderProjection
{
    public required LoadOrderRecommendation Recommendation { get; init; }
    public required IReadOnlyList<string> InactiveInstalledModuleIds { get; init; }
}

public static class PlayerLoadOrderProjectionBuilder
{
    public static PlayerLoadOrderProjection BuildEnabledSingleplayerProjection(LoadOrderRecommendation source)
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
            },
            InactiveInstalledModuleIds = inactiveInstalled,
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
}
