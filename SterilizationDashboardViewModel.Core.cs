//using Microsoft.Win32;
//using SterilizationGenie.Infrastructure;
//using SterilizationGenie.Models;
//using SterilizationGenie.Services;
//using System.Collections.ObjectModel;
//using System.IO;
//using System.Windows;
//using System.Windows.Input;
//using System.Windows.Threading;
//using System.Collections.Concurrent;
//using System.Threading;
//using LiveCharts;
//using LiveCharts.Wpf;

//namespace SterilizationGenie.ViewModels;

//public abstract partial class SterilizationDashboardViewModel : ObservableObject
//{
//    private readonly CycleDataService _dataService;
//    private readonly string _exportDirectory;
//    private readonly string _appRoot;
//    private readonly DispatcherTimer _liveTimer;
//    private readonly SemaphoreSlim _onlineDeltaGate = new(1, 1);
//    private readonly SemaphoreSlim _onlineQueueSignal = new(0);
//    private readonly ConcurrentQueue<List<SterilizationCycle>> _onlineCycleQueue = new();
//    private bool _hasInitialized;

//    // ── Online file-mode watching ─────────────────────────────────────────
//    // The user selects a single .xlsx file from ConfigurationPopup.
//    // FileSystemWatcher fires on every Changed/LastWrite event; we debounce
//    // 500 ms then read only the NEW rows (delta import).
//    private FileSystemWatcher? _onlineFileWatcher;
//    private CancellationTokenSource? _onlineWatcherDelayCts;
//    private CancellationTokenSource? _onlineProcessorCts;
//    private Task? _onlineProcessorTask;
//    private string? _activeWorkbookPath;           // currently watched file
//    private Dictionary<string, int> _onlineSheetRowPositions = new(StringComparer.OrdinalIgnoreCase);

//    // Sliding window for the live chart — oldest rows drop off so the chart
//    // scrolls like a stock ticker instead of compressing.
//    private const int LiveChartWindow = 60;

//    // ── backing fields ────────────────────────────────────────────────────
//    private bool _showRepresentations;
//    private bool _isInitializing = true;
//    private bool _isConfigurationPopupOpen;
//    private bool _isDashboardSelected = true;
//    private bool _isAlertsSelected;
//    private bool _isSummarySelected;
//    private bool _isOnline;
//    private bool _isAuthenticated;
//    private bool _parseExistingData = true;
//    private bool _watchNewRowsData;
//    private bool _isMetricSelectorOpen;
//    private bool _isBusy;
//    private bool _isBarChartRepresentation;
//    private bool _isExportPanelOpen;
//    private bool _isDateCalendarOpen;
//    private DateTime? _selectedChartDate = DateTime.Today;
//    private string _loginUsername = string.Empty;
//    private string _loginPassword = string.Empty;
//    private string _loginErrorMessage = string.Empty;
//    private string _lastActionMessage = "Ready.";
//    private string _databaseConnectionStatus = "Database disconnected.";
//    private string _databaseConnectionColor = "#E06262";
//    private string _selectedRole = string.Empty;
//    private MetricOption? _selectedMetric;
//    private ChartRepresentationOption? _selectedRepresentation;
//    private string? _recipeHeaderKey;
//    private string? _stepHeaderKey;
//    private string? _stepNameHeaderKey;
//    private string? _alarmHeaderKey;
//    private string? _durationHeaderKey;
//    private CycleAttemptSelectOption? _selectedFailedCycle;
//    private CycleAttemptSelectOption? _selectedGoodCycle;

//    // ── unified cycle multi-select ────────────────────────────────────────
//    private readonly HashSet<string> _selectedCycleKeys = new(StringComparer.OrdinalIgnoreCase);

//    protected List<SterilizationCycle> VisibleCycles { get; private set; } = new();
//    protected IReadOnlyList<AttemptSummary> AllAttemptSummaries { get; private set; } = Array.Empty<AttemptSummary>();
//    protected Dictionary<int, string> RowIdToAttemptName { get; } = new();
//    protected string AnalysisDataSignature { get; private set; } = string.Empty;

//    protected SterilizationDashboardViewModel()
//    {
//        _appRoot = Path.Combine(
//            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
//            "SterilizationGenie");
//        var databasePath = Path.Combine(_appRoot, "sterilization-genie.db");
//        _exportDirectory = Path.Combine(_appRoot, "Exports");
//        _dataService = new CycleDataService(databasePath);

//        BuildTimeRanges();
//        BuildChartRepresentations();

//        // 1-second tick so online charts feel responsive.
//        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
//        _liveTimer.Tick += (_, _) =>
//        {
//            if (IsOnline && WatchNewRowsData && HasVisualDataChanged())
//                RefreshAllVisuals();
//        };

//        // ── Nav commands ──
//        ShowDashboardCommand = new RelayCommand(() =>
//        {
//            IsDashboardSelected = true; IsAlertsSelected = false;
//            IsSummarySelected = false; IsConfigurationPopupOpen = false;
//        });
//        ShowAlertsCommand = new RelayCommand(() =>
//        {
//            IsDashboardSelected = false; IsAlertsSelected = true;
//            IsSummarySelected = false; IsConfigurationPopupOpen = false;
//        });
//        ShowSummaryCommand = new RelayCommand(() =>
//        {
//            IsDashboardSelected = false; IsAlertsSelected = false;
//            IsSummarySelected = true; IsConfigurationPopupOpen = false;
//        });
//        ShowSettingsCommand = new RelayCommand(() =>
//        {
//            if (!IsOnline) IsConfigurationPopupOpen = true;
//        });
//        CloseConfigurationPopupCommand = new RelayCommand(CloseConfigurationPopup);

//        // ── Data commands ──
//        ImportWorkbookCommand = new RelayCommand(ImportWorkbook, () => !IsBusy);
//        WipeDatabaseCommand = new RelayCommand(WipeDatabase, () => !IsBusy);
//        ResetConfigurationCommand = new RelayCommand(ResetConfiguration);
//        ExportCsvCommand = new RelayCommand(ExportCsv);
//        ExportJsonCommand = new RelayCommand(ExportJson);

//        // ── Online file picker (ConfigurationPopup "Select Live Excel File") ──
//        SelectOnlineFileCommand = new RelayCommand(SelectOnlineFile, () => !IsOnline);

//        // ── Chart commands ──
//        SelectTimeRangeCommand = new RelayCommand<TimeRangeOption>(SelectTimeRange);
//        ToggleMetricSelectorCommand = new RelayCommand(() => IsMetricSelectorOpen = !IsMetricSelectorOpen);
//        ToggleChartTypeCommand = new RelayCommand(() => IsBarChartRepresentation = !IsBarChartRepresentation);
//        ToggleExportPanelCommand = new RelayCommand(() => IsExportPanelOpen = !IsExportPanelOpen);
//        CloseExportPanelCommand = new RelayCommand(() => IsExportPanelOpen = false);

//        // ── Auth ──
//        LoginCommand = new RelayCommand(Login);
//        LogoutCommand = new RelayCommand(Logout);
//        ToggleOnlineCommand = new RelayCommand(ToggleOnline);

//        Roles.Add("Admin"); Roles.Add("Manager");
//        Roles.Add("Operator"); Roles.Add("Viewer");
//        SelectedRole = "Admin";

//        YAxisFormatter = v => v.ToString("0.00");
//        TooltipLabelPoint = cp => $"{cp.Y:0.##}";

//        InitDrillDown();
//    }

//    // ── Collections ──────────────────────────────────────────────────────
//    public ObservableCollection<CycleHeaderDefinition> HeaderCatalog { get; } = new();
//    public ObservableCollection<MetricOption> MetricOptions { get; } = new();
//    public ObservableCollection<MetricSelectionOption> ComparisonMetricOptions { get; } = new();
//    public ObservableCollection<ChartRepresentationOption> ChartRepresentations { get; } = new();
//    public ObservableCollection<TimeRangeOption> TimeRangeOptions { get; } = new();
//    public ObservableCollection<MetricPoint> TrendSeries1 { get; } = new();
//    public ObservableCollection<MetricPoint> TrendSeries2 { get; } = new();
//    public ObservableCollection<MetricPoint> TrendSeries3 { get; } = new();
//    public ObservableCollection<MetricPoint> TrendSeries4 { get; } = new();
//    public ObservableCollection<TopMetricBar> BvMetricBars { get; } = new();
//    public ObservableCollection<TopMetricBar> LpcMetricBars { get; } = new();
//    public ObservableCollection<TopMetricBar> TopDefectBars { get; } = new();
//    public ObservableCollection<TopMetricBar> AttemptStatusBars { get; } = new();
//    public ObservableCollection<DashboardStatCard> DashboardStatCards { get; } = new();
//    public ObservableCollection<EvAlertRow> AlertTiles { get; } = new();
//    public ObservableCollection<CycleRunCard> CycleRuns { get; } = new();
//    public ObservableCollection<CycleAttemptRow> CycleAttempts { get; } = new();
//    public ObservableCollection<string> Roles { get; } = new();
//    public ObservableCollection<CycleAttemptSelectOption> FailedCycleOptions { get; } = new();
//    public ObservableCollection<CycleAttemptSelectOption> GoodCycleOptions { get; } = new();
//    public ObservableCollection<CycleAttemptSelectOption> AllCycleOptions { get; } = new();

//    public SeriesCollection MainChartSeries { get; } = new SeriesCollection();
//    public SeriesCollection OfflineMainChartSeries { get; } = new SeriesCollection();
//    public SeriesCollection OnlineMainChartSeries { get; } = new SeriesCollection();
//    public string[] XLabels { get; protected set; } = Array.Empty<string>();
//    public Func<double, string>? XAxisFormatter { get; protected set; }
//    public Func<double, string>? YAxisFormatter { get; protected set; }
//    public Func<ChartPoint, string>? TooltipLabelPoint { get; protected set; }

//    // ── Commands ─────────────────────────────────────────────────────────
//    public ICommand ShowDashboardCommand { get; }
//    public ICommand ShowAlertsCommand { get; }
//    public ICommand ShowSummaryCommand { get; }
//    public ICommand ShowSettingsCommand { get; }
//    public ICommand CloseConfigurationPopupCommand { get; }
//    public ICommand ImportWorkbookCommand { get; }
//    public ICommand WipeDatabaseCommand { get; }
//    public ICommand ResetConfigurationCommand { get; }
//    public ICommand ExportCsvCommand { get; }
//    public ICommand ExportJsonCommand { get; }
//    public ICommand SelectTimeRangeCommand { get; }
//    public ICommand LoginCommand { get; }
//    public ICommand LogoutCommand { get; }
//    public ICommand ToggleOnlineCommand { get; }
//    public ICommand ToggleMetricSelectorCommand { get; }
//    public ICommand ToggleChartTypeCommand { get; }
//    public ICommand ToggleExportPanelCommand { get; }
//    public ICommand CloseExportPanelCommand { get; }

//    // NEW: file picker for online mode.
//    // Bound in ConfigurationPopup.xaml as SelectOnlineFileCommand.
//    public ICommand SelectOnlineFileCommand { get; }

//    // ── Online watch-file properties ──────────────────────────────────────
//    /// <summary>Full path of the Excel file selected for online monitoring.</summary>
//    public string OnlineWatchFilePath
//    {
//        get => _activeWorkbookPath ?? string.Empty;
//        private set
//        {
//            if (!string.Equals(_activeWorkbookPath, value, StringComparison.Ordinal))
//            {
//                _activeWorkbookPath = string.IsNullOrWhiteSpace(value) ? null : value;
//                OnPropertyChanged();
//                OnPropertyChanged(nameof(HasOnlineWatchFile));
//            }
//        }
//    }

//    /// <summary>True once the user has selected a live Excel file.</summary>
//    public bool HasOnlineWatchFile => !string.IsNullOrWhiteSpace(_activeWorkbookPath)
//                                      && File.Exists(_activeWorkbookPath);

