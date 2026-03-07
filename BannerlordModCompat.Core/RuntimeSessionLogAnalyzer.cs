using System.Globalization;
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
        string? logsRootOverride = null
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
        List<string> runtimeIssues = ParseRuntimeIssues(session, warnings, out bool hasCriticalRuntimeSignatures);

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
            modules,
            customOnlyFocus,
            byId,
            session,
            hasCriticalRuntimeSignatures
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
            warnings
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
        };
    }

    private static ConflictFinding? BuildRuntimeLoaderFailureFinding(
        IReadOnlyList<string> runtimeIssues,
        IReadOnlySet<string> runtimeModules,
        IReadOnlyList<ModuleManifest> modules,
        bool customOnlyFocus,
        IReadOnlyDictionary<string, ModuleManifest> byId,
        SessionFiles session,
        bool hasCriticalRuntimeSignatures
    )
    {
        if (runtimeIssues.Count == 0)
        {
            return null;
        }

        List<string> moduleIds = InferModulesFromRuntimeIssues(
            runtimeIssues,
            runtimeModules,
            modules,
            customOnlyFocus,
            byId
        );

        return new ConflictFinding
        {
            Category = ConflictCategory.RuntimeLoaderFailure,
            Severity = hasCriticalRuntimeSignatures ? ConflictSeverity.Critical : ConflictSeverity.High,
            Confidence = 0.92,
            ModuleIds = moduleIds,
            Reason = $"Latest runtime logs contain {runtimeIssues.Count} loader/runtime failure signature(s).",
            LikelyInGameOutcome = "Startup can fail or gameplay can crash when unresolved assembly/runtime faults are present.",
            Recommendation = "Resolve loader/runtime errors in launch logs first, then validate module order and retest from a clean launch.",
            Evidence = [.. PathEvidence(session), .. runtimeIssues.Take(8).Select(x => $"issue: {x}")],
        };
    }

    private static ConflictFinding? BuildRecurringIncidentClusterFinding(
        IReadOnlyList<SessionFiles> sessions,
        IReadOnlyList<ModuleManifest> modules,
        IReadOnlyList<string> currentOrder,
        bool customOnlyFocus,
        IReadOnlyDictionary<string, ModuleManifest> byId,
        List<string> warnings
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
            List<string> runtimeIssues = ParseRuntimeIssues(session, warnings, out bool hasCriticalRuntimeSignatures);

            if (runtimeIssues.Count == 0)
            {
                continue;
            }

            HashSet<string> sessionModules = InferModulesFromRuntimeIssues(
                runtimeIssues,
                runtimeModules,
                modules,
                customOnlyFocus,
                byId
            ).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                .Select(BuildIssueSignature)
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
                cluster.HasCriticalRuntimeSignatures |= hasCriticalRuntimeSignatures;
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

                foreach (string issue in runtimeIssues.Take(5))
                {
                    cluster.SampleIssues.Add(issue);
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

        string displaySignature = ToShortSignature(best.Signature);
        string reason = $"Recurring runtime incident cluster detected: signature '{displaySignature}' appeared in "
            + $"{best.SessionKeys.Count}/{sessions.Count} recent session(s).";
        string likelyOutcome = "Loader/runtime fault signature is repeating across sessions, indicating a stable incompatibility pattern.";
        string recommendation = "Treat this as a recurring incident cluster: keep one baseline profile, then disable modules in cohorts and rerun "
            + "until the repeated signature disappears. Preserve the same load order while isolating.";

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
        };
    }

    private static string BuildIssueSignature(string issue)
    {
        string normalized = NormalizeIssue(issue).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        string anchor = SignatureAnchorTokens
            .FirstOrDefault(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase))
            ?? "runtime-error";
        Match moduleMatch = ModulePathRegex.Match(issue);
        if (moduleMatch.Success)
        {
            string moduleId = moduleMatch.Groups["id"].Value.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(moduleId))
            {
                return $"{anchor}|module:{moduleId}";
            }
        }

        Match dllMatch = DllNameRegex.Match(issue);
        if (dllMatch.Success)
        {
            string dll = dllMatch.Groups["dll"].Value.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(dll))
            {
                return $"{anchor}|dll:{dll}";
            }
        }

        string compact = normalized
            .Replace("0x", "0x*", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ", StringComparison.Ordinal);
        if (compact.Length > 100)
        {
            compact = compact[..100];
        }

        return $"{anchor}|{compact}";
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

    private static List<string> ParseRuntimeIssues(
        SessionFiles session,
        List<string> warnings,
        out bool hasCriticalRuntimeSignatures
    )
    {
        HashSet<string> issues = new(StringComparer.OrdinalIgnoreCase);

        foreach (string line in ReadLinesSafe(session.LauncherLogPath, maxLines: 10_000, warnings))
        {
            if (ContainsAny(line, LauncherErrorTokens))
            {
                issues.Add(NormalizeIssue(line));
            }
        }

        foreach (string line in ReadLinesSafe(session.RglErrorLogPath, maxLines: 2_000, warnings))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                issues.Add(NormalizeIssue(line));
            }
        }

        foreach (string line in ReadTailLinesSafe(session.RglLogPath, tailCount: 4_000, warnings))
        {
            if (ContainsAny(line, RuntimeErrorTokens))
            {
                issues.Add(NormalizeIssue(line));
            }
        }

        hasCriticalRuntimeSignatures = issues.Any(issue =>
            issue.Contains("missingmethodexception", StringComparison.OrdinalIgnoreCase)
            || issue.Contains("typeloadexception", StringComparison.OrdinalIgnoreCase)
            || issue.Contains("could not load file or assembly", StringComparison.OrdinalIgnoreCase)
            || issue.Contains("unhandled exception", StringComparison.OrdinalIgnoreCase)
            || issue.Contains("stack trace", StringComparison.OrdinalIgnoreCase));

        return issues.Take(80).ToList();
    }

    private static List<string> InferModulesFromRuntimeIssues(
        IReadOnlyList<string> runtimeIssues,
        IReadOnlySet<string> runtimeModules,
        IReadOnlyList<ModuleManifest> modules,
        bool customOnlyFocus,
        IReadOnlyDictionary<string, ModuleManifest> byId
    )
    {
        Dictionary<string, List<string>> moduleOwnersByDll = modules
            .SelectMany(m => m.Dlls.Select(d => (m.Id, d.FileName)))
            .GroupBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase
            );

        HashSet<string> inferred = new(StringComparer.OrdinalIgnoreCase);
        foreach (string issue in runtimeIssues)
        {
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
        }

        if (inferred.Count == 0)
        {
            foreach (string id in runtimeModules)
            {
                if (byId.TryGetValue(id, out ModuleManifest? module))
                {
                    inferred.Add(module.Id);
                }
            }
        }

        return inferred
            .Where(id => !customOnlyFocus || (byId.TryGetValue(id, out ModuleManifest? mod) && mod.IsCustom))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
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
        public bool HasCriticalRuntimeSignatures { get; set; }
    }
}
