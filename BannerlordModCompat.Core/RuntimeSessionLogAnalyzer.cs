using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BannerlordModCompat.Core;

public sealed class RuntimeSessionLogAnalyzer
{
    private static readonly Regex SessionSuffixRegex = new(
        @"_(?<id>\d+)\.txt$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex UsedModuleRegex = new(
        @"#TW#Used Modules#TW#(?<id>[^#]+)#TW#Exists and Active",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex ModulesArgRegex = new(
        @"_MODULES_(?<mods>.+?)_MODULES_",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex SubModulePathRegex = new(
        @"subModulePath\s*=\s*(?<path>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex ModulePathRegex = new(
        @"Modules[\\/](?<id>[^\\/]+)[\\/]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex DllNameRegex = new(
        @"(?<dll>[A-Za-z0-9_.-]+\.dll)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly string[] SignatureAnchorTokens =
    [
        "missingmethodexception",
        "typeloadexception",
        "filenotfoundexception",
        "nullreferenceexception",
        "could not load file or assembly",
        "stack trace",
        "unhandled exception",
        "assert",
        "couldn't find .dll",
        "assembly load result: null",
    ];

    private static readonly string[] LauncherErrorTokens =
    [
        "ERROR:",
        "Could not load file or assembly",
        "Assembly load result: NULL",
        "Couldn't find .dll",
        "Couldn't verify dlls",
        "ASSERT!",
        "Could not",
        "Exception",
    ];

    private static readonly string[] RuntimeErrorTokens =
    [
        "MissingMethodException",
        "TypeLoadException",
        "FileNotFoundException",
        "NullReferenceException",
        "Unhandled Exception",
        "Stack trace",
        "could not",
        "error",
        "failed",
        "crash",
    ];

    private static readonly TimeSpan CrashListSessionWindow = TimeSpan.FromMinutes(10);

    public IReadOnlyList<ConflictFinding> Analyze(
        IReadOnlyList<ModuleManifest> modules,
        IReadOnlyList<string> currentOrder,
        bool customOnlyFocus,
        List<string> warnings,
        string? logsRootOverride = null,
        string? gameVersion = null
    )
    {
        string? logsRoot = ResolveLogsRoot(logsRootOverride);
        if (string.IsNullOrWhiteSpace(logsRoot) || !Directory.Exists(logsRoot))
        {
            return [];
        }

        List<SessionFiles> recentSessions = SelectRecentSessionFiles(logsRoot, warnings, maxSessions: 6);
        SessionFiles? session = recentSessions.FirstOrDefault();
        if (session is null)
        {
            return [];
        }

        Dictionary<string, ModuleManifest> byId = modules
            .ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        HashSet<string> runtimeModules = ParseRuntimeModules(session, byId, warnings);
        List<RuntimeIssueSignature> runtimeIssues = ParseRuntimeIssueSignatures(
            session,
            runtimeModules,
            modules,
            customOnlyFocus,
            byId,
            warnings);

        List<ConflictFinding> findings = [];
        ConflictFinding? mismatchFinding = BuildRuntimeModuleMismatchFinding(
            runtimeModules,
            currentOrder,
            customOnlyFocus,
            byId,
            session
        );
        if (mismatchFinding is not null)
        {
            findings.Add(mismatchFinding);
        }

        ConflictFinding? loaderFinding = BuildRuntimeLoaderFailureFinding(
            runtimeIssues,
            runtimeModules,
            customOnlyFocus,
            byId,
            session,
            gameVersion
        );
        if (loaderFinding is not null)
        {
            findings.Add(loaderFinding);
        }

        ConflictFinding? recurringCluster = BuildRecurringIncidentClusterFinding(
            recentSessions,
            modules,
            currentOrder,
            customOnlyFocus,
            byId,
            warnings,
            gameVersion
        );
        if (recurringCluster is not null)
        {
            findings.Add(recurringCluster);
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.Confidence)
            .Take(80)
            .ToList();
    }

    private static string? ResolveLogsRoot(string? logsRootOverride)
    {
        if (!string.IsNullOrWhiteSpace(logsRootOverride) && Directory.Exists(logsRootOverride))
        {
            return Path.GetFullPath(logsRootOverride);
        }

        string commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonAppData))
        {
            return null;
        }

        string inferred = Path.Combine(commonAppData, "Mount and Blade II Bannerlord", "logs");
        return Directory.Exists(inferred) ? Path.GetFullPath(inferred) : null;
    }

    private static SessionFiles? SelectLatestSessionFiles(string logsRoot, List<string> warnings)
    {
        return SelectRecentSessionFiles(logsRoot, warnings, maxSessions: 1).FirstOrDefault();
    }

    private static List<SessionFiles> SelectRecentSessionFiles(string logsRoot, List<string> warnings, int maxSessions)
    {
        maxSessions = Math.Clamp(maxSessions, 1, 20);
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(logsRoot, "*.txt", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex)
        {
            warnings.Add($"Runtime logs enumeration failed in '{logsRoot}': {ex.Message}");
            return [];
        }

        Dictionary<string, SessionBucket> buckets = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in files)
        {
            string fileName = Path.GetFileName(path);
            LogFileKind? kind = Classify(fileName);
            string? sessionId = kind is null ? null : TryExtractSessionId(fileName);
            if (kind is null || string.IsNullOrWhiteSpace(sessionId))
            {
                continue;
            }

            if (!buckets.TryGetValue(sessionId, out SessionBucket? bucket))
            {
                bucket = new SessionBucket(sessionId);
                buckets[sessionId] = bucket;
            }

            bucket.Assign(kind.Value, path);
        }

        string? crashListPath = files.FirstOrDefault(p =>
            Path.GetFileName(p).Equals("crashlist.txt", StringComparison.OrdinalIgnoreCase));

        if (buckets.Count > 0)
        {
            return buckets.Values
                .OrderByDescending(x => x.LastWriteUtc)
                .ThenByDescending(x => x.SessionId, StringComparer.OrdinalIgnoreCase)
                .Take(maxSessions)
                .Select(b => b.ToSessionFiles(GetRelevantCrashListPath(crashListPath, b.LastWriteUtc)))
                .ToList();
        }

        string? launcher = FindLatestByPrefix(files, "launcher_log_");
        string? watchdog = FindLatestByPrefix(files, "watchdog_log_");
        string? rglErrors = FindLatestByPrefix(files, "rgl_log_errors_");
        string? rgl = FindLatestByPrefix(files, "rgl_log_");
        if (launcher is null && watchdog is null && rglErrors is null && rgl is null)
        {
            return [];
        }

        DateTime lastWriteUtc = new[]
            {
                GetLastWriteUtcSafe(launcher),
                GetLastWriteUtcSafe(watchdog),
                GetLastWriteUtcSafe(rglErrors),
                GetLastWriteUtcSafe(rgl)
            }
            .Max();
        return
        [
            new SessionFiles(
            SessionId: null,
            LauncherLogPath: launcher,
            WatchdogLogPath: watchdog,
            RglErrorLogPath: rglErrors,
            RglLogPath: rgl,
            CrashListPath: GetRelevantCrashListPath(crashListPath, lastWriteUtc),
            LastWriteUtc: lastWriteUtc
            )
        ];
    }