//    // ── Export panel ─────────────────────────────────────────────────────
//    public bool IsExportPanelOpen
//    {
//        get => _isExportPanelOpen;
//        set
//        {
//            if (SetProperty(ref _isExportPanelOpen, value))
//                OnPropertyChanged(nameof(ExportPanelSummary));
//        }
//    }

//    public string ExportPanelSummary
//    {
//        get
//        {
//            var lines = new List<string>
//            {
//                $"View         : {SelectedRepresentationDisplayName}",
//                $"Mode         : {(IsBarChartRepresentation ? "Bar" : "Line")}",
//                $"Date         : {SelectedChartDateText}",
//                $"Time window  : {CurrentRangeLabel}",
//                $"Cycles shown : {SelectedCyclesSummary}",
//                $"Metric       : {SelectedMetric?.DisplayName ?? "(none)"}",
//                $"Series count : {MainChartSeries.Count}",
//                $"Workbook     : {ImportedWorkbookName}"
//            };
//            return string.Join(Environment.NewLine, lines);
//        }
//    }

//    // ── View/nav state ────────────────────────────────────────────────────
//    public bool IsConfigurationPopupOpen
//    {
//        get => _isConfigurationPopupOpen;
//        set => SetProperty(ref _isConfigurationPopupOpen, value);
//    }
//    public bool IsDashboardSelected
//    {
//        get => _isDashboardSelected;
//        set { if (SetProperty(ref _isDashboardSelected, value)) NotifyViewVisibility(); }
//    }
//    public bool IsAlertsSelected
//    {
//        get => _isAlertsSelected;
//        set { if (SetProperty(ref _isAlertsSelected, value)) NotifyViewVisibility(); }
//    }
//    public bool IsSummarySelected
//    {
//        get => _isSummarySelected;
//        set { if (SetProperty(ref _isSummarySelected, value)) NotifyViewVisibility(); }
//    }
//    private void NotifyViewVisibility()
//    {
//        OnPropertyChanged(nameof(ShowDashboardView));
//        OnPropertyChanged(nameof(ShowAlertsView));
//        OnPropertyChanged(nameof(ShowSummaryView));
//        OnPropertyChanged(nameof(ShowSettingsView));
//    }
//    public bool ShowDashboardView => IsDashboardSelected;
//    public bool ShowAlertsView => IsAlertsSelected;
//    public bool ShowSummaryView => IsSummarySelected;
//    public bool ShowSettingsView => false;

//    // ── IsOnline toggle ───────────────────────────────────────────────────
//    public bool IsOnline
//    {
//        get => _isOnline;
//        set
//        {
//            if (SetProperty(ref _isOnline, value))
//            {
//                OnPropertyChanged(nameof(OnlineStatusLabel));
//                OnPropertyChanged(nameof(OnlineStatusColor));
//                if (SelectOnlineFileCommand is RelayCommand rc) rc.RaiseCanExecuteChanged();

//                if (!value)
//                {
//                    WatchNewRowsData = false;
//                    _liveTimer.Stop();
//                    StopOnlineMonitoring();
//                    IsDrillDownOpen = false;
//                    RefreshAllVisuals();
//                    LastActionMessage = "Polling stopped.";
//                }
//                else
//                {
//                    // Guard: require a valid file to be selected first.
//                    if (!HasOnlineWatchFile)
//                    {
//                        LastActionMessage = "Select a live Excel file before going online.";
//                        _isOnline = false;
//                        OnPropertyChanged(nameof(IsOnline));
//                        OnPropertyChanged(nameof(OnlineStatusLabel));
//                        OnPropertyChanged(nameof(OnlineStatusColor));
//                        IsConfigurationPopupOpen = true;
//                        return;
//                    }
//                    IsConfigurationPopupOpen = false;
//                    WatchNewRowsData = true;
//                    IsDrillDownOpen = false;
//                    _liveTimer.Start();
//                    _ = StartOnlineMonitoringAsync();
//                    LastActionMessage = $"Online: watching {Path.GetFileName(OnlineWatchFilePath)} for new rows.";
//                }
//            }
//        }
//    }
//    public string OnlineStatusLabel => _isOnline ? "Online" : "Offline";
//    public string OnlineStatusColor => _isOnline ? "#48D964" : "#E06262";

//    // ── Misc bindable properties ──────────────────────────────────────────
//    public string SelectedRole
//    {
//        get => _selectedRole;
//        set { if (SetProperty(ref _selectedRole, value)) OnPropertyChanged(nameof(CurrentUserDisplayName)); }
//    }
//    public MetricOption? SelectedMetric
//    {
//        get => _selectedMetric;
//        set { if (SetProperty(ref _selectedMetric, value)) RefreshAllVisuals(); }
//    }
//    public ChartRepresentationOption? SelectedRepresentation
//    {
//        get => _selectedRepresentation;
//        set
//        {
//            if (SetProperty(ref _selectedRepresentation, value))
//            {
//                OnPropertyChanged(nameof(SelectedRepresentationDisplayName));
//                OnPropertyChanged(nameof(ChartModeLabel));
//                OnPropertyChanged(nameof(XAxisTitle));
//                OnPropertyChanged(nameof(YAxisTitle));
//                OnPropertyChanged(nameof(YAxisMin));
//                OnPropertyChanged(nameof(YAxisMax));
//                _showRepresentations = true;
//                RefreshAllVisuals();
//            }
//        }
//    }
//    public bool ShowSensorSelector => string.Equals(
//        SelectedRepresentation?.Key, "timeline", StringComparison.OrdinalIgnoreCase);
//    public bool CanToggleChartMode => SelectedRepresentation is not null;
//    public bool HasRenderableSeries => (IsOnline ? OnlineMainChartSeries : OfflineMainChartSeries).Count > 0;
//    public string SelectedRepresentationDisplayName =>
//        SelectedRepresentation?.DisplayName ?? "Select comparison view";
//    public string ChartModeLabel => SelectedRepresentation is null
//        ? "Select comparison view first"
//        : IsBarChartRepresentation ? "Switch to line graph mode" : "Switch to bar graph mode";

//    public bool IsDateCalendarOpen
//    {
//        get => _isDateCalendarOpen;
//        set => SetProperty(ref _isDateCalendarOpen, value);
//    }
//    public DateTime? SelectedChartDate
//    {
//        get => _selectedChartDate;
//        set
//        {
//            var normalized = value?.Date;
//            if (SetProperty(ref _selectedChartDate, normalized))
//            {
//                if (_isDateCalendarOpen)
//                {
//                    _isDateCalendarOpen = false;
//                    OnPropertyChanged(nameof(IsDateCalendarOpen));
//                }
//                OnPropertyChanged(nameof(SelectedChartDateText));
//                OnPropertyChanged(nameof(AvailableDateStart));
//                OnPropertyChanged(nameof(AvailableDateEnd));
//                _showRepresentations = true;
//                RefreshAllVisuals();
//            }
//        }
//    }
//    public string SelectedChartDateText => SelectedChartDate?.ToString("dd-MM-yyyy") ?? "Select date";
//    public IEnumerable<DateTime> AvailableDates =>
//        VisibleCycles.Select(c => c.RecordedAt.Date).Distinct().OrderBy(d => d);
//    public DateTime? AvailableDateStart =>
//        VisibleCycles.Count == 0 ? null : VisibleCycles.Min(c => c.RecordedAt).Date;
//    public DateTime? AvailableDateEnd =>
//        VisibleCycles.Count == 0 ? null : VisibleCycles.Max(c => c.RecordedAt).Date;

//    public string XAxisTitle => SelectedRepresentation?.Key switch
//    {
//        "good-failed-envelope" => $"Recorded time in {CurrentRangeLabel} window on {SelectedChartDateText}",
//        "cycles-info" => $"Recorded time in {CurrentRangeLabel} window on {SelectedChartDateText}",
//        "cycle-duration" => "Cycle attempts",
//        "temperature-profile" => $"Recorded time in {CurrentRangeLabel} window on {SelectedChartDateText}",
//        "pressure-profile" => $"Recorded time in {CurrentRangeLabel} window on {SelectedChartDateText}",
//        "f0-exposure" => "Cycle attempts",
//        "level-conductivity" => "Cycle attempts",
//        "recipe-step-map" => "Cycle attempts",
//        "timeline" => IsBarChartRepresentation
//                                    ? $"Sensor headers in {CurrentRangeLabel} window"
//                                    : $"Recorded time on {SelectedChartDateText}",
//        _ => $"Recorded time on {SelectedChartDateText}"
//    };
//    public string YAxisTitle => SelectedRepresentation?.Key switch
//    {
//        "good-failed-envelope" => "Temperature sensor value from workbook",
//        "cycles-info" => "Cycle duration / process metric",
//        "cycle-duration" => "Duration / process step",
//        "temperature-profile" => "Average temperature across selected sensors",
//        "pressure-profile" => "Average pressure across selected sensors",
//        "f0-exposure" => "Peak F0 value",
//        "level-conductivity" => "Average level / conductivity value",
//        "recipe-step-map" => "Step / stage count",
//        "timeline" => IsBarChartRepresentation
//                                    ? "Average sensor value in selected window"
//                                    : "Selected live sensor values",
//        _ => "Sensor value"
//    };
//    public double YAxisMin { get => _yAxisMin; set => SetProperty(ref _yAxisMin, value); }
//    public double YAxisMax { get => _yAxisMax; set => SetProperty(ref _yAxisMax, value); }
//    public double XAxisSeparatorStep { get => _xAxisSeparatorStep; set => SetProperty(ref _xAxisSeparatorStep, value); }
//    private double _yAxisMin = double.NaN;
//    private double _yAxisMax = double.NaN;
//    private double _xAxisSeparatorStep = 1d;

//    public string EmptyStateMessage => SelectedRepresentation is null
//        ? "Choose a comparison view to start."
//        : ShowSensorSelector && SelectedMetric is null
//            ? "Select one or more sensor values to render the chart."
//            : "No data available for the current selection.";

//    public bool IsAuthenticated
//    {
//        get => _isAuthenticated;
//        set
//        {
//            if (SetProperty(ref _isAuthenticated, value))
//            {
//                OnPropertyChanged(nameof(ShowLoginOverlay));
//                OnPropertyChanged(nameof(CurrentUserDisplayName));
//            }
//        }
//    }
//    public bool ShowLoginOverlay => !IsAuthenticated;
//    public string CurrentUserDisplayName => IsAuthenticated ? SelectedRole : "Guest";

//    public string LoginUsername
//    {
//        get => _loginUsername; set => SetProperty(ref _loginUsername, value);
//    }
//    public string LoginPassword
//    {
//        get => _loginPassword; private set => SetProperty(ref _loginPassword, value);
//    }
//    public string LoginErrorMessage
//    {
//        get => _loginErrorMessage; set => SetProperty(ref _loginErrorMessage, value);
//    }
//    public void UpdateLoginPassword(string password) => LoginPassword = password;

//    public bool ParseExistingData { get => _parseExistingData; set => SetProperty(ref _parseExistingData, value); }
//    public bool WatchNewRowsData { get => _watchNewRowsData; set => SetProperty(ref _watchNewRowsData, value); }

//    public bool IsBusy
//    {
//        get => _isBusy;
//        private set
//        {
//            if (SetProperty(ref _isBusy, value))
//            {
//                if (ImportWorkbookCommand is RelayCommand ic) ic.RaiseCanExecuteChanged();
//                if (WipeDatabaseCommand is RelayCommand wc) wc.RaiseCanExecuteChanged();
//            }
//        }
//    }

//    public string LastActionMessage
//    {
//        get => _lastActionMessage; set => SetProperty(ref _lastActionMessage, value);
//    }
//    public string DatabaseConnectionStatus
//    {
//        get => _databaseConnectionStatus; private set => SetProperty(ref _databaseConnectionStatus, value);
//    }
//    public string DatabaseConnectionColor
//    {
//        get => _databaseConnectionColor; private set => SetProperty(ref _databaseConnectionColor, value);
//    }

