using System.Text.RegularExpressions;

namespace BannerlordModCompat.Core;

public static class PathDiscovery
{
    private const string BannerlordAppFolder = "Mount & Blade II Bannerlord";
    private const string WorkshopAppId = "261550";

    public static DiscoveredPaths Discover(ScanOptions options)
    {
        HashSet<string> moduleRoots = new(StringComparer.OrdinalIgnoreCase);
        List<string> warnings = [];
        bool hasExplicitModuleRoots = options.ModuleRoots.Count > 0;
        bool hasExplicitWorkshopRoot = !string.IsNullOrWhiteSpace(options.WorkshopRoot);

        foreach (string root in options.ModuleRoots)
        {
            if (Directory.Exists(root))
            {
                moduleRoots.Add(Normalize(root));
            }
            else
            {
                warnings.Add($"Module root not found: {root}");
            }
        }

        string? workshopRoot = hasExplicitWorkshopRoot
            ? ResolveWorkshopRoot(options.WorkshopRoot)
            : null;
        if (hasExplicitWorkshopRoot && workshopRoot is null)
        {
            warnings.Add($"Workshop root not found: {options.WorkshopRoot}");
        }
        if (!string.IsNullOrWhiteSpace(workshopRoot))
        {
            workshopRoot = Normalize(workshopRoot);
        }

        if (!hasExplicitModuleRoots)
        {
            foreach (string steamLibrary in DiscoverSteamLibraries(warnings))
            {
                string gameModules = Path.Combine(steamLibrary, "steamapps", "common", BannerlordAppFolder, "Modules");
                if (Directory.Exists(gameModules))
                {
                    moduleRoots.Add(Normalize(gameModules));
                }

                string workshopPath = Path.Combine(steamLibrary, "steamapps", "workshop", "content", WorkshopAppId);
                if (workshopRoot is null && Directory.Exists(workshopPath))
                {
                    workshopRoot = Normalize(workshopPath);
                }
            }

            string? userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                string docsModules = Path.Combine(userProfile, "Documents", "Mount and Blade II Bannerlord", "Modules");
                if (Directory.Exists(docsModules))
                {
                    moduleRoots.Add(Normalize(docsModules));
                }
            }
        }

        string? launcherDataPath = ResolveLauncherDataPath(options.LauncherDataPath);
        if (launcherDataPath is null)
        {
            warnings.Add("LauncherData.xml was not found. Current load order checks will be limited.");
        }

        string? saveRoot = ResolveSaveRoot(options.SaveRoot);
        if (options.IncludeSaveFileAnalysis && saveRoot is null)
        {
            warnings.Add("Save root was not found. Save-level compatibility checks were skipped.");
        }

        if (moduleRoots.Count == 0 && workshopRoot is null)
        {
            warnings.Add("No module roots were discovered. Provide --module-root and/or --workshop-root manually.");
        }

        IReadOnlyList<string> harmonyLogRoots = DiscoverHarmonyLogRoots(
            options.HarmonyLogsRoot,
            moduleRoots,
            workshopRoot,
            warnings
        );

        return new DiscoveredPaths(
            moduleRoots.ToList(),
            workshopRoot,
            launcherDataPath,
            saveRoot,
            warnings,
            harmonyLogRoots
        );
    }

    private static string? ResolveWorkshopRoot(string? explicitWorkshopRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitWorkshopRoot))
        {
            return Directory.Exists(explicitWorkshopRoot) ? explicitWorkshopRoot : null;
        }

        string? pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string? pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        string[] candidates =
        [
            Path.Combine(pf86, "Steam", "steamapps", "workshop", "content", WorkshopAppId),
            Path.Combine(pf, "Steam", "steamapps", "workshop", "content", WorkshopAppId),
        ];

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static string? ResolveLauncherDataPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return File.Exists(explicitPath) ? Normalize(explicitPath) : null;
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return null;
        }

        string inferred = Path.Combine(
            userProfile,
            "Documents",
            "Mount and Blade II Bannerlord",
            "Configs",
            "LauncherData.xml"
        );

        return File.Exists(inferred) ? Normalize(inferred) : null;
    }

    private static string? ResolveSaveRoot(string? explicitSaveRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitSaveRoot))
        {
            return Directory.Exists(explicitSaveRoot) ? Normalize(explicitSaveRoot) : null;
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return null;
        }

        string inferred = Path.Combine(userProfile, "Documents", "Mount and Blade II Bannerlord", "Game Saves");
        return Directory.Exists(inferred) ? Normalize(inferred) : null;
    }

    private static IEnumerable<string> DiscoverSteamLibraries(List<string> warnings)
    {
        List<string> roots = [];
        string? pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string? pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        string[] steamRoots =
        [
            Path.Combine(pf86, "Steam"),
            Path.Combine(pf, "Steam"),
        ];

        foreach (string root in steamRoots.Where(Directory.Exists))
        {
            roots.Add(root);

            string libraryFoldersPath = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFoldersPath))
            {
                continue;
            }

            try
            {
                string text = File.ReadAllText(libraryFoldersPath);
                MatchCollection matches = Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                {
                    if (!match.Success || match.Groups.Count < 2)
                    {
                        continue;
                    }

                    string decoded = match.Groups[1].Value.Replace(@"\\", @"\").Trim();
                    if (!string.IsNullOrWhiteSpace(decoded) && Directory.Exists(decoded))
                    {
                        roots.Add(decoded);
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to parse Steam library folders '{libraryFoldersPath}': {ex.Message}");
            }
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).Select(Normalize);
    }

    private static string Normalize(string path) => Path.GetFullPath(path.Trim());

    private static IReadOnlyList<string> DiscoverHarmonyLogRoots(
        string? explicitRoot,
        IReadOnlyCollection<string> moduleRoots,
        string? workshopRoot,
        List<string> warnings
    )
    {
        HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            if (Directory.Exists(explicitRoot))
            {
                roots.Add(Normalize(explicitRoot));
            }
            else
            {
                warnings.Add($"Harmony logs root not found: {explicitRoot}");
            }
        }

        foreach (string moduleRoot in moduleRoots)
        {
            string scannerLogs = Path.Combine(moduleRoot, "HarmonyPatchScanner", "logs");
            if (Directory.Exists(scannerLogs))
            {
                roots.Add(Normalize(scannerLogs));
            }
        }

        if (!string.IsNullOrWhiteSpace(workshopRoot) && Directory.Exists(workshopRoot))
        {
            IEnumerable<string> workshopDirs = [];
            try
            {
                workshopDirs = Directory.EnumerateDirectories(workshopRoot).ToArray();
            }
            catch (Exception ex)
            {
                warnings.Add($"Workshop directory enumeration failed at '{workshopRoot}': {ex.Message}");
            }

            foreach (string workshopModDir in workshopDirs)
            {
                try
                {
                    string logsAtRoot = Path.Combine(workshopModDir, "logs");
                    if (Directory.Exists(logsAtRoot))
                    {
                        roots.Add(Normalize(logsAtRoot));
                    }

                    string nestedScannerLogs = Path.Combine(workshopModDir, "HarmonyPatchScanner", "logs");
                    if (Directory.Exists(nestedScannerLogs))
                    {
                        roots.Add(Normalize(nestedScannerLogs));
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Harmony logs discovery warning in '{workshopModDir}': {ex.Message}");
                }
            }
        }

        return roots.ToList();
    }
}
