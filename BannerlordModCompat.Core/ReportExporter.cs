using System.Text;
using System.Text.Json;

namespace BannerlordModCompat.Core;

public static class ReportExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static void ExportJson(ScanReport report, string outputPath)
    {
        EnsureParent(outputPath);
        string json = JsonSerializer.Serialize(report, JsonOptions);
        File.WriteAllText(outputPath, json, Encoding.UTF8);
    }

    public static void ExportMarkdown(ScanReport report, string outputPath)
    {
        EnsureParent(outputPath);

        StringBuilder sb = new();
        sb.AppendLine("# Bannerlord Mod Compatibility Report");
        sb.AppendLine();
        sb.AppendLine($"- Generated (UTC): `{report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}`");
        sb.AppendLine($"- Game version target: `{report.GameVersion}`");
        sb.AppendLine($"- Overall state: **{report.OverallState}**");
        sb.AppendLine($"- Installed modules scanned: `{report.Modules.Count}`");
        sb.AppendLine($"- Conflicts found: `{report.Conflicts.Count}`");
        sb.AppendLine();

        if (report.Warnings.Count > 0)
        {
            sb.AppendLine("## Warnings");
            foreach (string warning in report.Warnings)
            {
                sb.AppendLine($"- {warning}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Suggested Load Order");
        sb.AppendLine($"- Planner confidence: `{report.LoadOrder.Confidence:P0}`");
        if (report.LoadOrder.Rationale.Count > 0)
        {
            sb.AppendLine("- Basis:");
            foreach (string rationale in report.LoadOrder.Rationale.Take(12))
            {
                sb.AppendLine($"  - {rationale}");
            }
        }
        sb.AppendLine();

        for (int i = 0; i < report.LoadOrder.SuggestedOrder.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {report.LoadOrder.SuggestedOrder[i]}");
        }
        sb.AppendLine();

        sb.AppendLine("## Conflict Findings");
        if (report.Conflicts.Count == 0)
        {
            sb.AppendLine("- No issues detected.");
        }
        else
        {
            int index = 1;
            foreach (ConflictFinding conflict in report.Conflicts)
            {
                sb.AppendLine($"### {index}. {conflict.Category} ({conflict.Severity}, confidence {conflict.Confidence:P0})");
                sb.AppendLine($"- Modules: `{string.Join("`, `", conflict.ModuleIds)}`");
                sb.AppendLine($"- Reason: {conflict.Reason}");
                if (!string.IsNullOrWhiteSpace(conflict.LikelyInGameOutcome))
                {
                    sb.AppendLine($"- Likely in-game outcome: {conflict.LikelyInGameOutcome}");
                }
                if (!string.IsNullOrWhiteSpace(conflict.Recommendation))
                {
                    sb.AppendLine($"- Recommendation: {conflict.Recommendation}");
                }
                if (conflict.Evidence.Count > 0)
                {
                    sb.AppendLine("- Evidence:");
                    foreach (string evidence in conflict.Evidence.Take(10))
                    {
                        sb.AppendLine($"  - `{evidence}`");
                    }
                }
                sb.AppendLine();
                index++;
            }
        }

        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
    }

    private static void EnsureParent(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }
}