//    public bool IsMetricSelectorOpen { get => _isMetricSelectorOpen; set => SetProperty(ref _isMetricSelectorOpen, value); }
//    public bool IsBarChartRepresentation
//    {
//        get => _isBarChartRepresentation;
//        set
//        {
//            if (SetProperty(ref _isBarChartRepresentation, value))
//            {
//                OnPropertyChanged(nameof(ChartModeLabel));
//                OnPropertyChanged(nameof(XAxisTitle));
//                OnPropertyChanged(nameof(YAxisTitle));
//                OnPropertyChanged(nameof(YAxisMin));
//                OnPropertyChanged(nameof(YAxisMax));
//                RefreshAllVisuals();
//            }
//        }
//    }

//    public string? RecipeHeaderKey { get => _recipeHeaderKey; private set => _recipeHeaderKey = value; }
//    public string? StepHeaderKey { get => _stepHeaderKey; private set => _stepHeaderKey = value; }
//    public string? StepNameHeaderKey { get => _stepNameHeaderKey; private set => _stepNameHeaderKey = value; }
//    public string? AlarmHeaderKey { get => _alarmHeaderKey; private set => _alarmHeaderKey = value; }
//    public string? DurationHeaderKey { get => _durationHeaderKey; private set => _durationHeaderKey = value; }

//    public bool HasCycleData => VisibleCycles.Count > 0;

//    public CycleAttemptSelectOption? SelectedFailedCycle
//    {
//        get => _selectedFailedCycle;
//        set
//        {
//            if (SetProperty(ref _selectedFailedCycle, value))
//            {
//                OnPropertyChanged(nameof(SelectedFailedCycleLabel));
//                OnPropertyChanged(nameof(CycleSelectionSummary));
//                RefreshAllVisuals();
//            }
//        }
//    }
//    public CycleAttemptSelectOption? SelectedGoodCycle
//    {
//        get => _selectedGoodCycle;
//        set
//        {
//            if (SetProperty(ref _selectedGoodCycle, value))
//            {
//                OnPropertyChanged(nameof(SelectedGoodCycleLabel));
//                OnPropertyChanged(nameof(CycleSelectionSummary));
//                RefreshAllVisuals();
//            }
//        }
//    }

//    public string SelectedFailedCycleLabel => _selectedFailedCycle?.DisplayName ?? "All failed";
//    public string SelectedGoodCycleLabel => _selectedGoodCycle?.DisplayName ?? "All good";

//    public string SelectedCyclesSummary
//    {
//        get
//        {
//            var count = _selectedCycleKeys.Count;
//            if (count == 0) return "All Cycles";
//            var allOption = AllCycleOptions.FirstOrDefault(o => o.IsAll);
//            if (allOption is not null && _selectedCycleKeys.Contains(allOption.Key)) return "All Cycles";
//            return count == 1
//                ? AllCycleOptions.FirstOrDefault(o => _selectedCycleKeys.Contains(o.Key))?.DisplayName ?? "1 cycle selected"
//                : $"{count} cycles selected";
//        }
//    }

//    public bool IsCycleOptionSelected(string key) => _selectedCycleKeys.Contains(key);

//    public void ToggleCycleSelection(CycleAttemptSelectOption option, bool isSelected)
//    {
//        if (option is null) return;
//        if (option.IsAll)
//        {
//            _selectedCycleKeys.Clear();
//            if (isSelected) _selectedCycleKeys.Add(option.Key);
//            foreach (var o in AllCycleOptions) o.NotifyIsSelected();
//        }
//        else
//        {
//            var allKey = AllCycleOptions.FirstOrDefault(o => o.IsAll)?.Key;
//            if (allKey is not null) _selectedCycleKeys.Remove(allKey);
//            if (isSelected) _selectedCycleKeys.Add(option.Key);
//            else _selectedCycleKeys.Remove(option.Key);
//            if (_selectedCycleKeys.Count == 0 && allKey is not null)
//            {
//                _selectedCycleKeys.Add(allKey);
//                AllCycleOptions.FirstOrDefault(o => o.IsAll)?.NotifyIsSelected();
//            }
//        }
//        SyncLegacyCycleSelectors();
//        OnPropertyChanged(nameof(SelectedCyclesSummary));
//        OnPropertyChanged(nameof(CycleSelectionSummary));
//        RefreshAllVisuals();
//    }

//    private void SyncLegacyCycleSelectors()
//    {
//        var allKey = AllCycleOptions.FirstOrDefault(o => o.IsAll)?.Key;
//        var isAllSelected = allKey is not null && _selectedCycleKeys.Contains(allKey);

//        if (isAllSelected || _selectedCycleKeys.Count == 0)
//        {
//            _selectedFailedCycle = FailedCycleOptions.FirstOrDefault(o => o.IsAll) ?? FailedCycleOptions.FirstOrDefault();
//            _selectedGoodCycle = GoodCycleOptions.FirstOrDefault(o => o.IsAll) ?? GoodCycleOptions.FirstOrDefault();
//        }
//        else
//        {
//            var gk = GoodCycleOptions.FirstOrDefault(o => !o.IsAll && _selectedCycleKeys.Contains(o.Key))?.Key;
//            var fk = FailedCycleOptions.FirstOrDefault(o => !o.IsAll && _selectedCycleKeys.Contains(o.Key))?.Key;
//            _selectedFailedCycle = fk is null
//                ? FailedCycleOptions.FirstOrDefault(o => o.IsAll) ?? FailedCycleOptions.FirstOrDefault()
//                : FailedCycleOptions.FirstOrDefault(o => o.Key == fk);
//            _selectedGoodCycle = gk is null
//                ? GoodCycleOptions.FirstOrDefault(o => o.IsAll) ?? GoodCycleOptions.FirstOrDefault()
//                : GoodCycleOptions.FirstOrDefault(o => o.Key == gk);
//        }

//        OnPropertyChanged(nameof(SelectedFailedCycle));
//        OnPropertyChanged(nameof(SelectedGoodCycle));
//        OnPropertyChanged(nameof(SelectedFailedCycleLabel));
//        OnPropertyChanged(nameof(SelectedGoodCycleLabel));
//    }

//    public string CycleSelectionSummary
//    {
//        get
//        {
//            var failed = _selectedFailedCycle is null ? "all failed" : _selectedFailedCycle.DisplayName;
//            var good = _selectedGoodCycle is null ? "all good" : _selectedGoodCycle.DisplayName;
//            return $"Comparing {failed} vs {good}";
//        }
//    }

//    private string _drillDownYAxisTitle = "Sensor Values";
//    public string DrillDownYAxisTitle { get => _drillDownYAxisTitle; set => SetProperty(ref _drillDownYAxisTitle, value); }

//    private string _drillDownXAxisTitle = "Elapsed Time (Minutes)";
//    public string DrillDownXAxisTitle { get => _drillDownXAxisTitle; set => SetProperty(ref _drillDownXAxisTitle, value); }

//    private string _drillDownPointSummary = "Click a point to inspect the workbook coordinates.";
//    public string DrillDownPointSummary { get => _drillDownPointSummary; set => SetProperty(ref _drillDownPointSummary, value); }

//    private void BuildChartRepresentations()
//    {
//        ChartRepresentations.Clear();
//        ChartRepresentations.Add(new ChartRepresentationOption("timeline", "Live Sensor Timeline", true));
//        ChartRepresentations.Add(new ChartRepresentationOption("good-failed-envelope", "Good vs Failed Min/Max", false));
//        ChartRepresentations.Add(new ChartRepresentationOption("cycles-info", "Cycles Info", false));
//        ChartRepresentations.Add(new ChartRepresentationOption("cycle-duration", "Cycle Duration Analytics", false));
//        ChartRepresentations.Add(new ChartRepresentationOption("temperature-profile", "Temperature Sensor Profile", false));
//        ChartRepresentations.Add(new ChartRepresentationOption("pressure-profile", "Pressure Sensor Profile", false));
//        ChartRepresentations.Add(new ChartRepresentationOption("f0-exposure", "F0 Score & Exposure", false));
//        ChartRepresentations.Add(new ChartRepresentationOption("level-conductivity", "Chamber Level & Conductivity", false));
//        ChartRepresentations.Add(new ChartRepresentationOption("recipe-step-map", "Recipe Loading & Steps", false));
//        _selectedRepresentation = ChartRepresentations.FirstOrDefault();
//    }

//    protected void RebuildAbsoluteAttemptMapping()
//    {
//        RowIdToAttemptName.Clear();
//        if (VisibleCycles.Count == 0 || string.IsNullOrWhiteSpace(StepHeaderKey))
//        {
//            AllAttemptSummaries = Array.Empty<AttemptSummary>();
//            return;
//        }

//        var attempts = new List<AttemptSummary>();
//        var sheetOrdinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

//        foreach (var sheetGroup in VisibleCycles.GroupBy(c => c.SheetName).OrderBy(g => g.Min(c => c.RecordedAt)))
//        {
//            var ordered = sheetGroup.OrderBy(c => c.RecordedAt).ToList();
//            var sheetAttempts = BuildSheetAttempts(sheetGroup.Key, ordered, sheetOrdinals);
//            attempts.AddRange(sheetAttempts);
//            foreach (var a in sheetAttempts)
//                foreach (var r in a.Rows) RowIdToAttemptName[r.Id] = a.Name;
//        }
//        AllAttemptSummaries = attempts;
//    }

//    // ═════════════════════════════════════════════════════════════════════
//    //  Initialise
//    // ═════════════════════════════════════════════════════════════════════
//    private async Task InitializeAsync()
//    {
//        if (_hasInitialized) return;
//        _hasInitialized = true;
//        LastActionMessage = "Connecting to SQLite database…";
//        DatabaseConnectionStatus = "Connecting…";
//        DatabaseConnectionColor = "#D68A00";
//        _isInitializing = true;
//        try
//        {
//            await Task.Run(() => _dataService.EnsureDatabaseAsync());
//            var loaded = await TryLoadWorkbookBackedCyclesAsync();
//            VisibleCycles = loaded;
//            EnsureSelectedChartDate();
//            _isInitializing = false;
//            _showRepresentations = true;
//            await Task.Yield();
//            if (Application.Current?.Dispatcher is { } d)
//                await d.InvokeAsync(RefreshAllVisuals, DispatcherPriority.Background);
//            else
//                RefreshAllVisuals();
//            DatabaseConnectionStatus = $"SQLite connected. {VisibleCycles.Count} rows available.";
//            DatabaseConnectionColor = "#3BCB78";
//            LastActionMessage = VisibleCycles.Count == 0
//                ? "Ready. Import a workbook to begin."
//                : $"Loaded {VisibleCycles.Count} workbook-backed rows.";
//        }
//        catch (Exception ex)
//        {
//            _isInitializing = false;
//            DatabaseConnectionStatus = $"Connection failed: {ex.Message}";
//            DatabaseConnectionColor = "#E74C3C";
//            LastActionMessage = "Unable to initialise local database.";
//        }
//    }

//    private async Task<List<SterilizationCycle>> TryLoadWorkbookBackedCyclesAsync()
//    {
//        var path = ResolvePreferredWorkbookPath();
//        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
//        {
//            LastActionMessage = "Preferred workbook not found. Import to continue.";
//            await Task.Run(() => _dataService.ReplaceAllCyclesAsync([]));
//            return [];
//        }

//        var importer = new WorkbookImportService();
//        var result = await Task.Run(() => importer.ImportFile(path));
//        if (!result.Success || result.ImportedCycles.Count == 0)
//        {
//            LastActionMessage = $"Workbook auto-load failed: {result.Error}";
//            await Task.Run(() => _dataService.ReplaceAllCyclesAsync([]));
//            return [];
//        }

//        _activeWorkbookPath = path;
//        _onlineSheetRowPositions = new Dictionary<string, int>(
//            result.ImportedSheetRowPositions, StringComparer.OrdinalIgnoreCase);
//        await Task.Run(() => _dataService.ReplaceAllCyclesAsync(result.ImportedCycles));
//        return result.ImportedCycles;
//    }

