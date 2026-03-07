using BannerlordModCompat.Core;

CliArguments parsed = CliArguments.Parse(args);
if (!string.IsNullOrWhiteSpace(parsed.ErrorMessage))
{
    Console.Error.WriteLine($"[arg error] {parsed.ErrorMessage}");
    CliPrinter.PrintHelp();
    Environment.ExitCode = 64;
    return;
}

if (parsed.ShowHelp)
{
    CliPrinter.PrintHelp();
    return;
}

CompatibilityAnalyzer analyzer = new();
ScanReport report = analyzer.Analyze(parsed.Options);

CliPrinter.PrintReport(report);

if (!string.IsNullOrWhiteSpace(parsed.JsonOutputPath))
{
    try
    {
        ReportExporter.ExportJson(report, parsed.JsonOutputPath);
        Console.WriteLine($"[export] JSON report: {parsed.JsonOutputPath}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[export warning] JSON export failed: {ex.Message}");
    }
}

if (!string.IsNullOrWhiteSpace(parsed.MarkdownOutputPath))
{
    try
    {
        ReportExporter.ExportMarkdown(report, parsed.MarkdownOutputPath);
        Console.WriteLine($"[export] Markdown report: {parsed.MarkdownOutputPath}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[export warning] Markdown export failed: {ex.Message}");
    }
}

Environment.ExitCode = report.OverallState switch
{
    CompatibilityState.Incompatible => 2,
    CompatibilityState.LikelyIssue => 1,
    _ => 0,
};

internal sealed record CliArguments(
    ScanOptions Options,
    bool ShowHelp,
    string? JsonOutputPath,
    string? MarkdownOutputPath,
    string? ErrorMessage
)
{
    public static CliArguments Parse(string[] args)
    {
        static bool IsMissingValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal);
        }

        static CliArguments ParseError(string message)
        {
            return new CliArguments(new ScanOptions(), true, null, null, message);
        }

        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            return new CliArguments(new ScanOptions(), true, null, null, null);
        }

        List<string> moduleRoots = [];
        List<string> pins = [];
        string? workshopRoot = null;
        string? launcherPath = null;
        string? saveRoot = null;
        string? harmonyLogsRoot = null;
        string? gameVersion = null;
        string? jsonOutput = null;
        string? markdownOutput = null;
        bool includeSave = true;
        bool offline = true;
        bool allowCloud = false;
        bool autoApply = false;
        bool includeLikelyIssues = true;
        bool includeDataNoiseFindings = false;
        bool customOnlyFocus = false;

        for (int i = 0; i < args.Length; i++)
        {
            string current = args[i];
            string? next = i + 1 < args.Length ? args[i + 1] : null;

            switch (current)
            {
                case "--module-root":
                    if (IsMissingValue(next))
                    {
                        return ParseError("--module-root requires a <path> value.");
                    }
                    moduleRoots.Add(next!);
                    i++;
                    break;
                case "--workshop-root":
                    if (IsMissingValue(next))
                    {
                        return ParseError("--workshop-root requires a <path> value.");
                    }
                    workshopRoot = next;
                    i++;
                    break;
                case "--launcher-data":
                    if (IsMissingValue(next))
                    {
                        return ParseError("--launcher-data requires a <path> value.");
                    }
                    launcherPath = next;
                    i++;
                    break;
                case "--save-root":
                    if (IsMissingValue(next))
                    {
                        return ParseError("--save-root requires a <path> value.");
                    }
                    saveRoot = next;
                    i++;
                    break;
                case "--harmony-logs-root":
                    if (IsMissingValue(next))
                    {
                        return ParseError("--harmony-logs-root requires a <path> value.");
                    }
                    harmonyLogsRoot = next;
                    i++;
                    break;
                case "--game-version":
                    if (IsMissingValue(next))
                    {
                        return ParseError("--game-version requires a <value>.");
                    }
                    gameVersion = next;
                    i++;
                    break;
                case "--pin":
                    if (IsMissingValue(next))
                    {
                        return ParseError("--pin requires a <modId> value.");
                    }
                    pins.Add(next!);
                    i++;
                    break;
                case "--pins":
                    if (IsMissingValue(next))
                    {
                        return ParseError("--pins requires a comma-separated value list.");
                    }
                    pins.AddRange(next!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    i++;
                    break;
                case "--output-json":
                    if (IsMissingValue(next))
                    {
                        return ParseError("--output-json requires a <path> value.");
                    }
                    jsonOutput = next;
                    i++;
                    break;
                case "--output-md":
                    if (IsMissingValue(next))
                    {
                        return ParseError("--output-md requires a <path> value.");
                    }
                    markdownOutput = next;
                    i++;
                    break;
                case "--no-save-scan":
                    includeSave = false;
                    break;
                case "--no-likely-issues":
                    includeLikelyIssues = false;
                    break;
                case "--include-likely-issues":
                    includeLikelyIssues = true;
                    break;
                case "--include-data-noise-findings":
                    includeDataNoiseFindings = true;
                    break;
                case "--no-data-noise-findings":
                    includeDataNoiseFindings = false;
                    break;
                case "--cloud":
                    allowCloud = true;
                    offline = false;
                    break;
                case "--offline":
                    offline = true;
                    allowCloud = false;
                    break;
                case "--auto-apply":
                    autoApply = true;
                    break;
                case "--include-core-modules":
                    customOnlyFocus = false;
                    break;
                case "--custom-only-focus":
                case "--custom-only":
                    customOnlyFocus = true;
                    break;
                default:
                    if (current.StartsWith("-", StringComparison.Ordinal))
                    {
                        return ParseError($"Unknown option '{current}'.");
                    }
                    break;
            }
        }

        ScanOptions options = new()
        {
            GameVersion = gameVersion ?? "1.3.15",
            ModuleRoots = moduleRoots,
            WorkshopRoot = workshopRoot,
            LauncherDataPath = launcherPath,
            SaveRoot = saveRoot,
            HarmonyLogsRoot = harmonyLogsRoot,
            IncludeSaveFileAnalysis = includeSave,
            IncludeLikelyIssues = includeLikelyIssues,
            IncludeDataNoiseFindings = includeDataNoiseFindings,
            OfflineMode = offline,
            AllowCloudMetadata = allowCloud,
            AutoApplyLoadOrder = autoApply,
            CustomModsOnlyFocus = customOnlyFocus,
            PinnedMods = pins,
        };

        return new CliArguments(options, false, jsonOutput, markdownOutput, null);
    }
}

