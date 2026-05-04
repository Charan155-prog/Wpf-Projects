using Microsoft.Win32;
using SterilizationGenie.Infrastructure;
using SterilizationGenie.Models;
using SterilizationGenie.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LiveCharts;
using LiveCharts.Wpf;

namespace SterilizationGenie.ViewModels;

public abstract partial class SterilizationDashboardViewModel : ObservableObject
{
    private readonly CycleDataService _dataService;
    private readonly string _exportDirectory;
    private readonly string _appRoot;
    private readonly DispatcherTimer _liveTimer;
    private bool _hasInitialized;

    // ── backing fields ──
    private bool _isConfigurationPopupOpen;
    private bool _isDashboardSelected = true;
    private bool _isAlertsSelected;
    private bool _isSummarySelected;
    private bool _isOnline;
    private bool _isAuthenticated;
    private bool _parseExistingData = true;
    private bool _watchNewRowsData;
    private bool _isMetricSelectorOpen;
    private bool _isBusy;
    private bool _isBarChartRepresentation;
    private bool _isDateCalendarOpen;
    private DateTime? _selectedChartDate = DateTime.Today;
    private string _loginUsername = string.Empty;
    private string _loginPassword = string.Empty;
    private string _loginErrorMessage = string.Empty;
    private string _lastActionMessage = "Ready.";
    private string _databaseConnectionStatus = "Database disconnected.";
    private string _databaseConnectionColor = "#E06262";
    private string _selectedRole = string.Empty;
    private MetricOption? _selectedMetric;
    private ChartRepresentationOption? _selectedRepresentation;
    private string? _recipeHeaderKey;
    private string? _stepHeaderKey;
    private string? _stepNameHeaderKey;
    private string? _alarmHeaderKey;
    private string? _durationHeaderKey;

    private CycleAttemptSelectOption? _selectedFailedCycle;
    private CycleAttemptSelectOption? _selectedGoodCycle;

    protected List<SterilizationCycle> VisibleCycles { get; private set; } = new();
    protected IReadOnlyList<AttemptSummary> AllAttemptSummaries { get; private set; } = Array.Empty<AttemptSummary>();
    protected Dictionary<int, string> RowIdToAttemptName { get; } = new();
    protected string AnalysisDataSignature { get; private set; } = string.Empty;

    protected SterilizationDashboardViewModel()
    {
        _appRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SterilizationGenie");
        var databasePath = Path.Combine(_appRoot, "sterilization-genie.db");
        _exportDirectory = Path.Combine(_appRoot, "Exports");
        _dataService = new CycleDataService(databasePath);

        BuildTimeRanges();
        BuildChartRepresentations();

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _liveTimer.Tick += (s, e) =>
        {
            if (IsOnline && WatchNewRowsData && HasVisualDataChanged())
            {
                RefreshAllVisuals();
            }
        };

        // Nav commands
        ShowDashboardCommand = new RelayCommand(() =>
        {
            IsDashboardSelected = true;
            IsAlertsSelected = false;
            IsSummarySelected = false;
            IsConfigurationPopupOpen = false;
        });
        ShowAlertsCommand = new RelayCommand(() =>
        {
            IsDashboardSelected = false;
            IsAlertsSelected = true;
            IsSummarySelected = false;
            IsConfigurationPopupOpen = false;
        });
        ShowSummaryCommand = new RelayCommand(() =>
        {
            IsDashboardSelected = false;
            IsAlertsSelected = false;
            IsSummarySelected = true;
            IsConfigurationPopupOpen = false;
        });

        // Settings always opens config popup — no separate SettingsView, disabled when Online
        ShowSettingsCommand = new RelayCommand(() =>
        {
            if (!IsOnline) IsConfigurationPopupOpen = true;
        });
        CloseConfigurationPopupCommand = new RelayCommand(() =>
        {
            IsConfigurationPopupOpen = false;
            IsMetricSelectorOpen = false;
        });

        // Data commands
        ImportWorkbookCommand = new RelayCommand(ImportWorkbook, () => !IsBusy);
        WipeDatabaseCommand = new RelayCommand(WipeDatabase, () => !IsBusy);
        ResetConfigurationCommand = new RelayCommand(ResetConfiguration);
        ExportCsvCommand = new RelayCommand(ExportCsv);
        ExportJsonCommand = new RelayCommand(ExportJson);

        // Chart/view commands
        SelectTimeRangeCommand = new RelayCommand<TimeRangeOption>(SelectTimeRange);
        ToggleMetricSelectorCommand = new RelayCommand(() => IsMetricSelectorOpen = !IsMetricSelectorOpen);
        ToggleChartTypeCommand = new RelayCommand(() => IsBarChartRepresentation = !IsBarChartRepresentation);

        // Auth
        LoginCommand = new RelayCommand(Login);
        LogoutCommand = new RelayCommand(Logout);
        // ToggleOnline: pure toggle of IsOnline property, command fired by the toggle button
        ToggleOnlineCommand = new RelayCommand(ToggleOnline);

        // Roles for dropdown
        Roles.Add("Admin");
        Roles.Add("Manager");
        Roles.Add("Operator");
        Roles.Add("Viewer");
        SelectedRole = "Admin";

        // Default Formatters
        YAxisFormatter = value => value.ToString("0.00");
        TooltipLabelPoint = chartPoint => $"{chartPoint.Y:0.##}";

        InitDrillDown();
    }