//    private static string? ResolvePreferredWorkbookPath()
//    {
//        var downloads = Path.Combine(
//            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
//        var direct = Path.Combine(downloads, "Cycles on Sterilizer 34.xlsx");
//        if (File.Exists(direct)) return direct;
//        if (!Directory.Exists(downloads)) return null;

//        return Directory
//            .EnumerateFiles(downloads, "*.xlsx", SearchOption.TopDirectoryOnly)
//            .OrderByDescending(File.GetLastWriteTimeUtc)
//            .FirstOrDefault(p =>
//                Path.GetFileName(p).Contains("Sterilizer", StringComparison.OrdinalIgnoreCase) ||
//                Path.GetFileName(p).Contains("Cycle", StringComparison.OrdinalIgnoreCase));
//    }

//    // ═════════════════════════════════════════════════════════════════════
//    //  Import / Wipe / Reset
//    // ═════════════════════════════════════════════════════════════════════
//    private async void ImportWorkbook()
//    {
//        if (IsBusy) return;
//        var dlg = new OpenFileDialog
//        {
//            Filter = "Excel Files (*.xlsx)|*.xlsx|All files (*.*)|*.*",
//            Multiselect = true,
//            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
//        };
//        if (dlg.ShowDialog() != true) return;

//        IsBusy = true;
//        LastActionMessage = "Importing workbook(s) — dynamic header analysis…";
//        DatabaseConnectionStatus = "Writing rows to SQLite…";
//        DatabaseConnectionColor = "#D68A00";
//        try
//        {
//            var importer = new WorkbookImportService();
//            // Support multi-file selection via ImportFiles()
//            var result = await Task.Run(() =>
//                dlg.FileNames.Length == 1
//                    ? importer.ImportFile(dlg.FileNames[0])
//                    : importer.ImportFiles(dlg.FileNames));

//            if (!result.Success)
//            {
//                LastActionMessage = $"Import failed: {result.Error}";
//                DatabaseConnectionStatus = "Import failed before database update.";
//                DatabaseConnectionColor = "#E74C3C";
//                return;
//            }

//            await Task.Run(() => _dataService.ReplaceAllCyclesAsync(result.ImportedCycles));
//            if (dlg.FileNames.Length == 1)
//            {
//                _activeWorkbookPath = dlg.FileNames[0];
//                _onlineSheetRowPositions = new Dictionary<string, int>(
//                    result.ImportedSheetRowPositions, StringComparer.OrdinalIgnoreCase);
//                OnPropertyChanged(nameof(OnlineWatchFilePath));
//                OnPropertyChanged(nameof(HasOnlineWatchFile));
//            }
//            VisibleCycles = result.ImportedCycles;
//            _showRepresentations = true;
//            RebuildHeaderCatalog();
//            RebuildAbsoluteAttemptMapping();
//            EnsureSelectedChartDate();
//            RefreshAllVisuals();
//            DatabaseConnectionStatus = $"SQLite connected. {result.ImportedCycles.Count} rows · {result.ImportedHeaders.Count} headers.";
//            DatabaseConnectionColor = "#3BCB78";
//            LastActionMessage = $"Imported {result.ImportedCycles.Count} row(s) from {dlg.FileNames.Length} file(s).";
//        }
//        catch (Exception ex)
//        {
//            LastActionMessage = $"Unexpected error: {ex.Message}";
//            DatabaseConnectionStatus = $"Database update failed: {ex.Message}";
//            DatabaseConnectionColor = "#E74C3C";
//        }
//        finally { IsBusy = false; }
//    }

//    private async void WipeDatabase()
//    {
//        if (IsBusy) return;
//        IsBusy = true;
//        LastActionMessage = "Wiping database…";
//        DatabaseConnectionStatus = "Clearing SQLite rows…";
//        DatabaseConnectionColor = "#D68A00";
//        try
//        {
//            if (IsOnline)
//            {
//                _liveTimer.Stop();
//                StopOnlineMonitoring();
//            }

//            await Task.Run(() => _dataService.ReplaceAllCyclesAsync([]));
//            VisibleCycles = [];
//            OfflineMainChartSeries.Clear();
//            OnlineMainChartSeries.Clear();
//            MainChartSeries.Clear();
//            AllAttemptSummaries = Array.Empty<AttemptSummary>();
//            RowIdToAttemptName.Clear();
//            SelectedChartDate = DateTime.Today;
//            RefreshAllVisuals();
//            DatabaseConnectionStatus = "SQLite connected. 0 rows available.";
//            DatabaseConnectionColor = "#3BCB78";
//            LastActionMessage = "Database wiped. Header catalogue cleared.";
//        }
//        catch (Exception ex)
//        {
//            LastActionMessage = $"Unexpected error: {ex.Message}";
//            DatabaseConnectionStatus = $"Wipe failed: {ex.Message}";
//            DatabaseConnectionColor = "#E74C3C";
//        }
//        finally { IsBusy = false; }
//    }

//    private void ResetConfiguration()
//    {
//        ParseExistingData = true;
//        WatchNewRowsData = IsOnline;
//        IsMetricSelectorOpen = false;
//        IsDrillDownOpen = false;
//        _showRepresentations = true;

//        foreach (var o in ComparisonMetricOptions) o.SetSelectedSilently(false);
//        _selectedMetric = null;
//        _selectedRepresentation = ChartRepresentations.FirstOrDefault();

//        _selectedCycleKeys.Clear();
//        _selectedCycleKeys.Add("__ALL__");
//        foreach (var o in AllCycleOptions) o.NotifyIsSelected();
//        OnPropertyChanged(nameof(SelectedCyclesSummary));

//        foreach (var r in TimeRangeOptions)
//            r.IsSelected = string.Equals(r.Label, "5m", StringComparison.OrdinalIgnoreCase);

//        if (_isBarChartRepresentation)
//        {
//            _isBarChartRepresentation = false;
//            OnPropertyChanged(nameof(IsBarChartRepresentation));
//        }

//        _selectedChartDate = DateTime.Today;
//        foreach (var p in new[]
//        {
//            nameof(SelectedChartDate), nameof(SelectedChartDateText),
//            nameof(AvailableDateStart), nameof(AvailableDateEnd),
//            nameof(SelectedRepresentation), nameof(SelectedRepresentationDisplayName),
//            nameof(SelectedMetric), nameof(SelectedMetricsSummary),
//            nameof(ShowSensorSelector), nameof(ChartModeLabel), nameof(EmptyStateMessage)
//        }) OnPropertyChanged(p);
//        RefreshAllVisuals();
//    }

//    // ═════════════════════════════════════════════════════════════════════
//    //  Auth
//    // ═════════════════════════════════════════════════════════════════════
//    private void Login()
//    {
//        if (LoginUsername == "admin" && LoginPassword == "admin")
//        {
//            IsAuthenticated = true;
//            LoginErrorMessage = string.Empty;
//            _ = InitializeAsync();
//        }
//        else { LoginErrorMessage = "Invalid username or password."; }
//    }
//    private void Logout()
//    {
//        IsAuthenticated = false;
//        LoginUsername = string.Empty;
//        LoginPassword = string.Empty;
//        LoginErrorMessage = string.Empty;
//    }

//    // ═════════════════════════════════════════════════════════════════════
//    //  Online toggle
//    // ═════════════════════════════════════════════════════════════════════
//    private void ToggleOnline() => IsOnline = !IsOnline;

//    private void CloseConfigurationPopup()
//    {
//        IsConfigurationPopupOpen = false;
//        IsMetricSelectorOpen = false;

//        if (HasOnlineWatchFile && WatchNewRowsData && !IsOnline)
//        {
//            IsOnline = true;
//        }
//    }

//    // ─────────────────────────────────────────────────────────────────────
//    //  File picker for online mode
//    //  Opens a standard OpenFileDialog limited to .xlsx files.
//    //  The selected path is stored in _activeWorkbookPath / OnlineWatchFilePath.
//    // ─────────────────────────────────────────────────────────────────────
//    private void SelectOnlineFile()
//    {
//        var dlg = new OpenFileDialog
//        {
//            Title = "Select the Excel file to watch for live row additions",
//            Filter = "Excel workbooks (*.xlsx)|*.xlsx|All files (*.*)|*.*",
//            InitialDirectory = !string.IsNullOrWhiteSpace(_activeWorkbookPath) && File.Exists(_activeWorkbookPath)
//                ? Path.GetDirectoryName(_activeWorkbookPath)
//                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
//        };
//        if (dlg.ShowDialog() != true) return;

//        OnlineWatchFilePath = dlg.FileName;

//        // Snapshot current row counts so the watcher only picks up NEW rows.
//        _onlineSheetRowPositions.Clear();
//        LastActionMessage = $"Live file set: {Path.GetFileName(dlg.FileName)}";

//        // If already online, restart the watcher against the new file.
//        if (IsOnline)
//        {
//            StopOnlineMonitoring();
//            _ = StartOnlineMonitoringAsync();
//        }
//    }

//    // ═════════════════════════════════════════════════════════════════════
//    //  Online monitoring — FileSystemWatcher on the selected single file
//    // ═════════════════════════════════════════════════════════════════════

//    /// <summary>
//    /// Attaches a FileSystemWatcher to the directory of the selected file,
//    /// filtered to that filename only.  On each Change event the delta reader
//    /// is debounced 500 ms then called.
//    /// </summary>
//    private async Task StartOnlineMonitoringAsync()
//    {
//        var path = _activeWorkbookPath;
//        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
//        {
//            LastActionMessage = "Online mode needs a valid Excel file to watch.";
//            return;
//        }

//        StopOnlineMonitoring();

//        var importer = new WorkbookImportService();
//        var snapshot = await Task.Run(() => importer.ImportFile(path));
//        if (!snapshot.Success)
//        {
//            LastActionMessage = $"Unable to read live workbook: {snapshot.Error}";
//            return;
//        }

//        _onlineSheetRowPositions = new Dictionary<string, int>(
//            snapshot.ImportedSheetRowPositions, StringComparer.OrdinalIgnoreCase);

//        if (snapshot.ImportedCycles.Count > 0)
//        {
//            await SeedOnlineDashboardAsync(path, snapshot);
//        }

//        _onlineProcessorCts = new CancellationTokenSource();
//        _onlineProcessorTask = Task.Run(() =>
//            ProcessOnlineCycleQueueAsync(_onlineProcessorCts.Token));

//        var dir = Path.GetDirectoryName(path)!;
//        var file = Path.GetFileName(path);

//        _onlineFileWatcher = new FileSystemWatcher(dir, file)
//        {
//            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
//            IncludeSubdirectories = false,
//            EnableRaisingEvents = true
//        };
//        _onlineFileWatcher.Changed += OnOnlineFileChanged;
//        _onlineFileWatcher.Created += OnOnlineFileChanged;
//        _onlineFileWatcher.Renamed += OnOnlineFileRenamed;

//        LastActionMessage = $"Watching {file} for new rows…";
//    }

//    private async Task SeedOnlineDashboardAsync(string path, WorkbookImportResult snapshot)
//    {
//        var workbookName = Path.GetFileName(path);
//        var shouldReplaceView =
//            ParseExistingData ||
//            VisibleCycles.Count == 0 ||
//            !VisibleCycles.Any(cycle => string.Equals(cycle.SourceWorkbookName, workbookName, StringComparison.OrdinalIgnoreCase));

//        if (!shouldReplaceView)
//        {
//            PrepareOnlineChartDefaults();
//            RefreshAllVisuals();
//            return;
//        }

//        await Task.Run(() => _dataService.ReplaceAllCyclesAsync(snapshot.ImportedCycles));

//        VisibleCycles = snapshot.ImportedCycles
//            .OrderBy(cycle => cycle.RecordedAt)
//            .ThenBy(cycle => cycle.SheetIndex)
//            .TakeLast(LiveChartWindow)
//            .ToList();

//        PrepareOnlineChartDefaults();
//        EnsureSelectedChartDate();
//        RefreshAllVisuals();
//        DatabaseConnectionStatus = $"SQLite connected. Live workbook seeded with {snapshot.ImportedCycles.Count} row(s).";
//        DatabaseConnectionColor = "#3BCB78";
//    }

