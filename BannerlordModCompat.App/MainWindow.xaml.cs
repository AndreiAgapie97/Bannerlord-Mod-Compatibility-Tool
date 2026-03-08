using BannerlordModCompat.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BannerlordModCompat.App;

public partial class MainWindow : Window
{
    private readonly CompatibilityAnalyzer _analyzer = new();
    private readonly LauncherDataService _launcherDataService = new();
    private readonly RuntimeSessionLogAnalyzer _runtimeSessionLogAnalyzer = new();
    private readonly RuntimeEvidenceCorrelator _runtimeEvidenceCorrelator = new();
    private readonly ObservableCollection<FindingRow> _visibleFindings = [];
    private readonly ObservableCollection<LoadOrderRow> _loadOrderRows = [];
    private readonly List<LoadOrderRow> _allLoadOrderRows = [];
    private readonly ObservableCollection<string> _loadOrderMoveRows = [];
    private readonly ObservableCollection<string> _loadOrderInactiveRows = [];
    private readonly ObservableCollection<RuntimeSessionRow> _runtimeSessionRows = [];
    private readonly ObservableCollection<RuntimeLogRow> _runtimeLogRows = [];
    private readonly ObservableCollection<RuntimeModuleRow> _runtimeModuleRows = [];
    private readonly ObservableCollection<RuntimeChainRow> _runtimeChainRows = [];
    private readonly ObservableCollection<PlayerRuntimeSessionCardRow> _playerRuntimeSessionCardRows = [];
    private readonly ObservableCollection<PlayerRuntimeSummaryRow> _playerRuntimeSummaryRows = [];
    private readonly List<RuntimeSessionRow> _allRuntimeSessionRows = [];
    private readonly List<RuntimeLogRow> _allRuntimeLogRows = [];
    private readonly List<RuntimeModuleRow> _allRuntimeModuleRows = [];
    private readonly List<RuntimeChainRow> _allRuntimeChainRows = [];
    private readonly ObservableCollection<string> _nextStepRows = [];
    private readonly ObservableCollection<string> _isolationCandidateRows = [];
    private readonly ObservableCollection<IsolationStepRow> _isolationStepRows = [];
    private readonly ObservableCollection<PriorityQueueRow> _priorityQueueRows = [];
    private readonly ObservableCollection<CriticalDrawerRow> _criticalRuntimeDrawerRows = [];
    private readonly List<string> _isolationCandidates = [];
    private readonly List<string> _isolationFoundationModules = [];
    private readonly List<FindingRow> _allFindings = [];
    private readonly List<FindingRow> _riskTopDriverRows = [];

    private ScanReport? _lastReport;
    private bool _isBusy;
    private bool _controlsCollapsed;
    private bool _suppressUiPreferencePersist;
    private UiPreferences _uiPreferences = new();
    private GridLength _controlsExpandedWidth = new(360);
    private GridLength _findingsDrawerWidth = new(430);
    private readonly DispatcherTimer _progressSmoother;
    private int _targetProgressPercent;
    private int _displayedProgressPercent;
    private DateTime _busyStartedUtc = DateTime.MinValue;
    private DateTime _lastProgressUpdateUtc = DateTime.MinValue;
    private DateTime _lastSoftProgressBumpUtc = DateTime.MinValue;
    private bool _runtimeForensicsFilterSyncInProgress;
    private bool _compactFindingsLayout;
    private bool _findingsControlsCollapsed;
    private bool _workflowTriageTouched;
    private bool _workflowFixTouched;
    private bool _workflowValidateTouched;
    private readonly DispatcherTimer _liveRuntimeDebounceTimer;
    private readonly DispatcherTimer _liveRuntimeHeartbeatTimer;
    private FileSystemWatcher? _liveRuntimeLogWatcher;
    private bool _liveRuntimePollInFlight;
    private bool _liveRuntimeWatchEnabled;
    private bool _liveRuntimeRefreshPending;
    private DateTime _liveRuntimeLastRefreshUtc = DateTime.MinValue;
    private DateTime _liveRuntimeLastEventUtc = DateTime.MinValue;
    private string? _liveRuntimeLogsRoot;
    private string _lastLiveRuntimeHeartbeatStatus = string.Empty;
    private IsolationPendingStep? _isolationPendingStep;
    private int _isolationIteration;
    private bool _onboardingTipsDismissed;
    private bool _playerValidateRuntimeEvidenceExpanded;
    private string _lastValidationGoalText = "-";
    private string _lastValidationModulesText = "-";
    private const string UnknownRuntimeSessionKey = "session-unknown";
    private const int SoftProgressCeiling = 96;
    private const string UiPreferencesFileName = "ui-preferences.json";
    public MainWindow()
    {
        InitializeComponent();
        _suppressUiPreferencePersist = true;
        _uiPreferences = LoadUiPreferences();

        GridFindings.ItemsSource = _visibleFindings;
        GridLoadOrderTable.ItemsSource = _loadOrderRows;
        ListLoadOrderMoves.ItemsSource = _loadOrderMoveRows;
        ListLoadOrderInactiveModules.ItemsSource = _loadOrderInactiveRows;
        ListPlayerRuntimeRuns.ItemsSource = _playerRuntimeSessionCardRows;
        GridPlayerRuntimeSuggestions.ItemsSource = _playerRuntimeSummaryRows;
        GridPlayerRuntimeLogDetails.ItemsSource = _runtimeLogRows;
        GridPlayerRuntimeModuleDetails.ItemsSource = _runtimeModuleRows;
        ListNextSteps.ItemsSource = _nextStepRows;
        ListIsolationCandidates.ItemsSource = _isolationCandidateRows;
        GridIsolationSteps.ItemsSource = _isolationStepRows;
        ListPriorityQueue.ItemsSource = _priorityQueueRows;
        ListCriticalRuntimeDrawer.ItemsSource = _criticalRuntimeDrawerRows;

        InitializeFilterControls();
        InitializePriorityQueue();
        InitializeCriticalRuntimeDrawer();
        InitializePlayerLayout();
        InitializeFindingsDensityControls();

        _progressSmoother = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(45),
        };
        _progressSmoother.Tick += ProgressSmoother_Tick;