internal static class CliPrinter
{
    public static void PrintHelp()
    {
        Console.WriteLine("BannerlordModCompat CLI");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  BannerlordModCompat.Cli [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --module-root <path>     Add a module root (repeatable).");
        Console.WriteLine("  --workshop-root <path>   Set Steam workshop path for app 261550.");
        Console.WriteLine("  --launcher-data <path>   Path to LauncherData.xml.");
        Console.WriteLine("  --save-root <path>       Path to Bannerlord saves.");
        Console.WriteLine("  --harmony-logs-root <p>  Path to Harmony Patch Scanner logs directory.");
        Console.WriteLine("  --game-version <value>   Target game version (default: 1.3.15).");
        Console.WriteLine("  --pin <modId>            Pin a module to current index (repeatable).");
        Console.WriteLine("  --pins <a,b,c>           Comma-separated pinned module IDs.");
        Console.WriteLine("  --output-json <path>     Export full report as JSON.");
        Console.WriteLine("  --output-md <path>       Export report summary as Markdown.");
        Console.WriteLine("  --auto-apply             Apply suggested order to LauncherData.xml.");
        Console.WriteLine("  --include-core-modules   Include Native/framework module findings (default).");
        Console.WriteLine("  --custom-only-focus      Hide Native/framework findings and show custom mods only.");
        Console.WriteLine("  --no-save-scan           Skip save file analysis.");
        Console.WriteLine("  --no-likely-issues       Keep only deterministic findings in output.");
        Console.WriteLine("  --include-likely-issues  Include probabilistic findings (default).");
        Console.WriteLine("  --no-data-noise-findings Skip dependency-version, DLL, and XML/ModuleData overlap findings (default).");
        Console.WriteLine("  --include-data-noise-findings  Include dependency-version, DLL, and XML/ModuleData overlap findings.");
        Console.WriteLine("  --cloud                  Enable cloud metadata mode.");
        Console.WriteLine("  --offline                Force offline mode (default).");
        Console.WriteLine("  --help                   Show this help text.");
    }

    public static void PrintReport(ScanReport report)
    {
        Console.WriteLine("== Bannerlord Mod Compatibility Scan ==");
        Console.WriteLine($"Generated (UTC): {report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"Game Version: {report.GameVersion}");
        Console.WriteLine($"Overall State: {report.OverallState}");
        Console.WriteLine($"Scanned Modules: {report.Modules.Count}");
        Console.WriteLine($"Findings: {report.Conflicts.Count}");
        Console.WriteLine();

        PrintSection("Module Roots", report.ScannedModuleRoots.Select(x => x).ToList());
        if (!string.IsNullOrWhiteSpace(report.ScannedWorkshopRoot))
        {
            PrintSection("Workshop Root", [report.ScannedWorkshopRoot!]);
        }

        if (report.Warnings.Count > 0)
        {
            PrintSection("Warnings", report.Warnings.Take(15).ToList());
        }

        var grouped = report.Conflicts
            .GroupBy(c => c.Severity)
            .OrderByDescending(g => g.Key);

        Console.WriteLine("-- Findings by Severity --");
        foreach (var group in grouped)
        {
            Console.WriteLine($"{group.Key}: {group.Count()}");
        }
        Console.WriteLine();

        Console.WriteLine("-- Top Findings --");
        foreach (ConflictFinding finding in report.Conflicts.Take(10))
        {
            Console.WriteLine($"[{finding.Severity}] {finding.Category} | {string.Join(", ", finding.ModuleIds)}");
            Console.WriteLine($"  Reason: {finding.Reason}");
            if (!string.IsNullOrWhiteSpace(finding.Recommendation))
            {
                Console.WriteLine($"  Fix: {finding.Recommendation}");
            }
        }
        Console.WriteLine();

        Console.WriteLine("-- Suggested Load Order (Top 20) --");
        for (int i = 0; i < Math.Min(report.LoadOrder.SuggestedOrder.Count, 20); i++)
        {
            Console.WriteLine($"{i + 1,2}. {report.LoadOrder.SuggestedOrder[i]}");
        }
        Console.WriteLine();

        Console.WriteLine($"Load-order planner confidence: {report.LoadOrder.Confidence:P0}");
        if (report.LoadOrder.Rationale.Count > 0)
        {
            Console.WriteLine("-- Load-Order Basis --");
            foreach (string line in report.LoadOrder.Rationale.Take(8))
            {
                Console.WriteLine($"- {line}");
            }
            Console.WriteLine();
        }

        if (report.LoadOrder.Moves.Count > 0)
        {
            Console.WriteLine("-- Suggested Moves (Top 20) --");
            foreach (LoadOrderMove move in report.LoadOrder.Moves.Take(20))
            {
                Console.WriteLine($"  {move.ModuleId}: {move.FromIndex + 1} -> {move.ToIndex + 1}");
            }
            Console.WriteLine();
        }

        if (report.SaveFiles.Count > 0)
        {
            Console.WriteLine("-- Save File Scan --");
            Console.WriteLine($"Scanned saves: {report.SaveFiles.Count}");
            foreach (SaveFileInsight save in report.SaveFiles.Take(5))
            {
                Console.WriteLine($"  {Path.GetFileName(save.SavePath)}: refs={save.ReferencedInstalledMods.Count}");
            }
            Console.WriteLine();
        }

        if (!report.Conflicts.Any(c =>
                c.Category is ConflictCategory.DependencyVersionMismatch
                    or ConflictCategory.DllCollision
                    or ConflictCategory.XmlEntityCollision
                    or ConflictCategory.ModuleDataFileCollision))
        {
            Console.WriteLine("Note: dependency-version, DLL, and XML/ModuleData overlap findings are disabled by default.");
        }

        Console.WriteLine("Note: 'Likely issue' findings are probabilistic; verify against mod documentation and gameplay tests.");
    }

    private static void PrintSection(string title, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        Console.WriteLine($"-- {title} --");
        foreach (string line in lines)
        {
            Console.WriteLine(line);
        }
        Console.WriteLine();
    }
}