//    private void PrepareOnlineChartDefaults()
//    {
//        _showRepresentations = true;
//        _selectedRepresentation = ChartRepresentations.FirstOrDefault(
//            option => string.Equals(option.Key, "timeline", StringComparison.OrdinalIgnoreCase))
//            ?? ChartRepresentations.FirstOrDefault();
//        _isBarChartRepresentation = false;

//        foreach (var option in TimeRangeOptions)
//        {
//            option.IsSelected = string.Equals(option.Label, "30m", StringComparison.OrdinalIgnoreCase);
//        }

//        _selectedCycleKeys.Clear();
//        _selectedCycleKeys.Add("__ALL__");

//        OnPropertyChanged(nameof(SelectedRepresentation));
//        OnPropertyChanged(nameof(SelectedRepresentationDisplayName));
//        OnPropertyChanged(nameof(IsBarChartRepresentation));
//        OnPropertyChanged(nameof(ChartModeLabel));
//        OnPropertyChanged(nameof(ShowSensorSelector));
//        OnPropertyChanged(nameof(CurrentRangeLabel));
//    }

//    private void StopOnlineMonitoring()
//    {
//        if (_onlineFileWatcher is not null)
//        {
//            _onlineFileWatcher.EnableRaisingEvents = false;
//            _onlineFileWatcher.Changed -= OnOnlineFileChanged;
//            _onlineFileWatcher.Created -= OnOnlineFileChanged;
//            _onlineFileWatcher.Renamed -= OnOnlineFileRenamed;
//            _onlineFileWatcher.Dispose();
//            _onlineFileWatcher = null;
//        }

//        _onlineWatcherDelayCts?.Cancel();
//        _onlineWatcherDelayCts?.Dispose();
//        _onlineWatcherDelayCts = null;

//        _onlineProcessorCts?.Cancel();
//        _onlineProcessorCts?.Dispose();
//        _onlineProcessorCts = null;
//        _onlineProcessorTask = null;

//        while (_onlineCycleQueue.TryDequeue(out _)) { }
//    }

//    private void OnOnlineFileChanged(object sender, FileSystemEventArgs e)
//        => ScheduleOnlineDeltaRead(e.FullPath);

//    private void OnOnlineFileRenamed(object sender, RenamedEventArgs e)
//    {
//        _activeWorkbookPath = e.FullPath;
//        OnPropertyChanged(nameof(OnlineWatchFilePath));
//        OnPropertyChanged(nameof(HasOnlineWatchFile));
//        ScheduleOnlineDeltaRead(e.FullPath);
//    }

//    /// <summary>
//    /// Debounces multiple rapid Changed events (e.g. Excel flushing)
//    /// then queues a single delta import.
//    /// </summary>
//    private void ScheduleOnlineDeltaRead(string path)
//    {
//        if (!IsOnline || !WatchNewRowsData) return;

//        _onlineWatcherDelayCts?.Cancel();
//        _onlineWatcherDelayCts?.Dispose();
//        _onlineWatcherDelayCts = new CancellationTokenSource();
//        var token = _onlineWatcherDelayCts.Token;

//        _ = Task.Run(async () =>
//        {
//            try
//            {
//                await Task.Delay(500, token);
//                await ReadOnlineDeltaAsync(path, token);
//            }
//            catch (OperationCanceledException) { }
//        }, token);
//    }

//    private async Task ReadOnlineDeltaAsync(string path, CancellationToken ct)
//    {
//        if (!File.Exists(path)) return;

//        await _onlineDeltaGate.WaitAsync(ct);
//        try
//        {
//            var importer = new WorkbookImportService();
//            var delta = await Task.Run(
//                () => importer.ImportNewRows(path, _onlineSheetRowPositions), ct);

//            // Advance the row-position cursor regardless of whether rows arrived.
//            _onlineSheetRowPositions = new Dictionary<string, int>(
//                delta.ImportedSheetRowPositions, StringComparer.OrdinalIgnoreCase);

//            if (!delta.Success || delta.ImportedCycles.Count == 0) return;

//            _onlineCycleQueue.Enqueue(delta.ImportedCycles);
//            _onlineQueueSignal.Release();
//        }
//        finally { _onlineDeltaGate.Release(); }
//    }

//    // Background consumer: persists batches then merges into the UI.
//    private async Task ProcessOnlineCycleQueueAsync(CancellationToken ct)
//    {
//        while (!ct.IsCancellationRequested)
//        {
//            try { await _onlineQueueSignal.WaitAsync(ct); }
//            catch (OperationCanceledException) { break; }

//            while (_onlineCycleQueue.TryDequeue(out var batch))
//            {
//                await _dataService.SaveCyclesAsync(batch);
//                await Application.Current.Dispatcher.InvokeAsync(() =>
//                {
//                    MergeOnlineCycles(batch);
//                    LastActionMessage = $"Online: +{batch.Count} new row(s) — total {VisibleCycles.Count}.";
//                }, DispatcherPriority.Background, ct);
//            }
//        }
//    }

//    /// <summary>
//    /// Appends incoming cycles to the visible set.
//    /// Enforces the LiveChartWindow sliding window so the chart scrolls
//    /// like an ECG / live stock feed.
//    /// Automatically advances the selected date to track the latest data.
//    /// </summary>
//    private void MergeOnlineCycles(IEnumerable<SterilizationCycle> incoming)
//    {
//        var existingKeys = VisibleCycles
//            .Select(c => $"{c.SheetName}|{c.RecordedAt.Ticks}|{c.SourceWorkbookName}")
//            .ToHashSet(StringComparer.OrdinalIgnoreCase);

//        var appended = incoming
//            .Where(c => existingKeys.Add($"{c.SheetName}|{c.RecordedAt.Ticks}|{c.SourceWorkbookName}"))
//            .OrderBy(c => c.RecordedAt)
//            .ToList();

//        if (appended.Count == 0) return;

//        var merged = VisibleCycles
//            .Concat(appended)
//            .OrderBy(c => c.RecordedAt)
//            .ThenBy(c => c.SheetIndex)
//            .ToList();

//        // Sliding window — keep the chart window tight.
//        if (merged.Count > LiveChartWindow)
//            merged = merged.Skip(merged.Count - LiveChartWindow).ToList();

//        VisibleCycles = merged;

//        // Auto-advance date selector to follow the live feed.
//        var latestDate = VisibleCycles[^1].RecordedAt.Date;
//        if (_selectedChartDate != latestDate)
//        {
//            _selectedChartDate = latestDate;
//            OnPropertyChanged(nameof(SelectedChartDate));
//            OnPropertyChanged(nameof(SelectedChartDateText));
//        }

//        EnsureSelectedChartDate();
//        RefreshAllVisuals();
//    }

//    private void EnsureSelectedChartDate()
//    {
//        if (VisibleCycles.Count == 0)
//        {
//            SelectedChartDate ??= DateTime.Today;
//            return;
//        }
//        var latest = VisibleCycles.Max(c => c.RecordedAt).Date;
//        var hasData = SelectedChartDate.HasValue &&
//                       VisibleCycles.Any(c => c.RecordedAt.Date == SelectedChartDate.Value.Date);
//        if (!hasData) SelectedChartDate = latest;
//    }
//}


using Microsoft.Win32;
using SterilizationGenie.Infrastructure;
using SterilizationGenie.Models;
using SterilizationGenie.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Collections.Concurrent;
using System.Threading;
using LiveCharts;
using LiveCharts.Wpf;

namespace SterilizationGenie.ViewModels;

public abstract partial class SterilizationDashboardViewModel : ObservableObject
{
    private readonly CycleDataService _dataService;
    private readonly string _exportDirectory;
    private readonly string _appRoot;
    private readonly DispatcherTimer _liveTimer;
    private readonly SemaphoreSlim _onlineDeltaGate = new(1, 1);
    private readonly SemaphoreSlim _onlineQueueSignal = new(0);
    private readonly ConcurrentQueue<List<SterilizationCycle>> _onlineCycleQueue = new();
    private bool _hasInitialized;

    // ── Online file-mode watching ─────────────────────────────────────────
    // The user selects a single .xlsx file from ConfigurationPopup.
    // FileSystemWatcher fires on every Changed/LastWrite event; we debounce
    // 500 ms then read only the NEW rows (delta import).
    private FileSystemWatcher? _onlineFileWatcher;
    private CancellationTokenSource? _onlineWatcherDelayCts;
    private CancellationTokenSource? _onlineProcessorCts;
    private Task? _onlineProcessorTask;
    private string? _activeWorkbookPath;           // currently watched file
    private Dictionary<string, int> _onlineSheetRowPositions = new(StringComparer.OrdinalIgnoreCase);

    // Sliding window for the live chart — oldest rows drop off so the chart
    // scrolls like a stock ticker instead of compressing.
    // The window size is derived from the selected time-range so that selecting
    // "10m", "30m", "1h", etc. always retains enough rows to fill that window.
    // Assumption: live data arrives at ~1 row/second at peak; we keep
    // (selected_duration_seconds * 1.25) rows as a buffer so the filter always
    // has raw material to work with.  Floor of 120 rows (~2 min) for very short
    // intervals; hard ceiling of 10 000 rows to avoid unbounded memory growth.
    private int LiveChartWindow
    {
        get
        {
            var selected = TimeRangeOptions.FirstOrDefault(o => o.IsSelected);
            if (selected is null) return 120;
            var needed = (int)Math.Ceiling(selected.Duration.TotalSeconds * 1.25);
            return Math.Max(120, Math.Min(needed, 10_000));
        }
    }

    // ── backing fields ────────────────────────────────────────────────────
    private bool _showRepresentations;
    private bool _isInitializing = true;
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
    private bool _isExportPanelOpen;
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

    // ── unified cycle multi-select ────────────────────────────────────────
    private readonly HashSet<string> _selectedCycleKeys = new(StringComparer.OrdinalIgnoreCase);

    // ── Separate cycle stores for each mode ──────────────────────────────
    // Offline mode owns _offlineCycles (imported workbook, never touched by online logic).
    // Online mode owns _onlineCycles (only live/delta rows; starts empty on every new watch session).
    // VisibleCycles is a computed accessor so all analysis code remains unchanged.
    private List<SterilizationCycle> _offlineCycles = new();
    private List<SterilizationCycle> _onlineCycles = new();

    protected List<SterilizationCycle> VisibleCycles
    {
        get => _isOnline ? _onlineCycles : _offlineCycles;
        private set
        {
            if (_isOnline)
                _onlineCycles = value;
            else
                _offlineCycles = value;
        }
    }

    protected IReadOnlyList<AttemptSummary> AllAttemptSummaries { get; private set; } = Array.Empty<AttemptSummary>();
    protected Dictionary<int, string> RowIdToAttemptName { get; } = new();
    protected string AnalysisDataSignature { get; private set; } = string.Empty;