    private static string? GetRelevantCrashListPath(string? crashListPath, DateTime sessionLastWriteUtc)
    {
        if (string.IsNullOrWhiteSpace(crashListPath)
            || sessionLastWriteUtc == DateTime.MinValue
            || !File.Exists(crashListPath))
        {
            return null;
        }

        DateTime crashListWriteUtc = GetLastWriteUtcSafe(crashListPath);
        if (crashListWriteUtc == DateTime.MinValue)
        {
            return null;
        }

        TimeSpan delta = crashListWriteUtc >= sessionLastWriteUtc
            ? crashListWriteUtc - sessionLastWriteUtc
            : sessionLastWriteUtc - crashListWriteUtc;

        return delta <= CrashListSessionWindow
            ? crashListPath
            : null;
    }

    private static LogFileKind? Classify(string fileName)
    {
        if (fileName.StartsWith("launcher_log_", StringComparison.OrdinalIgnoreCase))
        {
            return LogFileKind.Launcher;
        }

        if (fileName.StartsWith("watchdog_log_", StringComparison.OrdinalIgnoreCase))
        {
            return LogFileKind.Watchdog;
        }

        if (fileName.StartsWith("rgl_log_errors_", StringComparison.OrdinalIgnoreCase))
        {
            return LogFileKind.RglErrors;
        }

        if (fileName.StartsWith("rgl_log_", StringComparison.OrdinalIgnoreCase))
        {
            return LogFileKind.Rgl;
        }

        return null;
    }

    private static string? TryExtractSessionId(string fileName)
    {
        Match match = SessionSuffixRegex.Match(fileName);
        if (!match.Success)
        {
            return null;
        }

        string value = match.Groups["id"].Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? FindLatestByPrefix(IReadOnlyList<string> files, string prefix)
    {
        return files
            .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(GetLastWriteUtcSafe)
            .FirstOrDefault();
    }

    private static DateTime GetLastWriteUtcSafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return DateTime.MinValue;
        }

        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static HashSet<string> ParseRuntimeModules(
        SessionFiles session,
        IReadOnlyDictionary<string, ModuleManifest> byId,
        List<string> warnings
    )
    {
        HashSet<string> modules = new(StringComparer.OrdinalIgnoreCase);

        foreach (string line in ReadLinesSafe(session.WatchdogLogPath, maxLines: 9_000, warnings))
        {
            Match usedModule = UsedModuleRegex.Match(line);
            if (usedModule.Success)
            {
                AddModuleCandidate(modules, usedModule.Groups["id"].Value, byId);
            }

            foreach (string moduleId in ParseModulesFromArgs(line))
            {
                AddModuleCandidate(modules, moduleId, byId);
            }
        }

        foreach (string line in ReadLinesSafe(session.LauncherLogPath, maxLines: 8_000, warnings))
        {
            Match subModulePath = SubModulePathRegex.Match(line);
            if (subModulePath.Success)
            {
                string? moduleId = TryExtractModuleIdFromPath(subModulePath.Groups["path"].Value);
                if (!string.IsNullOrWhiteSpace(moduleId))
                {
                    AddModuleCandidate(modules, moduleId, byId);
                }
            }

            foreach (string moduleId in ParseModulesFromArgs(line))
            {
                AddModuleCandidate(modules, moduleId, byId);
            }
        }

        foreach (string line in ReadLinesSafe(session.RglLogPath, maxLines: 8_000, warnings))
        {
            foreach (string moduleId in ParseModulesFromArgs(line))
            {
                AddModuleCandidate(modules, moduleId, byId);
            }
        }

        return modules;
    }

    private static void AddModuleCandidate(
        ISet<string> destination,
        string? candidate,
        IReadOnlyDictionary<string, ModuleManifest> byId
    )
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        string normalized = candidate.Trim();
        if (byId.TryGetValue(normalized, out ModuleManifest? module))
        {
            destination.Add(module.Id);
            return;
        }

