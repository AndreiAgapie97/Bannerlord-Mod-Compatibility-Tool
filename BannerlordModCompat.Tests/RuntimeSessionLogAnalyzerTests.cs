using BannerlordModCompat.Core;
using Xunit;

namespace BannerlordModCompat.Tests;

public class RuntimeSessionLogAnalyzerTests
{
    [Fact]
    public void Analyze_DetectsRuntimeModuleSetMismatchFromWatchdog()
    {
        string tempRoot = CreateTempRoot();
        try
        {
            string watchdogPath = Path.Combine(tempRoot, "watchdog_log_123456.txt");
            File.WriteAllLines(watchdogPath,
            [
                "Debuggee message: #TW#crash_tags.txt#TW#Runtime#TW#Arguments#TW#/singleplayer _MODULES_*ModA*ModB*_MODULES_",
                "Debuggee message: #TW#crash_tags.txt#TW#Used Modules#TW#ModA#TW#Exists and Active (Version: v1.0.0)",
                "Debuggee message: #TW#crash_tags.txt#TW#Used Modules#TW#ModB#TW#Exists and Active (Version: v1.0.0)",
            ]);

            RuntimeSessionLogAnalyzer analyzer = new();
            List<string> warnings = [];
            List<ModuleManifest> modules =
            [
                BuildModule("ModA"),
                BuildModule("ModB"),
            ];
            List<ConflictFinding> findings = analyzer.Analyze(
                modules,
                currentOrder: ["ModA"],
                customOnlyFocus: false,
                warnings,
                logsRootOverride: tempRoot
            ).ToList();

            ConflictFinding mismatch = Assert.Single(findings,
                f => f.Category == ConflictCategory.RuntimeModuleSetMismatch);
            Assert.Contains("ModB", mismatch.ModuleIds, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(findings, f => f.Category == ConflictCategory.RuntimeCrashSession);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void Analyze_DetectsRuntimeLoaderFailureFromLauncherLog()
    {
        string tempRoot = CreateTempRoot();
        try
        {
            string launcherPath = Path.Combine(tempRoot, "launcher_log_654321.txt");
            File.WriteAllLines(launcherPath,
            [
                "ERROR: BadMod.dll: Could not load file or assembly 'BadMod.dll' or one of its dependencies.",
                "Couldn't find .dll: ..\\..\\Modules\\BadMod\\bin\\Win64_Shipping_Client\\BadMod.dll",
            ]);

            RuntimeSessionLogAnalyzer analyzer = new();
            List<string> warnings = [];
            List<ModuleManifest> modules =
            [
                BuildModule("BadMod", dllName: "BadMod.dll"),
            ];
            List<ConflictFinding> findings = analyzer.Analyze(
                modules,
                currentOrder: ["BadMod"],
                customOnlyFocus: false,
                warnings,
                logsRootOverride: tempRoot
            ).ToList();

            ConflictFinding loader = Assert.Single(findings,
                f => f.Category == ConflictCategory.RuntimeLoaderFailure);
            Assert.Contains("BadMod", loader.ModuleIds, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(loader.Evidence, e =>
                e.Contains("launcher_log_654321.txt", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(loader.StructuredEvidence);
            Assert.Equal(FindingEvidenceScope.Session, loader.StructuredEvidence!.Scope);
            Assert.Contains(FindingEvidenceSource.RuntimeLog, loader.StructuredEvidence.Sources);
            Assert.Contains(FindingEvidenceKind.RuntimeLoaderIssue, loader.StructuredEvidence.Kinds);
            Assert.Equal("loader-failure", loader.StructuredEvidence.Details["issue-kind"]);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void Analyze_DetectsRecurringIncidentClusterAcrossRecentSessions()
    {
        string tempRoot = CreateTempRoot();
        try
        {
            File.WriteAllLines(Path.Combine(tempRoot, "launcher_log_100001.txt"),
            [
                "ERROR: BadMod.dll: Could not load file or assembly 'BadMod.dll' or one of its dependencies.",
            ]);
            File.WriteAllLines(Path.Combine(tempRoot, "watchdog_log_100001.txt"),
            [
                "Debuggee message: #TW#crash_tags.txt#TW#Runtime#TW#Arguments#TW#/singleplayer _MODULES_*BadMod*_MODULES_",
                "Debuggee message: #TW#Used Modules#TW#BadMod#TW#Exists and Active (Version: v1.0.0)",
            ]);

            File.WriteAllLines(Path.Combine(tempRoot, "launcher_log_100002.txt"),
            [
                "ERROR: BadMod.dll: Could not load file or assembly 'BadMod.dll' or one of its dependencies.",
            ]);
            File.WriteAllLines(Path.Combine(tempRoot, "watchdog_log_100002.txt"),
            [
                "Debuggee message: #TW#crash_tags.txt#TW#Runtime#TW#Arguments#TW#/singleplayer _MODULES_*BadMod*_MODULES_",
                "Debuggee message: #TW#Used Modules#TW#BadMod#TW#Exists and Active (Version: v1.0.0)",
            ]);

            RuntimeSessionLogAnalyzer analyzer = new();
            List<string> warnings = [];
            List<ConflictFinding> findings = analyzer.Analyze(
                modules:
                [
                    BuildModule("BadMod", dllName: "BadMod.dll"),
                ],
                currentOrder: ["BadMod"],
                customOnlyFocus: false,
                warnings: warnings,
                logsRootOverride: tempRoot
            ).ToList();

            ConflictFinding recurring = Assert.Single(findings, f =>
                f.StructuredEvidence?.Details.TryGetValue("recurrence-count", out string? value) == true
                && value == "2");
            Assert.Equal(ConflictCategory.RuntimeLoaderFailure, recurring.Category);
            Assert.Contains("Recurring", recurring.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(recurring.Evidence, e =>
                e.StartsWith("cluster-signature:", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(recurring.Evidence, e =>
                e.StartsWith("cluster-session-count:", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(recurring.StructuredEvidence);
            Assert.Equal("loader-failure", recurring.StructuredEvidence!.Details["issue-kind"]);
            Assert.Equal("2", recurring.StructuredEvidence.Details["recurrence-count"]);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void Analyze_DoesNotEmitRuntimeCrashFindingFromCrashListBreadcrumbsAlone()
    {
        string tempRoot = CreateTempRoot();
        try
        {
            string watchdogPath = Path.Combine(tempRoot, "watchdog_log_777777.txt");
            File.WriteAllLines(watchdogPath,
            [
                "Debuggee message: #TW#crash_tags.txt#TW#Runtime#TW#Arguments#TW#/singleplayer _MODULES_*ModA*ModB*_MODULES_",
                "Debuggee message: #TW#crash_tags.txt#TW#Used Modules#TW#ModA#TW#Exists and Active (Version: v1.0.0)",
                "Debuggee message: #TW#crash_tags.txt#TW#Used Modules#TW#ModB#TW#Exists and Active (Version: v1.0.0)",
            ]);
            string crashListPath = Path.Combine(tempRoot, "crashlist.txt");
            File.WriteAllText(crashListPath, "crash-entry");
            File.SetLastWriteTimeUtc(watchdogPath, DateTime.UtcNow);
            File.SetLastWriteTimeUtc(crashListPath, DateTime.UtcNow.AddSeconds(2));

            RuntimeSessionLogAnalyzer analyzer = new();
            List<string> warnings = [];
            List<ConflictFinding> findings = analyzer.Analyze(
                modules:
                [
                    BuildModule("ModA"),
                    BuildModule("ModB"),
                ],
                currentOrder: ["ModA", "ModB"],
                customOnlyFocus: false,
                warnings: warnings,
                logsRootOverride: tempRoot
            ).ToList();

            Assert.DoesNotContain(findings, f => f.Category == ConflictCategory.RuntimeCrashSession);
            Assert.Empty(findings);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void Analyze_DoesNotTreatStaleCrashListAsCrashForLatestSession()
    {
        string tempRoot = CreateTempRoot();
        try
        {
            string watchdogPath = Path.Combine(tempRoot, "watchdog_log_888888.txt");
            File.WriteAllLines(watchdogPath,
            [
                "Debuggee message: #TW#crash_tags.txt#TW#Runtime#TW#Arguments#TW#/singleplayer _MODULES_*ModA*ModB*_MODULES_",
                "Debuggee message: #TW#crash_tags.txt#TW#Used Modules#TW#ModA#TW#Exists and Active (Version: v1.0.0)",
                "Debuggee message: #TW#crash_tags.txt#TW#Used Modules#TW#ModB#TW#Exists and Active (Version: v1.0.0)",
            ]);
            string crashListPath = Path.Combine(tempRoot, "crashlist.txt");
            File.WriteAllText(crashListPath, "old-crash-entry");

            File.SetLastWriteTimeUtc(crashListPath, DateTime.UtcNow.AddHours(-3));
            File.SetLastWriteTimeUtc(watchdogPath, DateTime.UtcNow);

            RuntimeSessionLogAnalyzer analyzer = new();
            List<string> warnings = [];
            List<ConflictFinding> findings = analyzer.Analyze(
                modules:
                [
                    BuildModule("ModA"),
                    BuildModule("ModB"),
                ],
                currentOrder: ["ModA", "ModB"],
                customOnlyFocus: false,
                warnings: warnings,
                logsRootOverride: tempRoot
            ).ToList();

            Assert.DoesNotContain(findings, f => f.Category == ConflictCategory.RuntimeCrashSession);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void Analyze_ClassifiesMissingMethodAsConcreteRuntimeIssue()
    {
        string tempRoot = CreateTempRoot();
        try
        {
            string rglErrorsPath = Path.Combine(tempRoot, "rgl_log_errors_999001.txt");
            File.WriteAllLines(rglErrorsPath,
            [
                "System.MissingMethodException: Method not found: 'Void TaleWorlds.CampaignSystem.Campaign.AddBehavior()'.",
            ]);

            RuntimeSessionLogAnalyzer analyzer = new();
            List<string> warnings = [];
            List<ConflictFinding> findings = analyzer.Analyze(
                modules:
                [
                    BuildModule("BadPatch"),
                ],
                currentOrder: ["BadPatch"],
                customOnlyFocus: false,
                warnings: warnings,
                logsRootOverride: tempRoot
            ).ToList();

            ConflictFinding loader = Assert.Single(findings, f => f.Category == ConflictCategory.RuntimeLoaderFailure);
            Assert.NotNull(loader.StructuredEvidence);
            Assert.Equal("missing-method", loader.StructuredEvidence!.Details["issue-kind"]);
            Assert.Contains("API mismatch", loader.StructuredEvidence.Details["issue-summary"], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public void Analyze_DoesNotSurfaceGenericErrorNoiseWithoutConcreteIssueSignature()
    {
        string tempRoot = CreateTempRoot();
        try
        {
            string launcherPath = Path.Combine(tempRoot, "launcher_log_999002.txt");
            File.WriteAllLines(launcherPath,
            [
                "ERROR: something failed while doing a thing",
                "ERROR: generic error marker without dll or exception detail",
            ]);

            RuntimeSessionLogAnalyzer analyzer = new();
            List<string> warnings = [];
            List<ConflictFinding> findings = analyzer.Analyze(
                modules:
                [
                    BuildModule("ModA"),
                ],
                currentOrder: ["ModA"],
                customOnlyFocus: false,
                warnings: warnings,
                logsRootOverride: tempRoot
            ).ToList();

            Assert.Empty(findings);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    private static ModuleManifest BuildModule(string id, string? dllName = null)
    {
        List<DllArtifact> dlls = [];
        if (!string.IsNullOrWhiteSpace(dllName))
        {
            dlls.Add(new DllArtifact(
                Path: $@"C:\Test\{id}\bin\Win64_Shipping_Client\{dllName}",
                FileName: dllName!,
                Sha256: Guid.NewGuid().ToString("N"),
                AssemblyName: Path.GetFileNameWithoutExtension(dllName),
                AssemblyVersion: "1.0.0.0",
                ReferencesHarmony: false
            ));
        }

        return new ModuleManifest
        {
            Id = id,
            Name = id,
            Version = "1.0.0",
            RootPath = $@"C:\Test\{id}",
            SubModulePath = $@"C:\Test\{id}\SubModule.xml",
            SourceType = ModSourceType.Local,
            IsOfficial = false,
            IsFramework = false,
            Dependencies = [],
            ExplicitIncompatibilities = [],
            XmlEntities = [],
            Dlls = dlls,
        };
    }

    private static string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "BannerlordRuntimeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