    protected SterilizationDashboardViewModel()
    {
        _appRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SterilizationGenie");
        var databasePath = Path.Combine(_appRoot, "sterilization-genie.db");
        _exportDirectory = Path.Combine(_appRoot, "Exports");
        _dataService = new CycleDataService(databasePath);

        BuildTimeRanges();
        BuildChartRepresentations();

        // 1-second tick so online charts feel responsive.
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _liveTimer.Tick += (_, _) =>
        {
            if (IsOnline && WatchNewRowsData && HasVisualDataChanged())
                RefreshAllVisuals();
        };

        // ── Nav commands ──
        ShowDashboardCommand = new RelayCommand(() =>
        {
            IsDashboardSelected = true; IsAlertsSelected = false;
            IsSummarySelected = false; IsConfigurationPopupOpen = false;
        });
        ShowAlertsCommand = new RelayCommand(() =>
        {
            IsDashboardSelected = false; IsAlertsSelected = true;
            IsSummarySelected = false; IsConfigurationPopupOpen = false;
        });
        ShowSummaryCommand = new RelayCommand(() =>
        {
            IsDashboardSelected = false; IsAlertsSelected = false;
            IsSummarySelected = true; IsConfigurationPopupOpen = false;
        });
        ShowSettingsCommand = new RelayCommand(() =>
        {
            if (!IsOnline) IsConfigurationPopupOpen = true;
        });
        CloseConfigurationPopupCommand = new RelayCommand(CloseConfigurationPopup);

        // ── Data commands ──
        ImportWorkbookCommand = new RelayCommand(ImportWorkbook, () => !IsBusy);
        WipeDatabaseCommand = new RelayCommand(WipeDatabase, () => !IsBusy);
        ResetConfigurationCommand = new RelayCommand(ResetConfiguration);
        ExportCsvCommand = new RelayCommand(ExportCsv);
        ExportJsonCommand = new RelayCommand(ExportJson);

        // ── Online file picker (ConfigurationPopup "Select Live Excel File") ──
        SelectOnlineFileCommand = new RelayCommand(SelectOnlineFile, () => !IsOnline);

        // ── Chart commands ──
        SelectTimeRangeCommand = new RelayCommand<TimeRangeOption>(SelectTimeRange);
        ToggleMetricSelectorCommand = new RelayCommand(() => IsMetricSelectorOpen = !IsMetricSelectorOpen);
        ToggleChartTypeCommand = new RelayCommand(() => IsBarChartRepresentation = !IsBarChartRepresentation);
        ToggleExportPanelCommand = new RelayCommand(() => IsExportPanelOpen = !IsExportPanelOpen);
        CloseExportPanelCommand = new RelayCommand(() => IsExportPanelOpen = false);

        // ── Auth ──
        LoginCommand = new RelayCommand(Login);
        LogoutCommand = new RelayCommand(Logout);
        ToggleOnlineCommand = new RelayCommand(ToggleOnline);

        Roles.Add("Admin"); Roles.Add("Manager");
        Roles.Add("Operator"); Roles.Add("Viewer");
        SelectedRole = "Admin";

        YAxisFormatter = v => v.ToString("0.00");
        TooltipLabelPoint = cp => $"{cp.Y:0.##}";

        InitDrillDown();
    }

    // ── Collections ──────────────────────────────────────────────────────
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
    public ObservableCollection<CycleAttemptSelectOption> FailedCycleOptions { get; } = new();
    public ObservableCollection<CycleAttemptSelectOption> GoodCycleOptions { get; } = new();
    public ObservableCollection<CycleAttemptSelectOption> AllCycleOptions { get; } = new();

    public SeriesCollection MainChartSeries { get; } = new SeriesCollection();
    public SeriesCollection OfflineMainChartSeries { get; } = new SeriesCollection();
    public SeriesCollection OnlineMainChartSeries { get; } = new SeriesCollection();
    public string[] XLabels { get; protected set; } = Array.Empty<string>();
    public Func<double, string>? XAxisFormatter { get; protected set; }
    public Func<double, string>? YAxisFormatter { get; protected set; }
    public Func<ChartPoint, string>? TooltipLabelPoint { get; protected set; }

    // ── Commands ─────────────────────────────────────────────────────────
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
    public ICommand ToggleExportPanelCommand { get; }
    public ICommand CloseExportPanelCommand { get; }

    // NEW: file picker for online mode.
    // Bound in ConfigurationPopup.xaml as SelectOnlineFileCommand.
    public ICommand SelectOnlineFileCommand { get; }