        if (normalized.Length >= 3 && normalized.Any(char.IsLetter))
        {
            destination.Add(normalized);
        }
    }

    private static IEnumerable<string> ParseModulesFromArgs(string line)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ModulesArgRegex.Matches(line))
        {
            string payload = match.Groups["mods"].Value;
            foreach (string rawToken in payload.Split('*', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.IsNullOrWhiteSpace(rawToken)
                    || rawToken.StartsWith("/", StringComparison.Ordinal)
                    || rawToken.Contains(' '))
                {
                    continue;
                }

                result.Add(rawToken.Trim());
            }
        }

        return result;
    }

    private static string? TryExtractModuleIdFromPath(string pathText)
    {
        if (string.IsNullOrWhiteSpace(pathText))
        {
            return null;
        }

        string normalized = pathText.Trim().Trim('"').Replace('/', '\\');
        int markerIndex = normalized.IndexOf("\\Modules\\", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        string remainder = normalized[(markerIndex + "\\Modules\\".Length)..];
        int nextSlash = remainder.IndexOf('\\');
        if (nextSlash <= 0)
        {
            return null;
        }

        string moduleId = remainder[..nextSlash].Trim();
        return string.IsNullOrWhiteSpace(moduleId) ? null : moduleId;
    }

    private static ConflictFinding? BuildRuntimeModuleMismatchFinding(
        IReadOnlySet<string> runtimeModules,
        IReadOnlyList<string> currentOrder,
        bool customOnlyFocus,
        IReadOnlyDictionary<string, ModuleManifest> byId,
        SessionFiles session
    )
    {
        HashSet<string> runtimeSet = runtimeModules
            .Where(id => !customOnlyFocus || (byId.TryGetValue(id, out ModuleManifest? mod) && mod.IsCustom))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> currentSet = currentOrder
            .Where(id => !customOnlyFocus || (byId.TryGetValue(id, out ModuleManifest? mod) && mod.IsCustom))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (runtimeSet.Count == 0)
        {
            return null;
        }

        List<string> extraInRuntime = runtimeSet
            .Except(currentSet, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<string> missingInRuntime = currentSet
            .Except(runtimeSet, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (extraInRuntime.Count == 0 && missingInRuntime.Count == 0)
        {
            return null;
        }

        List<string> modules = [.. extraInRuntime, .. missingInRuntime];
        List<string> evidence =
        [
            .. PathEvidence(session),
            $"runtime-only: {string.Join(", ", extraInRuntime.Take(8))}",
            $"order-only: {string.Join(", ", missingInRuntime.Take(8))}",
        ];

        return new ConflictFinding
        {
            Category = ConflictCategory.RuntimeModuleSetMismatch,
            Severity = extraInRuntime.Count > 0 ? ConflictSeverity.High : ConflictSeverity.Medium,
            Confidence = 0.95,
            ModuleIds = modules.Take(24).ToList(),
            Reason = $"Latest runtime session module set differs from current launcher order ({extraInRuntime.Count} runtime-only, {missingInRuntime.Count} order-only).",
            LikelyInGameOutcome = "Analyzer results may not match real gameplay profile if runtime and launcher module sets are out of sync.",
            Recommendation = "Sync LauncherData with the actual runtime module set, then rerun compatibility and load-order validation.",
            Evidence = evidence.Where(x => !string.IsNullOrWhiteSpace(x)).Take(12).ToList(),
            StructuredEvidence = EvidenceProfiles.Create(
                FindingEvidenceScope.Session,
                [FindingEvidenceSource.RuntimeLog, FindingEvidenceSource.LauncherOrder],
                [FindingEvidenceKind.RuntimeModuleDrift],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["runtime-only-count"] = extraInRuntime.Count.ToString(CultureInfo.InvariantCulture),
                    ["order-only-count"] = missingInRuntime.Count.ToString(CultureInfo.InvariantCulture),
                }),
        };
    }

    private static ConflictFinding? BuildRuntimeLoaderFailureFinding(
        IReadOnlyList<RuntimeIssueSignature> runtimeIssues,
        IReadOnlySet<string> runtimeModules,
        bool customOnlyFocus,
        IReadOnlyDictionary<string, ModuleManifest> byId,
        SessionFiles session,
        string? gameVersion
    )
    {
        if (runtimeIssues.Count == 0)
        {
            return null;
        }

        RuntimeIssueSignature strongest = runtimeIssues
            .OrderByDescending(issue => issue.Criticality)
            .ThenByDescending(issue => issue.ModuleIds.Count)
            .ThenBy(issue => issue.Kind)
            .First();

        if (runtimeIssues.All(issue => issue.Kind == RuntimeIssueKind.GenericRuntimeError)
            && strongest.ModuleIds.Count == 0)
        {
            return null;
        }

        List<string> moduleIds = runtimeIssues
            .SelectMany(issue => issue.ModuleIds)
            .Where(id => !customOnlyFocus || (byId.TryGetValue(id, out ModuleManifest? mod) && mod.IsCustom))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList();
        if (moduleIds.Count == 0)
        {
            moduleIds = runtimeModules
                .Where(id => !customOnlyFocus || (byId.TryGetValue(id, out ModuleManifest? mod) && mod.IsCustom))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToList();
        }

        ConflictSeverity severity = runtimeIssues.Any(issue => issue.Criticality == RuntimeIssueCriticality.Critical)
            ? ConflictSeverity.Critical
            : runtimeIssues.Any(issue => issue.Criticality == RuntimeIssueCriticality.High)
                ? ConflictSeverity.High
                : ConflictSeverity.Medium;
        double confidence = ComputeRuntimeLoaderConfidence(runtimeIssues);
        string reason = runtimeIssues.Count == 1
            ? $"Latest runtime logs show a concrete {ToDisplayLabel(strongest.Kind)} issue."
            : $"Latest runtime logs show {runtimeIssues.Count} concrete loader/runtime issue signature(s). Strongest signal: {strongest.Summary}.";
        string likelyOutcome = BuildRuntimeIssueOutcome(strongest.Kind);
        string recommendation = BuildRuntimeIssueRecommendation(strongest.Kind);
        string phaseToken = ToToken(strongest.Phase);
        string fingerprint = BuildModlistSessionFingerprint(moduleIds, gameVersion);
        List<string> evidence = PathEvidence(session).ToList();
        evidence.AddRange(runtimeIssues
            .Take(6)
            .Select(issue => $"issue: {issue.Summary} [{issue.PrimarySource}]"));

        return new ConflictFinding
        {
            Category = ConflictCategory.RuntimeLoaderFailure,
            Severity = severity,
            Confidence = confidence,
            ModuleIds = moduleIds,
            Reason = reason,
            LikelyInGameOutcome = likelyOutcome,
            Recommendation = recommendation,
            Evidence = evidence,
            StructuredEvidence = EvidenceProfiles.Create(
                FindingEvidenceScope.Session,
                [FindingEvidenceSource.RuntimeLog],
                [FindingEvidenceKind.RuntimeLoaderIssue],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["issue-kind"] = ToToken(strongest.Kind),
                    ["issue-key"] = strongest.Key,
                    ["issue-summary"] = strongest.Summary,
                    ["criticality"] = ToToken(strongest.Criticality),
                    ["issue-count"] = runtimeIssues.Count.ToString(CultureInfo.InvariantCulture),
                    ["primary-source"] = strongest.PrimarySource,
                    ["issue-phase"] = phaseToken,
                    ["system-area"] = strongest.SystemArea,
                    ["session-fingerprint"] = fingerprint,
                }),
        };
    }

    private static ConflictFinding? BuildRecurringIncidentClusterFinding(
        IReadOnlyList<SessionFiles> sessions,
        IReadOnlyList<ModuleManifest> modules,
        IReadOnlyList<string> currentOrder,
        bool customOnlyFocus,
        IReadOnlyDictionary<string, ModuleManifest> byId,
        List<string> warnings,
        string? gameVersion
    )
    {
        if (sessions.Count < 2)
        {
            return null;
        }

        Dictionary<string, IncidentClusterAccumulator> clusterBySignature = new(StringComparer.OrdinalIgnoreCase);
        foreach (SessionFiles session in sessions.Take(10))
        {
            HashSet<string> runtimeModules = ParseRuntimeModules(session, byId, warnings);
            List<RuntimeIssueSignature> runtimeIssues = ParseRuntimeIssueSignatures(
                session,
                runtimeModules,
                modules,
                customOnlyFocus,
                byId,
                warnings);

            if (runtimeIssues.Count == 0)
            {
                continue;
            }

            HashSet<string> sessionModules = runtimeIssues
                .SelectMany(issue => issue.ModuleIds)
                .Where(id => !customOnlyFocus || (byId.TryGetValue(id, out ModuleManifest? mod) && mod.IsCustom))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (sessionModules.Count == 0)
            {
                foreach (string moduleId in runtimeModules)
                {
                    if (!customOnlyFocus || (byId.TryGetValue(moduleId, out ModuleManifest? mod) && mod.IsCustom))
                    {
                        sessionModules.Add(moduleId);
                    }
                }
            }

            if (sessionModules.Count == 0)
            {
                foreach (string moduleId in currentOrder)
                {
                    if (!customOnlyFocus || (byId.TryGetValue(moduleId, out ModuleManifest? mod) && mod.IsCustom))
                    {
                        sessionModules.Add(moduleId);
                    }
                }
            }

            HashSet<string> signatures = runtimeIssues
                .Select(issue => issue.Key)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (signatures.Count == 0)
            {
                continue;
            }

            string sessionKey = BuildSessionKey(session);
            foreach (string signature in signatures)
            {
                if (!clusterBySignature.TryGetValue(signature, out IncidentClusterAccumulator? cluster))
                {
                    cluster = new IncidentClusterAccumulator(signature);
                    clusterBySignature[signature] = cluster;
                }

                cluster.SessionKeys.Add(sessionKey);
                cluster.HasCriticalRuntimeSignatures |= runtimeIssues.Any(issue => issue.Criticality == RuntimeIssueCriticality.Critical);
                foreach (string moduleId in sessionModules)
                {
                    cluster.ModuleCounts[moduleId] = cluster.ModuleCounts.TryGetValue(moduleId, out int count)
                        ? count + 1
                        : 1;
                }

                foreach (string path in PathEvidence(session))
                {
                    cluster.EvidencePaths.Add(path);
                }

                foreach (RuntimeIssueSignature issue in runtimeIssues.Take(5))
                {
                    cluster.SampleIssues.Add(issue.Summary);
                    string issuePhaseToken = ToToken(issue.Phase);
                    cluster.PhaseCounts[issuePhaseToken] = cluster.PhaseCounts.TryGetValue(issuePhaseToken, out int phaseCount)
                        ? phaseCount + 1
                        : 1;
                    cluster.SystemAreaCounts[issue.SystemArea] = cluster.SystemAreaCounts.TryGetValue(issue.SystemArea, out int areaCount)
                        ? areaCount + 1
                        : 1;
                }
            }
        }

        IncidentClusterAccumulator? best = clusterBySignature.Values
            .Where(c => c.SessionKeys.Count >= 2)
            .OrderByDescending(c => c.SessionKeys.Count)
            .ThenByDescending(c => c.HasCriticalRuntimeSignatures)
            .ThenByDescending(c => c.ModuleCounts.Count)
            .FirstOrDefault();
        if (best is null)
        {
            return null;
        }

        List<string> modulesFromCluster = best.ModuleCounts
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Key)
            .Take(24)
            .ToList();
        if (modulesFromCluster.Count == 0)
        {
            modulesFromCluster = currentOrder
                .Where(id => !customOnlyFocus || (byId.TryGetValue(id, out ModuleManifest? mod) && mod.IsCustom))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToList();
        }

        ConflictCategory category = ConflictCategory.RuntimeLoaderFailure;
        ConflictSeverity severity = best.HasCriticalRuntimeSignatures || best.SessionKeys.Count >= 4
            ? ConflictSeverity.Critical
            : ConflictSeverity.High;
        double confidence = Math.Clamp(
            0.74
            + (0.06 * Math.Min(4, best.SessionKeys.Count - 1))
            + (best.HasCriticalRuntimeSignatures ? 0.04 : 0.0),
            0.80,
            0.97
        );

        RuntimeIssueKind clusterKind = ClassifySignatureKind(best.Signature);
        string displaySignature = ToShortSignature(best.Signature);
        string phaseToken = best.PhaseCounts
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Key)
            .FirstOrDefault() ?? "unknown";
        string systemArea = best.SystemAreaCounts
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Key)
            .FirstOrDefault() ?? "runtime";
        string reason = $"Recurring {ToDisplayLabel(clusterKind)} signature detected: '{displaySignature}' appeared in "
            + $"{best.SessionKeys.Count}/{sessions.Count} recent session(s).";
        string likelyOutcome = $"{BuildRuntimeIssueOutcome(clusterKind)} This signature is repeating across runs, which makes the signal much stronger than a one-off log line.";
        string recommendation = $"{BuildRuntimeIssueRecommendation(clusterKind)} Keep one baseline profile and rerun the same gameplay path until the repeated signature disappears.";

        List<string> evidence = [];
        evidence.AddRange(best.EvidencePaths.Take(8));
        evidence.Add($"cluster-signature:{best.Signature}");
        evidence.Add($"cluster-session-count:{best.SessionKeys.Count}");
        evidence.Add($"cluster-sessions:{string.Join(",", best.SessionKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(8))}");
        evidence.AddRange(best.SampleIssues
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(x => $"issue: {x}"));

        return new ConflictFinding
        {
            Category = category,
            Severity = severity,
            Confidence = confidence,
            ModuleIds = modulesFromCluster,
            Reason = reason,
            LikelyInGameOutcome = likelyOutcome,
            Recommendation = recommendation,
            Evidence = evidence,
            StructuredEvidence = EvidenceProfiles.Create(
                FindingEvidenceScope.Session,
                [FindingEvidenceSource.RuntimeLog, FindingEvidenceSource.RuntimeCluster],
                [FindingEvidenceKind.RuntimeLoaderIssue],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["issue-kind"] = ToToken(clusterKind),
                    ["issue-key"] = best.Signature,
                    ["issue-summary"] = displaySignature,
                    ["criticality"] = best.HasCriticalRuntimeSignatures ? "critical" : "high",
                    ["issue-count"] = best.SampleIssues.Count.ToString(CultureInfo.InvariantCulture),
                    ["recurrence-count"] = best.SessionKeys.Count.ToString(CultureInfo.InvariantCulture),
                    ["issue-phase"] = phaseToken,
                    ["system-area"] = systemArea,
                    ["session-fingerprint"] = BuildModlistSessionFingerprint(modulesFromCluster, gameVersion),
                }),
        };
    }

    private static string BuildSessionKey(SessionFiles session)
    {
        if (!string.IsNullOrWhiteSpace(session.SessionId))
        {
            return session.SessionId;
        }

        if (session.LastWriteUtc != DateTime.MinValue)
        {
            return session.LastWriteUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }

        string? basis = session.WatchdogLogPath
            ?? session.LauncherLogPath
            ?? session.RglErrorLogPath
            ?? session.RglLogPath;
        return string.IsNullOrWhiteSpace(basis)
            ? "runtime-session-unknown"
            : Path.GetFileNameWithoutExtension(basis);
    }

    private static string ToShortSignature(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return "runtime-error";
        }

        return signature.Length <= 96
            ? signature
            : signature[..96];
    }

    private static List<RuntimeIssueSignature> ParseRuntimeIssueSignatures(
        SessionFiles session,
        IReadOnlySet<string> runtimeModules,
        IReadOnlyList<ModuleManifest> modules,
        bool customOnlyFocus,
        IReadOnlyDictionary<string, ModuleManifest> byId,
        List<string> warnings
    )
    {
        Dictionary<string, List<string>> moduleOwnersByDll = modules
            .SelectMany(m => m.Dlls.Select(d => (m.Id, d.FileName)))
            .GroupBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RuntimeIssueAccumulator> issuesByKey = new(StringComparer.OrdinalIgnoreCase);

        CollectRuntimeIssueSignatures(
            issuesByKey,
            ReadLinesSafe(session.LauncherLogPath, maxLines: 10_000, warnings),
            "launcher",
            modules,
            customOnlyFocus,
            byId,
            moduleOwnersByDll);
        CollectRuntimeIssueSignatures(
            issuesByKey,
            ReadLinesSafe(session.RglErrorLogPath, maxLines: 2_000, warnings),
            "rgl-errors",
            modules,
            customOnlyFocus,
            byId,
            moduleOwnersByDll);
        CollectRuntimeIssueSignatures(
            issuesByKey,
            ReadTailLinesSafe(session.RglLogPath, tailCount: 4_000, warnings),
            "rgl",
            modules,
            customOnlyFocus,
            byId,
            moduleOwnersByDll);

        List<RuntimeIssueSignature> signatures = issuesByKey.Values
            .Select(acc => acc.Build())
            .OrderByDescending(issue => issue.Criticality)
            .ThenByDescending(issue => issue.ModuleIds.Count)
            .ThenBy(issue => issue.Kind)
            .ThenBy(issue => issue.Key, StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToList();
        if (signatures.Count == 0
            && runtimeModules.Count > 0
            && ReadLinesSafe(session.LauncherLogPath, maxLines: 200, warnings).Any(line => ContainsAny(line, LauncherErrorTokens)))
        {
            warnings.Add("Runtime logs contained broad launcher error markers, but no concrete typed issue signature was strong enough to surface.");
        }

        return signatures;
    }

    private static void CollectRuntimeIssueSignatures(
        IDictionary<string, RuntimeIssueAccumulator> issuesByKey,
        IEnumerable<string> lines,
        string sourceLabel,
        IReadOnlyList<ModuleManifest> modules,
        bool customOnlyFocus,
        IReadOnlyDictionary<string, ModuleManifest> byId,
        IReadOnlyDictionary<string, List<string>> moduleOwnersByDll
    )
    {
        foreach (string rawLine in lines)
        {
            if (!TryBuildRuntimeIssueSignature(
                    rawLine,
                    sourceLabel,
                    modules,
                    customOnlyFocus,
                    byId,
                    moduleOwnersByDll,
                    out RuntimeIssueSignature? signature))
            {
                continue;
            }

            if (signature is null)
            {
                continue;
            }

            RuntimeIssueSignature concreteSignature = signature;
            if (!issuesByKey.TryGetValue(concreteSignature.Key, out RuntimeIssueAccumulator? accumulator))
            {
                accumulator = new RuntimeIssueAccumulator(
                    concreteSignature.Key,
                    concreteSignature.Kind,
                    concreteSignature.Criticality,
                    concreteSignature.Summary);
                issuesByKey[concreteSignature.Key] = accumulator;
            }

            accumulator.Add(concreteSignature);
        }
    }

    private static bool TryBuildRuntimeIssueSignature(
        string rawLine,
        string sourceLabel,
        IReadOnlyList<ModuleManifest> modules,
        bool customOnlyFocus,
        IReadOnlyDictionary<string, ModuleManifest> byId,
        IReadOnlyDictionary<string, List<string>> moduleOwnersByDll,
        out RuntimeIssueSignature? signature
    )
    {
        signature = null;
        string normalized = NormalizeIssue(rawLine);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        RuntimeIssueKind? kind = ClassifyRuntimeIssueKind(normalized);
        if (kind is null)
        {
            return false;
        }

        List<string> moduleIds = InferModulesFromIssueText(
            normalized,
            modules,
            customOnlyFocus,
            byId,
            moduleOwnersByDll);
        bool hasConcreteAnchor = moduleIds.Count > 0
            || ModulePathRegex.IsMatch(normalized)
            || DllNameRegex.IsMatch(normalized)
            || ContainsAny(normalized, SignatureAnchorTokens);
        if (kind == RuntimeIssueKind.GenericRuntimeError && !hasConcreteAnchor)
        {
            return false;
        }

        RuntimeIssuePhase phase = ClassifyRuntimeIssuePhase(kind.Value, normalized, sourceLabel);
        signature = new RuntimeIssueSignature(
            BuildRuntimeIssueKey(kind.Value, normalized),
            kind.Value,
            DetermineRuntimeIssueCriticality(kind.Value),
            BuildRuntimeIssueSummary(kind.Value, normalized),
            moduleIds,
            normalized,
            sourceLabel,
            phase,
            ClassifyRuntimeSystemArea(normalized, phase));
        return true;
    }

    private static RuntimeIssueKind? ClassifyRuntimeIssueKind(string normalizedIssue)
    {
        string normalized = normalizedIssue.ToLowerInvariant();
        if (normalized.Contains("missingmethodexception", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeIssueKind.MissingMethod;
        }

        if (normalized.Contains("typeloadexception", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeIssueKind.TypeLoad;
        }

        if (normalized.Contains("filenotfoundexception", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("could not load file or assembly", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("couldn't find .dll", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("assembly load result: null", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("couldn't verify dlls", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeIssueKind.LoaderFailure;
        }

        if (normalized.Contains("nullreferenceexception", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeIssueKind.NullReference;
        }

        if (normalized.Contains("assert", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeIssueKind.Assertion;
        }

        if (normalized.Contains("unhandled exception", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeIssueKind.UnhandledException;
        }

        bool broadErrorMarker = normalized.Contains("error:", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("error", StringComparison.OrdinalIgnoreCase);
        return broadErrorMarker
            ? RuntimeIssueKind.GenericRuntimeError
            : null;
    }

    private static RuntimeIssueCriticality DetermineRuntimeIssueCriticality(RuntimeIssueKind kind)
    {
        return kind switch
        {
            RuntimeIssueKind.LoaderFailure or RuntimeIssueKind.MissingMethod or RuntimeIssueKind.TypeLoad or RuntimeIssueKind.UnhandledException
                => RuntimeIssueCriticality.Critical,
            RuntimeIssueKind.NullReference or RuntimeIssueKind.Assertion
                => RuntimeIssueCriticality.High,
            _ => RuntimeIssueCriticality.Advisory,
        };
    }

    private static string BuildRuntimeIssueKey(RuntimeIssueKind kind, string issue)
    {
        Match moduleMatch = ModulePathRegex.Match(issue);
        if (moduleMatch.Success)
        {
            string moduleId = moduleMatch.Groups["id"].Value.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(moduleId))
            {
                return $"{ToToken(kind)}|module:{moduleId}";
            }
        }

        Match dllMatch = DllNameRegex.Match(issue);
        if (dllMatch.Success)
        {
            string dll = dllMatch.Groups["dll"].Value.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(dll))
            {
                return $"{ToToken(kind)}|dll:{dll}";
            }
        }

        string compact = issue.ToLowerInvariant()
            .Replace("0x", "0x*", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ", StringComparison.Ordinal);
        if (compact.Length > 96)
        {
            compact = compact[..96];
        }

        return $"{ToToken(kind)}|{compact}";
    }

    private static string BuildRuntimeIssueSummary(RuntimeIssueKind kind, string issue)
    {
        string? dll = DllNameRegex.Match(issue) is Match dllMatch && dllMatch.Success
            ? dllMatch.Groups["dll"].Value.Trim()
            : null;

        return kind switch
        {
            RuntimeIssueKind.LoaderFailure => string.IsNullOrWhiteSpace(dll)
                ? "Assembly or DLL failed to load"
                : $"Assembly or DLL failed to load ({dll})",
            RuntimeIssueKind.MissingMethod => "Missing method or game API mismatch",
            RuntimeIssueKind.TypeLoad => "Type load failure",
            RuntimeIssueKind.NullReference => "Null reference runtime exception",
            RuntimeIssueKind.Assertion => "Engine or loader assertion",
            RuntimeIssueKind.UnhandledException => "Unhandled runtime exception",
            _ => "Runtime error marker",
        };
    }

    private static string BuildRuntimeIssueOutcome(RuntimeIssueKind kind)
    {
        return kind switch
        {
            RuntimeIssueKind.LoaderFailure => "Startup can fail because required assemblies or DLLs are not loading cleanly.",
            RuntimeIssueKind.MissingMethod or RuntimeIssueKind.TypeLoad => "Game API mismatches can break startup or fail when the affected code path executes.",
            RuntimeIssueKind.NullReference or RuntimeIssueKind.UnhandledException => "The affected gameplay path can fail at runtime when this exception path is reached.",
            RuntimeIssueKind.Assertion => "The engine or loader is already reporting an assertion failure on this runtime path.",
            _ => "Runtime logs already show a concrete failure marker on this profile.",
        };
    }

    private static string BuildRuntimeIssueRecommendation(RuntimeIssueKind kind)
    {
        return kind switch
        {
            RuntimeIssueKind.LoaderFailure => "Fix the assembly or DLL load problem first, then retest from a clean launch.",
            RuntimeIssueKind.MissingMethod or RuntimeIssueKind.TypeLoad => "Match mod builds to the same Bannerlord API baseline, then retest from a clean launch.",
            RuntimeIssueKind.NullReference or RuntimeIssueKind.UnhandledException => "Reproduce the same gameplay path once with the current stack, then isolate only if the failure returns.",
            RuntimeIssueKind.Assertion => "Treat the assertion as a hard runtime blocker and resolve the underlying module mismatch before deeper validation.",
            _ => "Use the logged error as a concrete baseline, then retest from a clean launch after each change.",
        };
    }

    private static List<string> InferModulesFromIssueText(
        string issue,
        IReadOnlyList<ModuleManifest> modules,
        bool customOnlyFocus,
        IReadOnlyDictionary<string, ModuleManifest> byId,
        IReadOnlyDictionary<string, List<string>> moduleOwnersByDll
    )
    {
        HashSet<string> inferred = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ModulePathRegex.Matches(issue))
        {
            string id = match.Groups["id"].Value.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (byId.TryGetValue(id, out ModuleManifest? module))
            {
                inferred.Add(module.Id);
            }
            else
            {
                inferred.Add(id);
            }
        }

        foreach ((string dllName, List<string> owners) in moduleOwnersByDll)
        {
            if (!issue.Contains(dllName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string owner in owners)
            {
                inferred.Add(owner);
            }
        }

        foreach (ModuleManifest module in modules)
        {
            if (ContainsToken(issue, module.Id) || ContainsToken(issue, module.Name))
            {
                inferred.Add(module.Id);
            }
        }

        return inferred
            .Where(id => !customOnlyFocus || (byId.TryGetValue(id, out ModuleManifest? mod) && mod.IsCustom))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList();
    }

    private static IEnumerable<string> PathEvidence(SessionFiles session)
    {
        if (!string.IsNullOrWhiteSpace(session.WatchdogLogPath))
        {
            yield return session.WatchdogLogPath;
        }

        if (!string.IsNullOrWhiteSpace(session.LauncherLogPath))
        {
            yield return session.LauncherLogPath;
        }

        if (!string.IsNullOrWhiteSpace(session.RglErrorLogPath))
        {
            yield return session.RglErrorLogPath;
        }

        if (!string.IsNullOrWhiteSpace(session.RglLogPath))
        {
            yield return session.RglLogPath;
        }
    }

    private static double ComputeRuntimeLoaderConfidence(IReadOnlyList<RuntimeIssueSignature> runtimeIssues)
    {
        double confidence = 0.70;
        if (runtimeIssues.Any(issue => issue.Criticality == RuntimeIssueCriticality.Critical))
        {
            confidence += 0.12;
        }

        if (runtimeIssues.Count > 1)
        {
            confidence += Math.Min(0.08, runtimeIssues.Count * 0.02);
        }

        if (runtimeIssues.All(issue => issue.Kind == RuntimeIssueKind.GenericRuntimeError))
        {
            confidence -= 0.10;
        }

        if (runtimeIssues.Any(issue => issue.ModuleIds.Count > 0))
        {
            confidence += 0.04;
        }

        return Math.Clamp(confidence, 0.64, 0.96);
    }

    private static RuntimeIssueKind ClassifySignatureKind(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return RuntimeIssueKind.GenericRuntimeError;
        }

        string token = signature.Split('|', 2, StringSplitOptions.TrimEntries)[0];
        return token switch
        {
            "loader-failure" => RuntimeIssueKind.LoaderFailure,
            "missing-method" => RuntimeIssueKind.MissingMethod,
            "type-load" => RuntimeIssueKind.TypeLoad,
            "null-reference" => RuntimeIssueKind.NullReference,
            "assertion" => RuntimeIssueKind.Assertion,
            "unhandled-exception" => RuntimeIssueKind.UnhandledException,
            _ => RuntimeIssueKind.GenericRuntimeError,
        };
    }

    private static string ToDisplayLabel(RuntimeIssueKind kind)
    {
        return kind switch
        {
            RuntimeIssueKind.LoaderFailure => "assembly/DLL load",
            RuntimeIssueKind.MissingMethod => "missing-method",
            RuntimeIssueKind.TypeLoad => "type-load",
            RuntimeIssueKind.NullReference => "null-reference",
            RuntimeIssueKind.Assertion => "assertion",
            RuntimeIssueKind.UnhandledException => "unhandled-exception",
            _ => "runtime",
        };
    }

    private static string ToToken(RuntimeIssueKind kind)
    {
        return kind switch
        {
            RuntimeIssueKind.LoaderFailure => "loader-failure",
            RuntimeIssueKind.MissingMethod => "missing-method",
            RuntimeIssueKind.TypeLoad => "type-load",
            RuntimeIssueKind.NullReference => "null-reference",
            RuntimeIssueKind.Assertion => "assertion",
            RuntimeIssueKind.UnhandledException => "unhandled-exception",
            _ => "generic-runtime-error",
        };
    }

    private static string ToToken(RuntimeIssueCriticality criticality)
    {
        return criticality switch
        {
            RuntimeIssueCriticality.Critical => "critical",
            RuntimeIssueCriticality.High => "high",
            _ => "advisory",
        };
    }

    private static string ToToken(RuntimeIssuePhase phase)
    {
        return phase switch
        {
            RuntimeIssuePhase.Startup => "startup",
            RuntimeIssuePhase.CampaignLoad => "campaign-load",
            RuntimeIssuePhase.BattleEntry => "battle-entry",
            RuntimeIssuePhase.SettlementEntry => "settlement-entry",
            RuntimeIssuePhase.SaveLoad => "save-load",
            _ => "unknown",
        };
    }

    private static RuntimeIssuePhase ClassifyRuntimeIssuePhase(
        RuntimeIssueKind kind,
        string normalizedIssue,
        string sourceLabel)
    {
        string normalized = normalizedIssue.ToLowerInvariant();

        if (normalized.Contains("save", StringComparison.OrdinalIgnoreCase)
            && (normalized.Contains("load", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains(".sav", StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeIssuePhase.SaveLoad;
        }

        if (normalized.Contains("battle", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("mission", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("agent", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("siege", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeIssuePhase.BattleEntry;
        }

        if (normalized.Contains("settlement", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("town", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("village", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("castle", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeIssuePhase.SettlementEntry;
        }

        if (normalized.Contains("campaign", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("dailytick", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("behavior", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("gamestart", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeIssuePhase.CampaignLoad;
        }

        if (kind == RuntimeIssueKind.LoaderFailure
            || kind == RuntimeIssueKind.TypeLoad
            || kind == RuntimeIssueKind.MissingMethod
            || sourceLabel.Equals("launcher", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeIssuePhase.Startup;
        }

        return RuntimeIssuePhase.Unknown;
    }

    private static string ClassifyRuntimeSystemArea(string normalizedIssue, RuntimeIssuePhase phase)
    {
        string normalized = normalizedIssue.ToLowerInvariant();

        if (normalized.Contains("gauntlet", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ui", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("widget", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("movie", StringComparison.OrdinalIgnoreCase))
        {
            return "ui";
        }

        if (normalized.Contains("harmony", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("patch", StringComparison.OrdinalIgnoreCase))
        {
            return "patching";
        }

        return phase switch
        {
            RuntimeIssuePhase.Startup => "startup",
            RuntimeIssuePhase.CampaignLoad => "campaign",
            RuntimeIssuePhase.BattleEntry => "battle",
            RuntimeIssuePhase.SettlementEntry => "settlement",
            RuntimeIssuePhase.SaveLoad => "save",
            _ => "runtime",
        };
    }

    private static string BuildModlistSessionFingerprint(IReadOnlyList<string> moduleIds, string? gameVersion)
    {
        string payload = $"{gameVersion ?? "unknown"}|{string.Join("|", moduleIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }

    private static List<string> ReadLinesSafe(string? path, int maxLines, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return [];
        }

        List<string> lines = [];
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                lines.Add(line);
                if (lines.Count >= maxLines)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Runtime log read warning in '{path}': {ex.Message}");
        }

        return lines;
    }

    private static List<string> ReadTailLinesSafe(string? path, int tailCount, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || tailCount <= 0)
        {
            return [];
        }

        Queue<string> tail = new();
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (tail.Count >= tailCount)
                {
                    tail.Dequeue();
                }

                tail.Enqueue(line);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Runtime log read warning in '{path}': {ex.Message}");
        }

        return tail.ToList();
    }

    private static bool ContainsAny(string value, IReadOnlyList<string> tokens)
    {
        return tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeIssue(string value)
    {
        return string.Join(" ",
            value.Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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

    private enum LogFileKind
    {
        Launcher,
        Watchdog,
        RglErrors,
        Rgl,
    }

    private sealed record SessionFiles(
        string? SessionId,
        string? LauncherLogPath,
        string? WatchdogLogPath,
        string? RglErrorLogPath,
        string? RglLogPath,
        string? CrashListPath,
        DateTime LastWriteUtc
    );

    private enum RuntimeIssueKind
    {
        LoaderFailure,
        MissingMethod,
        TypeLoad,
        NullReference,
        Assertion,
        UnhandledException,
        GenericRuntimeError,
    }

    private enum RuntimeIssueCriticality
    {
        Advisory,
        High,
        Critical,
    }

    private enum RuntimeIssuePhase
    {
        Unknown,
        Startup,
        CampaignLoad,
        BattleEntry,
        SettlementEntry,
        SaveLoad,
    }

    private sealed record RuntimeIssueSignature(
        string Key,
        RuntimeIssueKind Kind,
        RuntimeIssueCriticality Criticality,
        string Summary,
        IReadOnlyList<string> ModuleIds,
        string EvidenceLine,
        string PrimarySource,
        RuntimeIssuePhase Phase,
        string SystemArea
    );

    private sealed class RuntimeIssueAccumulator
    {
        public RuntimeIssueAccumulator(
            string key,
            RuntimeIssueKind kind,
            RuntimeIssueCriticality criticality,
            string summary)
        {
            Key = key;
            Kind = kind;
            Criticality = criticality;
            Summary = summary;
        }

        public string Key { get; }
        public RuntimeIssueKind Kind { get; private set; }
        public RuntimeIssueCriticality Criticality { get; private set; }
        public string Summary { get; private set; }
        public HashSet<string> ModuleIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> EvidenceLines { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<RuntimeIssuePhase, int> PhaseCounts { get; } = [];
        public Dictionary<string, int> SystemAreaCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Add(RuntimeIssueSignature signature)
        {
            if (signature.Criticality > Criticality)
            {
                Criticality = signature.Criticality;
            }

            if (signature.Kind != RuntimeIssueKind.GenericRuntimeError)
            {
                Kind = signature.Kind;
                Summary = signature.Summary;
            }

            foreach (string moduleId in signature.ModuleIds)
            {
                ModuleIds.Add(moduleId);
            }

            if (!string.IsNullOrWhiteSpace(signature.EvidenceLine))
            {
                EvidenceLines.Add(signature.EvidenceLine);
            }

            if (!string.IsNullOrWhiteSpace(signature.PrimarySource))
            {
                Sources.Add(signature.PrimarySource);
            }

            PhaseCounts[signature.Phase] = PhaseCounts.TryGetValue(signature.Phase, out int phaseCount)
                ? phaseCount + 1
                : 1;

            if (!string.IsNullOrWhiteSpace(signature.SystemArea))
            {
                SystemAreaCounts[signature.SystemArea] = SystemAreaCounts.TryGetValue(signature.SystemArea, out int areaCount)
                    ? areaCount + 1
                    : 1;
            }
        }

        public RuntimeIssueSignature Build()
        {
            RuntimeIssuePhase phase = PhaseCounts
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key)
                .Select(x => x.Key)
                .FirstOrDefault();
            string systemArea = SystemAreaCounts
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Key)
                .FirstOrDefault() ?? "runtime";

            return new RuntimeIssueSignature(
                Key,
                Kind,
                Criticality,
                Summary,
                ModuleIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
                EvidenceLines.FirstOrDefault() ?? Summary,
                Sources.OrderBy(source => source, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? "runtime-log",
                phase,
                systemArea);
        }
    }

    private sealed class SessionBucket
    {
        public SessionBucket(string sessionId)
        {
            SessionId = sessionId;
        }

        public string SessionId { get; }
        public string? LauncherLogPath { get; private set; }
        public string? WatchdogLogPath { get; private set; }
        public string? RglErrorLogPath { get; private set; }
        public string? RglLogPath { get; private set; }
        public DateTime LastWriteUtc { get; private set; } = DateTime.MinValue;

        public void Assign(LogFileKind kind, string path)
        {
            switch (kind)
            {
                case LogFileKind.Launcher:
                    LauncherLogPath = AssignLatest(LauncherLogPath, path);
                    break;
                case LogFileKind.Watchdog:
                    WatchdogLogPath = AssignLatest(WatchdogLogPath, path);
                    break;
                case LogFileKind.RglErrors:
                    RglErrorLogPath = AssignLatest(RglErrorLogPath, path);
                    break;
                case LogFileKind.Rgl:
                    RglLogPath = AssignLatest(RglLogPath, path);
                    break;
            }
        }

        public SessionFiles ToSessionFiles(string? crashListPath)
        {
            return new SessionFiles(
                SessionId,
                LauncherLogPath,
                WatchdogLogPath,
                RglErrorLogPath,
                RglLogPath,
                crashListPath,
                LastWriteUtc
            );
        }

        private string AssignLatest(string? current, string candidate)
        {
            string selected = current ?? candidate;
            if (current is null || GetLastWriteUtcSafe(candidate) >= GetLastWriteUtcSafe(current))
            {
                selected = candidate;
            }

            DateTime candidateWrite = GetLastWriteUtcSafe(candidate);
            if (candidateWrite > LastWriteUtc)
            {
                LastWriteUtc = candidateWrite;
            }

            return selected;
        }
    }

    private sealed class IncidentClusterAccumulator
    {
        public IncidentClusterAccumulator(string signature)
        {
            Signature = signature;
        }

        public string Signature { get; }
        public HashSet<string> SessionKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ModuleCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> EvidencePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SampleIssues { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> PhaseCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> SystemAreaCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasCriticalRuntimeSignatures { get; set; }
    }
}