    protected void RebuildAbsoluteAttemptMapping()
    {
        RowIdToAttemptName.Clear();
        if (VisibleCycles.Count == 0 || string.IsNullOrWhiteSpace(StepHeaderKey))
        {
            AllAttemptSummaries = Array.Empty<AttemptSummary>();
            return;
        }

        var attempts = new List<AttemptSummary>();
        var sheetOrdinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var sheetGroup in VisibleCycles.GroupBy(c => c.SheetName).OrderBy(g => g.Min(c => c.RecordedAt)))
        {
            var sheetGroupOrdered = sheetGroup.OrderBy(c => c.RecordedAt).ToList();
            var sheetAttempts = BuildSheetAttempts(sheetGroup.Key, sheetGroupOrdered, sheetOrdinals);
            attempts.AddRange(sheetAttempts);
            foreach (var a in sheetAttempts)
            {
                foreach (var r in a.Rows) RowIdToAttemptName[r.Id] = a.Name;
            }
        }
        AllAttemptSummaries = attempts;
    }

    // ── collections ──
    public ObservableCollection<CycleHeaderDefinition> HeaderCatalog { get; } = new();
    public ObservableCollection<MetricOption> MetricOptions { get; } = new();
    public ObservableCollection<MetricSelectionOption> ComparisonMetricOptions { get; } = new();
    public ObservableCollection<ChartRepresentationOption> ChartRepresentations { get; } = new();
    public ObservableCollection<TimeRangeOption> TimeRangeOptions { get; } = new();
    public ObservableCollection<MetricPoint> TrendSeries1 { get; } = new();
    public ObservableCollection<MetricPoint> TrendSeries2 { get; } = new();
    public ObservableCollection<MetricPoint> TrendSeries3 { get; } = new();
    public ObservableCollection<MetricPoint> TrendSeries4 { get; } = new();
    public ObservableCollection<TopMetricBar> BvMetricBars { get; } = new();
    public ObservableCollection<TopMetricBar> LpcMetricBars { get; } = new();
    public ObservableCollection<TopMetricBar> TopDefectBars { get; } = new();
    public ObservableCollection<TopMetricBar> AttemptStatusBars { get; } = new();
    public ObservableCollection<DashboardStatCard> DashboardStatCards { get; } = new();
    public ObservableCollection<EvAlertRow> AlertTiles { get; } = new();
    public ObservableCollection<CycleRunCard> CycleRuns { get; } = new();
    public ObservableCollection<CycleAttemptRow> CycleAttempts { get; } = new();
    public ObservableCollection<string> Roles { get; } = new();

    // ── NEW: cycle selector collections ──
    public ObservableCollection<CycleAttemptSelectOption> FailedCycleOptions { get; } = new();
    public ObservableCollection<CycleAttemptSelectOption> GoodCycleOptions { get; } = new();

    public SeriesCollection MainChartSeries { get; } = new SeriesCollection();
    public string[] XLabels { get; protected set; } = Array.Empty<string>();
    public Func<double, string>? XAxisFormatter { get; protected set; }
    public Func<double, string>? YAxisFormatter { get; protected set; }
    public Func<ChartPoint, string>? TooltipLabelPoint { get; protected set; }

    // ── commands ──
    public ICommand ShowDashboardCommand { get; }
    public ICommand ShowAlertsCommand { get; }
    public ICommand ShowSummaryCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand CloseConfigurationPopupCommand { get; }
    public ICommand ImportWorkbookCommand { get; }
    public ICommand WipeDatabaseCommand { get; }
    public ICommand ResetConfigurationCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportJsonCommand { get; }
    public ICommand SelectTimeRangeCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand ToggleOnlineCommand { get; }
    public ICommand ToggleMetricSelectorCommand { get; }
    public ICommand ToggleChartTypeCommand { get; }

    // ── properties ──

    public bool IsConfigurationPopupOpen
    {
        get => _isConfigurationPopupOpen;
        set => SetProperty(ref _isConfigurationPopupOpen, value);
    }

    public bool IsDashboardSelected
    {
        get => _isDashboardSelected;
        set { if (SetProperty(ref _isDashboardSelected, value)) NotifyViewVisibility(); }
    }
    public bool IsAlertsSelected
    {
        get => _isAlertsSelected;
        set { if (SetProperty(ref _isAlertsSelected, value)) NotifyViewVisibility(); }
    }
    public bool IsSummarySelected
    {
        get => _isSummarySelected;
        set { if (SetProperty(ref _isSummarySelected, value)) NotifyViewVisibility(); }
    }

    private void NotifyViewVisibility()
    {
        OnPropertyChanged(nameof(ShowDashboardView));
        OnPropertyChanged(nameof(ShowAlertsView));
        OnPropertyChanged(nameof(ShowSummaryView));
        OnPropertyChanged(nameof(ShowSettingsView));
    }

    // Settings is now ALWAYS shown as popup — never as a view panel
    public bool ShowDashboardView => IsDashboardSelected;
    public bool ShowAlertsView => IsAlertsSelected;
    public bool ShowSummaryView => IsSummarySelected;
    public bool ShowSettingsView => false;  // settings = popup only

    // Online status — IsOnline is toggled by the pill button command
    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            if (SetProperty(ref _isOnline, value))
            {
                OnPropertyChanged(nameof(OnlineStatusLabel));
                OnPropertyChanged(nameof(OnlineStatusColor));
                if (!value)
                {
                    WatchNewRowsData = false;
                    _liveTimer.Stop();
                }
                else
                {
                    IsConfigurationPopupOpen = false;
                    WatchNewRowsData = true;
                    ParseExistingData = true;
                    _liveTimer.Start();
                }
                LastActionMessage = value ? "Online polling started." : "Polling stopped.";
            }
        }
    }
    public string OnlineStatusLabel => _isOnline ? "Online" : "Offline";
    public string OnlineStatusColor => _isOnline ? "#48D964" : "#E06262";

    // Role selector — role name shown in dashboard strip
    public string SelectedRole
    {
        get => _selectedRole;
        set { if (SetProperty(ref _selectedRole, value)) OnPropertyChanged(nameof(CurrentUserDisplayName)); }
    }

    public MetricOption? SelectedMetric
    {
        get => _selectedMetric;
        set { if (SetProperty(ref _selectedMetric, value)) RefreshAllVisuals(); }
    }

    public ChartRepresentationOption? SelectedRepresentation
    {
        get => _selectedRepresentation;
        set
        {
            if (SetProperty(ref _selectedRepresentation, value))
            {
                OnPropertyChanged(nameof(SelectedRepresentationDisplayName));
                OnPropertyChanged(nameof(ChartModeLabel));
                OnPropertyChanged(nameof(XAxisTitle));
                OnPropertyChanged(nameof(YAxisTitle));
                OnPropertyChanged(nameof(YAxisMin));
                OnPropertyChanged(nameof(YAxisMax));
                RefreshAllVisuals();
            }
        }
    }

    public bool ShowSensorSelector => string.Equals(SelectedRepresentation?.Key, "timeline", StringComparison.OrdinalIgnoreCase);
    public bool CanToggleChartMode => SelectedRepresentation is not null;
    public bool HasRenderableSeries => MainChartSeries.Count > 0;
    public string SelectedRepresentationDisplayName => SelectedRepresentation?.DisplayName ?? "Select comparison view";
    public string ChartModeLabel => SelectedRepresentation is null
        ? "Select comparison view first"
        : IsBarChartRepresentation ? "Line Graph Mode" : "Bar Graph Mode";
    public bool IsDateCalendarOpen
    {
        get => _isDateCalendarOpen;
        set => SetProperty(ref _isDateCalendarOpen, value);
    }
    public DateTime? SelectedChartDate
    {
        get => _selectedChartDate;
        set
        {
            var normalized = value?.Date;
            if (SetProperty(ref _selectedChartDate, normalized))
            {
                if (_isDateCalendarOpen)
                {
                    _isDateCalendarOpen = false;
                    OnPropertyChanged(nameof(IsDateCalendarOpen));
                }
                OnPropertyChanged(nameof(SelectedChartDateText));
                OnPropertyChanged(nameof(AvailableDateStart));
                OnPropertyChanged(nameof(AvailableDateEnd));
                RefreshAllVisuals();
            }
        }
    }
    public string SelectedChartDateText => SelectedChartDate?.ToString("dd-MM-yyyy") ?? "Select date";
    public IEnumerable<DateTime> AvailableDates => VisibleCycles.Select(c => c.RecordedAt.Date).Distinct().OrderBy(d => d);
    public DateTime? AvailableDateStart => VisibleCycles.Count == 0 ? null : VisibleCycles.Min(c => c.RecordedAt).Date;
    public DateTime? AvailableDateEnd => VisibleCycles.Count == 0 ? null : VisibleCycles.Max(c => c.RecordedAt).Date;
    public string XAxisTitle => SelectedRepresentation?.Key switch
    {
        "temp-pressure" => $"Recorded time in {CurrentRangeLabel} window on {SelectedChartDateText}",
        "good-failed-envelope" => $"Recorded time in {CurrentRangeLabel} window on {SelectedChartDateText}",
        "cycles-info" => $"Recorded time in {CurrentRangeLabel} window on {SelectedChartDateText}",
        "timeline" => IsBarChartRepresentation
            ? $"Sensor headers in {CurrentRangeLabel} window"
            : $"Recorded time on {SelectedChartDateText}",
        _ => $"Recorded time on {SelectedChartDateText}"
    };
    public string YAxisTitle => SelectedRepresentation?.Key switch
    {
        "temp-pressure" => "Temperature / pressure sensor values from workbook",
        "good-failed-envelope" => "Temperature sensor value from workbook",
        "cycles-info" => "Cycle duration / process metric",
        "timeline" => IsBarChartRepresentation
            ? "Average sensor value in selected window"
            : "Selected live sensor values",
        _ => "Sensor value"
    };
    public double YAxisMin
    {
        get => _yAxisMin;
        set => SetProperty(ref _yAxisMin, value);
    }
    public double YAxisMax
    {
        get => _yAxisMax;
        set => SetProperty(ref _yAxisMax, value);
    }
    public double XAxisSeparatorStep
    {
        get => _xAxisSeparatorStep;
        set => SetProperty(ref _xAxisSeparatorStep, value);
    }
    private double _yAxisMin = double.NaN;
    private double _yAxisMax = double.NaN;
    private double _xAxisSeparatorStep = 1d;
    public string EmptyStateMessage => SelectedRepresentation is null
        ? "Choose a comparison view to start."
        : ShowSensorSelector && SelectedMetric is null
            ? "Select one or more sensor values to render the chart."
            : "No data available for the current selection.";

    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        set
        {
            if (SetProperty(ref _isAuthenticated, value))
            {
                OnPropertyChanged(nameof(ShowLoginOverlay));
                OnPropertyChanged(nameof(CurrentUserDisplayName));
            }
        }
    }
    public bool ShowLoginOverlay => !IsAuthenticated;
    public string CurrentUserDisplayName => IsAuthenticated ? SelectedRole : "Guest";

    public string LoginUsername
    {
        get => _loginUsername;
        set => SetProperty(ref _loginUsername, value);
    }
    public string LoginPassword
    {
        get => _loginPassword;
        private set => SetProperty(ref _loginPassword, value);
    }
    public string LoginErrorMessage
    {
        get => _loginErrorMessage;
        set => SetProperty(ref _loginErrorMessage, value);
    }
    public void UpdateLoginPassword(string password) => LoginPassword = password;

    public bool ParseExistingData
    {
        get => _parseExistingData;
        set => SetProperty(ref _parseExistingData, value);
    }
    public bool WatchNewRowsData
    {
        get => _watchNewRowsData;
        set => SetProperty(ref _watchNewRowsData, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                if (ImportWorkbookCommand is RelayCommand ic) ic.RaiseCanExecuteChanged();
                if (WipeDatabaseCommand is RelayCommand wc) wc.RaiseCanExecuteChanged();
            }
        }
    }

    public string LastActionMessage
    {
        get => _lastActionMessage;
        set => SetProperty(ref _lastActionMessage, value);
    }
    public string DatabaseConnectionStatus
    {
        get => _databaseConnectionStatus;
        private set => SetProperty(ref _databaseConnectionStatus, value);
    }
    public string DatabaseConnectionColor
    {
        get => _databaseConnectionColor;
        private set => SetProperty(ref _databaseConnectionColor, value);
    }

    public bool IsMetricSelectorOpen
    {
        get => _isMetricSelectorOpen;
        set => SetProperty(ref _isMetricSelectorOpen, value);
    }
    public bool IsBarChartRepresentation
    {
        get => _isBarChartRepresentation;
        set
        {
            if (SetProperty(ref _isBarChartRepresentation, value))
            {
                OnPropertyChanged(nameof(ChartModeLabel));
                OnPropertyChanged(nameof(XAxisTitle));
                OnPropertyChanged(nameof(YAxisTitle));
                OnPropertyChanged(nameof(YAxisMin));
                OnPropertyChanged(nameof(YAxisMax));
                RefreshAllVisuals();
            }
        }
    }

    public string? RecipeHeaderKey { get => _recipeHeaderKey; private set => _recipeHeaderKey = value; }
    public string? StepHeaderKey { get => _stepHeaderKey; private set => _stepHeaderKey = value; }
    public string? StepNameHeaderKey { get => _stepNameHeaderKey; private set => _stepNameHeaderKey = value; }
    public string? AlarmHeaderKey { get => _alarmHeaderKey; private set => _alarmHeaderKey = value; }
    public string? DurationHeaderKey { get => _durationHeaderKey; private set => _durationHeaderKey = value; }

    // ── NEW: cycle data availability ──
    public bool HasCycleData => VisibleCycles.Count > 0;

    // ── NEW: failed cycle selector property ──
    public CycleAttemptSelectOption? SelectedFailedCycle
    {
        get => _selectedFailedCycle;
        set
        {
            if (SetProperty(ref _selectedFailedCycle, value))
            {
                OnPropertyChanged(nameof(SelectedFailedCycleLabel));
                OnPropertyChanged(nameof(CycleSelectionSummary));
                RefreshAllVisuals();
            }
        }
    }

    // ── NEW: good cycle selector property ──
    public CycleAttemptSelectOption? SelectedGoodCycle
    {
        get => _selectedGoodCycle;
        set
        {
            if (SetProperty(ref _selectedGoodCycle, value))
            {
                OnPropertyChanged(nameof(SelectedGoodCycleLabel));
                OnPropertyChanged(nameof(CycleSelectionSummary));
                RefreshAllVisuals();
            }
        }
    }

    public string SelectedFailedCycleLabel => _selectedFailedCycle?.DisplayName ?? "All failed";
    public string SelectedGoodCycleLabel => _selectedGoodCycle?.DisplayName ?? "All good";

    public string CycleSelectionSummary
    {
        get
        {
            var failed = _selectedFailedCycle is null ? "all failed" : _selectedFailedCycle.DisplayName;
            var good = _selectedGoodCycle is null ? "all good" : _selectedGoodCycle.DisplayName;
            return $"Comparing {failed} vs {good}";
        }
    }

    private string _drillDownYAxisTitle = "Sensor Values";
    public string DrillDownYAxisTitle
    {
        get => _drillDownYAxisTitle;
        set => SetProperty(ref _drillDownYAxisTitle, value);
    }

    private string _drillDownPointSummary = "Click a point to inspect the workbook coordinates.";
    public string DrillDownPointSummary
    {
        get => _drillDownPointSummary;
        set => SetProperty(ref _drillDownPointSummary, value);
    }

    private void BuildChartRepresentations()
    {
        ChartRepresentations.Clear();
        ChartRepresentations.Add(new ChartRepresentationOption("timeline", "Live Sensor Timeline", true));
        ChartRepresentations.Add(new ChartRepresentationOption("temp-pressure", "Temperature vs Pressure", false));
        ChartRepresentations.Add(new ChartRepresentationOption("good-failed-envelope", "Good vs Failed Min/Max", false));
        ChartRepresentations.Add(new ChartRepresentationOption("cycles-info", "Cycles Info", false));
    }

    // ── init ──
    private async Task InitializeAsync()
    {
        if (_hasInitialized)
        {
            return;
        }

        _hasInitialized = true;
        LastActionMessage = "Connecting to SQLite database…";
        DatabaseConnectionStatus = "Connecting…";
        DatabaseConnectionColor = "#D68A00";
        try
        {
            await _dataService.EnsureDatabaseAsync();
            VisibleCycles = await TryLoadWorkbookBackedCyclesAsync();
            EnsureSelectedChartDate();
            await Task.Yield();
            if (Application.Current?.Dispatcher is { } dispatcher)
            {
                await dispatcher.InvokeAsync(RefreshAllVisuals, DispatcherPriority.Background);
            }
            else
            {
                RefreshAllVisuals();
            }
            DatabaseConnectionStatus = $"SQLite connected. {VisibleCycles.Count} rows available.";
            DatabaseConnectionColor = "#3BCB78";
            LastActionMessage = VisibleCycles.Count == 0
                ? "Ready. Import a workbook to begin."
                : $"Loaded {VisibleCycles.Count} workbook-backed rows.";
        }
        catch (Exception ex)
        {
            DatabaseConnectionStatus = $"Connection failed: {ex.Message}";
            DatabaseConnectionColor = "#E74C3C";
            LastActionMessage = "Unable to initialise local database.";
        }
    }

    private async Task<List<SterilizationCycle>> TryLoadWorkbookBackedCyclesAsync()
    {
        var workbookPath = ResolvePreferredWorkbookPath();
        if (string.IsNullOrWhiteSpace(workbookPath) || !File.Exists(workbookPath))
        {
            LastActionMessage = "Preferred workbook not found. Import the workbook to continue.";
            await _dataService.ReplaceAllCyclesAsync([]);
            return [];
        }

        var importer = new WorkbookImportService();
        var result = await Task.Run(() => importer.ImportFile(workbookPath));
        if (!result.Success || result.ImportedCycles.Count == 0)
        {
            LastActionMessage = $"Workbook auto-load failed: {result.Error}";
            await _dataService.ReplaceAllCyclesAsync([]);
            return [];
        }

        await _dataService.ReplaceAllCyclesAsync(result.ImportedCycles);
        return result.ImportedCycles;
    }

    private static string? ResolvePreferredWorkbookPath()
    {
        var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var direct = Path.Combine(downloads, "Downloads", "Cycles on Sterilizer 34.xlsx");
        if (File.Exists(direct))
        {
            return direct;
        }

        var downloadsDir = Path.Combine(downloads, "Downloads");
        if (!Directory.Exists(downloadsDir))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(downloadsDir, "*.xlsx", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault(path => Path.GetFileName(path).Contains("Sterilizer", StringComparison.OrdinalIgnoreCase) ||
                                    Path.GetFileName(path).Contains("Cycle", StringComparison.OrdinalIgnoreCase));
    }

    // ── import ──
    private async void ImportWorkbook()
    {
        if (IsBusy) return;
        var dlg = new OpenFileDialog
        {
            Filter = "Excel Files (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog() != true) return;

        IsBusy = true;
        LastActionMessage = "Importing workbook — dynamic header analysis…";
        DatabaseConnectionStatus = "Writing rows to SQLite…";
        DatabaseConnectionColor = "#D68A00";
        try
        {
            var importer = new WorkbookImportService();
            var result = await Task.Run(() => importer.ImportFile(dlg.FileName));

            if (!result.Success)
            {
                LastActionMessage = $"Import failed: {result.Error}";
                DatabaseConnectionStatus = "Import failed before database update.";
                DatabaseConnectionColor = "#E74C3C";
                return;
            }

            await _dataService.ReplaceAllCyclesAsync(result.ImportedCycles);
            VisibleCycles = result.ImportedCycles;
            RebuildHeaderCatalog();
            RebuildAbsoluteAttemptMapping();
            EnsureSelectedChartDate(forceLatest: true);
            RefreshAllVisuals();
            DatabaseConnectionStatus = $"SQLite connected. {result.ImportedCycles.Count} rows · {result.ImportedHeaders.Count} headers.";
            DatabaseConnectionColor = "#3BCB78";
            LastActionMessage = $"Imported {result.ImportedCycles.Count} row(s) across {result.ImportedHeaders.Count} header(s).";
        }
        catch (Exception ex)
        {
            LastActionMessage = $"Unexpected error: {ex.Message}";
            DatabaseConnectionStatus = $"Database update failed: {ex.Message}";
            DatabaseConnectionColor = "#E74C3C";
        }
        finally { IsBusy = false; }
    }

    // ── wipe ──
    private async void WipeDatabase()
    {
        if (IsBusy) return;
        IsBusy = true;
        LastActionMessage = "Wiping database…";
        DatabaseConnectionStatus = "Clearing SQLite rows…";
        DatabaseConnectionColor = "#D68A00";
        try
        {
            await _dataService.ReplaceAllCyclesAsync([]);
            VisibleCycles = [];
            AllAttemptSummaries = Array.Empty<AttemptSummary>();
            RowIdToAttemptName.Clear();
            SelectedChartDate = DateTime.Today;
            RefreshAllVisuals();
            DatabaseConnectionStatus = "SQLite connected. 0 rows available.";
            DatabaseConnectionColor = "#3BCB78";
            LastActionMessage = "Database wiped. Header catalogue cleared.";
        }
        catch (Exception ex)
        {
            LastActionMessage = $"Unexpected error: {ex.Message}";
            DatabaseConnectionStatus = $"Wipe failed: {ex.Message}";
            DatabaseConnectionColor = "#E74C3C";
        }
        finally { IsBusy = false; }
    }

    private void ResetConfiguration()
    {
        ParseExistingData = true;
        WatchNewRowsData = IsOnline;
        IsMetricSelectorOpen = false;

        foreach (var option in ComparisonMetricOptions)
        {
            option.SetSelectedSilently(false);
        }

        _selectedMetric = null;
        _selectedRepresentation = null;

        foreach (var range in TimeRangeOptions)
        {
            range.IsSelected = string.Equals(range.Label, "5m", StringComparison.OrdinalIgnoreCase);
        }

        if (_isBarChartRepresentation)
        {
            _isBarChartRepresentation = false;
            OnPropertyChanged(nameof(IsBarChartRepresentation));
        }

        EnsureSelectedChartDate(forceLatest: true);
        LastActionMessage = "Configuration reset. Imported workbook data is still available.";
        RefreshAllVisuals();
    }

    private void EnsureSelectedChartDate(bool forceLatest = false)
    {
        if (VisibleCycles.Count == 0)
        {
            if (_selectedChartDate != DateTime.Today)
            {
                _selectedChartDate = DateTime.Today;
                OnPropertyChanged(nameof(SelectedChartDate));
                OnPropertyChanged(nameof(SelectedChartDateText));
                OnPropertyChanged(nameof(AvailableDateStart));
                OnPropertyChanged(nameof(AvailableDateEnd));
                OnPropertyChanged(nameof(AvailableDates));
            }
            return;
        }

        var shouldReset = forceLatest ||
                          _selectedChartDate is null ||
                          _selectedChartDate.Value == DateTime.MinValue;
        if (!shouldReset)
        {
            OnPropertyChanged(nameof(SelectedChartDateText));
            OnPropertyChanged(nameof(AvailableDateStart));
            OnPropertyChanged(nameof(AvailableDateEnd));
            OnPropertyChanged(nameof(AvailableDates));
            return;
        }

        _selectedChartDate = VisibleCycles.Max(c => c.RecordedAt).Date;
        OnPropertyChanged(nameof(SelectedChartDate));
        OnPropertyChanged(nameof(SelectedChartDateText));
        OnPropertyChanged(nameof(AvailableDateStart));
        OnPropertyChanged(nameof(AvailableDateEnd));
        OnPropertyChanged(nameof(AvailableDates));
    }

    // ── auth ──
    private void Login()
    {
        if (LoginUsername == "admin" && LoginPassword == "admin")
        {
            IsAuthenticated = true;
            LoginErrorMessage = string.Empty;
            _ = InitializeAsync();
        }
        else
        {
            LoginErrorMessage = "Invalid username or password.";
        }
    }
    private void Logout()
    {
        IsAuthenticated = false;
        LoginUsername = string.Empty;
        LoginPassword = string.Empty;
        LoginErrorMessage = string.Empty;
    }

    // ── online toggle (pure property flip) ──
    private void ToggleOnline() => IsOnline = !IsOnline;
}
