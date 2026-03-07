using System.Xml.Linq;

namespace BannerlordModCompat.Core;

public sealed class LauncherDataService
{
    private static readonly Dictionary<string, int> FallbackTier = BuildFallbackTier();

    public IReadOnlyList<string> ReadCurrentOrder(
        string? launcherDataPath,
        IReadOnlyList<ModuleManifest> modules,
        List<string> warnings
    )
    {
        if (string.IsNullOrWhiteSpace(launcherDataPath) || !File.Exists(launcherDataPath))
        {
            return BuildFallbackOrder(modules);
        }

        try
        {
            XDocument doc = XDocument.Load(launcherDataPath, LoadOptions.None);
            List<string> selected = doc
                .Descendants()
                .Where(e => e.Name.LocalName.Equals("UserModData", StringComparison.OrdinalIgnoreCase))
                .Select(x => new
                {
                    Id = ChildValue(x, "Id"),
                    IsSelected = ChildValue(x, "IsSelected"),
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .Where(x => !bool.TryParse(x.IsSelected, out bool selectedFlag) || selectedFlag)
                .Select(x => x.Id!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (selected.Count == 0)
            {
                warnings.Add("LauncherData.xml is present but no module order entries were found.");
                return BuildFallbackOrder(modules);
            }

            List<string> installedIds = modules.Select(m => m.Id).ToList();
            List<string> matched = selected
                .Where(id => installedIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                .ToList();

            return matched;
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to read LauncherData.xml: {ex.Message}");
            return BuildFallbackOrder(modules);
        }
    }

    public bool TryApplySuggestedOrder(
        string? launcherDataPath,
        IReadOnlyList<string> suggestedOrder,
        List<string> warnings
    )
    {
        if (string.IsNullOrWhiteSpace(launcherDataPath) || !File.Exists(launcherDataPath))
        {
            warnings.Add("Load order auto-apply skipped: LauncherData.xml not found.");
            return false;
        }

        try
        {
            XDocument doc = XDocument.Load(launcherDataPath, LoadOptions.PreserveWhitespace);
            XElement? targetContainer = doc
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName.Equals("ModDatas", StringComparison.OrdinalIgnoreCase)
                    && e.Elements().Any(c => c.Name.LocalName.Equals("UserModData", StringComparison.OrdinalIgnoreCase)));

            if (targetContainer is null)
            {
                warnings.Add("Load order auto-apply skipped: no module container found in LauncherData.xml.");
                return false;
            }

            List<XElement> moduleNodes = targetContainer.Elements()
                .Where(e => e.Name.LocalName.Equals("UserModData", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(ChildValue(e, "Id")))
                .ToList();

            if (moduleNodes.Count == 0)
            {
                warnings.Add("Load order auto-apply skipped: no module nodes found in LauncherData.xml.");
                return false;
            }

            Dictionary<string, XElement> map = moduleNodes
                .Select(node => new { Id = ChildValue(node, "Id"), Node = node })
                .Where(x => x.Id is not null)
                .GroupBy(x => x.Id!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Node, StringComparer.OrdinalIgnoreCase);

            Queue<XElement> orderedSuggestedNodes = new(
                suggestedOrder
                    .Where(id => map.ContainsKey(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(id => new XElement(map[id])));
            HashSet<string> suggestedIds = suggestedOrder
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<XElement> reordered = [];
            foreach (XElement node in moduleNodes)
            {
                string? id = ChildValue(node, "Id");
                if (id is not null
                    && suggestedIds.Contains(id)
                    && orderedSuggestedNodes.Count > 0)
                {
                    reordered.Add(orderedSuggestedNodes.Dequeue());
                    continue;
                }

                reordered.Add(new XElement(node));
            }

            while (orderedSuggestedNodes.Count > 0)
            {
                reordered.Add(orderedSuggestedNodes.Dequeue());
            }

            targetContainer.ReplaceNodes(reordered);

            string backupPath = BuildUniqueBackupPath(launcherDataPath);
            File.Copy(launcherDataPath, backupPath, overwrite: false);
            doc.Save(launcherDataPath);
            warnings.Add($"Load order applied successfully. Backup created at: {backupPath}");
            return true;
        }
        catch (Exception ex)
        {
            warnings.Add($"Load order auto-apply failed: {ex.Message}");
            return false;
        }
    }

    private static IReadOnlyList<string> BuildFallbackOrder(IReadOnlyList<ModuleManifest> modules)
    {
        return modules
            .OrderBy(m => FallbackTier.GetValueOrDefault(m.Id, m.IsOfficial ? 1_000 : m.IsFramework ? 1_500 : 2_000))
            .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(m => m.Id)
            .ToList();
    }

    private static Dictionary<string, int> BuildFallbackTier()
    {
        string[][] groups =
        [
            ["Bannerlord.Harmony", "Harmony", "HarmonyLib"],
            ["Bannerlord.ButterLib", "ButterLib"],
            ["Bannerlord.UIExtenderEx", "UIExtenderEx"],
            ["Bannerlord.MBOptionScreen", "MBOptionScreen"],
            ["ModConfigurationMenu", "MCM", "MCMv5", "MCMv4"],
            ["Bannerlord.BLSE", "BLSE", "BUTRLoader"],
            ["Native"],
            ["SandBoxCore", "SandboxCore"],
            ["BirthAndDeath"],
            ["CustomBattle"],
            ["Sandbox"],
            ["StoryMode"],
            ["Multiplayer"],
            ["NavalDLC"],
            ["FastMode"],
        ];

        Dictionary<string, int> result = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < groups.Length; i++)
        {
            foreach (string alias in groups[i])
            {
                result[alias] = i;
            }
        }

        return result;
    }

    private static string? ChildValue(XElement node, string childName)
    {
        XElement? child = node.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals(childName, StringComparison.OrdinalIgnoreCase));
        if (child is null)
        {
            return null;
        }

        string value = child.Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string BuildUniqueBackupPath(string launcherDataPath)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
            string suffix = attempt == 0
                ? string.Empty
                : $".{attempt}";
            string candidate = $"{launcherDataPath}.bak.{stamp}{suffix}";
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            Thread.Sleep(2);
        }

        return $"{launcherDataPath}.bak.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}";
    }
}