        _liveRuntimeDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900),
        };
        _liveRuntimeDebounceTimer.Tick += LiveRuntimeDebounceTimer_Tick;

        _liveRuntimeHeartbeatTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _liveRuntimeHeartbeatTimer.Tick += LiveRuntimeHeartbeatTimer_Tick;

        Closed += (_, _) => StopLiveRuntimeWatchInfrastructure();
        Loaded += MainWindow_Loaded;
        ResetIsolationWorkflow("Build an isolation plan from a selected finding.");
        UpdateLiveRuntimeWatchButtonState();
        UpdateRuntimeForensicsActionState();
        UpdateIsolationWorkflowButtons();
        UpdateCriticalFocusPanel(report: null);
        ApplyDetailMode(advanced: false);
        UpdateHeroRiskChips(criticalCount: 0, highCount: 0, runtimeCount: 0);
        ResetRiskScorePanel();
        UpdateFindingsSeveritySnapshot();
        UpdateActiveFindingsFiltersSummary();
        UpdateFindingsSelectionSummary(selected: null);
        UpdateQuickFilterChips();
        UpdateFindingDetailActionState(hasSelection: false);
        UpdateWorkflowRail();
        UpdateOnboardingTip();
        _suppressUiPreferencePersist = false;
        ApplyFocusedPlayerLayout();
        PersistUiPreferences();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (MainTabs.SelectedItem is null)
        {
            MainTabs.SelectedItem = TabFindings;
        }

        UpdateOnboardingTip();
        ApplyFocusedPlayerLayout();
        ApplyValidatePlayerSurface();
    }

    private async void BtnRunScan_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        bool succeeded = false;
        try
        {
            SetBusy(true, "Scanning installed modules...");
            ScanOptions options = BuildOptions(autoApply: false);
            Progress<ScanProgressUpdate> progress = new(UpdateScanProgress);
            ScanReport report = await Task.Run(() => _analyzer.Analyze(options, progress));

            ScanReport? previous = _lastReport;
            PopulateReport(report, previous, runtimeEvidenceRequested: false);
            _lastReport = report;
            _workflowTriageTouched = false;
            _workflowFixTouched = false;
            _workflowValidateTouched = false;
            UpdateWorkflowRail();
            succeeded = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Scan failed: {ex.Message}", "Scan Error", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Scan failed.");
        }
        finally
        {
            SetBusy(false, completedSuccessfully: succeeded);
        }
    }

    private async void BtnApplyLoadOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        MessageBoxResult confirmation = MessageBox.Show(
            this,
            "Apply suggested load order to LauncherData.xml? A backup will be created.",
            "Confirm Load Order Apply",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        bool succeeded = false;
        try
        {
            SetBusy(true, "Applying suggested load order...");
            if (_lastReport is null)
            {
                MessageBox.Show(this, "Run a scan first.", "No Report", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            LoadOrderRecommendation recommendation = PlayerLoadOrderProjectionBuilder
                .BuildEnabledSingleplayerProjection(_lastReport.LoadOrder)
                .Recommendation;
            if (recommendation.Moves.Count == 0)
            {
                SetStatus("No enabled-module load-order changes are inferred for the current profile.");
                SelectValidateTab(expandRuntimeEvidence: false);

                MessageBox.Show(
                    this,
                    "No order changes are currently inferred for the enabled profile. Validate the current stack in game instead.",
                    "No Changes Needed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                succeeded = true;
                return;
            }

            ScanOptions options = BuildOptions(autoApply: false);
            List<string> applyWarnings = [];
            bool applied = await Task.Run(() => _launcherDataService.TryApplySuggestedOrder(
                options.LauncherDataPath,
                recommendation.SuggestedOrder,
                applyWarnings));
            if (!applied)
            {
                string warningText = applyWarnings.Count == 0
                    ? "No additional warning text was returned."
                    : string.Join(Environment.NewLine, applyWarnings.Take(6));
                MessageBox.Show(this, $"Load order apply failed.{Environment.NewLine}{Environment.NewLine}{warningText}", "Apply Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Load order apply failed.");
                return;
            }

            Progress<ScanProgressUpdate> progress = new(UpdateScanProgress);
            ScanReport refreshed = await Task.Run(() => _analyzer.Analyze(options, progress));
            ScanReport report = refreshed with
            {
                Warnings = refreshed.Warnings
                    .Concat(applyWarnings)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(350)
                    .ToList(),
            };

            ScanReport? previous = _lastReport;
            PopulateReport(report, previous, runtimeEvidenceRequested: false);
            _lastReport = report;
            _workflowFixTouched = true;
            UpdateWorkflowRail();
            MessageBox.Show(this, "Load order apply completed. Review warnings for backup path details.", "Apply Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            succeeded = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Load order apply failed: {ex.Message}", "Apply Error", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Load order apply failed.");
        }
        finally
        {
            SetBusy(false, completedSuccessfully: succeeded);
        }
    }

    private async void BtnCollectRuntimeEvidence_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        bool succeeded = false;
        try
        {
            SetBusy(true, "Collecting runtime evidence from Harmony logs...");
            ScanOptions options = BuildOptions(autoApply: false);
            Progress<ScanProgressUpdate> progress = new(UpdateScanProgress);
            ScanReport report = await Task.Run(() => _analyzer.Analyze(options, progress));

            ScanReport? previous = _lastReport;
            PopulateReport(report, previous, runtimeEvidenceRequested: true);
            _lastReport = report;
            _workflowValidateTouched = true;
            UpdateWorkflowRail();
            succeeded = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Runtime evidence scan failed: {ex.Message}", "Runtime Evidence Error", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Runtime evidence scan failed.");
        }
        finally
        {
            SetBusy(false, completedSuccessfully: succeeded);
        }
    }

    private void BtnExportJson_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null)
        {
            MessageBox.Show(this, "Run a scan first.", "No Report", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SaveFileDialog dialog = new()
        {
            Filter = "JSON file (*.json)|*.json",
            FileName = $"bannerlord-report-{DateTime.Now:yyyyMMdd-HHmmss}.json",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ReportExporter.ExportJson(_lastReport, dialog.FileName);
            SetStatus($"JSON export saved: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"JSON export failed: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnExportMd_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null)
        {
            MessageBox.Show(this, "Run a scan first.", "No Report", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SaveFileDialog dialog = new()
        {
            Filter = "Markdown file (*.md)|*.md",
            FileName = $"bannerlord-report-{DateTime.Now:yyyyMMdd-HHmmss}.md",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ReportExporter.ExportMarkdown(_lastReport, dialog.FileName);
            SetStatus($"Markdown export saved: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Markdown export failed: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Filters_Changed(object sender, EventArgs e) => ApplyFilters();

    private void LoadOrderFilter_Changed(object sender, RoutedEventArgs e) => ApplyLoadOrderFilter();

    private void BtnPlayerSettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (BtnPlayerSettingsMenu.ContextMenu is not ContextMenu menu)
        {
            return;
        }

        UpdatePlayerSettingsHeaderState();
        menu.PlacementTarget = BtnPlayerSettingsMenu;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void MenuToggleControlsPanel_Click(object sender, RoutedEventArgs e)
    {
        BtnToggleControls_Click(sender, e);
    }

    private void BtnQuickCriticalChip_Click(object sender, RoutedEventArgs e)
    {
        if (_allFindings.Count == 0)
        {
            SetStatus("Run a scan first.");
            return;
        }

        ComboPerspectiveFilter.SelectedItem = "All Findings";
        ComboSeverityFilter.SelectedItem = "Critical";
        ComboCategoryFilter.SelectedItem = "All";
        TxtSearch.Text = string.Empty;
        ApplyFilters();
        MainTabs.SelectedItem = TabFindings;
        _workflowTriageTouched = true;
        UpdateWorkflowRail();
        SetStatus($"Critical-only quick filter: {_visibleFindings.Count} finding(s).");
    }

    private void BtnQuickHighChip_Click(object sender, RoutedEventArgs e)
    {
        if (_allFindings.Count == 0)
        {
            SetStatus("Run a scan first.");
            return;
        }

        ComboPerspectiveFilter.SelectedItem = "Gameplay Stability (Recommended)";
        ComboSeverityFilter.SelectedItem = "High";
        ComboCategoryFilter.SelectedItem = "All";
        TxtSearch.Text = string.Empty;
        ApplyFilters();
        MainTabs.SelectedItem = TabFindings;
        _workflowTriageTouched = true;
        UpdateWorkflowRail();
        SetStatus($"High-risk quick filter: {_visibleFindings.Count} finding(s).");
    }

    private void BtnQuickRuntimeChip_Click(object sender, RoutedEventArgs e)
    {
        if (_allFindings.Count == 0)
        {
            SetStatus("Run a scan first.");
            return;
        }

        ComboPerspectiveFilter.SelectedItem = "Runtime-Confirmed First";
        ComboSeverityFilter.SelectedItem = "All";
        ComboCategoryFilter.SelectedItem = "All";
        TxtSearch.Text = string.Empty;
        ApplyFilters();
        MainTabs.SelectedItem = TabFindings;
        _workflowTriageTouched = true;
        UpdateWorkflowRail();
        SetStatus($"Runtime-focused quick filter: {_visibleFindings.Count} finding(s).");
    }

    private void BtnFocusPriorityQueueSelection_Click(object sender, RoutedEventArgs e)
    {
        TryFocusPriorityQueueSelection();
    }

    private void ListPriorityQueue_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        TryFocusPriorityQueueSelection();
    }

    private void TryFocusPriorityQueueSelection()
    {
        if (ListPriorityQueue.SelectedItem is not PriorityQueueRow selected || selected.Source is null)
        {
            SetStatus("Select a priority queue item first.");
            return;
        }

        FocusFindingRow(selected.Source, $"Focused priority queue item: {selected.Source.PlayerHeadline} ({selected.Source.Severity}).");
    }

    private void BtnFocusCriticalRuntimeSelection_Click(object sender, RoutedEventArgs e)
    {
        TryFocusCriticalRuntimeDrawerSelection();
    }

    private void ListCriticalRuntimeDrawer_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        TryFocusCriticalRuntimeDrawerSelection();
    }

    private void TryFocusCriticalRuntimeDrawerSelection()
    {
        if (ListCriticalRuntimeDrawer.SelectedItem is not CriticalDrawerRow selected || selected.Source is null)
        {
            SetStatus("Select a critical runtime drawer item first.");
            return;
        }

        FocusFindingRow(selected.Source, $"Focused blocker: {selected.Source.PlayerHeadline} ({selected.Source.Severity}).");
    }

    private void BtnFilterCriticalRuntimeModule_Click(object sender, RoutedEventArgs e)
    {
        if (ListCriticalRuntimeDrawer.SelectedItem is not CriticalDrawerRow selected || selected.Source is null)
        {
            SetStatus("Select a critical runtime drawer item first.");
            return;
        }

        string moduleId = selected.ModuleFocus;
        if (string.IsNullOrWhiteSpace(moduleId) || moduleId.Equals("module-set", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Selected blocker has no concrete module focus.");
            return;
        }

        ComboPerspectiveFilter.SelectedItem = "All Findings";
        ComboConfidenceFloor.SelectedItem = "0%";
        ComboSeverityFilter.SelectedItem = "Actionable (Critical+High)";
        ComboCategoryFilter.SelectedItem = "All";
        TxtSearch.Text = moduleId;
        ApplyFilters();
        MainTabs.SelectedItem = TabFindings;

        FindingRow? preferred = _visibleFindings.FirstOrDefault(f => ReferenceEquals(f, selected.Source))
            ?? _visibleFindings.FirstOrDefault(f => f.Source.ModuleIds.Contains(moduleId, StringComparer.OrdinalIgnoreCase));
        if (preferred is not null)
        {
            GridFindings.SelectedItem = preferred;
            GridFindings.ScrollIntoView(preferred);
        }

        _workflowTriageTouched = true;
        UpdateWorkflowRail();
        SetStatus($"Module focus filter applied: {moduleId} ({_visibleFindings.Count} finding(s)).");
    }

    private void BtnPresetCrashTriage_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null)
        {
            SetStatus("Run a scan first to use presets.");
            return;
        }

        ComboPerspectiveFilter.SelectedItem = "Runtime-Confirmed First";
        ComboSeverityFilter.SelectedItem = "Actionable (Critical+High)";
        ComboCategoryFilter.SelectedItem = "All";
        TxtSearch.Text = string.Empty;
        ApplyFilters();
        MainTabs.SelectedItem = TabFindings;
        _workflowTriageTouched = true;
        UpdateWorkflowRail();
        TxtPresetSummary.Text = "Crash Triage preset: runtime + critical/high focus activated.";
        SetStatus("Preset applied: Crash Triage.");
    }

    private void BtnPresetLoadOrderAudit_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null)
        {
            SetStatus("Run a scan first to use presets.");
            return;
        }

        MainTabs.SelectedItem = TabLoadOrder;
        TxtPresetSummary.Text = "Load Order Audit preset: review Do These Moves, then verify Exact Slots.";
        SetStatus("Preset applied: Load Order Audit.");
    }

    private void BtnPresetRuntimeValidation_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null)
        {
            SetStatus("Run a scan first to use presets.");
            return;
        }

        ComboPerspectiveFilter.SelectedItem = "Runtime-Confirmed First";
        ComboSeverityFilter.SelectedItem = "All";
        ComboCategoryFilter.SelectedItem = "All";
        TxtSearch.Text = string.Empty;
        ApplyFilters();
        MainTabs.SelectedItem = TabValidate;
        _workflowValidateTouched = true;
        UpdateWorkflowRail();
        TxtPresetSummary.Text = "Runtime Validation preset: validate the stack in game, then open Game Runs only if needed.";
        SetStatus("Preset applied: Runtime Validation.");
    }

    private void BtnFocusRiskDriver_Click(object sender, RoutedEventArgs e)
    {
        if (_riskTopDriverRows.Count == 0)
        {
            SetStatus("No risk driver is available yet. Run a scan first.");
            return;
        }

        FocusFindingRow(_riskTopDriverRows[0], $"Focused top risk driver: {_riskTopDriverRows[0].PlayerHeadline} ({_riskTopDriverRows[0].Severity}).");
    }

    private void FocusFindingRow(FindingRow row, string status)
    {
        ComboPerspectiveFilter.SelectedItem = "All Findings";
        ComboConfidenceFloor.SelectedItem = "0%";
        ComboSeverityFilter.SelectedItem = "All";
        ComboCategoryFilter.SelectedItem = "All";
        TxtSearch.Text = string.Empty;
        ApplyFilters();
        MainTabs.SelectedItem = TabFindings;
        GridFindings.SelectedItem = row;
        GridFindings.ScrollIntoView(row);
        _workflowTriageTouched = true;
        UpdateWorkflowRail();
        SetStatus(status);
    }

    private void BtnDismissOnboardingTip_Click(object sender, RoutedEventArgs e)
    {
        _onboardingTipsDismissed = true;
        UpdateOnboardingTip();
        SetStatus("Onboarding tip dismissed.");
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, MainTabs) || !ReferenceEquals(e.Source, MainTabs))
        {
            return;
        }

        UpdateOnboardingTip();
        ApplyFocusedPlayerLayout();
        ApplyValidatePlayerSurface();
    }

    private void ChkCompactFindingsLayout_Changed(object sender, RoutedEventArgs e)
    {
        bool compact = ChkCompactFindingsLayout.IsChecked == true;
        ApplyFindingsDensity(compact);
        if (_lastReport is not null)
        {
            SetStatus(compact
                ? "Compact findings layout enabled."
                : "Comfort layout enabled.");
        }
        PersistUiPreferences();
    }

    private void BtnToggleFindingsControls_Click(object sender, RoutedEventArgs e)
    {
        SetFindingsControlsCollapsed(!_findingsControlsCollapsed, announce: true);
    }

    private void BtnClearFindingSelection_Click(object sender, RoutedEventArgs e)
    {
        GridFindings.UnselectAll();
        GridFindings.SelectedItem = null;
        ResetDetailPanel("Select a finding to view details.");
        SetStatus("Finding selection cleared.");
    }

    private void SelectValidateTab(bool expandRuntimeEvidence)
    {
        _playerValidateRuntimeEvidenceExpanded = expandRuntimeEvidence && HasRuntimeEvidence();
        MainTabs.SelectedItem = TabValidate;
        ApplyValidatePlayerSurface();
    }

    private void SetFindingsControlsCollapsed(bool collapsed, bool announce)
    {
        _findingsControlsCollapsed = collapsed;
        BorderFindingsToolbar.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        BtnToggleFindingsControls.Content = collapsed
            ? "Show Findings Controls"
            : "Hide Findings Controls";
        UpdateFindingsHintText();

        if (announce && _lastReport is not null)
        {
            SetStatus(collapsed
                ? "Findings controls hidden. Table-first triage is active."
                : "Findings controls shown. Filters and runtime drawers are available.");
        }
    }

    private void BtnWorkflowGoFindings_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = TabFindings;
        SetStatus("Workflow navigation: Findings.");
    }

    private void BtnWorkflowGoLoadOrder_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = TabLoadOrder;
        SetStatus("Workflow navigation: Load Order.");
    }

    private void BtnWorkflowGoIsolation_Click(object sender, RoutedEventArgs e)
    {
        if (GridFindings.SelectedItem is FindingRow selected)
        {
            StartIsolationWorkflow(selected.Source, $"selected finding ({selected.PlayerHeadline})");
        }
        else
        {
            SelectValidateTab(expandRuntimeEvidence: false);
            SetStatus("Workflow navigation: Validate.");
        }
    }

    private void BtnWorkflowGoRuntime_Click(object sender, RoutedEventArgs e)
    {
        SelectValidateTab(expandRuntimeEvidence: true);
        SetStatus("Workflow navigation: Validate.");
    }

    private void BtnToggleValidateRuntimeEvidence_Click(object sender, RoutedEventArgs e)
    {
        _playerValidateRuntimeEvidenceExpanded = !_playerValidateRuntimeEvidenceExpanded;
        ApplyValidatePlayerSurface();
    }

    private void BtnValidateShowEvidence_Click(object sender, RoutedEventArgs e)
    {
        _playerValidateRuntimeEvidenceExpanded = true;
        ApplyValidatePlayerSurface();
        SetStatus("Validate: game runs opened.");
    }

    private void BtnValidateAnotherFinding_Click(object sender, RoutedEventArgs e)
    {
        BtnResetIsolation_Click(sender, e);
        SetStatus("Validation reset. Choose another finding to validate.");
    }

    private void BtnToggleControls_Click(object sender, RoutedEventArgs e)
    {
        SetControlsCollapsed(!_controlsCollapsed);
        PersistUiPreferences();
    }

    private void SetControlsCollapsed(bool collapsed)
    {
        if (collapsed == _controlsCollapsed)
        {
            return;
        }

        if (collapsed)
        {
            _controlsExpandedWidth = ColControlsPanel.ActualWidth > 120
                ? new GridLength(ColControlsPanel.ActualWidth)
                : new GridLength(320);
            ColControlsPanel.MinWidth = 0;
            ColControlsPanel.Width = new GridLength(0);
            ColControlsSplitter.Width = new GridLength(0);
            _controlsCollapsed = true;
            UpdatePlayerSettingsHeaderState();
            return;
        }

        ColControlsPanel.Width = _controlsExpandedWidth;
        ColControlsPanel.MinWidth = 240;
        ColControlsSplitter.Width = new GridLength(8);
        _controlsCollapsed = false;
        UpdatePlayerSettingsHeaderState();
    }

    private void ApplyFocusedPlayerLayout()
    {
        ApplyLoadOrderFocusedLayout();
        ApplyRuntimeFocusedLayout();
        ApplyFindingsDetailHostLayout();
    }

    private void UpdateFindingDetailActionState(bool hasSelection)
    {
        bool enabled = !_isBusy && hasSelection;
        BtnClearFindingSelection.IsEnabled = enabled;
        BtnDetailOpenLoadOrder.IsEnabled = enabled;
        BtnDetailOpenRuntime.IsEnabled = enabled;
        BtnDetailOpenIsolation.IsEnabled = enabled;
    }

    private void ApplyFindingsDetailHostLayout()
    {
        bool hasSelection = GridFindings.SelectedItem is FindingRow;

        Grid.SetColumn(GridFindings, 0);
        Grid.SetColumnSpan(GridFindings, 1);
        Grid.SetRow(GridFindings, 2);
        Grid.SetRowSpan(GridFindings, 3);

        Grid.SetColumn(BorderFindingsDetail, 2);
        Grid.SetColumnSpan(BorderFindingsDetail, 1);
        Grid.SetRow(BorderFindingsDetail, 2);
        Grid.SetRowSpan(BorderFindingsDetail, 3);
        FindingsRowSplitter.Visibility = Visibility.Collapsed;
        RowFindingsDetail.Height = new GridLength(0);

        if (!hasSelection)
        {
            if (BorderFindingsDetail.ActualWidth > 280)
            {
                _findingsDrawerWidth = new GridLength(BorderFindingsDetail.ActualWidth);
            }

            ColFindingsDrawerSplitter.Width = new GridLength(0);
            ColFindingsDrawer.Width = new GridLength(0);
            FindingsColumnSplitter.Visibility = Visibility.Collapsed;
            BorderFindingsDetail.Visibility = Visibility.Collapsed;
            BorderFindingsDetail.Margin = new Thickness(0);
            return;
        }

        ColFindingsDrawerSplitter.Width = new GridLength(8);
        ColFindingsDrawer.Width = _findingsDrawerWidth.Value > 0
            ? _findingsDrawerWidth
            : new GridLength(430);
        FindingsColumnSplitter.Visibility = Visibility.Visible;
        BorderFindingsDetail.Visibility = Visibility.Visible;
        BorderFindingsDetail.Margin = new Thickness(8, 0, 0, 0);
    }

    private bool IsLoadOrderFocusedSurfaceActive()
    {
        return ReferenceEquals(GetActiveMainTab(), TabLoadOrder);
    }

    private bool IsValidateTabActive()
    {
        return ReferenceEquals(GetActiveMainTab(), TabValidate);
    }

    private void ApplyLoadOrderFocusedLayout()
    {
        BorderLoadOrderFocusSummary.Visibility = Visibility.Visible;
        GridLoadOrderFocusedHost.Visibility = Visibility.Visible;
        ColLoadOrderPlan.Width = new GridLength(430);
        ColLoadOrderPlan.MinWidth = 340;
        UpdateLoadOrderGuidance();
    }

    private void ApplyRuntimeFocusedLayout()
    {
        ApplyValidatePlayerSurface();
    }

    private bool HasRuntimeEvidence()
    {
        return _allRuntimeChainRows.Any(row =>
            !string.Equals(row.Finding, "No runtime-linked findings.", StringComparison.OrdinalIgnoreCase));
    }

    private bool ShouldShowValidateIsolationSection()
    {
        if (_isolationPendingStep?.Mode == IsolationStepMode.BinaryIsolation)
        {
            return true;
        }

        return _isolationStepRows.Any(row =>
            !row.DisableSet.StartsWith("None", StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyValidatePlayerSurface()
    {
        bool validateTabSelected = ReferenceEquals(GetActiveMainTab(), TabValidate);
        int playerLoadOrderMoves = _allLoadOrderRows.Count(r => r.IsChanged);

        TxtRuntimeTabHeader.Text = "Validate";

        BorderValidateCurrentCard.Visibility = Visibility.Visible;
        BorderValidateRuntimeHeader.Visibility = Visibility.Collapsed;
        BorderValidateRuntimeEmptyState.Visibility = Visibility.Collapsed;

        ValidatePlayerSurfaceState state = BuildValidatePlayerSurfaceState(validateTabSelected, playerLoadOrderMoves);
        TxtRuntimeActionBanner.Text = state.BannerText;
        TxtRuntimeForensicsSummary.Text = state.StageSummary;
        TxtValidateStageSummary.Text = state.StageSummary;
        TxtValidateProfileHint.Text = state.ProfileHint;
        TxtValidateCurrentCardTitle.Text = state.Stage switch
        {
            ValidatePlayerStage.ChooseTarget => "Pick One Issue To Check",
            ValidatePlayerStage.TestTogether => "Try Current Stack",
            ValidatePlayerStage.Compatible => "This Looked Fine",
            _ => "Validate In Game",
        };
        TxtValidateStageSummary.Visibility = state.ShowTestTogether ? Visibility.Visible : Visibility.Collapsed;
        TxtValidateProfileHint.Visibility = state.ShowChooseTarget ? Visibility.Visible : Visibility.Collapsed;
        TxtValidateCompatibleSummary.Text = state.CompatibleSummary;
        TxtValidateCompatibleModules.Text = _lastValidationModulesText;
        TxtValidateCompatibleGoal.Text = _lastValidationGoalText;
        TxtValidateIsolatingSummary.Text = "The problem showed up. Split the suspect mods into small groups below.";

        BorderValidateCurrentCard.Visibility = !state.ShowIsolating ? Visibility.Visible : Visibility.Collapsed;
        PanelValidateChooseTarget.Visibility = state.ShowChooseTarget ? Visibility.Visible : Visibility.Collapsed;
        PanelValidateTestTogether.Visibility = state.ShowTestTogether ? Visibility.Visible : Visibility.Collapsed;
        PanelValidateCompatible.Visibility = state.ShowCompatible ? Visibility.Visible : Visibility.Collapsed;
        PanelValidateIsolating.Visibility = Visibility.Collapsed;

        BtnValidateUseSelectedFinding.IsEnabled = !_isBusy && GridFindings.SelectedItem is FindingRow;
        BtnValidateResetCurrent.Visibility = state.ShowReset ? Visibility.Visible : Visibility.Collapsed;
        BtnValidateResetCurrent.IsEnabled = !_isBusy;
        BtnValidateAnotherFinding.IsEnabled = !_isBusy;
        BorderValidateChooseTargetRuntimeAssist.Visibility =
            state.ShowChooseTarget && state.ShowUseTopRuntime && !state.ShowRuntimeEvidence
                ? Visibility.Visible
                : Visibility.Collapsed;
        BtnValidateChooseTargetShowEvidence.IsEnabled = !_isBusy && state.ShowUseTopRuntime;
        BtnValidateShowEvidence.Visibility =
            state.ShowCompatible && state.ShowUseTopRuntime && !state.ShowRuntimeEvidence
                ? Visibility.Visible
                : Visibility.Collapsed;
        BtnValidateShowEvidence.IsEnabled = !_isBusy && state.ShowUseTopRuntime;

        TxtValidateRuntimeSectionTitle.Text = state.RuntimeSectionTitle;
        TxtValidateRuntimeSectionHint.Text = state.RuntimeSectionHint;
        BtnToggleValidateRuntimeEvidence.Content = state.ShowRuntimeEvidence ? "Hide Game Runs" : "Show Game Runs";
        BtnToggleValidateRuntimeEvidence.IsEnabled = !_isBusy;
        BorderValidateRuntimeHeader.Visibility = state.ShowRuntimeEvidence ? Visibility.Visible : Visibility.Collapsed;
        BorderValidateRuntimeEmptyState.Visibility = state.ShowRuntimeEmptyState ? Visibility.Visible : Visibility.Collapsed;
        BorderPlayerRuntimeWorkbench.Visibility = state.ShowRuntimeWorkbench ? Visibility.Visible : Visibility.Collapsed;
        BtnPlayerRuntimeUseTopWarning.Visibility = state.ShowRuntimeWorkbench && _playerRuntimeSummaryRows.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        BtnPlayerRuntimeUseTopWarning.IsEnabled = !_isBusy && _playerRuntimeSummaryRows.Count > 0;
        RowRuntimeWorkbench.Height = state.ShowRuntimeWorkbench || state.ShowRuntimeEmptyState
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        GroupValidateIsolationEscalation.Visibility = state.ShowIsolationSection ? Visibility.Visible : Visibility.Collapsed;
        RowValidateIsolationEscalation.Height = state.ShowIsolationSection ? GridLength.Auto : new GridLength(0);

        UpdatePlayerRuntimeSelectionCard();
    }

    private void GridLoadOrder_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid)
        {
            return;
        }

        if (grid.SelectedItem is not LoadOrderRow selected)
        {
            UpdateLoadOrderGuidance();
            return;
        }

        TxtLoadOrderFocusSummary.Text =
            $"{selected.ModuleTypeText} module '{selected.ModuleId}': {selected.ActionText}. {selected.WhyText} Exact Slots remains the authoritative enabled-order view.";
    }

    private void UpdateLoadOrderGuidance()
    {
        int movedCount = _allLoadOrderRows.Count(r => r.IsChanged);
        int inactiveCount = _loadOrderInactiveRows.Count;
        TxtLoadOrderPlanHint.Text = movedCount == 0
            ? "No moves are required for the enabled profile. Exact Slots confirms the active order; the next step is Validate."
            : "Do These Moves is the actionable sequence for enabled modules only. Exact Slots is the authoritative enabled-order view.";

        string summary = movedCount == 0
            ? "No order changes inferred for enabled modules. Exact Slots is the source of truth for the active singleplayer profile."
            : $"{movedCount} enabled module(s) need reordering. Exact Slots is the source of truth; Do These Moves is the actionable subset.";
        if (inactiveCount > 0)
        {
            summary += $" {inactiveCount} installed module(s) are currently disabled and not part of this plan.";
        }

        TxtLoadOrderFocusSummary.Text = summary;
    }

    private void PopulateLoadOrderSection(ScanReport report)
    {
        _allLoadOrderRows.Clear();
        _loadOrderMoveRows.Clear();
        _loadOrderInactiveRows.Clear();
        PlayerLoadOrderProjection projection = PlayerLoadOrderProjectionBuilder.BuildEnabledSingleplayerProjection(
            report.LoadOrder,
            report.Modules,
            report.Conflicts);
        LoadOrderRecommendation viewRecommendation = projection.Recommendation;
        foreach (PlayerLoadOrderProjectionRow projectedRow in projection.Rows)
        {
            int suggestedPos = (projectedRow.SuggestedIndex ?? 0) + 1;
            int currentPos = projectedRow.CurrentIndex.HasValue ? projectedRow.CurrentIndex.Value + 1 : 0;
            int delta = projectedRow.CurrentIndex.HasValue && projectedRow.SuggestedIndex.HasValue
                ? suggestedPos - currentPos
                : 0;

            string action = !projectedRow.CurrentIndex.HasValue
                ? $"Enable at #{suggestedPos}"
                : delta < 0
                    ? $"Move up {Math.Abs(delta)}"
                    : delta > 0
                        ? $"Move down {delta}"
                        : "Keep as is";
            string deltaText = !projectedRow.CurrentIndex.HasValue
                ? "New"
                : delta == 0
                    ? "0"
                    : delta < 0
                        ? $"-{Math.Abs(delta)}"
                        : $"+{delta}";

            _allLoadOrderRows.Add(BuildLoadOrderRow(
                projectedRow,
                projectedRow.CurrentIndex.HasValue ? currentPos.ToString() : "-",
                projectedRow.SuggestedIndex.HasValue ? suggestedPos.ToString() : "-",
                deltaText,
                action,
                projectedRow.ReasonSummary,
                projectedRow.IsChanged,
                projectedRow.CurrentIndex.HasValue ? Math.Abs(delta) : suggestedPos));
        }

        foreach (PlayerLoadOrderProjectionRow inactiveRow in projection.InactiveInstalledRows)
        {
            _loadOrderInactiveRows.Add(BuildInactiveModuleDisplay(inactiveRow));
        }

        BorderLoadOrderInactiveModules.Visibility = _loadOrderInactiveRows.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        TxtLoadOrderInactiveSummary.Text = _loadOrderInactiveRows.Count switch
        {
            0 => "Installed But Not Enabled",
            1 => "1 installed module is currently disabled and not part of this plan.",
            _ => $"{_loadOrderInactiveRows.Count} installed modules are currently disabled and not part of this plan.",
        };

        int stepNumber = 1;
        foreach (LoadOrderRow row in _allLoadOrderRows.Where(r => r.IsChanged).Take(120))
        {
            _loadOrderMoveRows.Add($"{stepNumber}. {row.ModuleId}: {row.ActionText} (slot {row.CurrentIndexText} -> {row.SuggestedIndexText})");
            stepNumber++;
        }

        if (_loadOrderMoveRows.Count == 0)
        {
            _loadOrderMoveRows.Add("No moves required for the enabled profile.");
        }

        ApplyLoadOrderFilter();
        UpdateLoadOrderGuidance();
    }

    private LoadOrderRow BuildLoadOrderRow(
        PlayerLoadOrderProjectionRow projectedRow,
        string currentIndexText,
        string suggestedIndexText,
        string deltaText,
        string actionText,
        string whyText,
        bool isChanged,
        int absoluteShift)
    {
        (string moduleTypeText, Brush background, Brush border, Brush foreground) = GetLoadOrderTypeChrome(projectedRow);

        return new LoadOrderRow
        {
            ModuleId = projectedRow.ModuleId,
            ModuleTypeText = moduleTypeText,
            ModuleTypeBadgeBackground = background,
            ModuleTypeBadgeBorder = border,
            ModuleTypeForeground = foreground,
            CurrentIndexText = currentIndexText,
            SuggestedIndexText = suggestedIndexText,
            DeltaText = deltaText,
            ActionText = actionText,
            WhyText = whyText,
            IsOfficial = projectedRow.IsOfficial,
            IsFramework = projectedRow.IsFramework,
            IsCustom = projectedRow.IsCustom,
            IsActionable = isChanged,
            IsInactiveInstalled = projectedRow.IsInactiveInstalled,
            IsChanged = isChanged,
            AbsoluteShift = absoluteShift,
        };
    }

    private (string TypeText, Brush Background, Brush Border, Brush Foreground) GetLoadOrderTypeChrome(
        PlayerLoadOrderProjectionRow projectedRow)
    {
        if (projectedRow.IsOfficial)
        {
            return ("Official", ParseBrush("#223125"), ParseBrush("#6A7B50"), ParseBrush("#E2F0D5"));
        }

        if (projectedRow.IsFramework)
        {
            return ("Framework", ParseBrush("#1E2430"), ParseBrush("#5E708D"), ParseBrush("#DDE7F7"));
        }

        return ("Custom", ParseBrush("#30231A"), ParseBrush("#8B6736"), ParseBrush("#F4E4C6"));
    }

    private static string BuildInactiveModuleDisplay(PlayerLoadOrderProjectionRow row)
    {
        string type = row.IsOfficial
            ? "Official"
            : row.IsFramework
                ? "Framework"
                : "Custom";
        return $"{row.ModuleId} ({type})";
    }

    private RuntimeChainRow? GetSelectedRuntimeChainRow()
    {
        return (GridPlayerRuntimeSuggestions.SelectedItem as PlayerRuntimeSummaryRow)?.Source;
    }

    private RuntimeLogRow? GetSelectedRuntimeLogRow()
    {
        return GridPlayerRuntimeLogDetails.SelectedItem as RuntimeLogRow;
    }

    private RuntimeModuleRow? GetSelectedRuntimeModuleRow()
    {
        return GridPlayerRuntimeModuleDetails.SelectedItem as RuntimeModuleRow;
    }

    private void UpdatePlayerRuntimeSelectionCard()
    {
        // Player-mode runtime warnings are rendered as self-contained cards.
    }

    private void ListPlayerRuntimeRuns_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_runtimeForensicsFilterSyncInProgress)
        {
            return;
        }

        ApplyRuntimeForensicsFilters();
    }

    private void GridPlayerRuntimeSuggestions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateRuntimeForensicsActionState();
        UpdatePlayerRuntimeSelectionCard();
    }

    private void GridPlayerRuntimeSuggestions_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        FocusSelectedRuntimeChainFinding(switchToFindingsTab: true);
    }

    private void BtnPlayerRuntimeCardOpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PlayerRuntimeSummaryRow row })
        {
            return;
        }

        GridPlayerRuntimeSuggestions.SelectedItem = row;
        OpenSelectedRuntimeLogFromSelection();
        e.Handled = true;
    }

    private void BtnPlayerRuntimeCardFocusFinding_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PlayerRuntimeSummaryRow row })
        {
            return;
        }

        GridPlayerRuntimeSuggestions.SelectedItem = row;
        FocusSelectedRuntimeChainFinding(switchToFindingsTab: true);
        e.Handled = true;
    }

    private void GridPlayerRuntimeLogDetails_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_runtimeForensicsFilterSyncInProgress)
        {
            return;
        }

        ApplyRuntimeForensicsFilters();
        UpdatePlayerRuntimeSelectionCard();
    }

    private void GridPlayerRuntimeModuleDetails_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_runtimeForensicsFilterSyncInProgress)
        {
            return;
        }

        ApplyRuntimeForensicsFilters();
        UpdatePlayerRuntimeSelectionCard();
    }

    private void BtnClearRuntimeForensicsFilter_Click(object sender, RoutedEventArgs e)
    {
        if (_runtimeForensicsFilterSyncInProgress)
        {
            return;
        }

        ClearRuntimeForensicsSelections();
        ApplyRuntimeForensicsFilters();
    }

    private void BtnToggleLiveRuntimeWatch_Click(object sender, RoutedEventArgs e)
    {
        if (_liveRuntimeWatchEnabled)
        {
            _liveRuntimeWatchEnabled = false;
            StopLiveRuntimeWatchInfrastructure();
            UpdateLiveRuntimeWatchButtonState();
            SetStatus("Live runtime watch stopped.");
            return;
        }

        if (_lastReport is null)
        {
            SetStatus("Run a scan first before starting live runtime watch.");
            return;
        }

        _liveRuntimeWatchEnabled = true;
        if (!TryStartLiveRuntimeWatchInfrastructure(out string startMessage))
        {
            _liveRuntimeWatchEnabled = false;
            UpdateLiveRuntimeWatchButtonState();
            SetStatus(startMessage);
            return;
        }

        UpdateLiveRuntimeWatchButtonState();
        _ = PollLiveRuntimeWatchAsync();
        _workflowValidateTouched = true;
        UpdateWorkflowRail();
        SetStatus(startMessage);
    }

    private void GridRuntimeFocusedLogs_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedRuntimeLogFromSelection();
    }

    private void BtnJumpToCritical_Click(object sender, RoutedEventArgs e)
    {
        if (!TryFocusFirstCriticalFinding(applyCriticalFilter: false, switchToFindingsTab: true))
        {
            SetStatus("No critical finding is available in the current scan.");
            return;
        }

        _workflowTriageTouched = true;
        UpdateWorkflowRail();
    }

    private void BtnCriticalOnlyView_Click(object sender, RoutedEventArgs e)
    {
        if (!TryFocusFirstCriticalFinding(applyCriticalFilter: true, switchToFindingsTab: true))
        {
            SetStatus("No critical finding is available in the current scan.");
            return;
        }

        _workflowTriageTouched = true;
        UpdateWorkflowRail();
    }

    private void BtnActionableView_Click(object sender, RoutedEventArgs e)
    {
        if (_allFindings.Count == 0)
        {
            SetStatus("Run a scan first to use actionable view.");
            return;
        }

        ComboSeverityFilter.SelectedItem = "Actionable (Critical+High)";
        ComboPerspectiveFilter.SelectedItem = "Gameplay Stability (Recommended)";
        ComboCategoryFilter.SelectedItem = "All";
        TxtSearch.Text = string.Empty;
        ApplyFilters();
        MainTabs.SelectedItem = TabFindings;
        _workflowTriageTouched = true;
        UpdateWorkflowRail();
        SetStatus($"Actionable view active: {_visibleFindings.Count} critical/high finding(s).");
    }

    private void BtnResetFindingsView_Click(object sender, RoutedEventArgs e)
    {
        if (_allFindings.Count == 0)
        {
            SetStatus("No findings are loaded yet.");
            return;
        }

        ComboSeverityFilter.SelectedItem = "All";
        ComboPerspectiveFilter.SelectedItem = "All Findings";
        ComboCategoryFilter.SelectedItem = "All";
        TxtSearch.Text = string.Empty;
        ApplyFilters();
        MainTabs.SelectedItem = TabFindings;
        SetStatus("Findings view reset to all findings.");
    }

    private void BtnStartIsolationFromSelectedFinding_Click(object sender, RoutedEventArgs e)
    {
        if (GridFindings.SelectedItem is not FindingRow selected)
        {
            SetStatus("Select a warning first.");
            return;
        }

        StartIsolationWorkflow(selected.Source, $"selected finding ({selected.PlayerHeadline})");
    }

    private void BtnStartIsolationFromTopRuntime_Click(object sender, RoutedEventArgs e)
    {
        FindingRow? runtimeSeed = _allFindings
            .Where(f => IsIsolationRuntimePriority(f.Source)
                && GetIsolationSeedMode(f.Source) != IsolationSeedMode.Unsupported)
            .OrderByDescending(f => f.Source.Severity)
            .ThenByDescending(f => f.Source.Confidence)
            .FirstOrDefault();
        if (runtimeSeed is null)
        {
            SetStatus("No log warning is available yet.");
            return;
        }

        StartIsolationWorkflow(runtimeSeed.Source, $"runtime-priority finding ({runtimeSeed.Category})");
    }

    private void BtnResetIsolation_Click(object sender, RoutedEventArgs e)
    {
        _playerValidateRuntimeEvidenceExpanded = false;
        ResetIsolationWorkflow("Isolation workflow reset.");
        SetStatus("Isolation workflow reset.");
    }

    private void BtnIsolationIssueGone_Click(object sender, RoutedEventArgs e)
    {
        ApplyIsolationOutcome(issueGone: true);
    }

    private void BtnIsolationIssuePersists_Click(object sender, RoutedEventArgs e)
    {
        ApplyIsolationOutcome(issueGone: false);
    }

    private void GridFindings_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FindingRow? selected = GridFindings.SelectedItem as FindingRow;
        ListEvidence.ItemsSource = null;
        ListImmediateSteps.ItemsSource = null;

        if (selected is null)
        {
            ResetDetailPanel("Select a finding to view details.");
            ApplyValidatePlayerSurface();
            return;
        }

        if (!_isBusy && _lastReport is not null)
        {
            _workflowTriageTouched = true;
            UpdateWorkflowRail();
        }

        TxtDetailCategoryBadge.Text = $"Finding: {selected.Category}";
        TxtDetailSeverityBadge.Text = $"Risk: {selected.Severity}";
        TxtDetailConfidenceBadge.Text = $"How sure: {selected.ConfidenceText}";
        TxtDetailEvidenceBadge.Text = $"How I know: {selected.EvidenceStrength}";
        TxtDetailImpactBadge.Text = $"Main risk: {selected.ImpactRisk}";
        TxtDetailConfidenceInterpretation.Text = BuildConfidenceInterpretation(selected.Source);
        TxtDetailPlayerSummary.Text = BuildMeaningText(selected.Source);
        TxtDetailOutcome.Text = selected.PlayerSymptom;
        TxtDetailRecommendation.Text = selected.QuickFix;
        TxtDetailTechnical.Text = selected.TechnicalCause;
        TxtDetailExecutionChain.Text = selected.ExecutionChain;
        ListImmediateSteps.ItemsSource = BuildImmediateSteps(selected.Source);
        ApplyPriorityTone(selected.Source.Severity);
        UpdateCriticalExplainPanel(selected.Source);
        IReadOnlyList<string> affectedSaves = BuildAffectedSaveDisplayItems(selected.Source);
        GroupDetailAffectedSaves.Visibility = affectedSaves.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupDetailAffectedSaves.Header = affectedSaves.Count > 0
            ? $"Affected Saves ({affectedSaves.Count})"
            : "Affected Saves";
        ListAffectedSaves.ItemsSource = affectedSaves.Count > 0 ? affectedSaves : null;

        ListEvidence.ItemsSource = BuildEvidenceDisplayItems(selected.Source);
        UpdateFindingsSelectionSummary(selected);
        UpdateFindingDetailActionState(hasSelection: true);
        ApplyFindingsDetailHostLayout();
        ApplyValidatePlayerSurface();
    }

    private void DataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        if (sender is not DataGrid grid)
        {
            return;
        }

        if (TryCopySelectedRows(grid))
        {
            e.Handled = true;
        }
    }

    private bool TryCopySelectedRows(DataGrid grid)
    {
        List<object> selected = grid.SelectedItems.Cast<object>().ToList();
        if (selected.Count == 0 && grid.SelectedItem is not null)
        {
            selected.Add(grid.SelectedItem);
        }

        if (selected.Count == 0)
        {
            return false;
        }

        StringBuilder sb = new();
        if (ReferenceEquals(grid, GridFindings))
        {
            sb.AppendLine("Risk\tFinding\tHow Sure\tHow I Know\tMain Risk\tMods\tWhat You'd Notice\tTry This First\tWhy The App Flagged This (Advanced)\tLoad Order Context (Advanced)");
            foreach (FindingRow row in selected.OfType<FindingRow>())
            {
                sb.AppendLine(string.Join('\t',
                    row.Severity,
                    SanitizeForTsv(row.PlayerHeadline),
                    row.ConfidenceText,
                    row.EvidenceStrength,
                    row.ImpactRisk,
                    SanitizeForTsv(row.Modules),
                    SanitizeForTsv(row.PlayerSymptom),
                    SanitizeForTsv(row.QuickFix),
                    SanitizeForTsv(row.TechnicalCause),
                    SanitizeForTsv(row.ExecutionChain)));
            }
        }
        else if (ReferenceEquals(grid, GridLoadOrderTable))
        {
            sb.AppendLine("Type\tModule\tCurrent #\tSuggested #\tChange\tAction\tWhy It Matters");
            foreach (LoadOrderRow row in selected.OfType<LoadOrderRow>())
            {
                sb.AppendLine(string.Join('\t',
                    row.ModuleTypeText,
                    row.ModuleId,
                    row.CurrentIndexText,
                    row.SuggestedIndexText,
                    row.DeltaText,
                    SanitizeForTsv(row.ActionText),
                    SanitizeForTsv(row.WhyText)));
            }
        }
        else if (ReferenceEquals(grid, GridPlayerRuntimeLogDetails))
        {
            sb.AppendLine("Signal\tSession\tLog File\tFindings\tPath");
            foreach (RuntimeLogRow row in selected.OfType<RuntimeLogRow>())
            {
                sb.AppendLine(string.Join('\t',
                    SanitizeForTsv(row.SignalType),
                    SanitizeForTsv(BuildRuntimeSessionLabel(row.SessionKey)),
                    SanitizeForTsv(row.FileName),
                    row.FindingHits,
                    SanitizeForTsv(row.Path)));
            }
        }
        else if (ReferenceEquals(grid, GridPlayerRuntimeModuleDetails))
        {
            sb.AppendLine("Module\tCorrelated Findings\tSessions\tMax Risk\tTop Categories");
            foreach (RuntimeModuleRow row in selected.OfType<RuntimeModuleRow>())
            {
                sb.AppendLine(string.Join('\t',
                    SanitizeForTsv(row.ModuleId),
                    row.CorrelatedFindings,
                    SanitizeForTsv(row.SessionCoverage),
                    SanitizeForTsv(row.MaxRisk),
                    SanitizeForTsv(row.TopCategories)));
            }
        }
        else if (ReferenceEquals(grid, GridIsolationSteps))
        {
            sb.AppendLine("Step\tTurn Off\tFoundations Kept\tOther Suspects On\tOutcome\tRemaining");
            foreach (IsolationStepRow row in selected.OfType<IsolationStepRow>())
            {
                sb.AppendLine(string.Join('\t',
                    SanitizeForTsv(row.Step),
                    SanitizeForTsv(row.DisableSet),
                    SanitizeForTsv(row.FoundationSet),
                    SanitizeForTsv(row.KeepSet),
                    SanitizeForTsv(row.Outcome),
                    SanitizeForTsv(row.RemainingAfter)));
            }
        }
        else
        {
            return false;
        }

        if (sb.Length == 0)
        {
            return false;
        }

        try
        {
            Clipboard.SetText(sb.ToString());
        }
        catch (Exception ex)
        {
            SetStatus($"Copy failed: {ex.Message}");
            return false;
        }

        SetStatus($"Copied {selected.Count} selected row(s) to clipboard.");
        return true;
    }

    private void ResetDetailPanel(string summaryText)
    {
        TxtDetailCategoryBadge.Text = "Finding: -";
        TxtDetailSeverityBadge.Text = "Risk: -";
        TxtDetailConfidenceBadge.Text = "How sure: -";
        TxtDetailEvidenceBadge.Text = "How I know: -";
        TxtDetailImpactBadge.Text = "Main risk: -";
        TxtDetailPriority.Text = "Priority: Select a finding";
        TxtDetailConfidenceInterpretation.Text = "Confidence explanation appears after selecting a finding.";
        TxtDetailPlayerSummary.Text = summaryText;
        TxtDetailOutcome.Text = "-";
        TxtDetailRecommendation.Text = "-";
        TxtDetailTechnical.Text = "-";
        TxtDetailExecutionChain.Text = "-";
        ExpanderCriticalExplain.Visibility = Visibility.Collapsed;
        ExpanderCriticalExplain.IsExpanded = false;
        TxtCriticalExplainSummary.Text = "Select a high-risk finding to see explainability evidence.";
        ListCriticalExplain.ItemsSource = null;
        GroupDetailAffectedSaves.Visibility = Visibility.Collapsed;
        GroupDetailAffectedSaves.Header = "Affected Saves";
        ListAffectedSaves.ItemsSource = null;
        BorderDetailPriority.Background = ParseBrush("#2A231D");
        BorderDetailPriority.BorderBrush = ParseBrush("#6F5530");
        UpdateFindingDetailActionState(hasSelection: false);
        ApplyFindingsDetailHostLayout();
        UpdateFindingsSelectionSummary(selected: null);
    }

    private void ApplyDetailMode(bool advanced)
    {
        Visibility advancedVisibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        BlockDetailTechnicalRow.Visibility = advancedVisibility;
        BlockDetailExecutionRow.Visibility = advancedVisibility;
        GroupDetailEvidence.Visibility = advancedVisibility;
        ColDetailEvidence.Width = advanced
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        ColDetailChecklist.Width = advanced
            ? new GridLength(1.2, GridUnitType.Star)
            : new GridLength(1, GridUnitType.Star);
    }

    private void UpdateWorkflowRail()
    {
        bool hasScan = _lastReport is not null;
        bool hasTriage = hasScan && _workflowTriageTouched;
        bool hasFix = hasScan && _workflowFixTouched;
        bool hasValidate = hasScan && _workflowValidateTouched;

        bool scanActive = !hasScan;
        bool triageActive = hasScan && !hasTriage;
        bool fixActive = hasTriage && !hasFix;
        bool validateActive = hasFix && !hasValidate;

        TxtWorkflowScanState.Text = hasScan
            ? $"1) Scan: done ({_lastReport!.Conflicts.Count} findings from {_lastReport.Modules.Count} modules)."
            : "1) Scan: waiting for first scan.";
        TxtWorkflowTriageState.Text = !hasScan
            ? "2) Triage: run a scan first."
            : hasTriage
                ? "2) Triage: done (findings reviewed)."
                : "2) Triage: open Findings and review critical/high items.";
        TxtWorkflowFixState.Text = !hasScan
            ? "3) Fix: run a scan first."
            : hasFix
                ? "3) Fix: done (load-order/isolation action recorded)."
                : "3) Fix: apply suggested order or start Validate from the selected finding.";
        TxtWorkflowValidateState.Text = !hasScan
            ? "4) Validate: run a scan first."
            : hasValidate
                ? "4) Validate: done (runtime validation evidence collected)."
                : "4) Validate: collect runtime evidence after gameplay repro.";

        ApplyWorkflowStepTone(BorderWorkflowScan, complete: hasScan, active: scanActive);
        ApplyWorkflowStepTone(BorderWorkflowTriage, complete: hasTriage, active: triageActive);
        ApplyWorkflowStepTone(BorderWorkflowFix, complete: hasFix, active: fixActive);
        ApplyWorkflowStepTone(BorderWorkflowValidate, complete: hasValidate, active: validateActive);

        bool canNavigate = !_isBusy && hasScan;
        BtnWorkflowGoFindings.IsEnabled = canNavigate;
        BtnWorkflowGoLoadOrder.IsEnabled = canNavigate;
        BtnWorkflowGoIsolation.IsEnabled = canNavigate;
        BtnWorkflowGoRuntime.IsEnabled = canNavigate;

        TxtQuickStepScanState.Text = hasScan
            ? $"{_lastReport!.Modules.Count} modules analyzed, {_lastReport.Conflicts.Count} findings mapped."
            : "Run scan to build a clean baseline before changing anything.";
        TxtQuickStepFixState.Text = !hasScan
            ? "After scan, triage findings and apply suggested order."
            : hasFix
                ? "Fix action recorded. Re-scan if you changed module set."
                : hasTriage
                    ? "Triage started. Apply suggested order or open Validate from the selected finding."
                    : "Review Findings, then apply suggested order or start Validate.";
        TxtQuickStepValidateState.Text = !hasScan
            ? "Collect runtime logs after first gameplay session."
            : hasValidate
                ? "Runtime evidence captured for this session."
                : "Launch game, reproduce symptoms, then collect runtime evidence.";

        ApplyWorkflowStepTone(BorderQuickStepScan, complete: hasScan, active: scanActive);
        ApplyWorkflowStepTone(BorderQuickStepFix, complete: hasFix, active: fixActive);
        ApplyWorkflowStepTone(BorderQuickStepValidate, complete: hasValidate, active: validateActive);

        BtnQuickStepScan.IsEnabled = !_isBusy;
        BtnQuickStepFix.IsEnabled = !_isBusy && hasScan;
        BtnQuickStepValidate.IsEnabled = !_isBusy && hasScan;
    }

    private void ApplyWorkflowStepTone(Border border, bool complete, bool active)
    {
        if (complete)
        {
            border.Background = ParseBrush("#1F241D");
            border.BorderBrush = ParseBrush("#5E7658");
            return;
        }

        if (active)
        {
            border.Background = ParseBrush("#4A2B1E");
            border.BorderBrush = ParseBrush("#A57C43");
            return;
        }

        border.Background = ParseBrush("#231C17");
        border.BorderBrush = ParseBrush("#7C6036");
    }

    private void ApplyPriorityTone(ConflictSeverity severity)
    {
        string text;
        string background;
        string border;

        switch (severity)
        {
            case ConflictSeverity.Critical:
                text = "Priority: Fix before launching the game";
                background = "#4A212D";
                border = "#8E4C5C";
                break;
            case ConflictSeverity.High:
                text = "Priority: Fix before next campaign session";
                background = "#4A371A";
                border = "#8E7033";
                break;
            case ConflictSeverity.Medium:
                text = "Priority: Test soon and monitor in-game";
                background = "#3B3021";
                border = "#8F6D3C";
                break;
            default:
                text = "Priority: Optional tuning or monitoring";
                background = "#2A231D";
                border = "#6F5530";
                break;
        }

        TxtDetailPriority.Text = text;
        BorderDetailPriority.Background = ParseBrush(background);
        BorderDetailPriority.BorderBrush = ParseBrush(border);
    }

    private void UpdateCriticalFocusPanel(ScanReport? report)
    {
        ConflictFinding? topCritical = report?.Conflicts
            .Where(c => c.Severity == ConflictSeverity.Critical)
            .OrderByDescending(c => c.Confidence)
            .ThenByDescending(c => c.ModuleIds.Count)
            .FirstOrDefault();

        if (topCritical is null)
        {
            BorderCriticalFocus.Visibility = Visibility.Collapsed;
            TxtCriticalFocusSummary.Text = "No critical blockers detected.";
            TxtCriticalFocusContext.Text = "Critical means high-impact risk; runtime evidence determines certainty.";
            UpdateFindingsQuickActionButtons();
            return;
        }

        int criticalCount = report!.Conflicts.Count(c => c.Severity == ConflictSeverity.Critical);
        int runtimeCriticalCount = report.Conflicts.Count(c => c.Severity == ConflictSeverity.Critical && IsRuntimeConfirmed(c));
        string moduleSummary = topCritical.ModuleIds.Count == 0
            ? "the current module set"
            : FormatModuleSet(topCritical.ModuleIds, 3);
        string category = ToDisplayCategory(topCritical.Category);
        string topEvidenceStrength = BuildEvidenceStrength(topCritical);

        TxtCriticalFocusSummary.Text = criticalCount == 1
            ? $"1 critical blocker found: {category} involving {moduleSummary}."
            : $"{criticalCount} critical blockers found. Highest-confidence: {category} involving {moduleSummary}.";
        TxtCriticalFocusContext.Text = runtimeCriticalCount > 0
            ? $"{runtimeCriticalCount}/{criticalCount} critical finding(s) are runtime-confirmed. Top signal: {topEvidenceStrength}, confidence {topCritical.Confidence:P0}."
            : $"No runtime crash/session evidence attached to critical findings yet. Top signal: {topEvidenceStrength}, confidence {topCritical.Confidence:P0}. Treat as high-impact potential until validated in runtime logs.";
        BorderCriticalFocus.Background = runtimeCriticalCount > 0
            ? ParseBrush("#4A212D")
            : ParseBrush("#3B2A31");
        BorderCriticalFocus.BorderBrush = runtimeCriticalCount > 0
            ? ParseBrush("#8E4C5C")
            : ParseBrush("#86616C");
        BorderCriticalFocus.Visibility = Visibility.Visible;
        UpdateFindingsQuickActionButtons();
    }

    private void UpdateCriticalExplainPanel(ConflictFinding finding)
    {
        bool highImpact = finding.Severity is ConflictSeverity.Critical or ConflictSeverity.High;
        if (!highImpact)
        {
            ExpanderCriticalExplain.Visibility = Visibility.Collapsed;
            ExpanderCriticalExplain.IsExpanded = false;
            TxtCriticalExplainSummary.Text = "Explainability drawer is shown for critical/high findings.";
            ListCriticalExplain.ItemsSource = null;
            return;
        }

        List<string> bullets = BuildCriticalExplainBullets(finding);
        ExpanderCriticalExplain.Header = finding.Severity == ConflictSeverity.Critical
            ? "Why this is marked critical"
            : "Why this is marked high risk";
        TxtCriticalExplainSummary.Text = BuildCriticalExplainSummary(finding, bullets.Count);
        ListCriticalExplain.ItemsSource = bullets;
        ExpanderCriticalExplain.Visibility = Visibility.Visible;
        ExpanderCriticalExplain.IsExpanded = finding.Severity == ConflictSeverity.Critical;
    }

    private List<string> BuildCriticalExplainBullets(ConflictFinding finding)
    {
        List<string> bullets = [];
        string evidenceStrength = BuildEvidenceStrength(finding);
        bool runtimeConfirmed = IsRuntimeConfirmed(finding);
        bullets.Add(runtimeConfirmed
            ? $"Runtime-confirmed signal attached ({evidenceStrength}, confidence {finding.Confidence:P0})."
            : $"No runtime crash/session confirmation attached ({evidenceStrength}, confidence {finding.Confidence:P0}).");

        string? target = TryExtractHarmonyTarget(finding);
        if (!string.IsNullOrWhiteSpace(target))
        {
            bullets.Add($"Primary target in scope: {target}.");
        }

        List<string> runtimeArtifacts = finding.Evidence
            .Where(IsRuntimeForensicsPath)
            .Select(path => Path.GetFileName(path))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        if (runtimeArtifacts.Count > 0)
        {
            bullets.Add($"Runtime artifacts: {string.Join(", ", runtimeArtifacts)}.");
        }
        else if (finding.Evidence.Any(e => e.StartsWith("runtime-correlation:", StringComparison.OrdinalIgnoreCase)))
        {
            bullets.Add("Runtime-correlation markers detected in analyzer evidence.");
        }

        HashSet<string> focusModules = finding.ModuleIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (focusModules.Count > 0)
        {
            List<FindingRow> relatedFindings = _allFindings
                .Where(row =>
                    !ReferenceEquals(row.Source, finding)
                    && row.Source.ModuleIds.Any(focusModules.Contains))
                .ToList();
            int highCriticalRelated = relatedFindings.Count(row =>
                row.Source.Severity is ConflictSeverity.Critical or ConflictSeverity.High);
            HashSet<string> relatedModules = relatedFindings
                .SelectMany(row => row.Source.ModuleIds)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int overlapCount = focusModules.Count(relatedModules.Contains);
            bullets.Add(
                $"Module overlap: {overlapCount}/{focusModules.Count} module(s) also appear in {relatedFindings.Count} other finding(s), {highCriticalRelated} high/critical.");
        }
        else
        {
            bullets.Add("No explicit module IDs attached; classification came from structural/runtime signals.");
        }

        if (IsRecurringRuntimeCluster(finding))
        {
            int sessions = GetRuntimeClusterSessionCount(finding) ?? 2;
            bullets.Add($"Recurring runtime cluster signal appears in {sessions} session(s).");
        }

        if (bullets.Count < 4 && !string.IsNullOrWhiteSpace(finding.Reason))
        {
            bullets.Add($"Technical signal: {ToShortSentence(finding.Reason, 160)}");
        }

        return bullets
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    private static string BuildCriticalExplainSummary(ConflictFinding finding, int bulletCount)
    {
        string severity = finding.Severity == ConflictSeverity.Critical ? "Critical" : "High";
        string certainty = IsRuntimeConfirmed(finding)
            ? "Runtime-confirmed evidence is present."
            : "No runtime confirmation yet; this is model-based risk scoring.";
        return $"{severity} classification. {certainty} Key evidence bullets: {bulletCount}.";
    }

    private static string? TryExtractHarmonyTarget(ConflictFinding finding)
    {
        string? explicitTarget = finding.Evidence.FirstOrDefault(e =>
            e.StartsWith("harmony-target:", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(explicitTarget))
        {
            string raw = explicitTarget["harmony-target:".Length..].Trim();
            return raw.Length == 0 ? null : raw;
        }

        string? token = finding.Reason
            .Split([' ', '\t', '\r', '\n', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.Contains("::", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return token.Trim().TrimEnd('.', ',', ';');
    }

    private void UpdateFindingsQuickActionButtons()
    {
        bool hasFindings = _allFindings.Count > 0;
        bool hasCritical = _allFindings.Any(f => f.Source.Severity == ConflictSeverity.Critical);
        BtnJumpToCritical.IsEnabled = !_isBusy && hasCritical;
        BtnCriticalOnlyView.IsEnabled = !_isBusy && hasCritical;
        BtnResetFindingsView.IsEnabled = !_isBusy && hasFindings;
    }

    private void UpdateHeroRiskChips(int criticalCount, int highCount, int runtimeCount)
    {
        TxtHeroCriticalChip.Text = criticalCount.ToString();
        TxtHeroHighChip.Text = highCount.ToString();
        TxtHeroRuntimeChip.Text = runtimeCount.ToString();

        BorderHeroCriticalChip.Opacity = criticalCount > 0 ? 1.0 : 0.82;
        BorderHeroHighChip.Opacity = highCount > 0 ? 1.0 : 0.82;
        BorderHeroRuntimeChip.Opacity = runtimeCount > 0 ? 1.0 : 0.82;

        BorderHeroCriticalChip.ToolTip = criticalCount == 0
            ? "No critical blockers in the current scan."
            : $"{criticalCount} critical blocker(s) in the current scan.";
        BorderHeroHighChip.ToolTip = highCount == 0
            ? "No high-risk blockers in the current scan."
            : $"{highCount} high-risk blocker(s) in the current scan.";
        BorderHeroRuntimeChip.ToolTip = runtimeCount == 0
            ? "No runtime-backed findings yet."
            : $"{runtimeCount} finding(s) are backed by runtime/session evidence.";
    }

    private void PopulateReport(ScanReport report, ScanReport? previousReport, bool runtimeEvidenceRequested)
    {
        if (_isolationPendingStep is not null)
        {
            ResetIsolationWorkflow("Scan refreshed. Build a new isolation plan from current findings.");
        }

        int criticalCount = report.Conflicts.Count(c => c.Severity == ConflictSeverity.Critical);
        int highCount = report.Conflicts.Count(c => c.Severity == ConflictSeverity.High);
        int mediumCount = report.Conflicts.Count(c => c.Severity == ConflictSeverity.Medium);
        int runtimeCount = report.Conflicts.Count(HasRuntimeForensicsSignal);

        UpdateRuntimeEvidenceSummary(report, runtimeEvidenceRequested);
        PopulateRuntimeForensics(report);

        TxtStatusHeader.Text = ToDisplayState(report.OverallState);
        TxtModulesCount.Text = report.Modules.Count.ToString();
        TxtFindingsCount.Text = report.Conflicts.Count.ToString();
        TxtCriticalCount.Text = criticalCount.ToString();
        TxtHighCount.Text = highCount.ToString();
        UpdateHeroRiskChips(criticalCount, highCount, runtimeCount);
        TxtTopRecommendation.Text = BuildTopRecommendation(report);
        (string confidenceSummary, string confidenceBreakdown) = BuildConfidenceSummary(report);
        TxtConfidenceSummary.Text = confidenceSummary;
        TxtConfidenceBreakdown.Text = confidenceBreakdown;
        UpdateRiskScorePanel(report, previousReport);

        Dictionary<string, int> currentIndex = report.LoadOrder.CurrentOrder
            .Select((id, idx) => new { id, idx })
            .GroupBy(x => x.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().idx, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> suggestedIndex = report.LoadOrder.SuggestedOrder
            .Select((id, idx) => new { id, idx })
            .GroupBy(x => x.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().idx, StringComparer.OrdinalIgnoreCase);
        PopulateLoadOrderSection(report);

        _allFindings.Clear();
        foreach (ConflictFinding finding in report.Conflicts)
        {
            _allFindings.Add(new FindingRow
            {
                Source = finding,
                PlayerHeadline = BuildPlayerHeadline(finding),
                ImpactSummary = BuildImpactSummary(finding),
                Category = ToDisplayCategory(finding.Category),
                EvidenceStrength = BuildEvidenceStrength(finding),
                ImpactRisk = BuildImpactRisk(finding),
                PlayerSymptom = BuildPlayerSymptom(finding),
                QuickFix = BuildQuickFix(finding),
                TechnicalCause = finding.Reason,
                ExecutionChain = BuildExecutionChain(finding, currentIndex, suggestedIndex),
            });
        }

        UpdateRiskTopDrivers();
        PopulateCategoryFilter();
        ApplyFilters();
        UpdateCriticalFocusPanel(report);
        UpdateWorkflowRail();

        _nextStepRows.Clear();
        foreach (string step in BuildActionPlan(report, criticalCount, highCount, mediumCount))
        {
            _nextStepRows.Add(step);
        }

        UpdateIsolationWorkflowButtons();
        SetStatus($"Scan complete: {report.Conflicts.Count} findings across {report.Modules.Count} modules.");
    }

    private void PopulateRuntimeForensics(ScanReport report)
    {
        _runtimeSessionRows.Clear();
        _runtimeLogRows.Clear();
        _runtimeModuleRows.Clear();
        _runtimeChainRows.Clear();
        _allRuntimeSessionRows.Clear();
        _allRuntimeLogRows.Clear();
        _allRuntimeModuleRows.Clear();
        _allRuntimeChainRows.Clear();

        List<ConflictFinding> runtimeBackedFindings = report.Conflicts
            .Where(HasRuntimeForensicsSignal)
            .ToList();

        if (runtimeBackedFindings.Count == 0)
        {
            _allRuntimeChainRows.Add(new RuntimeChainRow
            {
                LogFile = "-",
                LogPath = string.Empty,
                Modules = "-",
                ModuleKey = string.Empty,
                SessionKey = UnknownRuntimeSessionKey,
                Finding = "No runtime-linked findings.",
                Risk = "-",
                Confidence = "-",
                Signal = "Run Collect Runtime Evidence after gameplay to populate this panel.",
                Meaning = "No runtime-backed warning is loaded yet.",
                QuickFix = "Play once, then collect logs only if you need help from runtime evidence.",
            });
            ClearRuntimeForensicsSelections();
            ApplyRuntimeForensicsFilters();
            return;
        }

        Dictionary<string, RuntimeLogAccumulator> logsByPath = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RuntimeModuleAccumulator> modulesById = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RuntimeSessionAccumulator> sessionsByKey = new(StringComparer.OrdinalIgnoreCase);
        List<RuntimeChainRow> chains = [];

        foreach (ConflictFinding finding in runtimeBackedFindings)
        {
            IReadOnlyList<string> runtimePaths = finding.Evidence
                .Where(IsRuntimeForensicsPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();
            string signal = BuildRuntimeSignalLabel(finding);
            string moduleKey = string.Join('|', finding.ModuleIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
            List<string> findingModules = finding.ModuleIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string moduleId in findingModules)
            {
                if (!modulesById.TryGetValue(moduleId, out RuntimeModuleAccumulator? moduleAcc))
                {
                    moduleAcc = new RuntimeModuleAccumulator(moduleId);
                    modulesById[moduleId] = moduleAcc;
                }

                moduleAcc.CorrelatedFindingCount++;
                moduleAcc.MaxSeverity = MaxSeverity(moduleAcc.MaxSeverity, finding.Severity);
                moduleAcc.Categories.Add(ToDisplayCategory(finding.Category));
            }

            string moduleSummary;
            if (findingModules.Count == 0)
            {
                moduleSummary = "-";
            }
            else
            {
                moduleSummary = findingModules.Count <= 3
                    ? string.Join(", ", findingModules)
                    : string.Join(", ", findingModules.Take(3)) + $" (+{findingModules.Count - 3})";
            }

            if (runtimePaths.Count == 0)
            {
                RuntimeSessionAccumulator unknownSession = GetOrCreateRuntimeSessionAccumulator(sessionsByKey, null);
                unknownSession.ChainCount++;
                unknownSession.MaxSeverity = MaxSeverity(unknownSession.MaxSeverity, finding.Severity);
                foreach (string moduleId in findingModules)
                {
                    unknownSession.ModuleIds.Add(moduleId);
                    modulesById[moduleId].SessionKeys.Add(UnknownRuntimeSessionKey);
                }

                chains.Add(new RuntimeChainRow
                {
                    LogFile = "runtime signal",
                    LogPath = string.Empty,
                    Modules = moduleSummary,
                    ModuleKey = moduleKey,
                    SessionKey = UnknownRuntimeSessionKey,
                    Finding = BuildPlayerHeadline(finding),
                    Risk = finding.Severity.ToString(),
                    Confidence = $"{finding.Confidence:P0}",
                    Signal = signal,
                    Meaning = BuildMeaningText(finding),
                    QuickFix = BuildQuickFix(finding),
                });
                continue;
            }

            foreach (string path in runtimePaths)
            {
                if (!logsByPath.TryGetValue(path, out RuntimeLogAccumulator? logAcc))
                {
                    string? extractedSessionKey = TryExtractRuntimeSessionKey(path);
                    logAcc = new RuntimeLogAccumulator(
                        path,
                        ClassifyRuntimeLogType(path),
                        NormalizeRuntimeSessionKey(extractedSessionKey),
                        GetLastWriteUtcSafe(path));
                    logsByPath[path] = logAcc;
                }

                logAcc.LinkedFindingCount++;
                logAcc.Categories.Add(ToDisplayCategory(finding.Category));
                RuntimeSessionAccumulator sessionAcc = GetOrCreateRuntimeSessionAccumulator(sessionsByKey, logAcc.SessionKey);
                sessionAcc.ChainCount++;
                sessionAcc.MaxSeverity = MaxSeverity(sessionAcc.MaxSeverity, finding.Severity);
                sessionAcc.LastLogUtc = sessionAcc.LastLogUtc > logAcc.LastWriteUtc ? sessionAcc.LastLogUtc : logAcc.LastWriteUtc;
                foreach (string moduleId in findingModules)
                {
                    sessionAcc.ModuleIds.Add(moduleId);
                    modulesById[moduleId].SessionKeys.Add(logAcc.SessionKey);
                }

                chains.Add(new RuntimeChainRow
                {
                    LogFile = Path.GetFileName(path),
                    LogPath = path,
                    Modules = moduleSummary,
                    ModuleKey = moduleKey,
                    SessionKey = logAcc.SessionKey,
                    Finding = BuildPlayerHeadline(finding),
                    Risk = finding.Severity.ToString(),
                    Confidence = $"{finding.Confidence:P0}",
                    Signal = signal,
                    Meaning = BuildMeaningText(finding),
                    QuickFix = BuildQuickFix(finding),
                });
            }
        }

        foreach (RuntimeLogAccumulator log in logsByPath.Values
                     .OrderByDescending(l => l.LinkedFindingCount)
                     .ThenByDescending(l => l.LastWriteUtc)
                     .ThenBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
                     .Take(120))
        {
            _allRuntimeLogRows.Add(new RuntimeLogRow
            {
                SignalType = log.SignalType,
                SessionKey = log.SessionKey,
                FileName = Path.GetFileName(log.Path),
                FindingHits = log.LinkedFindingCount,
                Path = log.Path,
            });
        }

        foreach (RuntimeModuleAccumulator module in modulesById.Values
                     .OrderByDescending(m => m.CorrelatedFindingCount)
                     .ThenByDescending(m => m.MaxSeverity)
                     .ThenBy(m => m.ModuleId, StringComparer.OrdinalIgnoreCase)
                     .Take(180))
        {
            _allRuntimeModuleRows.Add(new RuntimeModuleRow
            {
                ModuleId = module.ModuleId,
                CorrelatedFindings = module.CorrelatedFindingCount,
                SessionCoverage = module.SessionKeys.Count == 0 ? "-" : module.SessionKeys.Count.ToString(),
                SessionKeys = module.SessionKeys
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                MaxRisk = module.MaxSeverity.ToString(),
                TopCategories = string.Join(", ", module.Categories
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .Take(3)),
            });
        }

        foreach (RuntimeSessionAccumulator session in sessionsByKey.Values
                     .OrderByDescending(s => s.LastLogUtc)
                     .ThenByDescending(s => s.SessionKey, StringComparer.OrdinalIgnoreCase)
                     .Take(140))
        {
            _allRuntimeSessionRows.Add(new RuntimeSessionRow
            {
                SessionKey = session.SessionKey,
                SessionLabel = BuildRuntimeSessionLabel(session.SessionKey),
                LastLogUtc = session.LastLogUtc == DateTime.MinValue
                    ? "-"
                    : session.LastLogUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                ChainCount = session.ChainCount,
                ModuleCount = session.ModuleIds.Count,
                MaxRisk = session.MaxSeverity.ToString(),
                StateLabel = BuildRuntimeSessionStateLabel(session),
            });
        }

        List<RuntimeChainRow> sortedChains = chains
            .OrderByDescending(c => ParseSeverity(c.Risk))
            .ThenByDescending(c => ParseConfidence(c.Confidence))
            .ThenBy(c => c.SessionKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.LogFile, StringComparer.OrdinalIgnoreCase)
            .Take(220)
            .ToList();
        _allRuntimeChainRows.AddRange(sortedChains);
        ClearRuntimeForensicsSelections();
        ApplyRuntimeForensicsFilters();
    }

    private void ClearRuntimeForensicsSelections()
    {
        _runtimeForensicsFilterSyncInProgress = true;
        try
        {
            ListPlayerRuntimeRuns.UnselectAll();
            GridPlayerRuntimeSuggestions.UnselectAll();
            GridPlayerRuntimeLogDetails.UnselectAll();
            GridPlayerRuntimeModuleDetails.UnselectAll();
        }
        finally
        {
            _runtimeForensicsFilterSyncInProgress = false;
        }
    }

    private HashSet<string> GetSelectedRuntimeSessionKeys()
    {
        return ListPlayerRuntimeRuns.SelectedItems
            .OfType<PlayerRuntimeSessionCardRow>()
            .Select(r => r.SessionKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private HashSet<string> GetSelectedRuntimeLogPaths()
    {
        return GridPlayerRuntimeLogDetails.SelectedItems
            .OfType<RuntimeLogRow>()
            .Select(r => r.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private HashSet<string> GetSelectedRuntimeModuleIds()
    {
        return GridPlayerRuntimeModuleDetails.SelectedItems
            .OfType<RuntimeModuleRow>()
            .Select(r => r.ModuleId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void ApplyRuntimeForensicsFilters()
    {
        if (_runtimeForensicsFilterSyncInProgress)
        {
            return;
        }

        HashSet<string> selectedSessionKeys = GetSelectedRuntimeSessionKeys();
        HashSet<string> selectedLogPaths = GetSelectedRuntimeLogPaths();
        HashSet<string> selectedModuleIds = GetSelectedRuntimeModuleIds();
        string? selectedPlayerSessionKey = ListPlayerRuntimeRuns.SelectedItems
            .OfType<PlayerRuntimeSessionCardRow>()
            .Select(r => r.SessionKey)
            .FirstOrDefault();
        string? selectedPlayerSuggestionKey = (GridPlayerRuntimeSuggestions.SelectedItem as PlayerRuntimeSummaryRow)?.SelectionKey;

        IEnumerable<RuntimeChainRow> chainQuery = _allRuntimeChainRows;
        if (selectedSessionKeys.Count > 0)
        {
            chainQuery = chainQuery.Where(c => selectedSessionKeys.Contains(c.SessionKey));
        }

        if (selectedLogPaths.Count > 0)
        {
            chainQuery = chainQuery.Where(c =>
                !string.IsNullOrWhiteSpace(c.LogPath)
                && selectedLogPaths.Contains(c.LogPath));
        }

        if (selectedModuleIds.Count > 0)
        {
            chainQuery = chainQuery.Where(c => RuntimeChainMatchesModuleSelection(c, selectedModuleIds));
        }

        List<RuntimeChainRow> filteredChains = chainQuery.ToList();
        HashSet<string> visibleLogPaths = filteredChains
            .Where(c => !string.IsNullOrWhiteSpace(c.LogPath))
            .Select(c => c.LogPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> visibleModuleIds = filteredChains
            .SelectMany(GetRuntimeChainModuleIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<RuntimeLogRow> logsToShow = selectedLogPaths.Count == 0 && selectedModuleIds.Count == 0
            ? (selectedSessionKeys.Count == 0
                ? _allRuntimeLogRows
                : _allRuntimeLogRows.Where(r => selectedSessionKeys.Contains(r.SessionKey)).ToList())
            : _allRuntimeLogRows
                .Where(r => visibleLogPaths.Contains(r.Path))
                .ToList();
        List<RuntimeModuleRow> modulesToShow = selectedLogPaths.Count == 0 && selectedModuleIds.Count == 0
            ? (selectedSessionKeys.Count == 0
                ? _allRuntimeModuleRows
                : _allRuntimeModuleRows.Where(r => RuntimeModuleMatchesSessions(r, selectedSessionKeys)).ToList())
            : _allRuntimeModuleRows
                .Where(r => visibleModuleIds.Contains(r.ModuleId))
                .ToList();
        List<RuntimeSessionRow> sessionsToShow = selectedLogPaths.Count == 0 && selectedModuleIds.Count == 0
            ? _allRuntimeSessionRows
            : _allRuntimeSessionRows
                .Where(r => filteredChains.Any(c => c.SessionKey.Equals(r.SessionKey, StringComparison.OrdinalIgnoreCase)))
                .ToList();

        ReplaceCollection(_runtimeSessionRows, sessionsToShow);
        ReplaceCollection(_runtimeLogRows, logsToShow);
        ReplaceCollection(_runtimeModuleRows, modulesToShow);
        ReplaceCollection(_runtimeChainRows, filteredChains);
        ReplaceCollection(_playerRuntimeSessionCardRows, BuildPlayerRuntimeSessionCards(sessionsToShow));
        ReplaceCollection(_playerRuntimeSummaryRows, BuildPlayerRuntimeSummaryRows(filteredChains, sessionsToShow));
        RestorePlayerRuntimeSelections(selectedPlayerSessionKey, selectedPlayerSuggestionKey);
        UpdateRuntimeForensicsDashboard(selectedSessionKeys, selectedLogPaths, selectedModuleIds);
        UpdateRuntimeForensicsActionState();
    }

    private static List<PlayerRuntimeSessionCardRow> BuildPlayerRuntimeSessionCards(IReadOnlyList<RuntimeSessionRow> sessions)
    {
        return sessions
            .Select(session => new PlayerRuntimeSessionCardRow
            {
                SessionKey = session.SessionKey,
                Title = session.SessionLabel,
                Subtitle = session.LastLogUtc == "-"
                    ? $"{session.StateLabel} run"
                    : $"Latest log: {session.LastLogUtc} UTC",
                Summary = BuildPlayerRuntimeSessionSummary(session),
            })
            .ToList();
    }

    private static List<PlayerRuntimeSummaryRow> BuildPlayerRuntimeSummaryRows(
        IReadOnlyList<RuntimeChainRow> chains,
        IReadOnlyList<RuntimeSessionRow> sessions)
    {
        Dictionary<string, RuntimeSessionRow> sessionsByKey = sessions
            .GroupBy(session => session.SessionKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return chains
            .Where(chain => !string.Equals(chain.Finding, "No runtime-linked findings.", StringComparison.OrdinalIgnoreCase))
            .Select(chain => new PlayerRuntimeSummaryRow
            {
                SelectionKey = BuildPlayerRuntimeSummarySelectionKey(chain),
                Source = chain,
                Headline = chain.Finding,
                Explanation = chain.Meaning,
                Modules = chain.Modules,
                WhenAndLog = BuildPlayerRuntimeWhenAndLog(chain, sessionsByKey),
                NextStep = $"Try this first: {chain.QuickFix}",
                HasLogPath = !string.IsNullOrWhiteSpace(chain.LogPath),
            })
            .ToList();
    }

    private void RestorePlayerRuntimeSelections(string? sessionKey, string? suggestionKey)
    {
        _runtimeForensicsFilterSyncInProgress = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(sessionKey))
            {
                ListPlayerRuntimeRuns.SelectedItem = _playerRuntimeSessionCardRows
                    .FirstOrDefault(row => row.SessionKey.Equals(sessionKey, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(suggestionKey))
            {
                GridPlayerRuntimeSuggestions.SelectedItem = _playerRuntimeSummaryRows
                    .FirstOrDefault(row => row.SelectionKey.Equals(suggestionKey, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            _runtimeForensicsFilterSyncInProgress = false;
        }
    }

    private static string BuildPlayerRuntimeSummarySelectionKey(RuntimeChainRow row)
    {
        return string.Join("|", row.SessionKey, row.LogPath, row.LogFile, row.Finding, row.Modules);
    }

    private static string BuildPlayerRuntimeWhenAndLog(
        RuntimeChainRow row,
        IReadOnlyDictionary<string, RuntimeSessionRow> sessionsByKey)
    {
        string whenText = sessionsByKey.TryGetValue(row.SessionKey, out RuntimeSessionRow? session)
            ? session.LastLogUtc
            : "-";

        return whenText == "-" || string.IsNullOrWhiteSpace(row.LogFile)
            ? row.LogFile
            : $"{whenText} UTC - {row.LogFile}";
    }

    private static string BuildPlayerRuntimeSessionSummary(RuntimeSessionRow session)
    {
        string warningPart = session.MaxRisk == "-"
            ? "No clear warning is ranked yet."
            : $"Top visible warning: {session.MaxRisk}.";
        string statePart = string.IsNullOrWhiteSpace(session.StateLabel) ? string.Empty : $" {session.StateLabel} run.";
        return $"{session.ChainCount} log note(s) across {session.ModuleCount} mod(s). {warningPart}{statePart}".Trim();
    }

    private void UpdateRuntimeForensicsDashboard(
        HashSet<string> selectedSessionKeys,
        HashSet<string> selectedLogPaths,
        HashSet<string> selectedModuleIds)
    {
        if (_allRuntimeChainRows.Count == 0)
        {
            TxtRuntimeForensicsSummary.Text = "No runtime evidence loaded yet. Run Collect Runtime Evidence after gameplay.";
            TxtPlayerRuntimeRunsHint.Text = "No recent game runs are available yet.";
            TxtPlayerRuntimeSummaryHint.Text = "When logs exist, this panel will explain the clearest warnings in plain language.";
            UpdateRuntimeForensicsFocusSummary();
            UpdatePlayerRuntimeSelectionCard();
            return;
        }

        string highestRisk = _runtimeChainRows.Count == 0
            ? "-"
            : _runtimeChainRows
                .Select(c => c.Risk)
                .OrderByDescending(ParseSeverity)
                .FirstOrDefault() ?? "-";
        bool hasFilters = selectedSessionKeys.Count > 0 || selectedLogPaths.Count > 0 || selectedModuleIds.Count > 0;
        if (!hasFilters)
        {
            TxtRuntimeForensicsSummary.Text =
                $"Runtime evidence available. Highest visible risk: {highestRisk}. Open Game Runs only if you need help from logs.";
            TxtPlayerRuntimeRunsHint.Text = _playerRuntimeSessionCardRows.Count == 0
                ? "No recent game runs are visible."
                : "Pick the run that best matches what happened in game.";
            TxtPlayerRuntimeSummaryHint.Text = _playerRuntimeSummaryRows.Count == 0
                ? "No clear log warning is visible for the current runs."
                : $"{_playerRuntimeSummaryRows.Count} plain-language warning(s) are visible. Start with the first card.";
            UpdateRuntimeForensicsFocusSummary();
            UpdatePlayerRuntimeSelectionCard();
            return;
        }

        string sessionFilter = selectedSessionKeys.Count == 0 ? "all sessions" : $"{selectedSessionKeys.Count} session";
        string logFilter = selectedLogPaths.Count == 0 ? "all logs" : $"{selectedLogPaths.Count} log";
        string moduleFilter = selectedModuleIds.Count == 0 ? "all modules" : $"{selectedModuleIds.Count} module";
        TxtRuntimeForensicsSummary.Text =
            $"Focus active: {sessionFilter}, {logFilter}, {moduleFilter}. Highest visible risk: {highestRisk}.";
        TxtPlayerRuntimeRunsHint.Text = selectedSessionKeys.Count == 0
            ? "Recent game runs are still unfiltered."
            : "A specific run is selected. Clear the run filter to widen the view again.";
        TxtPlayerRuntimeSummaryHint.Text = _playerRuntimeSummaryRows.Count == 0
            ? "No plain-language warning remains for the current filter."
            : $"The current filter leaves {_playerRuntimeSummaryRows.Count} plain-language warning(s).";
        UpdateRuntimeForensicsFocusSummary();
        UpdatePlayerRuntimeSelectionCard();
    }

    private void UpdateRuntimeForensicsFocusSummary()
    {
        if (_allRuntimeChainRows.Count == 0)
        {
            TxtValidateRuntimeSectionHint.Text = "Leave this closed unless something actually goes wrong in game.";
            TxtPlayerRuntimeRunsHint.Text = "No recent game runs are available yet.";
            TxtPlayerRuntimeSummaryHint.Text = "When logs exist, this panel will explain the clearest warnings in plain language.";
            return;
        }

        if (GetSelectedRuntimeChainRow() is RuntimeChainRow selectedPlayerChain)
        {
            TxtValidateRuntimeSectionHint.Text = "Game runs are optional support. Open a raw log only if the summary is not enough.";
            TxtPlayerRuntimeSummaryHint.Text = $"{selectedPlayerChain.Finding} Try this first: {selectedPlayerChain.QuickFix}";
            return;
        }

        TxtValidateRuntimeSectionHint.Text = "Open this only if the issue actually showed up in game.";
        TxtPlayerRuntimeSummaryHint.Text = "Start with the run that matches what you just tested, then read the clearest visible warning.";
    }

    private void UpdateRuntimeForensicsActionState()
    {
        bool hasEvidence = _allRuntimeChainRows.Count > 0;
        bool hasFilter = GetSelectedRuntimeSessionKeys().Count > 0
            || GetSelectedRuntimeLogPaths().Count > 0
            || GetSelectedRuntimeModuleIds().Count > 0;
        BtnPlayerRuntimeClearFilterLocal.IsEnabled = !_isBusy && hasEvidence && hasFilter;
        BtnPlayerRuntimeUseTopWarning.IsEnabled = !_isBusy && _playerRuntimeSummaryRows.Count > 0;
        UpdatePlayerRuntimeSelectionCard();
        ApplyValidatePlayerSurface();
    }

    private void OpenSelectedRuntimeLogFromSelection()
    {
        string? path = null;
        string displayName = "runtime log";

        if (GetSelectedRuntimeLogRow() is RuntimeLogRow logRow)
        {
            path = logRow.Path;
            displayName = logRow.FileName;
        }
        else if (GetSelectedRuntimeChainRow() is RuntimeChainRow chainRow && !string.IsNullOrWhiteSpace(chainRow.LogPath))
        {
            path = chainRow.LogPath;
            displayName = chainRow.LogFile;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            SetStatus("Select a runtime log or chain row that has a log path.");
            return;
        }

        if (!File.Exists(path))
        {
            SetStatus($"Log file not found: {path}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            SetStatus($"Opened runtime log: {displayName}");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to open runtime log: {ex.Message}");
        }
    }

    private void FocusSelectedRuntimeChainFinding(bool switchToFindingsTab)
    {
        if (GetSelectedRuntimeChainRow() is not RuntimeChainRow chainRow)
        {
            SetStatus("Select a runtime chain row first.");
            return;
        }

        if (_allFindings.Count == 0)
        {
            SetStatus("No findings are loaded yet.");
            return;
        }

        HashSet<string> chainModules = GetRuntimeChainModuleIds(chainRow)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidate = _allFindings
            .Select(row =>
            {
                bool categoryMatch = row.Category.Equals(chainRow.Finding, StringComparison.OrdinalIgnoreCase);
                bool headlineMatch = row.PlayerHeadline.Equals(chainRow.Finding, StringComparison.OrdinalIgnoreCase);
                int overlap = CountModuleOverlap(chainModules, row.Source.ModuleIds);
                bool logPathMatch = !string.IsNullOrWhiteSpace(chainRow.LogPath)
                    && row.Source.Evidence.Any(e => e.Equals(chainRow.LogPath, StringComparison.OrdinalIgnoreCase));
                bool signalMatch = !string.IsNullOrWhiteSpace(chainRow.Signal)
                    && row.Source.Evidence.Any(e =>
                        e.Equals(chainRow.Signal, StringComparison.OrdinalIgnoreCase)
                        || e.Contains(chainRow.Signal, StringComparison.OrdinalIgnoreCase));

                return new
                {
                    Row = row,
                    CategoryMatch = categoryMatch || headlineMatch,
                    Overlap = overlap,
                    LogPathMatch = logPathMatch,
                    SignalMatch = signalMatch,
                    SeverityRank = (int)row.Source.Severity,
                    Confidence = row.Source.Confidence,
                };
            })
            .Where(x => x.CategoryMatch || x.Overlap > 0 || x.LogPathMatch || x.SignalMatch)
            .OrderByDescending(x => x.LogPathMatch)
            .ThenByDescending(x => x.SignalMatch)
            .ThenByDescending(x => x.CategoryMatch)
            .ThenByDescending(x => x.Overlap)
            .ThenByDescending(x => x.SeverityRank)
            .ThenByDescending(x => x.Confidence)
            .FirstOrDefault();

        if (candidate is null)
        {
            SetStatus("No matching finding could be resolved from the selected runtime chain.");
            return;
        }

        ComboSeverityFilter.SelectedItem = "All";
        ComboCategoryFilter.SelectedItem = "All";
        if (!string.IsNullOrWhiteSpace(TxtSearch.Text))
        {
            TxtSearch.Text = string.Empty;
        }
        else
        {
            ApplyFilters();
        }

        GridFindings.SelectedItem = candidate.Row;
        GridFindings.ScrollIntoView(candidate.Row);

        if (switchToFindingsTab)
        {
            MainTabs.SelectedItem = TabFindings;
        }

        SetStatus($"Focused finding from runtime chain: {candidate.Row.PlayerHeadline} ({candidate.Row.Severity}).");
    }

    private static IEnumerable<string> GetRuntimeChainModuleIds(RuntimeChainRow row)
    {
        return row.ModuleKey.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool RuntimeChainMatchesModuleSelection(RuntimeChainRow row, IReadOnlySet<string> selectedModuleIds)
    {
        if (selectedModuleIds.Count == 0)
        {
            return true;
        }

        return GetRuntimeChainModuleIds(row).Any(selectedModuleIds.Contains);
    }

    private static bool RuntimeModuleMatchesSessions(RuntimeModuleRow row, IReadOnlySet<string> selectedSessionKeys)
    {
        if (selectedSessionKeys.Count == 0)
        {
            return true;
        }

        return row.SessionKeys.Any(selectedSessionKeys.Contains);
    }

    private static int CountModuleOverlap(IReadOnlySet<string> selectedModules, IReadOnlyList<string> findingModules)
    {
        int overlap = 0;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string moduleId in findingModules)
        {
            if (!seen.Add(moduleId))
            {
                continue;
            }

            if (selectedModules.Contains(moduleId))
            {
                overlap++;
            }
        }

        return overlap;
    }

    private static RuntimeSessionAccumulator GetOrCreateRuntimeSessionAccumulator(
        IDictionary<string, RuntimeSessionAccumulator> sessionsByKey,
        string? sessionKey)
    {
        string normalized = NormalizeRuntimeSessionKey(sessionKey);
        if (!sessionsByKey.TryGetValue(normalized, out RuntimeSessionAccumulator? accumulator))
        {
            accumulator = new RuntimeSessionAccumulator(normalized);
            sessionsByKey[normalized] = accumulator;
        }

        return accumulator;
    }

    private static string NormalizeRuntimeSessionKey(string? sessionKey)
    {
        return string.IsNullOrWhiteSpace(sessionKey)
            ? UnknownRuntimeSessionKey
            : sessionKey.Trim();
    }

    private static string? TryExtractRuntimeSessionKey(string pathOrFile)
    {
        if (string.IsNullOrWhiteSpace(pathOrFile))
        {
            return null;
        }

        string fileName = Path.GetFileName(pathOrFile);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        int extensionIndex = fileName.LastIndexOf('.');
        string stem = extensionIndex > 0 ? fileName[..extensionIndex] : fileName;
        int lastUnderscoreIndex = stem.LastIndexOf('_');
        if (lastUnderscoreIndex < 0 || lastUnderscoreIndex >= stem.Length - 1)
        {
            return null;
        }

        string suffix = stem[(lastUnderscoreIndex + 1)..].Trim();
        if (suffix.Length == 0 || suffix.Any(ch => !char.IsDigit(ch)))
        {
            return null;
        }

        return suffix;
    }

    private static string BuildRuntimeSessionLabel(string sessionKey)
    {
        return sessionKey.Equals(UnknownRuntimeSessionKey, StringComparison.OrdinalIgnoreCase)
            ? "Unknown"
            : $"S{sessionKey}";
    }

    private static string BuildRuntimeSessionStateLabel(RuntimeSessionAccumulator session)
    {
        if (session.SessionKey.Equals(UnknownRuntimeSessionKey, StringComparison.OrdinalIgnoreCase))
        {
            return "Inferred";
        }

        if (session.LastLogUtc != DateTime.MinValue
            && DateTime.UtcNow - session.LastLogUtc <= TimeSpan.FromMinutes(2))
        {
            return "Live";
        }

        return "Captured";
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

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        target.Clear();
        foreach (T item in source)
        {
            target.Add(item);
        }
    }

    private static bool HasRuntimeForensicsSignal(ConflictFinding finding)
    {
        if (finding.Category is ConflictCategory.RuntimeModuleSetMismatch
            or ConflictCategory.RuntimeLoaderFailure
            or ConflictCategory.RuntimeCrashSession)
        {
            return true;
        }

        if (finding.Reason.Contains("Runtime evidence correlation:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return finding.Evidence.Any(e =>
            e.StartsWith("runtime-correlation:", StringComparison.OrdinalIgnoreCase)
            || IsRuntimeForensicsPath(e)
            || e.StartsWith("issue:", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRuntimeForensicsPath(string evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return false;
        }

        return evidence.Contains(@"\Mount and Blade II Bannerlord\logs\", StringComparison.OrdinalIgnoreCase)
            || evidence.EndsWith("AllHarmonyPatches.txt", StringComparison.OrdinalIgnoreCase)
            || evidence.EndsWith("DuplicateHarmonyPatches.txt", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifyRuntimeLogType(string path)
    {
        string file = Path.GetFileName(path);
        if (file.StartsWith("watchdog_log_", StringComparison.OrdinalIgnoreCase))
        {
            return "Watchdog";
        }

        if (file.StartsWith("launcher_log_", StringComparison.OrdinalIgnoreCase))
        {
            return "Launcher";
        }

        if (file.StartsWith("rgl_log_errors_", StringComparison.OrdinalIgnoreCase))
        {
            return "RGL Errors";
        }

        if (file.StartsWith("rgl_log_", StringComparison.OrdinalIgnoreCase))
        {
            return "RGL";
        }

        if (file.EndsWith("crashlist.txt", StringComparison.OrdinalIgnoreCase))
        {
            return "Crash List";
        }

        if (file.EndsWith("AllHarmonyPatches.txt", StringComparison.OrdinalIgnoreCase))
        {
            return "Harmony Patch Snapshot";
        }

        if (file.EndsWith("DuplicateHarmonyPatches.txt", StringComparison.OrdinalIgnoreCase))
        {
            return "Harmony Duplicate Snapshot";
        }

        return "Runtime Artifact";
    }

    private static string BuildRuntimeSignalLabel(ConflictFinding finding)
    {
        if (finding.Category == ConflictCategory.RuntimeLoaderFailure
            && finding.StructuredEvidence?.Details is not null
            && finding.StructuredEvidence.Details.TryGetValue("issue-summary", out string? summary)
            && !string.IsNullOrWhiteSpace(summary))
        {
            return summary;
        }

        string? marker = finding.Evidence.FirstOrDefault(e =>
            e.StartsWith("runtime-correlation:", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(marker))
        {
            return marker;
        }

        if (finding.Category is ConflictCategory.RuntimeModuleSetMismatch
            or ConflictCategory.RuntimeLoaderFailure
            or ConflictCategory.RuntimeCrashSession)
        {
            return finding.Category.ToString();
        }

        string? issue = finding.Evidence.FirstOrDefault(e =>
            e.StartsWith("issue:", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(issue))
        {
            return issue;
        }

        return "runtime-observed";
    }

    private static ConflictSeverity MaxSeverity(ConflictSeverity left, ConflictSeverity right)
    {
        return left >= right ? left : right;
    }

    private static ConflictSeverity ParseSeverity(string? text)
    {
        if (Enum.TryParse<ConflictSeverity>(text, ignoreCase: true, out ConflictSeverity parsed))
        {
            return parsed;
        }

        return ConflictSeverity.Info;
    }

    private static double ParseConfidence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0.0;
        }

        string trimmed = text.Trim();
        if (trimmed.EndsWith("%", StringComparison.Ordinal))
        {
            if (double.TryParse(trimmed[..^1], out double percent))
            {
                return percent / 100.0;
            }
        }

        return double.TryParse(trimmed, out double raw) ? raw : 0.0;
    }

    private static double ParseConfidenceFloor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0.0;
        }

        string trimmed = text.Trim();
        if (trimmed.EndsWith("%", StringComparison.Ordinal))
        {
            if (double.TryParse(trimmed[..^1], out double percent))
            {
                return Math.Clamp(percent / 100.0, 0.0, 1.0);
            }
        }

        if (double.TryParse(trimmed, out double raw))
        {
            return raw > 1.0 ? Math.Clamp(raw / 100.0, 0.0, 1.0) : Math.Clamp(raw, 0.0, 1.0);
        }

        return 0.0;
    }

    private void ApplyLoadOrderFilter()
    {
        _loadOrderRows.Clear();
        foreach (LoadOrderRow row in _allLoadOrderRows)
        {
            _loadOrderRows.Add(row);
        }
        UpdateLoadOrderGuidance();
    }

    private void InitializePlayerLayout()
    {
        TxtFindingsToolbarIntro.Text = "Plain-language triage table first. Open Refine Results only when you need filters.";
        ExpFindingsRefineResults.Header = "Refine Results";
        ExpFindingsRefineResults.IsExpanded = false;
        BtnDetailOpenRuntime.Content = "Open Validate";
        BtnDetailOpenIsolation.Content = "Start Validation";
        ApplyFindingsColumnVisibility();
        SetFindingsControlsCollapsed(collapsed: true, announce: false);

        string desiredConfidenceFloor = "60%";
        if (!string.Equals(ComboConfidenceFloor.SelectedItem?.ToString(), desiredConfidenceFloor, StringComparison.OrdinalIgnoreCase))
        {
            ComboConfidenceFloor.SelectedItem = desiredConfidenceFloor;
        }

        bool compactSetting = ChkCompactFindingsLayout.IsChecked ?? true;
        if (ChkCompactFindingsLayout.IsChecked is null)
        {
            ChkCompactFindingsLayout.IsChecked = compactSetting;
        }
        else
        {
            ApplyFindingsDensity(compactSetting);
        }

        SetControlsCollapsed(_uiPreferences.ControlsPanelCollapsed ?? true);
        UpdatePlayerSettingsHeaderState();
        TxtPresetSummary.Text = "Primary workflow: Findings, Load Order, then Validate.";
    }

    private void InitializeFindingsDensityControls()
    {
        bool preferredCompact = _uiPreferences.CompactFindingsLayout ?? true;
        bool currentCompact = ChkCompactFindingsLayout.IsChecked == true;
        if (currentCompact != preferredCompact)
        {
            ChkCompactFindingsLayout.IsChecked = preferredCompact;
        }
        else
        {
            ApplyFindingsDensity(preferredCompact);
        }
    }

    private void ApplyFindingsDensity(bool compact)
    {
        _compactFindingsLayout = compact;
        RowFindingsGrid.Height = compact
            ? new GridLength(2.8, GridUnitType.Star)
            : new GridLength(2.3, GridUnitType.Star);
        RowFindingsDetail.Height = compact
            ? new GridLength(1.5, GridUnitType.Star)
            : new GridLength(1.7, GridUnitType.Star);
        BorderFindingsToolbar.Padding = compact ? new Thickness(5) : new Thickness(8);
        BorderFindingsDetail.Padding = compact ? new Thickness(9) : new Thickness(10);
        ScrollFindingsToolbar.MaxHeight = compact ? 220 : 285;
        TxtFindingsHint.Margin = new Thickness(0);
        TxtFindingsHint.FontSize = compact ? 12.5 : 13.2;

        GridFindings.FontSize = compact ? 13 : 13.6;
        GridFindings.MinRowHeight = compact ? 33 : 38;
        GridFindings.ColumnHeaderHeight = compact ? 34 : 36;
        ListPriorityQueue.MaxHeight = compact ? 86 : 120;
        ListCriticalRuntimeDrawer.MaxHeight = compact ? 92 : 126;
        TxtDetailPlayerSummary.FontSize = compact ? 12.5 : 13;
        TxtDetailOutcome.FontSize = compact ? 12 : 13;
        TxtDetailRecommendation.FontSize = compact ? 12 : 13;
        ApplyFindingsDetailHostLayout();
    }

    private void ApplyFindingsColumnVisibility()
    {
        ColFindingConfidence.Visibility = Visibility.Collapsed;
        ColFindingEvidence.Visibility = Visibility.Collapsed;
        ColFindingImpact.Visibility = Visibility.Collapsed;
        ColFindingSymptoms.Visibility = Visibility.Collapsed;
        ColFindingIssue.Width = new DataGridLength(2.2, DataGridLengthUnitType.Star);
        ColFindingModules.Width = new DataGridLength(1.45, DataGridLengthUnitType.Star);
        ColFindingQuickFix.Width = new DataGridLength(2.35, DataGridLengthUnitType.Star);
    }

    private void UpdatePlayerSettingsHeaderState()
    {
        BtnPlayerSettingsMenu.Visibility = Visibility.Visible;
        MenuToggleControlsPanel.Header = _controlsCollapsed ? "Show Controls Panel" : "Hide Controls Panel";
    }

    private void UpdateFindingsHintText()
    {
        TxtFindingsHint.Text = _findingsControlsCollapsed
            ? "Table-first triage is active. Click a row to open the detail drawer."
            : "Filters are visible. Start with Risk and First Thing To Try, then narrow the list if needed.";
    }

    private void UpdateOnboardingTip()
    {
        object activeTab = GetActiveMainTab();
        BorderOnboardingTip.Visibility = Visibility.Visible;
        GridOnboardingMessage.Visibility = _onboardingTipsDismissed ? Visibility.Collapsed : Visibility.Visible;
        GridQuickWorkflowSteps.Visibility = !ReferenceEquals(activeTab, TabFindings)
            ? Visibility.Collapsed
            : Visibility.Visible;
        BtnDismissOnboardingTip.IsEnabled = !_isBusy;
        if (!_onboardingTipsDismissed)
        {
            TxtOnboardingTip.Text = BuildOnboardingTipText();
        }
    }

    private string BuildOnboardingTipText()
    {
        object activeTab = GetActiveMainTab();

        if (ReferenceEquals(activeTab, TabLoadOrder))
        {
            return "Load Order: Do These Moves covers only enabled modules that need action. Exact Slots is the authoritative order for the active stack.";
        }

        if (ReferenceEquals(activeTab, TabValidate))
        {
            return "Validate: try the current stack in game first. Check game runs only if something actually goes wrong.";
        }

        if (ReferenceEquals(activeTab, TabFindings))
        {
            return _compactFindingsLayout
                ? "Findings: table first. Select a row to open the right-side drawer, then jump directly to Load Order or Validate."
                : "Findings: start with the table, then use Refine Results only when you need filters.";
        }

        return "Run scan, review findings, apply load order, then validate the stack in-game.";
    }

    private object GetActiveMainTab()
    {
        return MainTabs.SelectedItem ?? TabFindings;
    }

    private void InitializePriorityQueue()
    {
        _priorityQueueRows.Clear();
        _priorityQueueRows.Add(new PriorityQueueRow
        {
            Source = null,
            Display = "No queue yet. Run scan to rank top risk drivers.",
        });
        TxtPriorityQueueSummary.Text = "Run scan to generate priority queue.";
        BtnFocusPriorityQueueSelection.IsEnabled = false;
    }

    private void InitializeCriticalRuntimeDrawer()
    {
        _criticalRuntimeDrawerRows.Clear();
        _criticalRuntimeDrawerRows.Add(new CriticalDrawerRow
        {
            Source = null,
            ModuleFocus = "module-set",
            Display = "No runtime-priority blockers yet. Run scan to populate this drawer.",
            Reason = "Runtime-linked critical/high blockers will appear here with the reason they are prioritized.",
        });
        TxtCriticalRuntimeDrawerSummary.Text = "Run scan to populate runtime-priority blockers.";
        BtnFocusCriticalRuntimeSelection.IsEnabled = false;
        BtnFilterCriticalRuntimeModule.IsEnabled = false;
    }

    private void UpdateQuickFilterChips()
    {
        int critical = _allFindings.Count(f => f.Source.Severity == ConflictSeverity.Critical);
        int high = _allFindings.Count(f => f.Source.Severity == ConflictSeverity.High);
        int runtimeLinked = _allFindings.Count(f => IsRuntimeSignalFinding(f.Source));

        BtnQuickCriticalChip.Content = $"Critical: {critical}";
        BtnQuickHighChip.Content = $"High: {high}";
        BtnQuickRuntimeChip.Content = $"Runtime Confirmed: {runtimeLinked}";

        BtnQuickCriticalChip.IsEnabled = !_isBusy && critical > 0;
        BtnQuickHighChip.IsEnabled = !_isBusy && high > 0;
        BtnQuickRuntimeChip.IsEnabled = !_isBusy && runtimeLinked > 0;
        BtnFocusPriorityQueueSelection.IsEnabled = !_isBusy && _priorityQueueRows.Any(x => x.Source is not null);
        bool hasCriticalDrawerRows = _criticalRuntimeDrawerRows.Any(x => x.Source is not null);
        BtnFocusCriticalRuntimeSelection.IsEnabled = !_isBusy && hasCriticalDrawerRows;
        BtnFilterCriticalRuntimeModule.IsEnabled = !_isBusy && hasCriticalDrawerRows;
    }

    private static bool IsRuntimeSignalFinding(ConflictFinding finding)
    {
        return IsRuntimeConfirmed(finding)
            || finding.Category is ConflictCategory.RuntimeModuleSetMismatch
            or ConflictCategory.RuntimeLoaderFailure
            or ConflictCategory.RuntimeCrashSession;
    }

    private void ResetRiskScorePanel()
    {
        _riskTopDriverRows.Clear();
        TxtRiskScore.Text = "Risk Score: n/a";
        TxtRiskScoreHint.Text = "Risk score updates after each scan.";
        TxtRiskTopDrivers.Text = "Top drivers: n/a";
        RiskScoreBar.Value = 0;
        RiskScoreBar.Foreground = ParseBrush("#B88A3C");
        BtnFocusRiskDriver.IsEnabled = false;
    }

    private void UpdateRiskScorePanel(ScanReport report, ScanReport? previousReport)
    {
        double score = ComputeRiskScore(report);
        double? previousScore = previousReport is null ? null : ComputeRiskScore(previousReport);
        string trend = previousScore is null
            ? "No previous baseline in this app session."
            : score > previousScore + 0.75
                ? $"Trend: +{score - previousScore:0.0} vs previous scan."
                : score < previousScore - 0.75
                    ? $"Trend: {score - previousScore:0.0} vs previous scan."
                    : "Trend: stable vs previous scan.";

        TxtRiskScore.Text = $"Risk Score: {score:0}/100 ({BuildRiskBandLabel(score)})";
        TxtRiskScoreHint.Text = trend;
        RiskScoreBar.Value = score;
        RiskScoreBar.Foreground = score >= 75
            ? ParseBrush("#C25E6A")
            : score >= 50
                ? ParseBrush("#D0A158")
            : score >= 30
                    ? ParseBrush("#B88A3C")
                    : ParseBrush("#5E7658");
    }

    private void UpdateRiskTopDrivers()
    {
        _riskTopDriverRows.Clear();
        _priorityQueueRows.Clear();
        _criticalRuntimeDrawerRows.Clear();
        if (_allFindings.Count == 0)
        {
            TxtRiskTopDrivers.Text = "Top drivers: n/a";
            BtnFocusRiskDriver.IsEnabled = false;
            _priorityQueueRows.Add(new PriorityQueueRow
            {
                Source = null,
                Display = "No queue yet. Run scan to rank top risk drivers.",
            });
            TxtPriorityQueueSummary.Text = "Run scan to generate priority queue.";
            BtnFocusPriorityQueueSelection.IsEnabled = false;
            _criticalRuntimeDrawerRows.Add(new CriticalDrawerRow
            {
                Source = null,
                ModuleFocus = "module-set",
                Display = "No runtime-priority blockers yet. Run scan to populate this drawer.",
                Reason = "Runtime-linked critical/high blockers will appear here with the reason they are prioritized.",
            });
            TxtCriticalRuntimeDrawerSummary.Text = "Run scan to populate runtime-priority blockers.";
            BtnFocusCriticalRuntimeSelection.IsEnabled = false;
            BtnFilterCriticalRuntimeModule.IsEnabled = false;
            return;
        }

        List<(FindingRow Row, double Score)> ranked = _allFindings
            .Select(row => (Row: row, Score: ComputeRiskContribution(row.Source)))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Row.Source.Confidence)
            .ToList();
        foreach ((FindingRow Row, double _) in ranked.Take(5))
        {
            _riskTopDriverRows.Add(Row);
        }
        foreach ((FindingRow Row, double Score) in ranked.Take(8))
        {
            string module = Row.Source.ModuleIds.FirstOrDefault() ?? "module-set";
            _priorityQueueRows.Add(new PriorityQueueRow
            {
                Source = Row,
                Display = $"[{Row.Severity}] {Row.PlayerHeadline} | {module} | score {Score:0.0}",
            });
        }
        TxtPriorityQueueSummary.Text =
            $"Top {_priorityQueueRows.Count(x => x.Source is not null)} risk drivers ranked by severity, runtime evidence, and confidence.";

        string summary = string.Join(" | ", ranked.Take(3).Select((x, idx) =>
        {
            string module = x.Row.Source.ModuleIds.FirstOrDefault() ?? "module-set";
            return $"{idx + 1}) {x.Row.PlayerHeadline} ({module})";
        }));
        TxtRiskTopDrivers.Text = summary.Length == 0
            ? "Top drivers: n/a"
            : $"Top drivers: {summary}";
        BtnFocusRiskDriver.IsEnabled = !_isBusy && _riskTopDriverRows.Count > 0;
        BtnFocusPriorityQueueSelection.IsEnabled = !_isBusy && _priorityQueueRows.Any(x => x.Source is not null);
        UpdateCriticalRuntimeDrawer(ranked);
    }

    private void UpdateCriticalRuntimeDrawer(IReadOnlyList<(FindingRow Row, double Score)> ranked)
    {
        _criticalRuntimeDrawerRows.Clear();

        List<(FindingRow Row, double Score)> actionable = ranked
            .Where(x => x.Row.Source.Severity is ConflictSeverity.Critical or ConflictSeverity.High)
            .ToList();

        if (actionable.Count == 0)
        {
            _criticalRuntimeDrawerRows.Add(new CriticalDrawerRow
            {
                Source = null,
                ModuleFocus = "module-set",
                Display = "No critical/high blockers in this scan.",
                Reason = "No actionable blocker currently qualifies for this drawer.",
            });
            TxtCriticalRuntimeDrawerSummary.Text = "No critical/high blockers in this scan.";
            BtnFocusCriticalRuntimeSelection.IsEnabled = false;
            BtnFilterCriticalRuntimeModule.IsEnabled = false;
            return;
        }

        List<(FindingRow Row, double Score)> ordered = actionable
            .OrderByDescending(x => IsRuntimeSignalFinding(x.Row.Source))
            .ThenByDescending(x => x.Row.Source.Severity)
            .ThenByDescending(x => x.Score)
            .ThenByDescending(x => x.Row.Source.Confidence)
            .ToList();

        int runtimeLinked = actionable.Count(x => IsRuntimeSignalFinding(x.Row.Source));
        foreach ((FindingRow Row, double Score) in ordered.Take(10))
        {
            string module = Row.Source.ModuleIds.FirstOrDefault() ?? "module-set";
            string runtimeBadge = IsRuntimeSignalFinding(Row.Source) ? "Runtime" : "Model";
            _criticalRuntimeDrawerRows.Add(new CriticalDrawerRow
            {
                Source = Row,
                ModuleFocus = module,
                Display = $"[{Row.Severity}][{runtimeBadge}] {Row.PlayerHeadline} | {module} | score {Score:0.0}",
                Reason = BuildCriticalDrawerReason(Row.Source),
            });
        }

        TxtCriticalRuntimeDrawerSummary.Text = runtimeLinked > 0
            ? $"Runtime-priority ordering active: {runtimeLinked}/{actionable.Count} critical/high finding(s) are runtime-linked."
            : $"No runtime-linked critical/high finding yet. Showing strongest {actionable.Count} analysis-priority blocker(s).";
        bool hasRows = _criticalRuntimeDrawerRows.Any(x => x.Source is not null);
        BtnFocusCriticalRuntimeSelection.IsEnabled = !_isBusy && hasRows;
        BtnFilterCriticalRuntimeModule.IsEnabled = !_isBusy && hasRows;
    }

    private static double ComputeRiskContribution(ConflictFinding finding)
    {
        double baseScore = finding.Severity switch
        {
            ConflictSeverity.Critical => 30,
            ConflictSeverity.High => 20,
            ConflictSeverity.Medium => 11,
            ConflictSeverity.Low => 5,
            _ => 2,
        };

        if (IsRuntimeSignalFinding(finding))
        {
            baseScore += 12;
        }

        if (finding.Category is ConflictCategory.HarmonyPatchConflict
            or ConflictCategory.HarmonyPatchStack
            or ConflictCategory.LifecycleRegistrationOverlap)
        {
            baseScore += 8;
        }

        if (finding.Category is ConflictCategory.RuntimeLoaderFailure
            or ConflictCategory.RuntimeCrashSession
            or ConflictCategory.MissingDependency)
        {
            baseScore += 6;
        }

        double confidenceFactor = 0.70 + (0.60 * Math.Clamp(finding.Confidence, 0.0, 1.0));
        return baseScore * confidenceFactor;
    }

    private static double ComputeRiskScore(ScanReport report)
    {
        if (report.Conflicts.Count == 0)
        {
            return 4.0;
        }

        int severityPoints = report.Conflicts.Sum(f => f.Severity switch
        {
            ConflictSeverity.Critical => 18,
            ConflictSeverity.High => 11,
            ConflictSeverity.Medium => 6,
            ConflictSeverity.Low => 3,
            _ => 1,
        });
        int runtimeSignals = report.Conflicts.Count(IsRuntimeSignalFinding);
        double modulePressure = report.Modules.Count == 0
            ? severityPoints
            : severityPoints / (double)report.Modules.Count;

        double score = (modulePressure * 4.6) + (runtimeSignals * 2.5);
        return Math.Clamp(score, 0.0, 100.0);
    }

    private static string BuildRiskBandLabel(double score)
    {
        if (score >= 75)
        {
            return "High";
        }

        if (score >= 50)
        {
            return "Elevated";
        }

        if (score >= 30)
        {
            return "Moderate";
        }

        return "Low";
    }

    private void InitializeFilterControls()
    {
        ComboPerspectiveFilter.ItemsSource = new[]
        {
            "Gameplay Stability (Recommended)",
            "All Findings",
            "Runtime-Confirmed First",
            "Data/Noise Audit",
        };
        ComboPerspectiveFilter.SelectedIndex = 0;

        ComboConfidenceFloor.ItemsSource = new[]
        {
            "0%",
            "40%",
            "60%",
            "75%",
            "85%",
        };
        ComboConfidenceFloor.SelectedItem = "60%";

        ComboSeverityFilter.ItemsSource = new[]
        {
            "All",
            "Actionable (Critical+High)",
            "Critical",
            "High",
            "Medium",
            "Low",
            "Info",
        };
        ComboSeverityFilter.SelectedIndex = 0;

        ComboCategoryFilter.ItemsSource = new[] { "All" };
        ComboCategoryFilter.SelectedIndex = 0;
    }

    private void PopulateCategoryFilter()
    {
        List<string> categories = ["All"];
        categories.AddRange(_allFindings
            .Select(f => f.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        string? previous = ComboCategoryFilter.SelectedItem?.ToString();
        ComboCategoryFilter.ItemsSource = categories;
        ComboCategoryFilter.SelectedItem = categories.Contains(previous ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            ? previous
            : "All";
    }

    private void ApplyFilters()
    {
        string perspective = ComboPerspectiveFilter.SelectedItem?.ToString() ?? "Gameplay Stability (Recommended)";
        double confidenceFloor = ParseConfidenceFloor(ComboConfidenceFloor.SelectedItem?.ToString());
        string severity = ComboSeverityFilter.SelectedItem?.ToString() ?? "All";
        string category = ComboCategoryFilter.SelectedItem?.ToString() ?? "All";
        string search = TxtSearch.Text.Trim();
        FindingRow? previousSelection = GridFindings.SelectedItem as FindingRow;

        IEnumerable<FindingRow> filtered = _allFindings
            .Where(f => MatchesPerspectiveFilter(f, perspective));
        if (confidenceFloor > 0)
        {
            filtered = filtered.Where(f =>
                f.Source.Severity is ConflictSeverity.Critical or ConflictSeverity.High
                || IsRuntimeSignalFinding(f.Source)
                || f.Source.Confidence >= confidenceFloor);
        }
        if (severity.Equals("Actionable (Critical+High)", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(f =>
                f.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)
                || f.Severity.Equals("High", StringComparison.OrdinalIgnoreCase));
        }
        else if (!severity.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(f => f.Severity.Equals(severity, StringComparison.OrdinalIgnoreCase));
        }

        if (!category.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(f => f.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(f =>
                f.Modules.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                f.PlayerHeadline.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                f.TechnicalCause.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                f.Category.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                f.ImpactSummary.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                f.EvidenceStrength.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                f.ImpactRisk.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                f.PlayerSymptom.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                f.QuickFix.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                f.ExecutionChain.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        _visibleFindings.Clear();
        foreach (FindingRow row in filtered)
        {
            _visibleFindings.Add(row);
        }

        TxtFindingsFiltered.Text = $"{_visibleFindings.Count} of {_allFindings.Count} visible findings";
        if (_visibleFindings.Count == 0)
        {
            GridFindings.SelectedItem = null;
            ResetDetailPanel(
                perspective.Equals("All Findings", StringComparison.OrdinalIgnoreCase)
                    ? "No findings match current filters. Try Severity=All and clear Search."
                    : "No findings in this perspective. Try Perspective=All Findings to inspect the full report.");
            ListEvidence.ItemsSource = null;
            ListImmediateSteps.ItemsSource = null;
            UpdateFindingsSeveritySnapshot();
            UpdateActiveFindingsFiltersSummary();
            UpdateQuickFilterChips();
            return;
        }

        bool keepTablePrimary = true;
        if (previousSelection is not null && _visibleFindings.Contains(previousSelection))
        {
            GridFindings.SelectedItem = previousSelection;
        }
        else if (keepTablePrimary)
        {
            GridFindings.SelectedItem = null;
            ResetDetailPanel("Select a finding to open the detail drawer.");
        }
        else
        {
            GridFindings.SelectedIndex = 0;
        }

        UpdateFindingsSeveritySnapshot();
        UpdateActiveFindingsFiltersSummary();
        UpdateQuickFilterChips();
        UpdateFindingsSelectionSummary(GridFindings.SelectedItem as FindingRow);
    }

    private void UpdateFindingsSeveritySnapshot()
    {
        string perspective = ComboPerspectiveFilter.SelectedItem?.ToString() ?? "Gameplay Stability (Recommended)";
        double confidenceFloor = ParseConfidenceFloor(ComboConfidenceFloor.SelectedItem?.ToString());
        string floorLabel = confidenceFloor <= 0 ? "none" : $"{confidenceFloor:P0}";
        string perspectiveLabel = perspective switch
        {
            "Gameplay Stability (Recommended)" => "Gameplay",
            "All Findings" => "All",
            "Runtime-Confirmed First" => "Runtime-first",
            "Data/Noise Audit" => "Data/noise",
            _ => perspective,
        };

        if (_allFindings.Count == 0)
        {
            TxtFindingsPlayerStatus.Text = "Visible findings: no scan yet.";
            TxtFindingsSeveritySnapshot.Text = $"Perspective: {perspectiveLabel}. Confidence floor: {floorLabel}. Visible mix: no findings yet.";
            TxtFindingsSeveritySnapshot.Foreground = ParseBrush("#E2D1B6");
            TxtFindingsPlayerStatus.Foreground = ParseBrush("#D9C8AB");
            return;
        }

        if (_visibleFindings.Count == 0)
        {
            TxtFindingsPlayerStatus.Text = "Visible findings: current filters hide everything.";
            TxtFindingsSeveritySnapshot.Text = $"Perspective: {perspectiveLabel}. Confidence floor: {floorLabel}. Visible mix: none (current filters hide all findings).";
            TxtFindingsSeveritySnapshot.Foreground = ParseBrush("#D3C1A2");
            TxtFindingsPlayerStatus.Foreground = ParseBrush("#D9C8AB");
            return;
        }

        int critical = _visibleFindings.Count(f => f.Source.Severity == ConflictSeverity.Critical);
        int high = _visibleFindings.Count(f => f.Source.Severity == ConflictSeverity.High);
        int medium = _visibleFindings.Count(f => f.Source.Severity == ConflictSeverity.Medium);
        int lowOrInfo = _visibleFindings.Count - critical - high - medium;
        int observed = _visibleFindings.Count(f =>
            f.Source.Category is ConflictCategory.RuntimeLoaderFailure or ConflictCategory.RuntimeModuleSetMismatch
            || IsRuntimeConfirmed(f.Source));
        int saveWarnings = _visibleFindings.Count(f => f.Source.Category == ConflictCategory.SaveFileRisk);
        int staticWarnings = Math.Max(0, _visibleFindings.Count - observed - saveWarnings);
        TxtFindingsPlayerStatus.Text = $"Visible findings: {observed} observed evidence, {staticWarnings} scan-only warning(s), {saveWarnings} save compatibility warning(s).";
        TxtFindingsSeveritySnapshot.Text = $"Visible mix: {critical} Critical, {high} High, {medium} Medium, {lowOrInfo} Low/Info.";
        TxtFindingsSeveritySnapshot.Foreground = critical > 0
            ? ParseBrush("#FFC8D2")
            : high > 0
                ? ParseBrush("#FFE1B0")
                : ParseBrush("#E2D1B6");
        TxtFindingsPlayerStatus.Foreground = observed > 0
            ? ParseBrush("#EAD8B4")
            : saveWarnings > 0
                ? ParseBrush("#E5D0AA")
                : ParseBrush("#D9C8AB");
    }

    private void UpdateActiveFindingsFiltersSummary()
    {
        string perspective = ComboPerspectiveFilter.SelectedItem?.ToString() ?? "Gameplay Stability (Recommended)";
        string severity = ComboSeverityFilter.SelectedItem?.ToString() ?? "All";
        string category = ComboCategoryFilter.SelectedItem?.ToString() ?? "All";
        string search = TxtSearch.Text.Trim();
        double confidenceFloor = ParseConfidenceFloor(ComboConfidenceFloor.SelectedItem?.ToString());

        List<string> parts = [];
        if (!perspective.Equals("Gameplay Stability (Recommended)", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"Perspective: {perspective}");
        }

        if (!severity.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"Severity: {severity}");
        }

        if (!category.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"Category: {category}");
        }

        if (confidenceFloor > 0)
        {
            parts.Add($"Confidence >= {confidenceFloor:P0}");
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            parts.Add($"Search: \"{search}\"");
        }

        TxtActiveFindingsFilters.Text = parts.Count == 0
            ? "Active filters: defaults (gameplay stability, all severities, all categories)."
            : $"Active filters: {string.Join(" | ", parts)}.";
    }

    private void UpdateFindingsSelectionSummary(FindingRow? selected)
    {
        if (selected is null)
        {
            TxtSelectedFindingSummary.Text = _visibleFindings.Count == 0
                ? "Selected finding: none."
                : "Selected finding: none. Click a row for full details.";
            TxtSelectedFindingSummary.Foreground = ParseBrush("#D8C7AA");
            return;
        }

        string moduleSummary = selected.Modules;
        if (moduleSummary.Length > 90)
        {
            moduleSummary = $"{moduleSummary[..87]}...";
        }

        string runtimeLabel = IsRuntimeConfirmed(selected.Source) ? "runtime-confirmed" : "analysis-only";
        TxtSelectedFindingSummary.Text = selected.Source.Category == ConflictCategory.SaveFileRisk
            ? $"Selected: [{selected.Severity}] {selected.PlayerHeadline} ({runtimeLabel}) | {moduleSummary} | Affected saves: {BuildSaveRiskSaveSummary(selected.Source, 3)}"
            : $"Selected: [{selected.Severity}] {selected.PlayerHeadline} ({runtimeLabel}) | {moduleSummary}";
        TxtSelectedFindingSummary.Foreground = selected.Source.Severity switch
        {
            ConflictSeverity.Critical => ParseBrush("#FFD2DA"),
            ConflictSeverity.High => ParseBrush("#FFE1B6"),
            _ => ParseBrush("#E2D1B6"),
        };
    }

    private static bool MatchesPerspectiveFilter(FindingRow row, string perspective)
    {
        if (perspective.Equals("All Findings", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (perspective.Equals("Runtime-Confirmed First", StringComparison.OrdinalIgnoreCase))
        {
            return IsRuntimeConfirmed(row.Source)
                || row.Source.Category is ConflictCategory.RuntimeModuleSetMismatch
                or ConflictCategory.RuntimeLoaderFailure
                or ConflictCategory.RuntimeCrashSession;
        }

        if (perspective.Equals("Data/Noise Audit", StringComparison.OrdinalIgnoreCase))
        {
            return IsDataNoiseCategory(row.Source.Category);
        }

        return IsGameplayStabilityCategory(row.Source.Category);
    }

    private static bool IsGameplayStabilityCategory(ConflictCategory category)
    {
        return category is ConflictCategory.MissingDependency
            or ConflictCategory.ExplicitIncompatibility
            or ConflictCategory.LoadOrderViolation
            or ConflictCategory.HarmonyPatchConflict
            or ConflictCategory.HarmonyPatchStack
            or ConflictCategory.BehaviorEventOverlap
            or ConflictCategory.GameModelOverlap
            or ConflictCategory.MissionBehaviorOverlap
            or ConflictCategory.LifecycleRegistrationOverlap
            or ConflictCategory.AssemblyReferenceMismatch
            or ConflictCategory.SaveFileRisk
            or ConflictCategory.RuntimeModuleSetMismatch
            or ConflictCategory.RuntimeLoaderFailure
            or ConflictCategory.RuntimeCrashSession;
    }

    private static bool IsDataNoiseCategory(ConflictCategory category)
    {
        return category is ConflictCategory.DependencyVersionMismatch
            or ConflictCategory.DllCollision
            or ConflictCategory.XmlEntityCollision
            or ConflictCategory.ModuleDataFileCollision
            or ConflictCategory.AnalyzerWarning;
    }

    private bool TryFocusFirstCriticalFinding(bool applyCriticalFilter, bool switchToFindingsTab)
    {
        if (_allFindings.Count == 0)
        {
            return false;
        }

        if (applyCriticalFilter)
        {
            ComboPerspectiveFilter.SelectedItem = "All Findings";
            ComboSeverityFilter.SelectedItem = "Critical";
            ComboCategoryFilter.SelectedItem = "All";
            TxtSearch.Text = string.Empty;
            ApplyFilters();
        }

        FindingRow? critical = _visibleFindings
            .FirstOrDefault(f => f.Source.Severity == ConflictSeverity.Critical)
            ?? _allFindings.FirstOrDefault(f => f.Source.Severity == ConflictSeverity.Critical);

        if (critical is null)
        {
            return false;
        }

        if (!_visibleFindings.Contains(critical))
        {
            ComboPerspectiveFilter.SelectedItem = "All Findings";
            ComboSeverityFilter.SelectedItem = "Critical";
            ComboCategoryFilter.SelectedItem = "All";
            TxtSearch.Text = string.Empty;
            ApplyFilters();
        }

        GridFindings.SelectedItem = critical;
        GridFindings.ScrollIntoView(critical);
        if (switchToFindingsTab)
        {
            MainTabs.SelectedItem = TabFindings;
        }

        string moduleSummary = critical.Source.ModuleIds.Count == 0
            ? "module set unknown"
            : FormatModuleSet(critical.Source.ModuleIds, 3);
        SetStatus($"Focused critical finding: {critical.PlayerHeadline} ({moduleSummary}).");
        return true;
    }

    private void StartIsolationWorkflow(ConflictFinding seedFinding, string sourceLabel)
    {
        IsolationSeedMode seedMode = GetIsolationSeedMode(seedFinding);
        if (seedMode == IsolationSeedMode.Unsupported)
        {
            ResetIsolationWorkflow("This finding is not a cohort-isolation problem.");
            if (seedFinding.Category == ConflictCategory.SaveFileRisk)
            {
                TxtIsolationFocusSummary.Text = $"Affected saves: {BuildSaveRiskSaveSummary(seedFinding, 10)}. Save-risk findings tell you which campaigns need their original mod set; toggling mods off does not prove compatibility for those saves.";
                TxtIsolationCurrentDisableSet.Text = "No disable cohort. This is save provenance, not a runtime interaction test.";
                TxtIsolationCurrentFoundationSet.Text = FormatModuleSet(seedFinding.ModuleIds, 6);
                TxtIsolationCurrentKeepSet.Text = "Open the selected finding details for the exact save list and missing mods per save.";
                TxtIsolationNextAction.Text = "Use the affected-save list to decide which campaigns need the original profile restored. If you want live compatibility science, build the lab from a runtime, Harmony, model, or behavior overlap finding instead.";
                SetIsolationActionChrome(
                    disableHeader: "No Disable Cohort",
                    foundationHeader: "Mods Missing From Profile",
                    keepHeader: "What To Do Instead",
                    issueGoneLabel: "Compatible In Practice",
                    issuePersistsLabel: "Issue Reproduced");
                SetStatus("Isolation plan not created: save-risk findings need profile restoration, not mod-disable cohorts.");
            }
            else
            {
                TxtIsolationFocusSummary.Text = "Selected finding has a direct fix path and does not become more truthful through a disable-cohort experiment.";
                TxtIsolationCurrentDisableSet.Text = "No disable cohort for this finding type.";
                TxtIsolationCurrentFoundationSet.Text = FormatModuleSet(seedFinding.ModuleIds, 6);
                TxtIsolationCurrentKeepSet.Text = "Apply the direct fix shown in the finding details first.";
                TxtIsolationNextAction.Text = "Use Findings or Load Order for the direct fix path. Isolation Lab is reserved for runtime or overlap findings that need real repro validation.";
                SetStatus("Isolation plan not created: selected finding should be handled through its direct fix path.");
            }

            UpdateIsolationWorkflowButtons();
            SelectValidateTab(expandRuntimeEvidence: false);
            return;
        }

        List<string> candidates = BuildIsolationCandidatePool(
            seedFinding,
            expandWithRelatedFindings: ShouldExpandIsolationSeedWithRelatedFindings(seedFinding, seedMode));
        _isolationFoundationModules.Clear();
        _isolationFoundationModules.AddRange(BuildIsolationFoundationModules(seedFinding, candidates));
        _isolationStepRows.Clear();
        _isolationPendingStep = null;
        _isolationIteration = 0;

        if (candidates.Count == 0)
        {
            ResetIsolationWorkflow("Isolation could not find a player-controlled suspect cohort for this finding.");
            TxtIsolationFocusSummary.Text = "Selected finding only references framework/core modules or a non-actionable foundation set. Pick a finding that includes actual player mods.";
            TxtIsolationCurrentDisableSet.Text = "-";
            TxtIsolationCurrentFoundationSet.Text = FormatModuleSet(_isolationFoundationModules, 6);
            TxtIsolationCurrentKeepSet.Text = "-";
            SetStatus("Isolation plan not created: no player-controlled suspect modules were found in this finding context.");
            return;
        }

        _isolationCandidates.Clear();
        _isolationCandidates.AddRange(candidates);

        if (seedMode == IsolationSeedMode.CompatibilityValidation)
        {
            StartIsolationCompatibilityValidation(seedFinding, sourceLabel);
            SelectValidateTab(expandRuntimeEvidence: false);
            _workflowFixTouched = true;
            UpdateWorkflowRail();
            SetStatus($"Compatibility validation started with {_isolationCandidates.Count} suspect module(s).");
            return;
        }

        TxtIsolationFocusSummary.Text = $"Isolation seed: {ToDisplayCategory(seedFinding.Category)} from {sourceLabel}. Suspect rail includes only player-controlled mods; frameworks and official foundations stay locked.";
        SyncIsolationCandidateRows();
        if (_isolationCandidates.Count == 1)
        {
            string culprit = _isolationCandidates[0];
            SetIsolationActionChrome(
                disableHeader: "Turn Off This Suspect",
                foundationHeader: "Keep Foundations Active",
                keepHeader: "Other Suspects Stay On",
                issueGoneLabel: "Issue Gone",
                issuePersistsLabel: "Issue Persists");
            TxtIsolationFocusSummary.Text = "Only one player-controlled suspect remains in this evidence cluster. This is now a direct validation pass, not a binary split.";
            TxtIsolationCurrentDisableSet.Text = culprit;
            TxtIsolationCurrentFoundationSet.Text = FormatModuleSet(_isolationFoundationModules, 6);
            TxtIsolationCurrentKeepSet.Text = "No alternate suspect cohort remains.";
            TxtIsolationNextAction.Text = $"Direct validation: keep foundations active, disable only '{culprit}', reproduce once, then re-enable to confirm.";
            UpdateIsolationWorkflowButtons();
            SelectValidateTab(expandRuntimeEvidence: false);
            _workflowFixTouched = true;
            UpdateWorkflowRail();
            SetStatus($"Isolation narrowed immediately to one suspect module: {culprit}.");
            return;
        }

        GenerateNextIsolationStep();
        SelectValidateTab(expandRuntimeEvidence: false);
        _workflowFixTouched = true;
        UpdateWorkflowRail();
        SetStatus($"Isolation plan started with {_isolationCandidates.Count} suspect module(s); {_isolationFoundationModules.Count} foundation module(s) stay locked.");
    }

    private void StartIsolationCompatibilityValidation(ConflictFinding seedFinding, string sourceLabel)
    {
        SyncIsolationCandidateRows();
        _lastValidationModulesText = FormatModuleSet(_isolationCandidates, 6);
        _lastValidationGoalText = BuildIsolationValidationGoal(seedFinding);
        _isolationIteration++;
        _isolationPendingStep = new IsolationPendingStep(
            _isolationIteration,
            IsolationStepMode.CompatibilityValidation,
            disableModules: [],
            keepModules: _isolationCandidates);

        _isolationStepRows.Add(new IsolationStepRow
        {
            Step = _isolationIteration.ToString(),
            DisableSet = "None (keep all suspect mods on)",
            FoundationSet = FormatModuleSet(_isolationFoundationModules, limit: 8),
            KeepSet = FormatModuleSet(_isolationCandidates, limit: 8),
            Outcome = "Pending",
            RemainingAfter = _isolationCandidates.Count.ToString(),
        });

        SetIsolationActionChrome(
            disableHeader: "Keep Suspect Mods On",
            foundationHeader: "Keep Foundations Active",
            keepHeader: "Validation Goal",
            issueGoneLabel: "Compatible In Practice",
            issuePersistsLabel: "Issue Reproduced");
        TxtIsolationFocusSummary.Text = $"Compatibility validation seed: {ToDisplayCategory(seedFinding.Category)} from {sourceLabel}. Start by keeping the suspect mods together and reproducing the relevant gameplay path.";
        TxtIsolationFocusSummary.Text = IsPostfixOnlyHarmonyFinding(seedFinding)
            ? "This Harmony overlap is postfix-only. Postfix stacks are usually safe, so the first pass keeps every suspect mod enabled and checks whether the gameplay path is actually stable."
            : "Start with a real compatibility check: keep suspect mods together, run the relevant gameplay path once, and only isolate modules if the issue reproduces.";
        TxtIsolationCurrentDisableSet.Text = FormatModuleSet(_isolationCandidates, 6);
        TxtIsolationCurrentFoundationSet.Text = FormatModuleSet(_isolationFoundationModules, 6);
        TxtIsolationCurrentKeepSet.Text = _lastValidationGoalText;
        TxtIsolationNextAction.Text =
            $"Step {_isolationIteration}: keep suspect mods [{FormatModuleSet(_isolationCandidates, 6)}] together, keep foundations [{FormatModuleSet(_isolationFoundationModules, 6)}], reproduce the target gameplay path once, then choose whether the stack looks compatible in practice or reproduced the issue.";
        UpdateIsolationWorkflowButtons();
    }

    private void ApplyIsolationValidationOutcome(bool stableTogether)
    {
        string outcome = stableTogether ? "Compatible In Practice" : "Issue Reproduced";
        UpdateIsolationPendingRow(outcome, stableTogether ? "Compatible" : _isolationCandidates.Count.ToString());

        int completedStep = _isolationPendingStep!.Step;
        _isolationPendingStep = null;

        if (stableTogether)
        {
            TxtIsolationFocusSummary.Text = "The issue did not reproduce while all suspect mods stayed enabled together. Treat this as compatible in practice for the tested path, then re-check only after updates or new symptoms.";
            TxtIsolationCurrentDisableSet.Text = "No disable step needed.";
            TxtIsolationCurrentFoundationSet.Text = FormatModuleSet(_isolationFoundationModules, 6);
            TxtIsolationCurrentKeepSet.Text = FormatModuleSet(_isolationCandidates, 6);
            TxtIsolationNextAction.Text = "Keep the current stack. Re-run this validation after mod updates or if the gameplay symptom appears later.";
            SetStatus($"Compatibility validation marked this stack compatible in practice at step {completedStep}.");
            UpdateIsolationWorkflowButtons();
            return;
        }

        if (_isolationCandidates.Count <= 1)
        {
            string culprit = _isolationCandidates.Count == 1 ? _isolationCandidates[0] : "the remaining suspect module";
            SetIsolationActionChrome(
                disableHeader: "Optional Single-Mod Check",
                foundationHeader: "Keep Foundations Active",
                keepHeader: "Why This Matters",
                issueGoneLabel: "Issue Gone",
                issuePersistsLabel: "Issue Persists");
            TxtIsolationFocusSummary.Text = "The issue reproduced with the full stack enabled, but only one player-controlled suspect is in scope. The next step is optional single-mod proof, not another cohort split.";
            TxtIsolationCurrentDisableSet.Text = culprit;
            TxtIsolationCurrentFoundationSet.Text = FormatModuleSet(_isolationFoundationModules, 6);
            TxtIsolationCurrentKeepSet.Text = "No alternate suspect cohort remains.";
            TxtIsolationNextAction.Text = $"Optional proof step: disable only '{culprit}' once while keeping the same foundations active. If the symptom disappears, that module is the strongest remaining suspect in this seed.";
            SetStatus($"Compatibility validation reproduced the issue; only one suspect remains in scope: {culprit}.");
            UpdateIsolationWorkflowButtons();
            return;
        }

        TxtIsolationFocusSummary.Text = $"Compatibility validation reproduced the issue. Starting cohort isolation across {_isolationCandidates.Count} suspect module(s).";
        GenerateNextIsolationStep();
        _workflowFixTouched = true;
        UpdateWorkflowRail();
        SetStatus($"Compatibility validation reproduced the issue at step {completedStep}; cohort isolation started.");
    }

    private void UpdateIsolationPendingRow(string outcome, string remainingAfter)
    {
        if (_isolationPendingStep is null)
        {
            return;
        }

        int rowIndex = _isolationStepRows
            .Select((row, index) => new { row, index })
            .Where(x => x.row.Step == _isolationPendingStep.Step.ToString() && x.row.Outcome.Equals("Pending", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.index)
            .DefaultIfEmpty(-1)
            .First();
        if (rowIndex < 0)
        {
            return;
        }

        IsolationStepRow old = _isolationStepRows[rowIndex];
        _isolationStepRows[rowIndex] = new IsolationStepRow
        {
            Step = old.Step,
            DisableSet = old.DisableSet,
            FoundationSet = old.FoundationSet,
            KeepSet = old.KeepSet,
            Outcome = outcome,
            RemainingAfter = remainingAfter,
        };
    }

    private void SetIsolationActionChrome(
        string disableHeader,
        string foundationHeader,
        string keepHeader,
        string issueGoneLabel,
        string issuePersistsLabel)
    {
        TxtIsolationDisableHeader.Text = disableHeader;
        TxtIsolationFoundationHeader.Text = foundationHeader;
        TxtIsolationKeepHeader.Text = keepHeader;
        BtnIsolationIssueGone.Content = issueGoneLabel;
        BtnIsolationIssuePersists.Content = issuePersistsLabel;
    }

    private static IsolationSeedMode GetIsolationSeedMode(ConflictFinding finding)
    {
        return finding.Category switch
        {
            ConflictCategory.SaveFileRisk => IsolationSeedMode.Unsupported,
            ConflictCategory.LoadOrderViolation => IsolationSeedMode.Unsupported,
            ConflictCategory.AssemblyReferenceMismatch => IsolationSeedMode.Unsupported,
            ConflictCategory.RuntimeModuleSetMismatch => IsolationSeedMode.Unsupported,
            ConflictCategory.RuntimeLoaderFailure => IsolationSeedMode.BinaryIsolation,
            ConflictCategory.HarmonyPatchConflict or ConflictCategory.HarmonyPatchStack
                or ConflictCategory.GameModelOverlap
                or ConflictCategory.BehaviorEventOverlap
                or ConflictCategory.MissionBehaviorOverlap
                when !IsRuntimeConfirmed(finding) => IsolationSeedMode.CompatibilityValidation,
            _ => IsolationSeedMode.BinaryIsolation,
        };
    }

    private static bool ShouldExpandIsolationSeedWithRelatedFindings(ConflictFinding finding, IsolationSeedMode seedMode)
    {
        return seedMode == IsolationSeedMode.BinaryIsolation && IsIsolationRuntimePriority(finding);
    }

    private static string BuildIsolationValidationGoal(ConflictFinding finding)
    {
        string? target = TryExtractHarmonyTarget(finding);
        if (!string.IsNullOrWhiteSpace(target))
        {
            return $"Reproduce the gameplay path touching {target} while the whole suspect stack stays enabled.";
        }

        return finding.Category switch
        {
            ConflictCategory.GameModelOverlap => "Exercise the shared gameplay system once with all suspect mods still enabled.",
            ConflictCategory.BehaviorEventOverlap => "Trigger the overlapping campaign/event behavior once with the current stack intact.",
            ConflictCategory.MissionBehaviorOverlap => "Run one short mission or battle with the current stack intact and watch the affected behavior path.",
            _ => "Run the exact gameplay path that made this finding interesting while all suspect mods stay enabled together.",
        };
    }

    private void ResetIsolationWorkflow(string summary)
    {
        _isolationCandidates.Clear();
        _isolationFoundationModules.Clear();
        _isolationStepRows.Clear();
        _isolationPendingStep = null;
        _isolationIteration = 0;
        _lastValidationGoalText = "-";
        _lastValidationModulesText = "-";

        TxtIsolationFocusSummary.Text = "Isolation Lab tests player-controlled suspect cohorts while keeping frameworks and official foundations active.";
        TxtIsolationCurrentDisableSet.Text = "-";
        TxtIsolationCurrentFoundationSet.Text = "No locked foundations.";
        TxtIsolationCurrentKeepSet.Text = "-";
        TxtIsolationNextAction.Text = "No active isolation step. Build a plan from findings first.";
        SetIsolationActionChrome(
            disableHeader: "Turn Off This Cohort",
            foundationHeader: "Keep Foundations Active",
            keepHeader: "Other Suspects Stay On",
            issueGoneLabel: "Issue Gone",
            issuePersistsLabel: "Issue Persists");
        SyncIsolationCandidateRows();
        UpdateIsolationWorkflowButtons();
    }

    private void ApplyIsolationOutcome(bool issueGone)
    {
        if (_isolationPendingStep is null)
        {
            SetStatus("No pending isolation step. Build or continue a plan first.");
            return;
        }

        if (_isolationPendingStep.Mode == IsolationStepMode.CompatibilityValidation)
        {
            ApplyIsolationValidationOutcome(issueGone);
            return;
        }

        List<string> nextCandidates = (issueGone
                ? _isolationPendingStep.DisableModules
                : _isolationPendingStep.KeepModules)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        string outcome = issueGone ? "Issue Gone" : "Issue Persists";

        UpdateIsolationPendingRow(outcome, nextCandidates.Count.ToString());

        int completedStep = _isolationPendingStep.Step;
        _isolationPendingStep = null;
        _isolationCandidates.Clear();
        _isolationCandidates.AddRange(RankIsolationCandidates(nextCandidates));
        SyncIsolationCandidateRows();

        if (_isolationCandidates.Count <= 1)
        {
            if (_isolationCandidates.Count == 1)
            {
                string culprit = _isolationCandidates[0];
                TxtIsolationFocusSummary.Text = "Binary isolation is complete. Only one player-controlled suspect remains after keeping foundations intact.";
                TxtIsolationCurrentDisableSet.Text = culprit;
                TxtIsolationCurrentFoundationSet.Text = FormatModuleSet(_isolationFoundationModules, 6);
                TxtIsolationCurrentKeepSet.Text = "No alternate suspect cohort remains.";
                TxtIsolationNextAction.Text = $"Validate by disabling only '{culprit}' while keeping the foundation stack active, then reproduce once and re-enable to confirm.";
                SetStatus($"Isolation converged after step {completedStep}: likely culprit is {culprit}.");
            }
            else
            {
                TxtIsolationFocusSummary.Text = "The last branch eliminated every player-controlled suspect. That usually means the seed cluster was too broad or the repro changed.";
                TxtIsolationCurrentDisableSet.Text = "-";
                TxtIsolationCurrentFoundationSet.Text = FormatModuleSet(_isolationFoundationModules, 6);
                TxtIsolationCurrentKeepSet.Text = "-";
                TxtIsolationNextAction.Text = "No remaining candidates. Use Reset, then build plan from a specific runtime/Harmony finding.";
                SetStatus("Isolation branch returned no candidates.");
            }

            UpdateIsolationWorkflowButtons();
            return;
        }

        TxtIsolationFocusSummary.Text = $"Isolation step {completedStep} recorded: {outcome}. {_isolationCandidates.Count} suspect module(s) remain.";
        GenerateNextIsolationStep();
        _workflowFixTouched = true;
        UpdateWorkflowRail();
        SetStatus($"Isolation step {completedStep}: {outcome}. Remaining candidates: {_isolationCandidates.Count}.");
    }

    private void GenerateNextIsolationStep()
    {
        _isolationPendingStep = null;
        if (_isolationCandidates.Count <= 1)
        {
            UpdateIsolationWorkflowButtons();
            return;
        }

        (List<string> disableSet, List<string> keepSet) = BuildIsolationStepSets(_isolationCandidates);
        _isolationIteration++;
        _isolationPendingStep = new IsolationPendingStep(_isolationIteration, IsolationStepMode.BinaryIsolation, disableSet, keepSet);

        _isolationStepRows.Add(new IsolationStepRow
        {
            Step = _isolationIteration.ToString(),
            DisableSet = FormatModuleSet(disableSet, limit: 8),
            FoundationSet = FormatModuleSet(_isolationFoundationModules, limit: 8),
            KeepSet = FormatModuleSet(keepSet, limit: 8),
            Outcome = "Pending",
            RemainingAfter = "-",
        });

        SetIsolationActionChrome(
            disableHeader: "Turn Off This Cohort",
            foundationHeader: "Keep Foundations Active",
            keepHeader: "Other Suspects Stay On",
            issueGoneLabel: "Issue Gone",
            issuePersistsLabel: "Issue Persists");
        TxtIsolationFocusSummary.Text = "Current test disables one suspect cohort while keeping frameworks and official foundations active. Decision history is now player-actionable.";
        TxtIsolationCurrentDisableSet.Text = FormatModuleSet(disableSet, 6);
        TxtIsolationCurrentFoundationSet.Text = FormatModuleSet(_isolationFoundationModules, 6);
        TxtIsolationCurrentKeepSet.Text = keepSet.Count == 0 ? "No alternate suspect cohort." : FormatModuleSet(keepSet, 6);
        TxtIsolationNextAction.Text =
            $"Step {_isolationIteration}: turn off [{FormatModuleSet(disableSet, 6)}], keep foundations [{FormatModuleSet(_isolationFoundationModules, 6)}], leave other suspect mods [{FormatModuleSet(keepSet, 6)}] on, run the same repro, then choose outcome.";
        UpdateIsolationWorkflowButtons();
    }

    private void SyncIsolationCandidateRows()
    {
        _isolationCandidateRows.Clear();
        foreach (string moduleId in _isolationCandidates)
        {
            _isolationCandidateRows.Add(moduleId);
        }

        TxtIsolationCandidatesCount.Text = _isolationFoundationModules.Count == 0
            ? $"{_isolationCandidates.Count} suspect module(s)"
            : $"{_isolationCandidates.Count} suspect module(s) | {_isolationFoundationModules.Count} locked foundation module(s)";
    }

    private List<string> RankIsolationCandidates(IEnumerable<string> moduleIds)
    {
        HashSet<string> inputSet = moduleIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (inputSet.Count == 0)
        {
            return [];
        }

        Dictionary<string, double> scoreByModule = inputSet.ToDictionary(
            id => id,
            _ => 0.0,
            StringComparer.OrdinalIgnoreCase
        );
        foreach (FindingRow finding in _allFindings)
        {
            double score = ((int)finding.Source.Severity + 1) * Math.Max(0.25, finding.Source.Confidence);
            foreach (string moduleId in finding.Source.ModuleIds
                         .Where(inputSet.Contains)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                scoreByModule[moduleId] += score;
            }
        }

        return inputSet
            .OrderByDescending(id => scoreByModule.GetValueOrDefault(id, 0.0))
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList();
    }

    private List<string> BuildIsolationCandidatePool(ConflictFinding seedFinding, bool expandWithRelatedFindings)
    {
        HashSet<string> seedModules = seedFinding.ModuleIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seedPlayerModules = seedModules
            .Where(IsIsolationPlayerCandidateModule)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> seedOverlapScope = seedPlayerModules.Count > 0 ? seedPlayerModules : seedModules;
        HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);
        foreach (string moduleId in seedModules.Where(IsIsolationPlayerCandidateModule))
        {
            candidates.Add(moduleId);
        }

        if (!expandWithRelatedFindings)
        {
            return RankIsolationCandidates(candidates);
        }

        foreach (FindingRow finding in _allFindings)
        {
            bool moduleOverlap = CountModuleOverlap(seedOverlapScope, finding.Source.ModuleIds) > 0;
            bool evidenceOverlap = SharesIsolationEvidenceContext(seedFinding, finding.Source);
            if (!moduleOverlap && !evidenceOverlap)
            {
                continue;
            }

            foreach (string moduleId in finding.Source.ModuleIds.Where(IsIsolationPlayerCandidateModule))
            {
                candidates.Add(moduleId);
            }
        }

        return RankIsolationCandidates(candidates);
    }

    private List<string> BuildIsolationFoundationModules(ConflictFinding seedFinding, IReadOnlyCollection<string> candidates)
    {
        HashSet<string> candidateSet = candidates.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> foundations = seedFinding.ModuleIds
            .Where(ModuleTaxonomy.IsIsolationFoundation)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ModuleManifest> modulesById = _lastReport?.Modules
            .ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ModuleManifest>(StringComparer.OrdinalIgnoreCase);

        foreach (string moduleId in candidates)
        {
            if (!modulesById.TryGetValue(moduleId, out ModuleManifest? module))
            {
                continue;
            }

            foreach (ModuleDependency dep in module.Dependencies.Where(d => !d.Optional))
            {
                if (candidateSet.Contains(dep.Id))
                {
                    continue;
                }

                if (ModuleTaxonomy.IsIsolationFoundation(dep.Id))
                {
                    foundations.Add(dep.Id);
                }
            }
        }

        return OrderModulesForDisplay(foundations);
    }

    private (List<string> DisableSet, List<string> KeepSet) BuildIsolationStepSets(IReadOnlyList<string> rankedCandidates)
    {
        int target = rankedCandidates.Count == 2
            ? 1
            : (int)Math.Ceiling(rankedCandidates.Count / 2.0);
        List<List<string>> cohorts = BuildIsolationCohorts(rankedCandidates);
        List<string> disableSet = [];

        if (cohorts.Count == 1 && cohorts[0].Count == rankedCandidates.Count)
        {
            disableSet.AddRange(rankedCandidates.Take(target));
        }
        else
        {
            foreach (List<string> cohort in cohorts)
            {
                if (disableSet.Count > 0 && disableSet.Count >= target)
                {
                    break;
                }

                disableSet.AddRange(cohort);
            }
        }

        if (disableSet.Count == 0)
        {
            disableSet.Add(rankedCandidates[0]);
        }

        if (disableSet.Count >= rankedCandidates.Count)
        {
            disableSet = rankedCandidates.Take(target).ToList();
        }

        HashSet<string> disableSetLookup = disableSet.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> keepSet = rankedCandidates
            .Where(id => !disableSetLookup.Contains(id))
            .ToList();
        if (keepSet.Count == 0 && disableSet.Count > 1)
        {
            string last = disableSet[^1];
            disableSet.RemoveAt(disableSet.Count - 1);
            keepSet.Add(last);
        }

        return (disableSet, keepSet);
    }

    private List<List<string>> BuildIsolationCohorts(IReadOnlyList<string> rankedCandidates)
    {
        HashSet<string> candidateSet = rankedCandidates.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<string>> adjacency = rankedCandidates.ToDictionary(
            id => id,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ModuleManifest> modulesById = _lastReport?.Modules
            .ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ModuleManifest>(StringComparer.OrdinalIgnoreCase);

        foreach (string moduleId in rankedCandidates)
        {
            if (!modulesById.TryGetValue(moduleId, out ModuleManifest? module))
            {
                continue;
            }

            foreach (ModuleDependency dep in module.Dependencies.Where(d => !d.Optional && candidateSet.Contains(d.Id)))
            {
                adjacency[moduleId].Add(dep.Id);
                adjacency[dep.Id].Add(moduleId);
            }
        }

        List<List<string>> cohorts = [];
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        foreach (string moduleId in rankedCandidates)
        {
            if (!visited.Add(moduleId))
            {
                continue;
            }

            Queue<string> queue = new();
            queue.Enqueue(moduleId);
            List<string> cohort = [];
            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                cohort.Add(current);
                foreach (string neighbor in adjacency[current])
                {
                    if (visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            HashSet<string> cohortLookup = cohort.ToHashSet(StringComparer.OrdinalIgnoreCase);
            cohorts.Add(rankedCandidates.Where(cohortLookup.Contains).ToList());
        }

        return cohorts;
    }

    private List<string> OrderModulesForDisplay(IEnumerable<string> moduleIds)
    {
        Dictionary<string, int> orderIndex = _lastReport?.LoadOrder.CurrentOrder
            .Select((id, index) => new { id, index })
            .GroupBy(x => x.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        return moduleIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => orderIndex.GetValueOrDefault(id, int.MaxValue))
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsIsolationPlayerCandidateModule(string moduleId)
    {
        return !string.IsNullOrWhiteSpace(moduleId)
            && !ModuleTaxonomy.IsSingleplayerHiddenLoadOrderModule(moduleId)
            && !ModuleTaxonomy.IsIsolationFoundation(moduleId);
    }

    private static bool SharesIsolationEvidenceContext(ConflictFinding seedFinding, ConflictFinding otherFinding)
    {
        HashSet<string> markers = seedFinding.Evidence
            .Where(e => e.StartsWith("cluster-signature:", StringComparison.OrdinalIgnoreCase)
                || e.StartsWith("runtime-correlation:", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (markers.Count == 0)
        {
            return false;
        }

        return otherFinding.Evidence.Any(markers.Contains);
    }

    private static bool IsIsolationRuntimePriority(ConflictFinding finding)
    {
        if (finding.Category is ConflictCategory.RuntimeCrashSession
            or ConflictCategory.RuntimeLoaderFailure
            or ConflictCategory.RuntimeModuleSetMismatch)
        {
            return true;
        }

        return finding.Evidence.Any(e =>
            e.StartsWith("cluster-signature:", StringComparison.OrdinalIgnoreCase)
            || e.StartsWith("runtime-correlation:", StringComparison.OrdinalIgnoreCase)
            || e.Contains(@"\Mount and Blade II Bannerlord\logs\", StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatModuleSet(IReadOnlyList<string> modules, int limit)
    {
        if (modules.Count == 0)
        {
            return "-";
        }

        return modules.Count <= limit
            ? string.Join(", ", modules)
            : string.Join(", ", modules.Take(limit)) + $" (+{modules.Count - limit})";
    }

    private void UpdateIsolationWorkflowButtons()
    {
        bool canSeed = !_isBusy && _allFindings.Count > 0;
        BtnValidateUseSelectedFinding.IsEnabled = canSeed;
        BtnPlayerRuntimeUseTopWarning.IsEnabled = canSeed;
        BtnValidateResetCurrent.IsEnabled = !_isBusy;

        bool canRespond = !_isBusy && _isolationPendingStep is not null;
        BtnIsolationIssueGone.IsEnabled = canRespond;
        BtnIsolationIssuePersists.IsEnabled = canRespond;
        ApplyValidatePlayerSurface();
    }

    private ScanOptions BuildOptions(bool autoApply)
    {
        List<string> moduleRoots = TxtModuleRoots.Text
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        bool allowCloud = ChkAllowCloud.IsChecked == true;

        return new ScanOptions
        {
            GameVersion = string.IsNullOrWhiteSpace(TxtGameVersion.Text) ? "1.3.15" : TxtGameVersion.Text.Trim(),
            ModuleRoots = moduleRoots,
            WorkshopRoot = NormalizeNullable(TxtWorkshopRoot.Text),
            LauncherDataPath = NormalizeNullable(TxtLauncherData.Text),
            SaveRoot = NormalizeNullable(TxtSaveRoot.Text),
            HarmonyLogsRoot = NormalizeNullable(TxtHarmonyLogsRoot.Text),
            IncludeSaveFileAnalysis = ChkIncludeSave.IsChecked == true,
            IncludeLikelyIssues = ChkIncludeLikelyIssues.IsChecked != false,
            IncludeDataNoiseFindings = ChkIncludeDataNoise.IsChecked == true,
            OfflineMode = !allowCloud,
            AllowCloudMetadata = allowCloud,
            AutoApplyLoadOrder = autoApply,
            CustomModsOnlyFocus = ChkCustomOnlyFocus.IsChecked == true,
        };
    }

    private static string? NormalizeNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string GetUiPreferencesPath()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BannerlordModCompat");
        return Path.Combine(directory, UiPreferencesFileName);
    }

    private static UiPreferences LoadUiPreferences()
    {
        try
        {
            string path = GetUiPreferencesPath();
            if (!File.Exists(path))
            {
                return new UiPreferences();
            }

            string json = File.ReadAllText(path);
            UiPreferences? loaded = JsonSerializer.Deserialize<UiPreferences>(json);
            return loaded ?? new UiPreferences();
        }
        catch
        {
            return new UiPreferences();
        }
    }

    private static void SaveUiPreferences(UiPreferences preferences)
    {
        try
        {
            string path = GetUiPreferencesPath();
            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            JsonSerializerOptions options = new()
            {
                WriteIndented = true,
            };
            string json = JsonSerializer.Serialize(preferences, options);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Ignore persistence failures and keep the UI usable.
        }
    }

    private void PersistUiPreferences()
    {
        if (_suppressUiPreferencePersist)
        {
            return;
        }

        _uiPreferences.CompactFindingsLayout = ChkCompactFindingsLayout.IsChecked == true;
        _uiPreferences.ControlsPanelCollapsed = _controlsCollapsed;
        SaveUiPreferences(_uiPreferences);
    }

    private static Brush ParseBrush(string hex)
    {
        object? brush = new BrushConverter().ConvertFromString(hex);
        return brush as Brush ?? Brushes.Transparent;
    }

    private void UpdateScanProgress(ScanProgressUpdate update)
    {
        int normalized = Math.Clamp(update.Percent, 0, 100);
        _targetProgressPercent = Math.Max(_targetProgressPercent, normalized);
        _lastProgressUpdateUtc = DateTime.UtcNow;
        if (!_progressSmoother.IsEnabled)
        {
            _progressSmoother.Start();
        }

        if (!string.IsNullOrWhiteSpace(update.Stage))
        {
            TxtStatusFooter.Text = $"{update.Stage} ({_targetProgressPercent}%)";
        }
    }

    private void ProgressSmoother_Tick(object? sender, EventArgs e)
    {
        if (_isBusy)
        {
            MaybeApplySoftProgressBump();
        }

        if (_displayedProgressPercent == _targetProgressPercent)
        {
            if (!_isBusy)
            {
                _progressSmoother.Stop();
            }

            UpdateProgressFooterText();
            return;
        }

        int delta = _targetProgressPercent - _displayedProgressPercent;
        int step = Math.Clamp(Math.Abs(delta) / 6, 1, 5);
        _displayedProgressPercent += Math.Sign(delta) * step;
        if ((delta > 0 && _displayedProgressPercent > _targetProgressPercent)
            || (delta < 0 && _displayedProgressPercent < _targetProgressPercent))
        {
            _displayedProgressPercent = _targetProgressPercent;
        }

        ScanProgressBar.Value = _displayedProgressPercent;
        UpdateProgressFooterText();
    }

    private void MaybeApplySoftProgressBump()
    {
        if (_targetProgressPercent >= SoftProgressCeiling
            || _displayedProgressPercent >= SoftProgressCeiling
            || _busyStartedUtc == DateTime.MinValue
            || _lastProgressUpdateUtc == DateTime.MinValue)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (now - _busyStartedUtc < TimeSpan.FromSeconds(2.2))
        {
            return;
        }

        if (now - _lastProgressUpdateUtc < TimeSpan.FromSeconds(1.25))
        {
            return;
        }

        if (now - _lastSoftProgressBumpUtc < TimeSpan.FromMilliseconds(420))
        {
            return;
        }

        _targetProgressPercent = Math.Min(_targetProgressPercent + 1, SoftProgressCeiling);
        _lastSoftProgressBumpUtc = now;
    }

    private void UpdateProgressFooterText()
    {
        if (_isBusy && _busyStartedUtc != DateTime.MinValue)
        {
            TimeSpan elapsed = DateTime.UtcNow - _busyStartedUtc;
            string elapsedText = elapsed.TotalHours >= 1
                ? elapsed.ToString(@"hh\:mm\:ss")
                : elapsed.ToString(@"mm\:ss");
            TxtProgressFooter.Text = $"{_displayedProgressPercent}% · {elapsedText}";
            return;
        }

        TxtProgressFooter.Text = $"{_displayedProgressPercent}%";
    }

    private void SetBusy(bool busy, string? message = null, bool completedSuccessfully = false)
    {
        _isBusy = busy;

        BtnRunScan.IsEnabled = !busy;
        BtnCollectRuntimeEvidence.IsEnabled = !busy;
        BtnApplyLoadOrder.IsEnabled = !busy;
        BtnExportJson.IsEnabled = !busy && _lastReport is not null;
        BtnExportMd.IsEnabled = !busy && _lastReport is not null;
        BtnValidateRuntimeEmptyWatch.IsEnabled = !busy && _lastReport is not null;
        BtnPlayerRuntimeWatchLocal.IsEnabled = !busy && _lastReport is not null;
        BtnPresetCrashTriage.IsEnabled = !busy && _lastReport is not null;
        BtnPresetLoadOrderAudit.IsEnabled = !busy && _lastReport is not null;
        BtnPresetRuntimeValidation.IsEnabled = !busy && _lastReport is not null;
        BtnFocusPriorityQueueSelection.IsEnabled = !busy && _priorityQueueRows.Any(x => x.Source is not null);
        BtnFocusCriticalRuntimeSelection.IsEnabled = !busy && _criticalRuntimeDrawerRows.Any(x => x.Source is not null);
        BtnFilterCriticalRuntimeModule.IsEnabled = !busy && _criticalRuntimeDrawerRows.Any(x => x.Source is not null);
        ComboConfidenceFloor.IsEnabled = !busy;
        ChkCompactFindingsLayout.IsEnabled = !busy;
        BtnToggleFindingsControls.IsEnabled = !busy;
        UpdateFindingDetailActionState(GridFindings.SelectedItem is FindingRow);

        if (busy)
        {
            DateTime now = DateTime.UtcNow;
            _busyStartedUtc = now;
            _lastProgressUpdateUtc = now;
            _lastSoftProgressBumpUtc = now;
            _targetProgressPercent = 0;
            _displayedProgressPercent = 0;
            ScanProgressBar.Value = 0;
            UpdateProgressFooterText();
            if (!_progressSmoother.IsEnabled)
            {
                _progressSmoother.Start();
            }
            Mouse.OverrideCursor = Cursors.Wait;
            if (!string.IsNullOrWhiteSpace(message))
            {
                SetStatus(message);
            }
        }
        else
        {
            Mouse.OverrideCursor = null;
            if (completedSuccessfully)
            {
                _targetProgressPercent = 100;
                if (!_progressSmoother.IsEnabled)
                {
                    _progressSmoother.Start();
                }
            }
            else
            {
                _targetProgressPercent = Math.Clamp(_targetProgressPercent, 0, 100);
                if (!_progressSmoother.IsEnabled)
                {
                    _progressSmoother.Start();
                }
            }
            if (!string.IsNullOrWhiteSpace(message))
            {
                SetStatus(message);
            }

            if (completedSuccessfully || _displayedProgressPercent >= _targetProgressPercent)
            {
                _busyStartedUtc = DateTime.MinValue;
                _lastProgressUpdateUtc = DateTime.MinValue;
                _lastSoftProgressBumpUtc = DateTime.MinValue;
            }
        }

        UpdateLiveRuntimeWatchButtonState();
        UpdateRuntimeForensicsActionState();
        UpdateIsolationWorkflowButtons();
        UpdateFindingsQuickActionButtons();
        UpdateQuickFilterChips();
        UpdateWorkflowRail();
        UpdateOnboardingTip();
        BtnFocusRiskDriver.IsEnabled = !_isBusy && _riskTopDriverRows.Count > 0;
    }

    private void SetStatus(string text)
    {
        TxtStatusFooter.Text = text;
        _lastLiveRuntimeHeartbeatStatus = text;
    }

    private void UpdateLiveRuntimeWatchButtonState()
    {
        bool canRun = !_isBusy && _lastReport is not null;
        if (!canRun && _liveRuntimeWatchEnabled)
        {
            _liveRuntimeWatchEnabled = false;
            StopLiveRuntimeWatchInfrastructure();
        }

        string buttonText = _liveRuntimeWatchEnabled
            ? "Stop Live Session Watch"
            : "Start Live Session Watch";

        BtnValidateRuntimeEmptyWatch.IsEnabled = canRun;
        BtnValidateRuntimeEmptyWatch.Content = buttonText;
        BtnPlayerRuntimeWatchLocal.IsEnabled = canRun;
        BtnPlayerRuntimeWatchLocal.Content = buttonText;
    }

    private async Task PollLiveRuntimeWatchAsync()
    {
        if (!_liveRuntimeWatchEnabled || _isBusy || _liveRuntimePollInFlight || _lastReport is null)
        {
            return;
        }

        _liveRuntimeRefreshPending = false;
        _liveRuntimePollInFlight = true;
        try
        {
            ScanReport baseline = _lastReport;
            bool customOnlyFocus = BuildOptions(autoApply: false).CustomModsOnlyFocus;

            (List<ConflictFinding> runtimeSignals, List<string> runtimeWarnings) = await Task.Run(() =>
            {
                List<string> warnings = [];
                List<ConflictFinding> findings = _runtimeSessionLogAnalyzer.Analyze(
                    baseline.Modules,
                    baseline.LoadOrder.CurrentOrder,
                    customOnlyFocus,
                    warnings,
                    logsRootOverride: _liveRuntimeLogsRoot
                ).ToList();
                return (findings, warnings);
            });

            List<ConflictFinding> structuralBaseline = baseline.Conflicts
                .Where(f => f.Category is not ConflictCategory.RuntimeModuleSetMismatch
                    and not ConflictCategory.RuntimeLoaderFailure
                    and not ConflictCategory.RuntimeCrashSession)
                .ToList();
            List<ConflictFinding> mergedForCorrelation = [.. structuralBaseline, .. runtimeSignals];
            List<string> correlationWarnings = [];
            IReadOnlyList<ConflictFinding> correlated = _runtimeEvidenceCorrelator.Correlate(
                mergedForCorrelation,
                correlationWarnings
            );

            ScanReport liveOverlay = new()
            {
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                GameVersion = baseline.GameVersion,
                ScannedModuleRoots = baseline.ScannedModuleRoots,
                ScannedWorkshopRoot = baseline.ScannedWorkshopRoot,
                OverallState = baseline.OverallState,
                Modules = baseline.Modules,
                Conflicts = correlated
                    .OrderByDescending(c => c.Severity)
                    .ThenByDescending(c => c.Confidence)
                    .ToList(),
                LoadOrder = baseline.LoadOrder,
                SaveFiles = baseline.SaveFiles,
                Warnings = baseline.Warnings
                    .Concat(runtimeWarnings)
                    .Concat(correlationWarnings)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(600)
                    .ToList(),
            };

            UpdateRuntimeEvidenceSummary(liveOverlay, runtimeEvidenceRequested: true);
            PopulateRuntimeForensics(liveOverlay);
            int runtimeLinked = liveOverlay.Conflicts.Count(HasRuntimeForensicsSignal);
            _liveRuntimeLastRefreshUtc = DateTime.UtcNow;
            SetStatus($"Live runtime refresh: {runtimeSignals.Count} runtime signal(s), {runtimeLinked} runtime-linked finding(s).");
        }
        catch (Exception ex)
        {
            SetStatus($"Live runtime refresh failed: {ex.Message}");
        }
        finally
        {
            _liveRuntimePollInFlight = false;
            if (_liveRuntimeWatchEnabled && !_isBusy && _liveRuntimeRefreshPending)
            {
                _liveRuntimeDebounceTimer.Stop();
                _liveRuntimeDebounceTimer.Start();
            }
        }
    }

    private bool TryStartLiveRuntimeWatchInfrastructure(out string statusMessage)
    {
        StopLiveRuntimeWatchInfrastructure();

        string? logsRoot = ResolveRuntimeLogsRoot();
        if (string.IsNullOrWhiteSpace(logsRoot))
        {
            statusMessage = "Live runtime watch unavailable: runtime logs folder was not found under ProgramData.";
            return false;
        }

        try
        {
            FileSystemWatcher watcher = new(logsRoot, "*.txt")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = false,
            };
            watcher.Changed += LiveRuntimeLogWatcher_Changed;
            watcher.Created += LiveRuntimeLogWatcher_Changed;
            watcher.Renamed += LiveRuntimeLogWatcher_Renamed;
            watcher.EnableRaisingEvents = true;

            _liveRuntimeLogWatcher = watcher;
            _liveRuntimeLogsRoot = logsRoot;
            _liveRuntimeRefreshPending = true;
            _liveRuntimeLastEventUtc = DateTime.UtcNow;
            _liveRuntimeLastRefreshUtc = DateTime.MinValue;
            _liveRuntimeDebounceTimer.Start();
            _liveRuntimeHeartbeatTimer.Start();
            statusMessage = $"Live runtime watch started. Watching {logsRoot}.";
            return true;
        }
        catch (Exception ex)
        {
            StopLiveRuntimeWatchInfrastructure();
            statusMessage = $"Live runtime watch failed to start: {ex.Message}";
            return false;
        }
    }

    private void StopLiveRuntimeWatchInfrastructure()
    {
        _liveRuntimeDebounceTimer.Stop();
        _liveRuntimeHeartbeatTimer.Stop();
        _liveRuntimeRefreshPending = false;
        _liveRuntimeLastEventUtc = DateTime.MinValue;
        _liveRuntimeLastRefreshUtc = DateTime.MinValue;
        _liveRuntimeLogsRoot = null;

        if (_liveRuntimeLogWatcher is null)
        {
            return;
        }

        _liveRuntimeLogWatcher.EnableRaisingEvents = false;
        _liveRuntimeLogWatcher.Changed -= LiveRuntimeLogWatcher_Changed;
        _liveRuntimeLogWatcher.Created -= LiveRuntimeLogWatcher_Changed;
        _liveRuntimeLogWatcher.Renamed -= LiveRuntimeLogWatcher_Renamed;
        _liveRuntimeLogWatcher.Dispose();
        _liveRuntimeLogWatcher = null;
    }

    private void LiveRuntimeLogWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        QueueLiveRuntimeRefreshFromEvent(e.FullPath);
    }

    private void LiveRuntimeLogWatcher_Renamed(object sender, RenamedEventArgs e)
    {
        QueueLiveRuntimeRefreshFromEvent(e.FullPath);
    }

    private void QueueLiveRuntimeRefreshFromEvent(string path)
    {
        if (!_liveRuntimeWatchEnabled || !IsRelevantRuntimeLogPath(path))
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => QueueLiveRuntimeRefreshFromEvent(path), DispatcherPriority.Background);
            return;
        }

        _liveRuntimeRefreshPending = true;
        _liveRuntimeLastEventUtc = DateTime.UtcNow;
        if (_liveRuntimePollInFlight || _isBusy)
        {
            return;
        }

        _liveRuntimeDebounceTimer.Stop();
        _liveRuntimeDebounceTimer.Start();
    }

    private async void LiveRuntimeDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _liveRuntimeDebounceTimer.Stop();
        if (!_liveRuntimeWatchEnabled || _isBusy || _liveRuntimePollInFlight || !_liveRuntimeRefreshPending)
        {
            return;
        }

        await PollLiveRuntimeWatchAsync();
    }

    private void LiveRuntimeHeartbeatTimer_Tick(object? sender, EventArgs e)
    {
        if (!_liveRuntimeWatchEnabled || _isBusy || _liveRuntimePollInFlight)
        {
            return;
        }

        string rootName = string.IsNullOrWhiteSpace(_liveRuntimeLogsRoot)
            ? "runtime logs"
            : Path.GetFileName(_liveRuntimeLogsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string eventAge = _liveRuntimeLastEventUtc == DateTime.MinValue
            ? "no events yet"
            : $"{Math.Max(0, (int)(DateTime.UtcNow - _liveRuntimeLastEventUtc).TotalSeconds)}s since last change";
        string refreshAge = _liveRuntimeLastRefreshUtc == DateTime.MinValue
            ? "no refresh yet"
            : $"{Math.Max(0, (int)(DateTime.UtcNow - _liveRuntimeLastRefreshUtc).TotalSeconds)}s since refresh";
        string mode = _liveRuntimeRefreshPending ? "refresh pending" : "monitoring";
        string message = $"Live watch active ({mode}) - {eventAge}, {refreshAge} [{rootName}].";
        if (!_lastLiveRuntimeHeartbeatStatus.Equals(message, StringComparison.Ordinal))
        {
            SetStatus(message);
        }
    }

    private static string? ResolveRuntimeLogsRoot()
    {
        string commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonAppData))
        {
            return null;
        }

        string path = Path.Combine(commonAppData, "Mount and Blade II Bannerlord", "logs");
        return Directory.Exists(path) ? Path.GetFullPath(path) : null;
    }

    private static bool IsRelevantRuntimeLogPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return fileName.StartsWith("launcher_log_", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("watchdog_log_", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("rgl_log_", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("crashlist.txt", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateRuntimeEvidenceSummary(ScanReport report, bool runtimeEvidenceRequested)
    {
        int runtimeConfirmed = report.Conflicts.Count(IsRuntimeConfirmed);
        int runtimeConfirmedStructural = report.Conflicts.Count(c =>
            IsRuntimeConfirmed(c)
            && c.Category is not ConflictCategory.RuntimeModuleSetMismatch
            and not ConflictCategory.RuntimeLoaderFailure
            and not ConflictCategory.RuntimeCrashSession);
        int runtimeSignalFindings = report.Conflicts.Count(c =>
            c.Category is ConflictCategory.RuntimeModuleSetMismatch
            or ConflictCategory.RuntimeLoaderFailure
            or ConflictCategory.RuntimeCrashSession);
        int harmonyTotal = report.Conflicts.Count(c =>
            c.Category is ConflictCategory.HarmonyPatchConflict or ConflictCategory.HarmonyPatchStack);
        int harmonyRuntimeConfirmed = report.Conflicts.Count(c =>
            (c.Category is ConflictCategory.HarmonyPatchConflict or ConflictCategory.HarmonyPatchStack)
            && IsRuntimeConfirmed(c));

        if (runtimeConfirmed > 0)
        {
            TxtRuntimeEvidenceSummary.Text =
                $"{runtimeConfirmed} finding(s) are runtime-confirmed ({runtimeConfirmedStructural} structural + {runtimeSignalFindings} runtime-log signals). "
                + $"Harmony confirmations: {harmonyRuntimeConfirmed}/{harmonyTotal}.";
            return;
        }

        TxtRuntimeEvidenceSummary.Text = runtimeEvidenceRequested
            ? "Runtime evidence mode ran, but no Harmony runtime-confirmed findings were parsed. Generate fresh Harmony logs and re-scan."
            : "No runtime-confirmed findings in this scan. Use Collect Runtime Evidence after a gameplay session.";
    }

    private sealed class FindingRow
    {
        public required ConflictFinding Source { get; init; }
        public required string PlayerHeadline { get; init; }
        public required string ImpactSummary { get; init; }
        public required string Category { get; init; }
        public required string EvidenceStrength { get; init; }
        public required string ImpactRisk { get; init; }
        public required string PlayerSymptom { get; init; }
        public required string QuickFix { get; init; }
        public required string TechnicalCause { get; init; }
        public required string ExecutionChain { get; init; }
        public string Severity => Source.Severity.ToString();
        public string ConfidenceText => $"{Source.Confidence:P0}";
        public string Modules => Source.ModuleIds.Count <= 4
            ? string.Join(", ", Source.ModuleIds)
            : string.Join(", ", Source.ModuleIds.Take(4)) + $" (+{Source.ModuleIds.Count - 4})";
    }

    private sealed class PriorityQueueRow
    {
        public FindingRow? Source { get; init; }
        public required string Display { get; init; }

        public override string ToString()
        {
            return Display;
        }
    }

    private sealed class CriticalDrawerRow
    {
        public FindingRow? Source { get; init; }
        public required string ModuleFocus { get; init; }
        public required string Display { get; init; }
        public required string Reason { get; init; }

        public override string ToString()
        {
            return Display;
        }
    }

    private sealed class UiPreferences
    {
        public bool? CompactFindingsLayout { get; set; }
        public bool? ControlsPanelCollapsed { get; set; }
    }

    private sealed class LoadOrderRow
    {
        public required string ModuleId { get; init; }
        public required string ModuleTypeText { get; init; }
        public required Brush ModuleTypeBadgeBackground { get; init; }
        public required Brush ModuleTypeBadgeBorder { get; init; }
        public required Brush ModuleTypeForeground { get; init; }
        public required string CurrentIndexText { get; init; }
        public required string SuggestedIndexText { get; init; }
        public required string DeltaText { get; init; }
        public required string ActionText { get; init; }
        public required string WhyText { get; init; }
        public bool IsOfficial { get; init; }
        public bool IsFramework { get; init; }
        public bool IsCustom { get; init; }
        public bool IsActionable { get; init; }
        public bool IsInactiveInstalled { get; init; }
        public bool IsChanged { get; init; }
        public int AbsoluteShift { get; init; }
    }

    private sealed class SaveInsightRow
    {
        public required string SaveName { get; init; }
        public required string SizeMb { get; init; }
        public int ReferencedCount { get; init; }
    }

    private sealed class IsolationStepRow
    {
        public required string Step { get; init; }
        public required string DisableSet { get; init; }
        public required string FoundationSet { get; init; }
        public required string KeepSet { get; init; }
        public required string Outcome { get; init; }
        public required string RemainingAfter { get; init; }
    }

    private enum IsolationSeedMode
    {
        Unsupported,
        CompatibilityValidation,
        BinaryIsolation,
    }

    private enum IsolationStepMode
    {
        CompatibilityValidation,
        BinaryIsolation,
    }

    private sealed class IsolationPendingStep
    {
        public IsolationPendingStep(int step, IsolationStepMode mode, IReadOnlyList<string> disableModules, IReadOnlyList<string> keepModules)
        {
            Step = step;
            Mode = mode;
            DisableModules = disableModules.ToList();
            KeepModules = keepModules.ToList();
        }

        public int Step { get; }
        public IsolationStepMode Mode { get; }
        public List<string> DisableModules { get; }
        public List<string> KeepModules { get; }
    }

    private sealed class RuntimeSessionRow
    {
        public required string SessionKey { get; init; }
        public required string SessionLabel { get; init; }
        public required string LastLogUtc { get; init; }
        public int ChainCount { get; init; }
        public int ModuleCount { get; init; }
        public required string MaxRisk { get; init; }
        public required string StateLabel { get; init; }
    }

    private sealed class RuntimeLogRow
    {
        public required string SignalType { get; init; }
        public required string SessionKey { get; init; }
        public required string FileName { get; init; }
        public int FindingHits { get; init; }
        public required string Path { get; init; }
    }

    private sealed class RuntimeModuleRow
    {
        public required string ModuleId { get; init; }
        public int CorrelatedFindings { get; init; }
        public required string SessionCoverage { get; init; }
        public required IReadOnlyList<string> SessionKeys { get; init; }
        public required string MaxRisk { get; init; }
        public required string TopCategories { get; init; }
    }

    private sealed class RuntimeChainRow
    {
        public required string LogFile { get; init; }
        public required string LogPath { get; init; }
        public required string Modules { get; init; }
        public required string ModuleKey { get; init; }
        public required string SessionKey { get; init; }
        public required string Finding { get; init; }
        public required string Risk { get; init; }
        public required string Confidence { get; init; }
        public required string Signal { get; init; }
        public required string Meaning { get; init; }
        public required string QuickFix { get; init; }
    }

    private sealed class PlayerRuntimeSessionCardRow
    {
        public required string SessionKey { get; init; }
        public required string Title { get; init; }
        public required string Subtitle { get; init; }
        public required string Summary { get; init; }
    }

    private sealed class PlayerRuntimeSummaryRow
    {
        public required string SelectionKey { get; init; }
        public required RuntimeChainRow Source { get; init; }
        public required string Headline { get; init; }
        public required string Explanation { get; init; }
        public required string Modules { get; init; }
        public required string WhenAndLog { get; init; }
        public required string NextStep { get; init; }
        public required bool HasLogPath { get; init; }
    }

    private sealed class RuntimeLogAccumulator
    {
        public RuntimeLogAccumulator(string path, string signalType, string sessionKey, DateTime lastWriteUtc)
        {
            Path = path;
            SignalType = signalType;
            SessionKey = sessionKey;
            LastWriteUtc = lastWriteUtc;
        }

        public string Path { get; }
        public string SignalType { get; }
        public string SessionKey { get; }
        public DateTime LastWriteUtc { get; }
        public int LinkedFindingCount { get; set; }
        public HashSet<string> Categories { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RuntimeModuleAccumulator
    {
        public RuntimeModuleAccumulator(string moduleId)
        {
            ModuleId = moduleId;
        }

        public string ModuleId { get; }
        public int CorrelatedFindingCount { get; set; }
        public ConflictSeverity MaxSeverity { get; set; } = ConflictSeverity.Info;
        public HashSet<string> SessionKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Categories { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RuntimeSessionAccumulator
    {
        public RuntimeSessionAccumulator(string sessionKey)
        {
            SessionKey = sessionKey;
        }

        public string SessionKey { get; }
        public DateTime LastLogUtc { get; set; } = DateTime.MinValue;
        public int ChainCount { get; set; }
        public ConflictSeverity MaxSeverity { get; set; } = ConflictSeverity.Info;
        public HashSet<string> ModuleIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