    // ── Online watch-file properties ──────────────────────────────────────
    /// <summary>Full path of the Excel file selected for online monitoring.</summary>
    public string OnlineWatchFilePath
    {
        get => _activeWorkbookPath ?? string.Empty;
        private set
        {
            if (!string.Equals(_activeWorkbookPath, value, StringComparison.Ordinal))
            {
                _activeWorkbookPath = string.IsNullOrWhiteSpace(value) ? null : value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasOnlineWatchFile));
            }
        }
    }

    /// <summary>True once the user has selected a live Excel file.</summary>
    public bool HasOnlineWatchFile => !string.IsNullOrWhiteSpace(_activeWorkbookPath)
                                      && File.Exists(_activeWorkbookPath);

    // ── Export panel ─────────────────────────────────────────────────────
    public bool IsExportPanelOpen
    {
        get => _isExportPanelOpen;
        set
        {
            if (SetProperty(ref _isExportPanelOpen, value))
                OnPropertyChanged(nameof(ExportPanelSummary));
        }
    }

    public string ExportPanelSummary
    {
        get
        {
            var lines = new List<string>
            {
                $"View         : {SelectedRepresentationDisplayName}",
                $"Mode         : {(IsBarChartRepresentation ? "Bar" : "Line")}",
                $"Date         : {SelectedChartDateText}",
                $"Time window  : {CurrentRangeLabel}",
                $"Cycles shown : {SelectedCyclesSummary}",
                $"Metric       : {SelectedMetric?.DisplayName ?? "(none)"}",
                $"Series count : {MainChartSeries.Count}",
                $"Workbook     : {ImportedWorkbookName}"
            };
            return string.Join(Environment.NewLine, lines);
        }
    }

    // ── View/nav state ────────────────────────────────────────────────────
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
    public bool ShowDashboardView => IsDashboardSelected;
    public bool ShowAlertsView => IsAlertsSelected;
    public bool ShowSummaryView => IsSummarySelected;
    public bool ShowSettingsView => false;

    // ── IsOnline toggle ───────────────────────────────────────────────────
    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            if (SetProperty(ref _isOnline, value))
            {
                OnPropertyChanged(nameof(OnlineStatusLabel));
                OnPropertyChanged(nameof(OnlineStatusColor));
                if (SelectOnlineFileCommand is RelayCommand rc) rc.RaiseCanExecuteChanged();

                if (!value)
                {
                    WatchNewRowsData = false;
                    _liveTimer.Stop();
                    StopOnlineMonitoring();
                    IsDrillDownOpen = false;
                    // Restore offline context — invalidate caches so offline charts rebuild
                    // from the preserved _offlineCycles, not from online-mode leftovers.
                    AnalysisDataSignature = string.Empty;
                    EnsureSelectedChartDate();
                    RefreshAllVisuals();
                    LastActionMessage = "Polling stopped.";
                }
                else
                {
                    // Guard: require a valid file to be selected first.
                    if (!HasOnlineWatchFile)
                    {
                        LastActionMessage = "Select a live Excel file before going online.";
                        _isOnline = false;
                        OnPropertyChanged(nameof(IsOnline));
                        OnPropertyChanged(nameof(OnlineStatusLabel));
                        OnPropertyChanged(nameof(OnlineStatusColor));
                        IsConfigurationPopupOpen = true;
                        return;
                    }
                    IsConfigurationPopupOpen = false;
                    WatchNewRowsData = true;
                    IsDrillDownOpen = false;
                    _liveTimer.Start();
                    _ = StartOnlineMonitoringAsync();
                    LastActionMessage = $"Online: watching {Path.GetFileName(OnlineWatchFilePath)} for new rows.";
                }
            }
        }
    }
    public string OnlineStatusLabel => _isOnline ? "Online" : "Offline";
    public string OnlineStatusColor => _isOnline ? "#48D964" : "#E06262";

    // ── Misc bindable properties ──────────────────────────────────────────
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
                _showRepresentations = true;
                RefreshAllVisuals();
            }
        }
    }
    public bool ShowSensorSelector => string.Equals(
        SelectedRepresentation?.Key, "timeline", StringComparison.OrdinalIgnoreCase);
    public bool CanToggleChartMode => SelectedRepresentation is not null;
    public bool HasRenderableSeries => (IsOnline ? OnlineMainChartSeries : OfflineMainChartSeries).Count > 0;
    public string SelectedRepresentationDisplayName =>
        SelectedRepresentation?.DisplayName ?? "Select comparison view";
    public string ChartModeLabel => SelectedRepresentation is null
        ? "Select comparison view first"
        : IsBarChartRepresentation ? "Switch to line graph mode" : "Switch to bar graph mode";

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
                _showRepresentations = true;
                RefreshAllVisuals();
            }
        }
    }
    public string SelectedChartDateText => SelectedChartDate?.ToString("dd-MM-yyyy") ?? "Select date";
    public IEnumerable<DateTime> AvailableDates =>
        VisibleCycles.Select(c => c.RecordedAt.Date).Distinct().OrderBy(d => d);
    public DateTime? AvailableDateStart =>
        VisibleCycles.Count == 0 ? null : VisibleCycles.Min(c => c.RecordedAt).Date;
    public DateTime? AvailableDateEnd =>
        VisibleCycles.Count == 0 ? null : VisibleCycles.Max(c => c.RecordedAt).Date;

    public string XAxisTitle => SelectedRepresentation?.Key switch
    {
        "good-failed-envelope" => $"Recorded time in {CurrentRangeLabel} window on {SelectedChartDateText}",
        "cycles-info" => $"Recorded time in {CurrentRangeLabel} window on {SelectedChartDateText}",
        "cycle-duration" => "Cycle attempts",
        "temperature-profile" => $"Recorded time in {CurrentRangeLabel} window on {SelectedChartDateText}",
        "pressure-profile" => $"Recorded time in {CurrentRangeLabel} window on {SelectedChartDateText}",
        "f0-exposure" => "Cycle attempts",
        "level-conductivity" => "Cycle attempts",
        "recipe-step-map" => "Cycle attempts",
        "timeline" => IsBarChartRepresentation
                                    ? $"Sensor headers in {CurrentRangeLabel} window"
                                    : $"Recorded time on {SelectedChartDateText}",
        _ => $"Recorded time on {SelectedChartDateText}"
    };
    public string YAxisTitle => SelectedRepresentation?.Key switch
    {
        "good-failed-envelope" => "Temperature sensor value from workbook",
        "cycles-info" => "Cycle duration / process metric",
        "cycle-duration" => "Duration / process step",
        "temperature-profile" => "Average temperature across selected sensors",
        "pressure-profile" => "Average pressure across selected sensors",
        "f0-exposure" => "Peak F0 value",
        "level-conductivity" => "Average level / conductivity value",
        "recipe-step-map" => "Step / stage count",
        "timeline" => IsBarChartRepresentation
                                    ? "Average sensor value in selected window"
                                    : "Selected live sensor values",
        _ => "Sensor value"
    };
    public double YAxisMin { get => _yAxisMin; set => SetProperty(ref _yAxisMin, value); }
    public double YAxisMax { get => _yAxisMax; set => SetProperty(ref _yAxisMax, value); }
    public double XAxisSeparatorStep { get => _xAxisSeparatorStep; set => SetProperty(ref _xAxisSeparatorStep, value); }
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
        get => _loginUsername; set => SetProperty(ref _loginUsername, value);
    }
    public string LoginPassword
    {
        get => _loginPassword; private set => SetProperty(ref _loginPassword, value);
    }
    public string LoginErrorMessage
    {
        get => _loginErrorMessage; set => SetProperty(ref _loginErrorMessage, value);
    }
    public void UpdateLoginPassword(string password) => LoginPassword = password;

    public bool ParseExistingData { get => _parseExistingData; set => SetProperty(ref _parseExistingData, value); }
    public bool WatchNewRowsData { get => _watchNewRowsData; set => SetProperty(ref _watchNewRowsData, value); }

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
        get => _lastActionMessage; set => SetProperty(ref _lastActionMessage, value);
    }
    public string DatabaseConnectionStatus
    {
        get => _databaseConnectionStatus; private set => SetProperty(ref _databaseConnectionStatus, value);
    }
    public string DatabaseConnectionColor
    {
        get => _databaseConnectionColor; private set => SetProperty(ref _databaseConnectionColor, value);
    }

    public bool IsMetricSelectorOpen { get => _isMetricSelectorOpen; set => SetProperty(ref _isMetricSelectorOpen, value); }
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

    public bool HasCycleData => VisibleCycles.Count > 0;

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

    public string SelectedCyclesSummary
    {
        get
        {
            var count = _selectedCycleKeys.Count;
            if (count == 0) return "All Cycles";
            var allOption = AllCycleOptions.FirstOrDefault(o => o.IsAll);
            if (allOption is not null && _selectedCycleKeys.Contains(allOption.Key)) return "All Cycles";
            return count == 1
                ? AllCycleOptions.FirstOrDefault(o => _selectedCycleKeys.Contains(o.Key))?.DisplayName ?? "1 cycle selected"
                : $"{count} cycles selected";
        }
    }

    public bool IsCycleOptionSelected(string key) => _selectedCycleKeys.Contains(key);

    public void ToggleCycleSelection(CycleAttemptSelectOption option, bool isSelected)
    {
        if (option is null) return;
        if (option.IsAll)
        {
            _selectedCycleKeys.Clear();
            if (isSelected) _selectedCycleKeys.Add(option.Key);
            foreach (var o in AllCycleOptions) o.NotifyIsSelected();
        }
        else
        {
            var allKey = AllCycleOptions.FirstOrDefault(o => o.IsAll)?.Key;
            if (allKey is not null) _selectedCycleKeys.Remove(allKey);
            if (isSelected) _selectedCycleKeys.Add(option.Key);
            else _selectedCycleKeys.Remove(option.Key);
            if (_selectedCycleKeys.Count == 0 && allKey is not null)
            {
                _selectedCycleKeys.Add(allKey);
                AllCycleOptions.FirstOrDefault(o => o.IsAll)?.NotifyIsSelected();
            }
        }
        SyncLegacyCycleSelectors();
        OnPropertyChanged(nameof(SelectedCyclesSummary));
        OnPropertyChanged(nameof(CycleSelectionSummary));
        RefreshAllVisuals();
    }

    private void SyncLegacyCycleSelectors()
    {
        var allKey = AllCycleOptions.FirstOrDefault(o => o.IsAll)?.Key;
        var isAllSelected = allKey is not null && _selectedCycleKeys.Contains(allKey);

        if (isAllSelected || _selectedCycleKeys.Count == 0)
        {
            _selectedFailedCycle = FailedCycleOptions.FirstOrDefault(o => o.IsAll) ?? FailedCycleOptions.FirstOrDefault();
            _selectedGoodCycle = GoodCycleOptions.FirstOrDefault(o => o.IsAll) ?? GoodCycleOptions.FirstOrDefault();
        }
        else
        {
            var gk = GoodCycleOptions.FirstOrDefault(o => !o.IsAll && _selectedCycleKeys.Contains(o.Key))?.Key;
            var fk = FailedCycleOptions.FirstOrDefault(o => !o.IsAll && _selectedCycleKeys.Contains(o.Key))?.Key;
            _selectedFailedCycle = fk is null
                ? FailedCycleOptions.FirstOrDefault(o => o.IsAll) ?? FailedCycleOptions.FirstOrDefault()
                : FailedCycleOptions.FirstOrDefault(o => o.Key == fk);
            _selectedGoodCycle = gk is null
                ? GoodCycleOptions.FirstOrDefault(o => o.IsAll) ?? GoodCycleOptions.FirstOrDefault()
                : GoodCycleOptions.FirstOrDefault(o => o.Key == gk);
        }

        OnPropertyChanged(nameof(SelectedFailedCycle));
        OnPropertyChanged(nameof(SelectedGoodCycle));
        OnPropertyChanged(nameof(SelectedFailedCycleLabel));
        OnPropertyChanged(nameof(SelectedGoodCycleLabel));
    }

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
    public string DrillDownYAxisTitle { get => _drillDownYAxisTitle; set => SetProperty(ref _drillDownYAxisTitle, value); }

    private string _drillDownXAxisTitle = "Elapsed Time (Minutes)";
    public string DrillDownXAxisTitle { get => _drillDownXAxisTitle; set => SetProperty(ref _drillDownXAxisTitle, value); }

    private string _drillDownPointSummary = "Click a point to inspect the workbook coordinates.";
    public string DrillDownPointSummary { get => _drillDownPointSummary; set => SetProperty(ref _drillDownPointSummary, value); }

    private void BuildChartRepresentations()
    {
        ChartRepresentations.Clear();
        ChartRepresentations.Add(new ChartRepresentationOption("timeline", "Live Sensor Timeline", true));
        ChartRepresentations.Add(new ChartRepresentationOption("good-failed-envelope", "Good vs Failed Min/Max", false));
        ChartRepresentations.Add(new ChartRepresentationOption("cycles-info", "Cycles Info", false));
        ChartRepresentations.Add(new ChartRepresentationOption("cycle-duration", "Cycle Duration Analytics", false));
        ChartRepresentations.Add(new ChartRepresentationOption("temperature-profile", "Temperature Sensor Profile", false));
        ChartRepresentations.Add(new ChartRepresentationOption("pressure-profile", "Pressure Sensor Profile", false));
        ChartRepresentations.Add(new ChartRepresentationOption("f0-exposure", "F0 Score & Exposure", false));
        ChartRepresentations.Add(new ChartRepresentationOption("level-conductivity", "Chamber Level & Conductivity", false));
        ChartRepresentations.Add(new ChartRepresentationOption("recipe-step-map", "Recipe Loading & Steps", false));
        _selectedRepresentation = ChartRepresentations.FirstOrDefault();
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
            var ordered = sheetGroup.OrderBy(c => c.RecordedAt).ToList();
            var sheetAttempts = BuildSheetAttempts(sheetGroup.Key, ordered, sheetOrdinals);
            attempts.AddRange(sheetAttempts);
            foreach (var a in sheetAttempts)
                foreach (var r in a.Rows) RowIdToAttemptName[r.Id] = a.Name;
        }
        AllAttemptSummaries = attempts;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Initialise
    // ═════════════════════════════════════════════════════════════════════
    private async Task InitializeAsync()
    {
        if (_hasInitialized) return;
        _hasInitialized = true;
        LastActionMessage = "Connecting to SQLite database…";
        DatabaseConnectionStatus = "Connecting…";
        DatabaseConnectionColor = "#D68A00";
        _isInitializing = true;
        try
        {
            await Task.Run(() => _dataService.EnsureDatabaseAsync());
            var loaded = await TryLoadWorkbookBackedCyclesAsync();
            // TryLoadWorkbookBackedCyclesAsync already sets _offlineCycles; no assignment needed.
            // (VisibleCycles routes through IsOnline — at startup IsOnline is always false.)
            EnsureSelectedChartDate();
            _isInitializing = false;
            _showRepresentations = true;
            await Task.Yield();
            if (Application.Current?.Dispatcher is { } d)
                await d.InvokeAsync(RefreshAllVisuals, DispatcherPriority.Background);
            else
                RefreshAllVisuals();
            DatabaseConnectionStatus = $"SQLite connected. {VisibleCycles.Count} rows available.";
            DatabaseConnectionColor = "#3BCB78";
            LastActionMessage = VisibleCycles.Count == 0
                ? "Ready. Import a workbook to begin."
                : $"Loaded {VisibleCycles.Count} workbook-backed rows.";
        }
        catch (Exception ex)
        {
            _isInitializing = false;
            DatabaseConnectionStatus = $"Connection failed: {ex.Message}";
            DatabaseConnectionColor = "#E74C3C";
            LastActionMessage = "Unable to initialise local database.";
        }
    }

    private async Task<List<SterilizationCycle>> TryLoadWorkbookBackedCyclesAsync()
    {
        var path = ResolvePreferredWorkbookPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            LastActionMessage = "Preferred workbook not found. Import to continue.";
            await Task.Run(() => _dataService.ReplaceAllCyclesAsync([]));
            return [];
        }

        var importer = new WorkbookImportService();
        var result = await Task.Run(() => importer.ImportFile(path));
        if (!result.Success || result.ImportedCycles.Count == 0)
        {
            LastActionMessage = $"Workbook auto-load failed: {result.Error}";
            await Task.Run(() => _dataService.ReplaceAllCyclesAsync([]));
            return [];
        }

        _activeWorkbookPath = path;
        _onlineSheetRowPositions = new Dictionary<string, int>(
            result.ImportedSheetRowPositions, StringComparer.OrdinalIgnoreCase);
        await Task.Run(() => _dataService.ReplaceAllCyclesAsync(result.ImportedCycles));
        // Auto-loaded workbook data belongs to the offline store only.
        _offlineCycles = result.ImportedCycles;
        return result.ImportedCycles;
    }

    private static string? ResolvePreferredWorkbookPath()
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var direct = Path.Combine(downloads, "Cycles on Sterilizer 34.xlsx");
        if (File.Exists(direct)) return direct;
        if (!Directory.Exists(downloads)) return null;

        return Directory
            .EnumerateFiles(downloads, "*.xlsx", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault(p =>
                Path.GetFileName(p).Contains("Sterilizer", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(p).Contains("Cycle", StringComparison.OrdinalIgnoreCase));
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Import / Wipe / Reset
    // ═════════════════════════════════════════════════════════════════════
    private async void ImportWorkbook()
    {
        if (IsBusy) return;
        var dlg = new OpenFileDialog
        {
            Filter = "Excel Files (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog() != true) return;

        IsBusy = true;
        LastActionMessage = "Importing workbook(s) — dynamic header analysis…";
        DatabaseConnectionStatus = "Writing rows to SQLite…";
        DatabaseConnectionColor = "#D68A00";
        try
        {
            var importer = new WorkbookImportService();
            // Support multi-file selection via ImportFiles()
            var result = await Task.Run(() =>
                dlg.FileNames.Length == 1
                    ? importer.ImportFile(dlg.FileNames[0])
                    : importer.ImportFiles(dlg.FileNames));

            if (!result.Success)
            {
                LastActionMessage = $"Import failed: {result.Error}";
                DatabaseConnectionStatus = "Import failed before database update.";
                DatabaseConnectionColor = "#E74C3C";
                return;
            }

            await Task.Run(() => _dataService.ReplaceAllCyclesAsync(result.ImportedCycles));
            if (dlg.FileNames.Length == 1)
            {
                _activeWorkbookPath = dlg.FileNames[0];
                _onlineSheetRowPositions = new Dictionary<string, int>(
                    result.ImportedSheetRowPositions, StringComparer.OrdinalIgnoreCase);
                OnPropertyChanged(nameof(OnlineWatchFilePath));
                OnPropertyChanged(nameof(HasOnlineWatchFile));
            }
            // Import always targets the offline store — the online store is unaffected.
            _offlineCycles = result.ImportedCycles;
            _showRepresentations = true;
            RebuildHeaderCatalog();
            RebuildAbsoluteAttemptMapping();
            EnsureSelectedChartDate();
            RefreshAllVisuals();
            DatabaseConnectionStatus = $"SQLite connected. {result.ImportedCycles.Count} rows · {result.ImportedHeaders.Count} headers.";
            DatabaseConnectionColor = "#3BCB78";
            LastActionMessage = $"Imported {result.ImportedCycles.Count} row(s) from {dlg.FileNames.Length} file(s).";
        }
        catch (Exception ex)
        {
            LastActionMessage = $"Unexpected error: {ex.Message}";
            DatabaseConnectionStatus = $"Database update failed: {ex.Message}";
            DatabaseConnectionColor = "#E74C3C";
        }
        finally { IsBusy = false; }
    }

    private async void WipeDatabase()
    {
        if (IsBusy) return;
        IsBusy = true;
        LastActionMessage = "Wiping database…";
        DatabaseConnectionStatus = "Clearing SQLite rows…";
        DatabaseConnectionColor = "#D68A00";
        try
        {
            if (IsOnline)
            {
                _liveTimer.Stop();
                StopOnlineMonitoring();
            }

            await Task.Run(() => _dataService.ReplaceAllCyclesAsync([]));
            _offlineCycles = [];
            _onlineCycles = [];
            OfflineMainChartSeries.Clear();
            OnlineMainChartSeries.Clear();
            MainChartSeries.Clear();
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
        IsDrillDownOpen = false;
        _showRepresentations = true;

        foreach (var o in ComparisonMetricOptions) o.SetSelectedSilently(false);
        _selectedMetric = null;
        _selectedRepresentation = ChartRepresentations.FirstOrDefault();

        _selectedCycleKeys.Clear();
        _selectedCycleKeys.Add("__ALL__");
        foreach (var o in AllCycleOptions) o.NotifyIsSelected();
        OnPropertyChanged(nameof(SelectedCyclesSummary));

        foreach (var r in TimeRangeOptions)
            r.IsSelected = string.Equals(r.Label, "5m", StringComparison.OrdinalIgnoreCase);

        if (_isBarChartRepresentation)
        {
            _isBarChartRepresentation = false;
            OnPropertyChanged(nameof(IsBarChartRepresentation));
        }

        _selectedChartDate = DateTime.Today;
        foreach (var p in new[]
        {
            nameof(SelectedChartDate), nameof(SelectedChartDateText),
            nameof(AvailableDateStart), nameof(AvailableDateEnd),
            nameof(SelectedRepresentation), nameof(SelectedRepresentationDisplayName),
            nameof(SelectedMetric), nameof(SelectedMetricsSummary),
            nameof(ShowSensorSelector), nameof(ChartModeLabel), nameof(EmptyStateMessage)
        }) OnPropertyChanged(p);
        RefreshAllVisuals();
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Auth
    // ═════════════════════════════════════════════════════════════════════
    private void Login()
    {
        if (LoginUsername == "admin" && LoginPassword == "admin")
        {
            IsAuthenticated = true;
            LoginErrorMessage = string.Empty;
            _ = InitializeAsync();
        }
        else { LoginErrorMessage = "Invalid username or password."; }
    }
    private void Logout()
    {
        IsAuthenticated = false;
        LoginUsername = string.Empty;
        LoginPassword = string.Empty;
        LoginErrorMessage = string.Empty;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Online toggle
    // ═════════════════════════════════════════════════════════════════════
    private void ToggleOnline() => IsOnline = !IsOnline;

    private void CloseConfigurationPopup()
    {
        IsConfigurationPopupOpen = false;
        IsMetricSelectorOpen = false;

        if (HasOnlineWatchFile && WatchNewRowsData && !IsOnline)
        {
            IsOnline = true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  File picker for online mode
    //  Opens a standard OpenFileDialog limited to .xlsx files.
    //  The selected path is stored in _activeWorkbookPath / OnlineWatchFilePath.
    // ─────────────────────────────────────────────────────────────────────
    private void SelectOnlineFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select the Excel file to watch for live row additions",
            Filter = "Excel workbooks (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            InitialDirectory = !string.IsNullOrWhiteSpace(_activeWorkbookPath) && File.Exists(_activeWorkbookPath)
                ? Path.GetDirectoryName(_activeWorkbookPath)
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dlg.ShowDialog() != true) return;

        OnlineWatchFilePath = dlg.FileName;

        // Snapshot current row counts so the watcher only picks up NEW rows.
        _onlineSheetRowPositions.Clear();
        LastActionMessage = $"Live file set: {Path.GetFileName(dlg.FileName)}";

        // If already online, restart the watcher against the new file.
        if (IsOnline)
        {
            StopOnlineMonitoring();
            _ = StartOnlineMonitoringAsync();
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Online monitoring — FileSystemWatcher on the selected single file
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attaches a FileSystemWatcher to the directory of the selected file,
    /// filtered to that filename only.  On each Change event the delta reader
    /// is debounced 500 ms then called.
    /// </summary>
    private async Task StartOnlineMonitoringAsync()
    {
        var path = _activeWorkbookPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            LastActionMessage = "Online mode needs a valid Excel file to watch.";
            return;
        }

        StopOnlineMonitoring();

        var importer = new WorkbookImportService();
        var snapshot = await Task.Run(() => importer.ImportFile(path));
        if (!snapshot.Success)
        {
            LastActionMessage = $"Unable to read live workbook: {snapshot.Error}";
            return;
        }

        _onlineSheetRowPositions = new Dictionary<string, int>(
            snapshot.ImportedSheetRowPositions, StringComparer.OrdinalIgnoreCase);

        if (snapshot.ImportedCycles.Count > 0)
        {
            await SeedOnlineDashboardAsync(path, snapshot);
        }

        _onlineProcessorCts = new CancellationTokenSource();
        _onlineProcessorTask = Task.Run(() =>
            ProcessOnlineCycleQueueAsync(_onlineProcessorCts.Token));

        var dir = Path.GetDirectoryName(path)!;
        var file = Path.GetFileName(path);

        _onlineFileWatcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        _onlineFileWatcher.Changed += OnOnlineFileChanged;
        _onlineFileWatcher.Created += OnOnlineFileChanged;
        _onlineFileWatcher.Renamed += OnOnlineFileRenamed;

        LastActionMessage = $"Watching {file} for new rows…";
    }

    private async Task SeedOnlineDashboardAsync(string path, WorkbookImportResult snapshot)
    {
        // Online mode shows ONLY live/new rows — it never seeds from historical workbook data.
        // We still save the snapshot to the DB (so new delta rows get correct FK headers),
        // but VisibleCycles for the online store starts empty; charts begin blank and fill
        // as new rows arrive via the FileSystemWatcher delta reader.
        await Task.Run(() => _dataService.ReplaceAllCyclesAsync(snapshot.ImportedCycles));

        // Start the online store empty so the view is clean.
        _onlineCycles = new List<SterilizationCycle>();

        PrepareOnlineChartDefaults();
        EnsureSelectedChartDate();
        RefreshAllVisuals();
        DatabaseConnectionStatus = $"SQLite connected. Watching for new rows (snapshot: {snapshot.ImportedCycles.Count} existing).";
        DatabaseConnectionColor = "#3BCB78";
    }

    private void PrepareOnlineChartDefaults()
    {
        _showRepresentations = true;
        _selectedRepresentation = ChartRepresentations.FirstOrDefault(
            option => string.Equals(option.Key, "timeline", StringComparison.OrdinalIgnoreCase))
            ?? ChartRepresentations.FirstOrDefault();
        _isBarChartRepresentation = false;

        foreach (var option in TimeRangeOptions)
        {
            option.IsSelected = string.Equals(option.Label, "30m", StringComparison.OrdinalIgnoreCase);
        }

        _selectedCycleKeys.Clear();
        _selectedCycleKeys.Add("__ALL__");

        OnPropertyChanged(nameof(SelectedRepresentation));
        OnPropertyChanged(nameof(SelectedRepresentationDisplayName));
        OnPropertyChanged(nameof(IsBarChartRepresentation));
        OnPropertyChanged(nameof(ChartModeLabel));
        OnPropertyChanged(nameof(ShowSensorSelector));
        OnPropertyChanged(nameof(CurrentRangeLabel));
    }

    private void StopOnlineMonitoring()
    {
        if (_onlineFileWatcher is not null)
        {
            _onlineFileWatcher.EnableRaisingEvents = false;
            _onlineFileWatcher.Changed -= OnOnlineFileChanged;
            _onlineFileWatcher.Created -= OnOnlineFileChanged;
            _onlineFileWatcher.Renamed -= OnOnlineFileRenamed;
            _onlineFileWatcher.Dispose();
            _onlineFileWatcher = null;
        }

        _onlineWatcherDelayCts?.Cancel();
        _onlineWatcherDelayCts?.Dispose();
        _onlineWatcherDelayCts = null;

        _onlineProcessorCts?.Cancel();
        _onlineProcessorCts?.Dispose();
        _onlineProcessorCts = null;
        _onlineProcessorTask = null;

        while (_onlineCycleQueue.TryDequeue(out _)) { }
    }

    private void OnOnlineFileChanged(object sender, FileSystemEventArgs e)
        => ScheduleOnlineDeltaRead(e.FullPath);

    private void OnOnlineFileRenamed(object sender, RenamedEventArgs e)
    {
        _activeWorkbookPath = e.FullPath;
        OnPropertyChanged(nameof(OnlineWatchFilePath));
        OnPropertyChanged(nameof(HasOnlineWatchFile));
        ScheduleOnlineDeltaRead(e.FullPath);
    }

    /// <summary>
    /// Debounces multiple rapid Changed events (e.g. Excel flushing)
    /// then queues a single delta import.
    /// </summary>
    private void ScheduleOnlineDeltaRead(string path)
    {
        if (!IsOnline || !WatchNewRowsData) return;

        _onlineWatcherDelayCts?.Cancel();
        _onlineWatcherDelayCts?.Dispose();
        _onlineWatcherDelayCts = new CancellationTokenSource();
        var token = _onlineWatcherDelayCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token);
                await ReadOnlineDeltaAsync(path, token);
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private async Task ReadOnlineDeltaAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return;

        await _onlineDeltaGate.WaitAsync(ct);
        try
        {
            var importer = new WorkbookImportService();
            var delta = await Task.Run(
                () => importer.ImportNewRows(path, _onlineSheetRowPositions), ct);

            // Advance the row-position cursor regardless of whether rows arrived.
            _onlineSheetRowPositions = new Dictionary<string, int>(
                delta.ImportedSheetRowPositions, StringComparer.OrdinalIgnoreCase);

            if (!delta.Success || delta.ImportedCycles.Count == 0) return;

            _onlineCycleQueue.Enqueue(delta.ImportedCycles);
            _onlineQueueSignal.Release();
        }
        finally { _onlineDeltaGate.Release(); }
    }

    // Background consumer: persists batches then merges into the UI.
    private async Task ProcessOnlineCycleQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _onlineQueueSignal.WaitAsync(ct); }
            catch (OperationCanceledException) { break; }

            while (_onlineCycleQueue.TryDequeue(out var batch))
            {
                await _dataService.SaveCyclesAsync(batch);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MergeOnlineCycles(batch);
                    LastActionMessage = $"Online: +{batch.Count} new row(s) — total {VisibleCycles.Count}.";
                }, DispatcherPriority.Background, ct);
            }
        }
    }

    /// <summary>
    /// Appends incoming cycles to the visible set.
    /// Enforces the LiveChartWindow sliding window so the chart scrolls
    /// like an ECG / live stock feed.
    /// Automatically advances the selected date to track the latest data.
    /// </summary>
    private void MergeOnlineCycles(IEnumerable<SterilizationCycle> incoming)
    {
        var existingKeys = VisibleCycles
            .Select(c => $"{c.SheetName}|{c.RecordedAt.Ticks}|{c.SourceWorkbookName}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var appended = incoming
            .Where(c => existingKeys.Add($"{c.SheetName}|{c.RecordedAt.Ticks}|{c.SourceWorkbookName}"))
            .OrderBy(c => c.RecordedAt)
            .ToList();

        if (appended.Count == 0) return;

        var merged = VisibleCycles
            .Concat(appended)
            .OrderBy(c => c.RecordedAt)
            .ThenBy(c => c.SheetIndex)
            .ToList();

        // Sliding window — keep the chart window tight.
        if (merged.Count > LiveChartWindow)
            merged = merged.Skip(merged.Count - LiveChartWindow).ToList();

        VisibleCycles = merged;

        // Auto-advance date selector to follow the live feed.
        var latestDate = VisibleCycles[^1].RecordedAt.Date;
        if (_selectedChartDate != latestDate)
        {
            _selectedChartDate = latestDate;
            OnPropertyChanged(nameof(SelectedChartDate));
            OnPropertyChanged(nameof(SelectedChartDateText));
        }

        EnsureSelectedChartDate();
        RefreshAllVisuals();
    }

    private void EnsureSelectedChartDate()
    {
        if (VisibleCycles.Count == 0)
        {
            SelectedChartDate ??= DateTime.Today;
            return;
        }
        var latest = VisibleCycles.Max(c => c.RecordedAt).Date;
        var hasData = SelectedChartDate.HasValue &&
                       VisibleCycles.Any(c => c.RecordedAt.Date == SelectedChartDate.Value.Date);
        if (!hasData) SelectedChartDate = latest;
    }
}