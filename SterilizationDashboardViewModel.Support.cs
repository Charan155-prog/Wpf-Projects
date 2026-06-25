using LiveCharts;
using LiveCharts.Configurations;
using LiveCharts.Defaults;
using LiveCharts.Definitions.Series;
using LiveCharts.Wpf;
using SterilizationGenie.Infrastructure;
using SterilizationGenie.Models;
using SterilizationGenie.Services;
using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Documents;
using System.Windows.Input;
using static SterilizationGenie.ViewModels.SterilizationDashboardViewModel;

namespace SterilizationGenie.ViewModels;

public abstract partial class SterilizationDashboardViewModel
{
    private bool _isRefreshingVisuals;
    private string _lastVisualDataSignature = string.Empty;
    private string _filteredCyclesCacheKey = string.Empty;
    private List<SterilizationCycle> _filteredCyclesCache = [];
    private string _attemptSummariesCacheKey = string.Empty;
    private List<AttemptSummary> _attemptSummariesCache = [];
    private string _filteredAttemptSummariesCacheKey = string.Empty;
    private List<AttemptSummary> _filteredAttemptSummariesCache = [];

    private void RefreshAllVisuals()
    {
        if (_isRefreshingVisuals) return;
        try
        {
            _isRefreshingVisuals = true;
            var currentDataSignature = BuildVisualDataSignature();
            var dataChanged = !string.Equals(currentDataSignature, AnalysisDataSignature, StringComparison.Ordinal);
            if (dataChanged)
            {
                RebuildHeaderCatalog();
                RebuildAbsoluteAttemptMapping();
                BuildMetricOptions();
                AnalysisDataSignature = currentDataSignature;
                InvalidateComputedCaches();
            }

            if (!_showRepresentations)
            {
                MainChartSeries.Clear();
                XLabels = Array.Empty<string>();
                RefreshAlertTiles();
                if (AlertTiles.Count == 0)
                {
                    MlDetectionStatus = "Waiting for view selection";
                    MlDetectionStatusColor = "#777777";
                }
                BvMetricBars.Clear();
                LpcMetricBars.Clear();
                TopDefectBars.Clear();
                AttemptStatusBars.Clear();
                DashboardStatCards.Clear();
                CycleRuns.Clear();
                CycleAttempts.Clear();
                RebuildCycleSelectOptions();
                var emptyProps = new[]
                {
                    nameof(HasCycleData),
                    nameof(SelectedFailedCycle),       nameof(SelectedGoodCycle),
                    nameof(SelectedFailedCycleLabel),  nameof(SelectedGoodCycleLabel),
                    nameof(CycleSelectionSummary),     nameof(SelectedCyclesSummary),
                    nameof(EmptyStateMessage),         nameof(HasRenderableSeries)
                };
                foreach (var p in emptyProps) OnPropertyChanged(p);
                return;
            }

            if (SelectedRepresentation is null)
            {
                ApplyChartState(new SeriesCollection(), Array.Empty<string>());
                RebuildCycleSelectOptions();
            }
            else
            {
                RefreshTrendSeries();
                RefreshAttemptMetricBars();
                RefreshDashboardCards();
                RefreshStatusDistribution();
                RefreshTopDefects();

                // -- NEW: rebuild cycle selectors before refreshing alerts --
                RebuildCycleSelectOptions();

                RefreshAlertTiles();
                RefreshSummaryCollections();
            }

            var props = new[]
            {
                nameof(SelectedMetric),            nameof(SelectedRepresentation),      nameof(SelectedMetricsSummary),
                nameof(TrendChartTitle),                nameof(PeakBarChartTitle),         nameof(AverageBarChartTitle),
                nameof(TopDefectChartTitle),       nameof(StatusChartTitle),
                nameof(ActiveWindowSummary),       nameof(FilteredCycleCount),
                nameof(ActiveSeriesSummary),       nameof(SummaryHeaderTitle),
                nameof(GoodCycleCount),            nameof(AverageGoodDurationMinutes),
                nameof(FailedAttemptCount),        nameof(PeakFailedDurationMinutes),
                nameof(ImportedWorkbookName),      nameof(VisibleCycleCount),
                nameof(DistinctRecipeCount),       nameof(DistinctStepCount),
                nameof(VisibleDateRangeSummary),   nameof(SummaryInsight),
                nameof(DominantRecipeSummary),     nameof(LiveAlertSummary),
                nameof(CurrentRangeLabel),
                nameof(AverageGoodDurationLabel),  nameof(PeakFailedDurationLabel),   nameof(PeakFailedAttemptLabel),
                nameof(SummaryFooterInsight1),     nameof(SummaryFooterInsight2),     nameof(SummaryFooterInsight3),
                nameof(ShowSensorSelector),        nameof(CanToggleChartMode),        nameof(HasRenderableSeries),
                 nameof(EmptyStateMessage),         nameof(SelectedRepresentationDisplayName), nameof(ChartModeLabel),
                 nameof(XAxisTitle),                nameof(YAxisTitle),                 nameof(YAxisMin),
                 nameof(YAxisMax),                  nameof(XAxisSeparatorStep),         nameof(SelectedChartDate),          nameof(SelectedChartDateText),
                 nameof(AvailableDateStart),        nameof(AvailableDateEnd),           nameof(IsDateCalendarOpen),
                // -- NEW cycle selector notifications --
                nameof(HasCycleData),
                nameof(SelectedFailedCycle),       nameof(SelectedGoodCycle),
                nameof(SelectedFailedCycleLabel),  nameof(SelectedGoodCycleLabel),
                nameof(CycleSelectionSummary),     nameof(SelectedCyclesSummary),
                nameof(OnlineStepNamesSummary)
            };
            foreach (var p in props) OnPropertyChanged(p);
            _lastVisualDataSignature = currentDataSignature;
        }
        finally { _isRefreshingVisuals = false; }
    }

    private void InvalidateComputedCaches()
    {
        _filteredCyclesCacheKey = string.Empty;
        _filteredCyclesCache = [];
        _attemptSummariesCacheKey = string.Empty;
        _attemptSummariesCache = [];
        _filteredAttemptSummariesCacheKey = string.Empty;
        _filteredAttemptSummariesCache = [];
    }

    private bool HasVisualDataChanged()
    {
        var currentSignature = BuildVisualDataSignature();
        return !string.Equals(currentSignature, _lastVisualDataSignature, StringComparison.Ordinal);
    }

    private string BuildVisualDataSignature()
    {
        if (VisibleCycles.Count == 0)
            return "EMPTY";

        var latest = VisibleCycles
            .OrderByDescending(cycle => cycle.RecordedAt)
            .ThenByDescending(cycle => cycle.Id)
            .First();
        return $"{VisibleCycles.Count}|{latest.Id}|{latest.RecordedAt.Ticks}";
    }

    // -- header catalogue --
    private void RebuildHeaderCatalog()
    {
        HeaderCatalog.Clear();
        var headers = VisibleCycles
            .SelectMany(c => c.Values)
            .Where(v => v.Header is not null)
            .Select(v => v.Header!)
            .GroupBy(h => h.NormalizedName)
            .Select(g => new CycleHeaderDefinition
            {
                Name = g.OrderBy(h => h.DisplayOrder).First().Name,
                NormalizedName = g.Key,
                DisplayOrder = g.Min(h => h.DisplayOrder),
                IsNumeric = g.Any(h => h.IsNumeric)
            })
            .OrderBy(h => h.DisplayOrder).ThenBy(h => h.Name)
            .ToList();
        foreach (var h in headers) HeaderCatalog.Add(h);

        RecipeHeaderKey = FindHeaderKey("RECIPENAME", "RECIPE") ?? RecipeHeaderKey;
        StepHeaderKey = FindHeaderKey("STEP") ?? StepHeaderKey;
        // -- Sticky resolution --
        // In online mode, VisibleCycles is a rolling sliding window (see
        // LiveChartWindow), not the full dataset. A categorical column like
        // Step Name can be entirely blank for every row currently inside that
        // window (e.g. the process sitting idle/in standby) even though the
        // column itself is real and had values moments ago -- the importer only
        // emits a Header for cells that actually have content, so a wholly-blank
        // window produces no Step Name entry in HeaderCatalog at all. Falling
        // back to null here used to silently disable PassesStepNameFilter
        // (which treats a null StepNameHeaderKey as "no filter active") even
        // while the user still had specific steps checked in the dropdown -- the
        // checkboxes looked like they had no effect. Keeping the previously
        // resolved key when no match is found this pass fixes that, and is safe
        // because every place that loads a genuinely NEW dataset (offline
        // import, DB wipe, online full re-seed) explicitly calls
        // ResetHeaderKeys() first, so a structurally different workbook can't
        // inherit a stale key from this fallback.
        StepNameHeaderKey = FindHeaderKey("STEPNAME") ?? StepNameHeaderKey;
        AlarmHeaderKey = FindHeaderKey("CRITICALALARM", "ALARM") ?? AlarmHeaderKey;
        DurationHeaderKey = FindHeaderKey("EXPTIME", "DURATION") ?? DurationHeaderKey;
    }

    private string? FindHeaderKey(params string[] candidates)
    {
        foreach (var c in candidates)
        {
            var h = HeaderCatalog.FirstOrDefault(
                item => item.NormalizedName.Contains(c, StringComparison.OrdinalIgnoreCase));
            if (h is not null) return h.NormalizedName;
        }
        return null;
    }

    // -- time-range filtering --
    private List<SterilizationCycle> GetDateTimeFilteredCycles()
    {
        var dateFiltered = GetRowsForSelectedOrLatestDate();

        if (dateFiltered.Count == 0)
        {
            return [];
        }

        var stepFiltered = dateFiltered.Where(PassesStepNameFilter).ToList();
        return ApplyTimeRange(stepFiltered).ToList();
    }

    private List<SterilizationCycle> GetFilteredCycles()
    {
        var pinnedKey = string.Join("|", GetPinnedCycleKeys().OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
        var stepNameKey = string.Join("|", OnlineStepNameOptions.Where(o => !o.IsAll && o.IsChecked).Select(o => o.Key).OrderBy(k => k));
        var cacheKey = $"{AnalysisDataSignature}|{SelectedChartDate?.Date.Ticks ?? 0}|{CurrentRangeLabel}|{IsOnline}|{pinnedKey}|{stepNameKey}";
        if (string.Equals(cacheKey, _filteredCyclesCacheKey, StringComparison.Ordinal))
        {
            return _filteredCyclesCache;
        }

        _filteredCyclesCache = FilterCyclesForCurrentSelection();
        _filteredCyclesCacheKey = cacheKey;
        return _filteredCyclesCache;
    }

    private List<SterilizationCycle> FilterCyclesForCurrentSelection()
    {
        var dateFiltered = GetRowsForSelectedOrLatestDate();

        if (dateFiltered.Count == 0)
        {
            return [];
        }

        if (!HasExplicitCycleFilter())
        {
            var stepFilteredRows = dateFiltered
                .Where(PassesStepNameFilter)
                .ToList();
            return ApplyTimeRange(stepFilteredRows).ToList();
        }

        var matchedAttempts = GetMatchedAttemptsForDate()
            .OrderBy(attempt => attempt.Start)
            .ToList();
        if (matchedAttempts.Count == 0)
        {
            return [];
        }

        var selectedRange = TimeRangeOptions.FirstOrDefault(option => option.IsSelected);
        if (selectedRange is null)
        {
            return matchedAttempts.SelectMany(attempt => attempt.Rows).OrderBy(row => row.RecordedAt).ToList();
        }

        if (IsOnline)
        {
            var rows = matchedAttempts
                .SelectMany(attempt => attempt.Rows)
                .Where(PassesStepNameFilter)
                .OrderBy(row => row.RecordedAt)
                .ToList();
            return ApplyTimeRange(rows).ToList();
        }

        var matchedRows = matchedAttempts
            .SelectMany(attempt => attempt.Rows)
            .Where(PassesStepNameFilter)
            .OrderBy(row => row.RecordedAt)
            .ToList();
        var timeRangeResult = ApplyTimeRange(matchedRows).ToList();

        // In bar chart mode a short time-range window (e.g. 5m) can clip all matched attempt
        // rows, causing the bar chart to render nothing. Fall back to all matched rows so the
        // bar chart always has data regardless of which time-range pill is selected.
        if (timeRangeResult.Count == 0 && IsBarChartRepresentation)
            return matchedRows;

        return timeRangeResult;
    }

    private List<SterilizationCycle> GetRowsForSelectedOrLatestDate()
    {
        if (VisibleCycles.Count == 0)
        {
            return [];
        }

        var ordered = VisibleCycles.OrderBy(cycle => cycle.RecordedAt).ToList();
        if (!SelectedChartDate.HasValue)
        {
            return ordered;
        }

        var selectedRows = ordered
            .Where(cycle => cycle.RecordedAt.Date == SelectedChartDate.Value.Date)
            .ToList();

        return selectedRows;
    }

    private IEnumerable<SterilizationCycle> ApplyTimeRange(IEnumerable<SterilizationCycle> source)
    {
        var ordered = source.OrderBy(c => c.RecordedAt).ToList();
        if (ordered.Count == 0) return ordered;

        var selected = TimeRangeOptions.FirstOrDefault(o => o.IsSelected);
        if (selected is null) return ordered;

        if (IsOnline)
        {
            // Anchor to the LATEST row timestamp (not DateTime.Now) so the window
            // faithfully reflects the data written to the file.  Using DateTime.Now
            // would create an empty gap at the right edge whenever the data clock
            // lags behind wall-clock time (e.g. historical replays, test files).
            // Using Max(RecordedAt) means the right edge of the visible window always
            // aligns with the most-recent data point, and older rows drop off the
            // left edge automatically as new ones arrive - exactly the ECG scroll
            // behaviour the user expects.
            var anchor = ordered.Max(c => c.RecordedAt);
            var first = ordered.First().RecordedAt;
            if (anchor - first < selected.Duration)
                return ordered;

            return ordered.Where(c => c.RecordedAt >= anchor - selected.Duration).ToList();
        }

        // If the user just switched from online to offline mode, _offlineSyncAnchor
        // holds the same "now" timestamp online was anchored to. Apply the identical
        // anchor-minus-duration window here so offline shows the same data online
        // was just displaying, rather than a window computed from the start of the day.
        if (_offlineSyncAnchor.HasValue && _offlineSyncAnchor.Value.Date == SelectedChartDate?.Date)
        {
            if (_offlineSyncWindowStart.HasValue && _offlineSyncWindowEnd.HasValue)
            {
                var exactRows = ordered
                    .Where(c => c.RecordedAt >= _offlineSyncWindowStart.Value && c.RecordedAt <= _offlineSyncWindowEnd.Value)
                    .ToList();
                if (exactRows.Count > 0)
                    return exactRows;
            }

            var syncAnchor = _offlineSyncAnchor.Value;
            var syncedRows = ordered.Where(c => c.RecordedAt > syncAnchor - selected.Duration && c.RecordedAt <= syncAnchor).ToList();
            if (syncedRows.Count >= 2)
                return syncedRows;
        }

        // Anchor to the first actual recorded timestamp on the selected date, NOT midnight (00:00:00).
        // This ensures time-range windows (5m, 30m, 1h, etc.) align with real data start times --
        // e.g. if data on 25-Aug-2025 starts at 00:00:00 that is used; if data on 19-Dec-2025
        // starts at 07:01:00 then 07:01:00 is the window start rather than the blank midnight.
        var windowStart = ordered.First().RecordedAt;
        var windowEnd = windowStart + selected.Duration;
        return ordered.Where(c => c.RecordedAt >= windowStart && c.RecordedAt < windowEnd).ToList();
    }

    private void CaptureOnlineTimelineSyncWindow()
    {
        _offlineSyncAnchor = null;
        _offlineSyncWindowStart = null;
        _offlineSyncWindowEnd = null;

        var rows = _onlineCycles
            .Where(row => !SelectedChartDate.HasValue || row.RecordedAt.Date == SelectedChartDate.Value.Date)
            .Where(PassesStepNameFilter)
            .OrderBy(row => row.RecordedAt)
            .ToList();
        if (rows.Count == 0)
            return;

        var latest = rows[^1].RecordedAt;
        var first = rows[0].RecordedAt;
        var selected = TimeRangeOptions.FirstOrDefault(option => option.IsSelected);

        _offlineSyncWindowStart = selected is null || latest - first < selected.Duration
            ? first
            : latest - selected.Duration;
        _offlineSyncWindowEnd = latest;
        _offlineSyncAnchor = latest;
    }

    // -- attempt summaries --
    private List<AttemptSummary> GetAttemptSummaries(bool applyTimeFilter = false, bool bypassDateFilter = false)
    {
        if (AllAttemptSummaries.Count == 0 || string.IsNullOrWhiteSpace(StepHeaderKey)) return [];

        var metricKey = SelectedMetric?.PropertyName ?? string.Empty;
        var dateKey = (SelectedChartDate?.Date.Ticks ?? 0) + (bypassDateFilter ? "_bypass" : "");
        var stepNameKey = string.Join("|", OnlineStepNameOptions.Where(o => !o.IsAll && o.IsChecked).Select(o => o.Key).OrderBy(k => k));

        if (!applyTimeFilter)
        {
            var cacheKey = $"{AnalysisDataSignature}|all|{dateKey}|{metricKey}|{stepNameKey}";
            if (string.Equals(cacheKey, _attemptSummariesCacheKey, StringComparison.Ordinal))
            {
                return _attemptSummariesCache;
            }

            var currentAttempts = SelectedChartDate.HasValue && !bypassDateFilter
                ? AllAttemptSummaries.Where(a => a.Start.Date == SelectedChartDate.Value.Date).ToList()
                : AllAttemptSummaries.ToList();

            var selectedSteps = OnlineStepNameOptions.Where(o => !o.IsAll && o.IsChecked).Select(o => o.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selectedSteps.Count > 0 && !string.IsNullOrWhiteSpace(StepNameHeaderKey))
            {
                var filteredAttempts = new List<AttemptSummary>();
                foreach (var a in currentAttempts)
                {
                    var slicedRows = a.Rows.Where(r => selectedSteps.Contains(r.GetText(StepNameHeaderKey)?.Trim() ?? string.Empty)).ToList();
                    if (slicedRows.Count > 0)
                    {
                        filteredAttempts.Add(ProjectAttemptMetric(a, slicedRows));
                    }
                }
                _attemptSummariesCache = filteredAttempts;
            }
            else
            {
                _attemptSummariesCache = ProjectAttemptMetrics(currentAttempts);
            }

            _attemptSummariesCacheKey = cacheKey;
            return _attemptSummariesCache;
        }

        var filteredCacheKey = $"{AnalysisDataSignature}|filtered|{dateKey}|{CurrentRangeLabel}|{metricKey}|{IsOnline}|{stepNameKey}";
        if (string.Equals(filteredCacheKey, _filteredAttemptSummariesCacheKey, StringComparison.Ordinal))
        {
            return _filteredAttemptSummariesCache;
        }

        var relevantAttempts = SelectedChartDate.HasValue && !bypassDateFilter
            ? AllAttemptSummaries.Where(a => a.Start.Date == SelectedChartDate.Value.Date).ToList()
            : AllAttemptSummaries.ToList();

        var sourceFiltered = GetDateTimeFilteredCycles();
        if (sourceFiltered.Count == 0) return [];
        var validRowIds = sourceFiltered.Select(r => r.Id).ToHashSet();

        var results = new List<AttemptSummary>();
        foreach (var a in relevantAttempts)
        {
            var slicedRows = a.Rows.Where(r => validRowIds.Contains(r.Id)).ToList();
            if (slicedRows.Count > 0)
            {
                results.Add(ProjectAttemptMetric(a, slicedRows));
            }
        }

        _filteredAttemptSummariesCache = results;
        _filteredAttemptSummariesCacheKey = filteredCacheKey;
        return _filteredAttemptSummariesCache;
    }

    private List<AttemptSummary> ProjectAttemptMetrics(IEnumerable<AttemptSummary> attempts)
        => attempts.Select(attempt => ProjectAttemptMetric(attempt, attempt.Rows)).ToList();

    private AttemptSummary ProjectAttemptMetric(AttemptSummary attempt, IReadOnlyList<SterilizationCycle> rows)
    {
        var peakAlarm = AlarmHeaderKey is null ? 0d : rows.Max(c => c.GetNumericValue(AlarmHeaderKey) ?? 0d);
        var peakMetric = SelectedMetric is null
            ? 0d
            : rows.Select(SelectedMetric.GetValue).Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty(0d).Max();
        var avgMetric = SelectedMetric is null
            ? 0d
            : Math.Round(rows.Select(SelectedMetric.GetValue).Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty(0d).Average(), 2);
        var durationMinutes = rows.Count == 0
            ? attempt.DurationMinutes
            : Math.Max(1, (int)Math.Round((rows[^1].RecordedAt - rows[0].RecordedAt).TotalMinutes, MidpointRounding.AwayFromZero));
        var leadText = $"{attempt.Status}  -  {durationMinutes} min  -  Peak {SelectedMetric?.DisplayName ?? "metric"} {peakMetric:0.##}";

        return attempt with
        {
            Rows = rows,
            PeakAlarm = peakAlarm,
            PeakSelectedMetric = peakMetric,
            AverageSelectedMetric = avgMetric,
            DurationMinutes = durationMinutes,
            LeadText = leadText
        };
    }

    private IEnumerable<AttemptSummary> BuildSheetAttempts(
        string sheetName, List<SterilizationCycle> ordered,
        IDictionary<string, int> namedSheetOrdinals)
    {
        var attempts = new List<AttemptSummary>();
        var segStart = 0;
        var i = 0;

        while (i < ordered.Count)
        {
            while (i < ordered.Count && !IsActiveStep(ordered[i])) i++;
            if (i >= ordered.Count) break;

            var aStart = i;
            var aEnd = i;
            while (aEnd + 1 < ordered.Count && IsActiveStep(ordered[aEnd + 1])) aEnd++;

            var prelude = ordered.GetRange(segStart, aStart - segStart);
            var activeRows = ordered.GetRange(aStart, aEnd - aStart + 1);
            var ordinal = NextOrdinal(sheetName, namedSheetOrdinals);
            attempts.Add(BuildAttemptSummary(sheetName, ordinal, activeRows, prelude, true));

            segStart = aEnd + 1;
            i = aEnd + 1;
        }

        if (attempts.Count == 0 && ordered.Count > 0)
        {
            var ordinal = NextOrdinal(sheetName, namedSheetOrdinals);
            attempts.Add(BuildAttemptSummary(sheetName, ordinal, ordered, ordered, false));
        }
        return attempts;
    }

    private static int NextOrdinal(string name, IDictionary<string, int> map)
    {
        map.TryGetValue(name, out var cur);
        map[name] = cur + 1;
        return cur + 1;
    }

    private AttemptSummary BuildAttemptSummary(
        string sheetName, int ordinal,
        List<SterilizationCycle> activeRows,
        List<SterilizationCycle> preludeRows,
        bool hasActiveStart)
    {
        var rows = activeRows.Count > 0 ? activeRows : preludeRows;
        var start = rows.First().RecordedAt;
        var end = rows.Last().RecordedAt;
        var durMin = Math.Max(1, (int)Math.Round((end - start).TotalMinutes, MidpointRounding.AwayFromZero));
        if (durMin == 1 && rows.Count > 1) durMin = rows.Count;

        var peakAlarm = AlarmHeaderKey is null ? 0d : rows.Max(c => c.GetNumericValue(AlarmHeaderKey) ?? 0d);
        var peakMetric = SelectedMetric is null ? 0d : rows.Max(c => SelectedMetric.GetValue(c) ?? 0d);
        var avgMetric = SelectedMetric is null ? 0d : Math.Round(rows.Average(c => SelectedMetric.GetValue(c) ?? 0d), 2);
        var recipe = ResolveRecipeName(rows);
        var maxStep = StepHeaderKey is null ? 0d : rows.Max(c => c.GetNumericValue(StepHeaderKey) ?? 0d);

        var preIdle = preludeRows
            .Select(c => c.GetNumericValue(StepHeaderKey!))
            .Where(v => v.HasValue)
            .Select(v => Convert.ToInt32(v!.Value).ToString(CultureInfo.InvariantCulture))
            .Distinct().ToList();

        var recipeLoadStep = preIdle.LastOrDefault() ?? "-";
        var status = ResolveStatus(hasActiveStart, peakAlarm, rows.Count, maxStep);
        var statusColor = status switch
        {
            "Complete" => "#3BCB78",
            "Short Run" => "#F4B740",
            "Standby Only" => "#6A86A8",
            _ => "#FF6A6A"
        };
        var leadText = $"{status}  -  {durMin} min  -  Peak {SelectedMetric?.DisplayName ?? "metric"} {peakMetric:0.##}";

        // Pre-compute the compressed phase sequence once at build time so it is never
        // recalculated row-by-row on every filter change (eliminates the filter-click lag).
        var cachedSequence = new List<string>();
        string? lastCachedPhase = null;
        foreach (var row in rows)
        {
            var phase = GetProcessPhaseLabel(row);
            if (string.IsNullOrWhiteSpace(phase) || string.Equals(phase, "Unknown", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(phase, lastCachedPhase, StringComparison.OrdinalIgnoreCase))
                continue;
            cachedSequence.Add(phase);
            lastCachedPhase = phase;
        }

        return new AttemptSummary(
            BuildAttemptName(sheetName, ordinal), sheetName,
            recipe, status, statusColor, rows.Count, durMin, start, end,
            peakAlarm, peakMetric, avgMetric, maxStep,
            string.Join("+", preIdle), recipeLoadStep,
            start.ToString("MMM dd", CultureInfo.InvariantCulture),
            leadText, rows, cachedSequence);
    }

    private static string BuildAttemptName(string sheetName, int ordinal)
    {
        if (sheetName.Contains("good", StringComparison.OrdinalIgnoreCase))
        {
            return $"GD{ordinal}";
        }

        if (sheetName.Contains("failed cycle 1", StringComparison.OrdinalIgnoreCase))
        {
            return $"F1A{ordinal}";
        }

        if (sheetName.Contains("failed cycle 2", StringComparison.OrdinalIgnoreCase))
        {
            // Failed Cycle 2 in the workbook stays at step 10 (Standby Only) - single attempt,
            // but use ordinal for uniqueness in case the sheet ever has multiple segments.
            return $"F2A{ordinal}";
        }

        var tokens = sheetName
            .Split([' ', '_', '-', '.', '/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Take(3).Select(t => char.ToUpperInvariant(t[0])).ToArray();
        return $"{(tokens.Length == 0 ? "AT" : new string(tokens))}{ordinal}";
    }

    private string ResolveRecipeName(IEnumerable<SterilizationCycle> rows)
    {
        if (RecipeHeaderKey is null) return "Unclassified";
        return rows.Select(r => r.GetText(RecipeHeaderKey))
                   .Where(v => !string.IsNullOrWhiteSpace(v))
                   .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                   .OrderByDescending(g => g.Count())
                   .Select(g => g.First())
                   .FirstOrDefault() ?? "Unclassified";
    }

    private static string ResolveStatus(bool hasActiveStart, double peakAlarm, int sampleCount, double maxStep)
    {
        if (!hasActiveStart) return "Standby Only";
        if (peakAlarm > 0) return "Alarmed";
        // Active phase begins at step 26 (FILLING CHAMBER) per the workbook.
        // A cycle that never cleared step 25 or had too few samples is a short run.
        if (sampleCount < 3 || maxStep < 26) return "Short Run";
        return "Complete";
    }

    private bool IsActiveStep(SterilizationCycle c)
    {
        var step = StepHeaderKey is null ? null : c.GetNumericValue(StepHeaderKey);
        return step.HasValue && step.Value >= 26;
    }

    private List<AttemptSummary> GetBaselineAttempts(bool applyTimeFilter = false, bool bypassDateFilter = false)
    {
        // Baseline = attempts that ran cleanly to completion (no alarms, full step
        // progression, enough samples). Derived purely from the data, no reliance
        // on sheet/tab naming.
        var attempts = GetAttemptSummaries(applyTimeFilter, bypassDateFilter);
        return attempts.Where(a => a.Status == "Complete").ToList();
    }

    private List<AttemptSummary> GetReviewAttempts(bool applyTimeFilter = false, bool bypassDateFilter = false)
    {
        // Review = anything that did not run cleanly (alarmed, short run, standby
        // only, etc.). Derived purely from the data, no reliance on sheet/tab naming.
        var attempts = GetAttemptSummaries(applyTimeFilter, bypassDateFilter);
        return attempts.Where(a => a.Status != "Complete").ToList();
    }

    private List<AttemptSummary> GetBaselineAttemptsForComparison()
        => GetBaselineAttempts(applyTimeFilter: false, bypassDateFilter: true);

    private List<AttemptSummary> GetReviewAttemptsForComparison()
        => GetReviewAttempts(applyTimeFilter: false, bypassDateFilter: true);

    private List<AttemptSummary> GetAttemptsForComparison()
        => GetAttemptSummaries(applyTimeFilter: true);

    private List<AttemptSummary> GetMatchedAttemptsForDate()
        => ApplyPinnedCycleFilter(GetAttemptSummaries(applyTimeFilter: true));

    private List<SterilizationCycle> GetComparisonRenderCycles()
    {
        return GetFilteredCycles().OrderBy(cycle => cycle.RecordedAt).ToList();
    }

    private List<SterilizationCycle> GetDateScopedCyclesOrVisible()
    {
        if (SelectedChartDate.HasValue)
        {
            var dateRows = VisibleCycles
                .Where(cycle => cycle.RecordedAt.Date == SelectedChartDate.Value.Date)
                .OrderBy(cycle => cycle.RecordedAt)
                .ToList();
            if (dateRows.Count > 0)
            {
                return dateRows;
            }
        }

        return VisibleCycles.OrderBy(cycle => cycle.RecordedAt).ToList();
    }

    private List<TimeBucketSlice> BuildTimeBucketSlices(IReadOnlyList<SterilizationCycle> cycles)
    {
        if (cycles.Count == 0)
        {
            return [];
        }

        var ordered = cycles.OrderBy(cycle => cycle.RecordedAt).ToList();

        // X-axis policy: when a date is selected and we're not in Online live mode,
        // anchor the x-axis at 00:00:00 of that selected date so the timeline always
        // starts at midnight regardless of when the first cycle on that day began.
        // In Online mode (rolling live window) keep the original first-row anchor.
        var useMidnightAnchor = !IsOnline && SelectedChartDate.HasValue;
        var anchor = useMidnightAnchor
            ? SelectedChartDate!.Value.Date
            : ordered.First().RecordedAt;

        // Adaptive bucket size so a full 24h day doesn't blow up to 1440 entries.
        // Cap at ~240 buckets per render; 1-minute granularity for short windows.
        var lastOffsetMinutes = Math.Max(0, (int)Math.Ceiling((ordered.Last().RecordedAt - anchor).TotalMinutes));
        var step = Math.Max(1, (int)Math.Ceiling((lastOffsetMinutes + 1) / 240.0));

        var byBucket = ordered
            .GroupBy(c => Math.Max(0, (int)Math.Floor((c.RecordedAt - anchor).TotalMinutes / step)))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SterilizationCycle>)g.ToList());

        var maxBucket = lastOffsetMinutes / step;
        var slices = new List<TimeBucketSlice>(maxBucket + 1);
        for (var i = 0; i <= maxBucket; i++)
        {
            var ts = anchor.AddMinutes(i * step);
            var rows = byBucket.TryGetValue(i, out var r) ? r : (IReadOnlyList<SterilizationCycle>)Array.Empty<SterilizationCycle>();
            slices.Add(new TimeBucketSlice(i, ts, rows.ToList()));
        }
        return slices;
    }

    private List<TimeBucketSlice> BuildRelativeTimeBucketSlices(IReadOnlyList<AttemptSummary> attempts)
    {
        if (attempts.Count == 0) return [];

        var maxDurationMinutes = attempts.Max(a => a.DurationMinutes);
        var step = Math.Max(1.0, (double)maxDurationMinutes / 120.0);

        var bucketRowsMap = new Dictionary<int, List<SterilizationCycle>>();
        for (int i = 0; i < 120; i++)
        {
            bucketRowsMap[i] = new List<SterilizationCycle>();
        }

        foreach (var a in attempts)
        {
            if (a.Rows.Count == 0) continue;
            var start = a.Start;
            foreach (var row in a.Rows)
            {
                var offsetMinutes = (row.RecordedAt - start).TotalMinutes;
                var bucketIdx = (int)Math.Floor(offsetMinutes / step);
                if (bucketIdx >= 0 && bucketIdx < 120)
                {
                    bucketRowsMap[bucketIdx].Add(row);
                }
            }
        }

        var buckets = new List<TimeBucketSlice>();
        var anchor = DateTime.Today;
        for (int i = 0; i < 120; i++)
        {
            buckets.Add(new TimeBucketSlice(i, anchor.AddMinutes(i * step), bucketRowsMap[i]));
        }

        return buckets;
    }

    private static List<double> GetValidMetricValues(IEnumerable<SterilizationCycle> rows, IReadOnlyCollection<MetricOption> metrics)
    {
        var values = new List<double>();
        foreach (var row in rows)
        {
            foreach (var metric in metrics)
            {
                var value = metric.GetValue(row);
                if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
                {
                    continue;
                }

                if (IsImpossibleSensorValue(metric, value.Value))
                {
                    continue;
                }

                values.Add(value.Value);
            }
        }

        return values;
    }

    private static string[] BuildBucketLabels(IReadOnlyList<TimeBucketSlice> buckets)
        => buckets.Select(bucket => bucket.Timestamp.ToString("HH:mm")).ToArray();

    private static int CountProcessPhaseChanges(IReadOnlyList<SterilizationCycle> rows, Func<SterilizationCycle, string> phaseSelector)
    {
        string? previous = null;
        var changes = 0;

        foreach (var row in rows.OrderBy(item => item.RecordedAt))
        {
            var phase = phaseSelector(row);
            if (string.IsNullOrWhiteSpace(phase) || string.Equals(phase, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (previous is not null && !string.Equals(previous, phase, StringComparison.OrdinalIgnoreCase))
            {
                changes++;
            }

            previous = phase;
        }

        return changes;
    }

    private TimeBucketSlice? GetCurrentComparisonBucket(int pointIndex)
    {
        var buckets = BuildTimeBucketSlices(GetComparisonRenderCycles());
        return pointIndex >= 0 && pointIndex < buckets.Count ? buckets[pointIndex] : null;
    }

    private List<SterilizationCycle> GetTimelineRowsForLabel(string label)
    {
        return GetFilteredCycles()
            .Where(row => string.Equals(row.RecordedAt.ToString("HH:mm:ss"), label, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(row.RecordedAt.ToString("HH:mm"), label, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.RecordedAt)
            .ToList();
    }

    private List<SterilizationCycle> GetSampledTimelineCycles()
    {
        var metrics = GetActiveMetrics();
        var timelineCycles = GetFilteredCycles().OrderBy(c => c.RecordedAt).ToList();
        var allDayAttempts = GetAttemptSummaries(applyTimeFilter: false);
        var matchedAttempts = ApplyPinnedCycleFilter(allDayAttempts);
        if (timelineCycles.Count == 0) return [];

        var hasExplicitCycleFilter = HasExplicitCycleFilter();
        var matchedRowIds = hasExplicitCycleFilter
            ? matchedAttempts.SelectMany(a => a.Rows).Select(r => r.Id).ToHashSet()
            : null;

        var renderCycles = matchedRowIds is null
            ? timelineCycles
            : timelineCycles.Where(cycle => matchedRowIds.Contains(cycle.Id)).ToList();

        if (renderCycles.Count == 0) return [];

        return DownsampleTimelineCycles(renderCycles, GetTimelinePointBudget(metrics.Count));
    }

    private AttemptSummary? ResolveContextAttemptForBucket(TimeBucketSlice bucket, IReadOnlyList<AttemptSummary> attempts)
    {
        var attemptByRowId = attempts
            .SelectMany(attempt => attempt.Rows.Select(row => new { row.Id, Attempt = attempt }))
            .GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.First().Attempt);

        var overlappingAttempts = bucket.Rows
            .Select(row => attemptByRowId.TryGetValue(row.Id, out var attempt) ? attempt : null)
            .Where(attempt => attempt is not null)
            .Distinct()
            .Cast<AttemptSummary>()
            .ToList();

        if (overlappingAttempts.Count > 0)
        {
            return overlappingAttempts.OrderBy(attempt => attempt.Start).First();
        }

        var sameDateAttempts = attempts
            .Where(attempt => attempt.Start.Date == bucket.Timestamp.Date)
            .OrderBy(attempt => Math.Abs((attempt.Start - bucket.Timestamp).TotalMinutes))
            .ToList();

        return sameDateAttempts.FirstOrDefault();
    }

    private void AppendBucketWorkbookDetails(TimeBucketSlice? bucket, string seriesTitle, string selectedLabel, double pointValue)
    {
        if (bucket is null || bucket.Rows.Count == 0)
        {
            return;
        }

        var attempts = GetAttemptsForComparison();
        var contextAttempt = ResolveContextAttemptForBucket(bucket, attempts);
        var recipe = ResolveRecipeName(bucket.Rows);
        var phases = bucket.Rows
            .Select(GetProcessPhaseLabel)
            .Where(phase => !string.IsNullOrWhiteSpace(phase) && !string.Equals(phase, "Unknown", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var peakAlarm = AlarmHeaderKey is null
            ? 0d
            : bucket.Rows.Select(row => row.GetNumericValue(AlarmHeaderKey) ?? 0d).DefaultIfEmpty(0d).Max();
        var exposureMetric = MetricOptions.FirstOrDefault(metric =>
            string.Equals(metric.PropertyName, "STR34_EXPTIME", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(metric.PropertyName, "STR34_EXP_TIME", StringComparison.OrdinalIgnoreCase));
        var exposureValues = exposureMetric is null
            ? []
            : bucket.Rows.Select(exposureMetric.GetValue).Where(value => value.HasValue).Select(value => value!.Value).ToList();

        DrillDownSubtitle = $"{SelectedRepresentation?.DisplayName ?? "Representation"} at {selectedLabel} on {(SelectedChartDate?.ToString("dd-MM-yyyy") ?? "selected date")}";
        DrillDownPointSummary = $"Workbook rows from {bucket.Rows.First().RecordedAt:HH:mm:ss} to {bucket.Rows.Last().RecordedAt:HH:mm:ss}. Y = {pointValue:0.##} for {seriesTitle}.";
        DrillDownStatRows.Add(new DashboardStatCard("Bucket Time", $"{bucket.Rows.First().RecordedAt:HH:mm:ss} -> {bucket.Rows.Last().RecordedAt:HH:mm:ss}", "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Rows", bucket.Rows.Count.ToString(CultureInfo.InvariantCulture), "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Recipe", string.IsNullOrWhiteSpace(recipe) ? "-" : recipe, "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Process Stages", phases.Count == 0 ? "-" : string.Join(" -> ", phases), "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Critical Alarm", peakAlarm.ToString("0.##", CultureInfo.InvariantCulture), "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Exposure Time", exposureValues.Count == 0 ? "-" : $"{exposureValues.Min():0.##} -> {exposureValues.Max():0.##}", "#CCCCCC", "", ""));
        if (contextAttempt is not null)
        {
            DrillDownStatRows.Add(new DashboardStatCard("Cycle", contextAttempt.Name, "#CCCCCC", "", ""));
            DrillDownStatRows.Add(new DashboardStatCard("Cycle Status", contextAttempt.Status, contextAttempt.StatusColor, "", ""));
            DrillDownStatRows.Add(new DashboardStatCard("Cycle Duration", $"{contextAttempt.DurationMinutes} min", "#CCCCCC", "", ""));
        }
    }

    private void AppendWorkbookRowDetails(IReadOnlyList<SterilizationCycle> rows, string seriesTitle, string selectedLabel, double pointValue)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var recipe = ResolveRecipeName(rows);
        var phases = rows
            .Select(GetProcessPhaseLabel)
            .Where(phase => !string.IsNullOrWhiteSpace(phase) && !string.Equals(phase, "Unknown", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var peakAlarm = AlarmHeaderKey is null
            ? 0d
            : rows.Select(row => row.GetNumericValue(AlarmHeaderKey) ?? 0d).DefaultIfEmpty(0d).Max();
        var matchingAttempts = GetAttemptSummaries(applyTimeFilter: false)
            .Where(attempt => attempt.Rows.Any(row => rows.Any(selected => selected.Id == row.Id)))
            .ToList();
        var exposureMetric = MetricOptions.FirstOrDefault(metric =>
            string.Equals(metric.PropertyName, "STR34_EXPTIME", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(metric.PropertyName, "STR34_EXP_TIME", StringComparison.OrdinalIgnoreCase));
        var exposureValues = exposureMetric is null
            ? []
            : rows.Select(exposureMetric.GetValue).Where(value => value.HasValue).Select(value => value!.Value).ToList();

        DrillDownSubtitle = $"{SelectedRepresentation?.DisplayName ?? "Representation"} at {selectedLabel} on {(SelectedChartDate?.ToString("dd-MM-yyyy") ?? "selected date")}";
        DrillDownPointSummary = $"Workbook rows at {selectedLabel}. Y = {pointValue:0.##} for {seriesTitle}.";
        DrillDownStatRows.Add(new DashboardStatCard("Rows", rows.Count.ToString(CultureInfo.InvariantCulture), "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Recipe", string.IsNullOrWhiteSpace(recipe) ? "-" : recipe, "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Process Stages", phases.Count == 0 ? "-" : string.Join(" -> ", phases), "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Critical Alarm", peakAlarm.ToString("0.##", CultureInfo.InvariantCulture), "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Exposure Time", exposureValues.Count == 0 ? "-" : $"{exposureValues.Min():0.##} -> {exposureValues.Max():0.##}", "#CCCCCC", "", ""));
        if (matchingAttempts.Count > 0)
        {
            DrillDownStatRows.Add(new DashboardStatCard("Cycles", string.Join(", ", matchingAttempts.Select(attempt => attempt.Name).Distinct()), "#CCCCCC", "", ""));
        }
    }

    private List<SterilizationCycle> GetCyclesForAnalysis()
    {
        var filtered = GetFilteredCycles();
        if (SelectedChartDate.HasValue || (filtered.Count > 0 && filtered.Count >= 20))
            return filtered;
        return VisibleCycles.OrderBy(c => c.RecordedAt).ToList();
    }

    private List<AttemptSummary> GetAttemptSummariesForAnalysis()
    {
        return GetAttemptSummaries(applyTimeFilter: true);
    }

    private HashSet<string> GetPinnedCycleKeys()
    {
        var keys = AllCycleOptions
            .Where(option => !option.IsAll && option.IsSelected)
            .Select(option => option.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return keys;
    }

    private List<AttemptSummary> ApplyPinnedCycleFilter(IEnumerable<AttemptSummary> attempts)
    {
        var pinnedKeys = GetPinnedCycleKeys();
        if (pinnedKeys.Count == 0)
        {
            return attempts.ToList();
        }

        return attempts
            .Where(attempt => pinnedKeys.Contains(attempt.Name))
            .ToList();
    }

    private List<AttemptSummary> GetBaselineAttemptsForAnalysis()
    {
        // Baseline = attempts that ran cleanly to completion. Derived purely from
        // the data's own Status classification, no reliance on sheet/tab naming.
        var attempts = IsComparativeRepresentation()
            ? GetAttemptSummaries(applyTimeFilter: false, bypassDateFilter: true)
            : GetAttemptSummaries(applyTimeFilter: true, bypassDateFilter: false);
        var result = attempts.Where(a => a.Status == "Complete").ToList();
        return ApplyPinnedCycleFilter(result);
    }

    private List<AttemptSummary> GetReviewAttemptsForAnalysis()
    {
        // Review = attempts that did not run cleanly (alarmed, short run, standby
        // only, etc.). Derived purely from the data's own Status classification,
        // no reliance on sheet/tab naming.
        var attempts = IsComparativeRepresentation()
            ? GetAttemptSummaries(applyTimeFilter: false, bypassDateFilter: true)
            : GetAttemptSummaries(applyTimeFilter: true, bypassDateFilter: false);
        var result = attempts.Where(a => a.Status != "Complete").ToList();
        return ApplyPinnedCycleFilter(result);
    }

    private bool IsComparativeRepresentation()
        => string.Equals(SelectedRepresentation?.Key, "good-failed-envelope", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(SelectedRepresentation?.Key, "temperature-profile", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(SelectedRepresentation?.Key, "pressure-profile", StringComparison.OrdinalIgnoreCase);

    private bool IsEnvelopeAggregateMode()
        => string.Equals(SelectedRepresentation?.Key, "good-failed-envelope", StringComparison.OrdinalIgnoreCase);

    private bool HasExplicitCycleFilter()
        => GetPinnedCycleKeys().Count > 0;

    private bool HasMetricSelection()
        => ComparisonMetricOptions.Any(option => option.IsSelected) ||
           (IsOnline && MetricOptions.Count > 0);

    private bool RequiresMetricSelection()
        => SelectedRepresentation?.UsesSensorSelection == true;

    private bool CanRenderCurrentSelection()
    {
        if (SelectedRepresentation is null) return false;
        return !RequiresMetricSelection() || HasMetricSelection();
    }

    private static bool IsSensorHeader(CycleHeaderDefinition header)
        => GetSensorFamily(header.NormalizedName) is not SensorFamily.Other;

    // -- NEW: Rebuild cycle selector combo boxes --
    private void RebuildCycleSelectOptions()
    {
        var currentSelectedDateAttempts = GetAttemptSummaries(applyTimeFilter: true);
        var failedAttempts = currentSelectedDateAttempts.Where(a => a.Status != "Complete").OrderBy(a => a.Start).ToList();
        var goodAttempts = currentSelectedDateAttempts.Where(a => a.Status == "Complete").OrderBy(a => a.Start).ToList();

        var prevFailedKey = SelectedFailedCycle?.Key;
        var prevGoodKey = SelectedGoodCycle?.Key;

        FailedCycleOptions.Clear();
        FailedCycleOptions.Add(new CycleAttemptSelectOption("__ALL__", "All failed cycles", isAll: true));
        foreach (var a in failedAttempts)
            FailedCycleOptions.Add(new CycleAttemptSelectOption(a.Name, a.Name));

        GoodCycleOptions.Clear();
        GoodCycleOptions.Add(new CycleAttemptSelectOption("__ALL__", "All good cycles", isAll: true));
        foreach (var attempt in goodAttempts)
        {
            GoodCycleOptions.Add(new CycleAttemptSelectOption(attempt.Name, attempt.Name));
        }

        // -- Rebuild unified AllCycleOptions (single dropdown, date-filtered, checkbox multi-select) --
        AllCycleOptions.Clear();
        AllCycleOptions.Add(new CycleAttemptSelectOption("__ALL__", "All Cycles", isAll: true, owner: this));
        foreach (var a in failedAttempts)
        {
            AllCycleOptions.Add(new CycleAttemptSelectOption(a.Name, a.Name, isAll: false, owner: this));
        }
        foreach (var attempt in goodAttempts)
        {
            AllCycleOptions.Add(new CycleAttemptSelectOption(attempt.Name, attempt.Name, isAll: false, owner: this));
        }

        // Ensure "All Cycles" is selected by default when rebuilding
        var validKeys = AllCycleOptions.Select(option => option.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selectedCycleKeys.RemoveWhere(key => !validKeys.Contains(key));
        if (_selectedCycleKeys.Count == 0)
            _selectedCycleKeys.Add("__ALL__");

        // Restore previous selection or default to "All"
        _selectedFailedCycle = FailedCycleOptions.FirstOrDefault(o => o.Key == prevFailedKey)
                               ?? FailedCycleOptions.FirstOrDefault();
        _selectedGoodCycle = GoodCycleOptions.FirstOrDefault(o => o.Key == prevGoodKey)
                               ?? GoodCycleOptions.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedFailedCycle));
        OnPropertyChanged(nameof(SelectedGoodCycle));
        OnPropertyChanged(nameof(SelectedFailedCycleLabel));
        OnPropertyChanged(nameof(SelectedGoodCycleLabel));
        OnPropertyChanged(nameof(CycleSelectionSummary));
        OnPropertyChanged(nameof(SelectedCyclesSummary));
        OnPropertyChanged(nameof(HasCycleData));
    }

    // -- trend series --
    private void RefreshTrendSeries()
    {
        if (!CanRenderCurrentSelection())
        {
            ApplyChartState(new SeriesCollection(), Array.Empty<string>());
            return;
        }

        switch (SelectedRepresentation?.Key)
        {
            case "good-failed-envelope":
                BuildGoodVsFailedEnvelopeSeries();
                break;
            case "cycles-info":
                BuildCyclesInfoSeries();
                break;
            case "cycle-duration":
                BuildCycleDurationAnalyticsSeries();
                break;
            case "temperature-profile":
                BuildSensorProfileSeries(SensorFamily.Temperature, "Good cycle temperature", "Failed cycle temperature");
                break;
            case "pressure-profile":
                BuildSensorProfileSeries(SensorFamily.Pressure, "Good cycle pressure", "Failed cycle pressure");
                break;
            case "f0-exposure":
                BuildF0ExposureSeries();
                break;
            case "level-conductivity":
                BuildLevelConductivitySeries();
                break;
            case "recipe-step-map":
                BuildRecipeStepMapSeries();
                break;
            default:
                BuildTimelineSeries();
                break;
        }
    }

    private void BuildTimelineSeries()
    {
        var metrics = GetActiveMetrics();
        var allDayAttempts = GetAttemptSummaries(applyTimeFilter: false);
        var timelineCycles = GetFilteredCycles().OrderBy(c => c.RecordedAt).ToList();
        var matchedAttempts = ApplyPinnedCycleFilter(allDayAttempts);

        if (timelineCycles.Count == 0)
        {
            ApplyChartState(new SeriesCollection(), Array.Empty<string>());
            return;
        }

        var hasExplicitCycleFilter = HasExplicitCycleFilter();

        var matchedRowIds = hasExplicitCycleFilter
            ? matchedAttempts.SelectMany(a => a.Rows).Select(r => r.Id).ToHashSet()
            : null;

        var renderCycles = matchedRowIds is null
            ? timelineCycles
            : timelineCycles.Where(cycle => matchedRowIds.Contains(cycle.Id)).ToList();

        if (renderCycles.Count == 0 && matchedRowIds is not null)
        {
            renderCycles = matchedAttempts
                .SelectMany(a => a.Rows)
                .OrderBy(c => c.RecordedAt)
                .ToList();
        }

        if (renderCycles.Count == 0)
        {
            ApplyChartState(new SeriesCollection(), Array.Empty<string>());
            return;
        }

        var palette = GetPalette();

        // -- BAR CHART MODE --
        if (IsBarChartRepresentation)
        {
            var barLabels = new List<string>();
            var barValues = new ChartValues<MetricPoint>();

            var idx = 0;
            foreach (var metric in metrics)
            {
                var vals = renderCycles
                    .Select(metric.GetValue)
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .Where(v => !double.IsNaN(v) && !double.IsInfinity(v) && !IsImpossibleSensorValue(metric, v))
                    .ToList();
                if (vals.Count == 0) continue;
                var label = GetSensorCode(metric);
                var avg = Math.Round(vals.Average(), 2);
                barLabels.Add(label);
                barValues.Add(new MetricPoint(idx, DateTime.MinValue, avg, metric.DisplayName, label, "Bar", ImportedWorkbookName));
                idx++;
            }

            if (barValues.Count == 0)
            {
                ApplyChartState(new SeriesCollection(), Array.Empty<string>());
                return;
            }

            // Single ColumnSeries with all sensor averages so columns render at proper
            // width. (Previously each sensor got its own ColumnSeries with a single point,
            // which caused LiveCharts to render them as overlapping zero-width slots and
            // the bar chart appeared blank for "Live Sensor Timeline".)
            var barFill = palette[0];
            var barCollection = new SeriesCollection
            {
                new ColumnSeries
                {
                    Title = "Average sensor value",
                    Values = barValues,
                    Configuration = Mappers.Xy<MetricPoint>().X(p => p.Index).Y(p => p.Value),
                    LabelPoint = TooltipLabelPoint,
                    Fill = barFill,
                    Stroke = barFill,
                    StrokeThickness = 0.8,
                    MaxColumnWidth = 42,
                    ColumnPadding = 6
                }
            };
            ApplyChartState(barCollection, barLabels);
            return;
        }


        // -- LINE / LIVE CHART MODE --

        var sampledCycles = DownsampleTimelineCycles(renderCycles, GetTimelinePointBudget(metrics.Count));
        var cycleAttemptMap = allDayAttempts
       .SelectMany(a => a.Rows.Select(r => new { r.Id, a.Name, a.Status, a.Recipe }))
       .GroupBy(x => x.Id)
       .ToDictionary(g => g.Key, g =>
       {
           var first = g.First();
           return (first.Name, first.Status, first.Recipe);
       });
        var newSeries = new SeriesCollection();
        var (axisStart, axisEnd) = ResolveTimelineWindow(renderCycles);
        _timelineAxisOrigin = axisStart;
        var axisStartValue = 0d;
        var axisEndValue = DateTimeToAxisValue(axisEnd);
        if (axisEndValue <= axisStartValue)
            axisEndValue = axisStartValue + Math.Max(1d, ResolveTimelineCadence().TotalSeconds);
        var observedCadence = ResolveTimelineCadence();
        var splitAtGaps = IsStepNameFilterActive();

        for (var metricIdx = 0; metricIdx < metrics.Count; metricIdx++)
        {
            var metric = metrics[metricIdx];
            var values = new ChartValues<MetricPoint>();

            for (var index = 0; index < sampledCycles.Count; index++)
            {
                var cycle = sampledCycles[index];
                var val = metric.GetValue(cycle);
                if (!val.HasValue) continue;

                var attempt = cycleAttemptMap.TryGetValue(cycle.Id, out var mapped)
                    ? mapped
                    : (Name: cycle.SheetName, Status: "Live", Recipe: ResolveRecipeName([cycle]));

                var point = new MetricPoint(DateTimeToAxisValue(cycle.RecordedAt), cycle.RecordedAt, val.Value, metric.DisplayName, attempt.Name, attempt.Status, attempt.Recipe);
                values.Add(point);
            }

            if (splitAtGaps)
            {
                AddIndexedMetricSeries(
                    newSeries,
                    metric.DisplayName,
                    InsertTimelineBreaks(values, observedCadence),
                    palette[metricIdx % palette.Length],
                    showPeakMarkers: false);
            }
            else
            {
                AddIndexedMetricSeries(
                    newSeries,
                    metric.DisplayName,
                    values,
                    palette[metricIdx % palette.Length],
                    showPeakMarkers: false);
            }
        }

        AddAnomalyBoundarySeries(newSeries, axisStart, axisEnd);

        ApplyChartState(
            newSeries,
            Array.Empty<string>(),
            value => AxisValueToDateTime(value).ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            numericXAxisMin: axisStartValue,
            numericXAxisMax: axisEndValue);
    }

    private (DateTime Start, DateTime End) ResolveTimelineWindow(IReadOnlyList<SterilizationCycle>? sourceRows = null)
    {
        var rows = (sourceRows is { Count: > 0 } ? sourceRows : GetRowsForSelectedOrLatestDate())
            .OrderBy(row => row.RecordedAt)
            .ToList();
        if (rows.Count == 0)
            return (DateTime.MinValue, DateTime.MinValue.AddSeconds(1));

        var selected = TimeRangeOptions.FirstOrDefault(option => option.IsSelected);
        if (selected is null)
            return (rows[0].RecordedAt, rows[^1].RecordedAt);

        if (!IsOnline)
        {
            if (_offlineSyncWindowStart.HasValue &&
                _offlineSyncWindowEnd.HasValue &&
                _offlineSyncWindowEnd.Value.Date == SelectedChartDate?.Date)
            {
                return (_offlineSyncWindowStart.Value, _offlineSyncWindowEnd.Value);
            }

            if (_offlineSyncAnchor.HasValue &&
                _offlineSyncAnchor.Value.Date == SelectedChartDate?.Date)
            {
                return (_offlineSyncAnchor.Value - selected.Duration, _offlineSyncAnchor.Value);
            }

            var start = rows[0].RecordedAt;
            return (start, start + selected.Duration);
        }

        var first = rows[0].RecordedAt;
        var latest = rows[^1].RecordedAt;
        if (latest - first < selected.Duration)
            return (first, latest);

        return (latest - selected.Duration, latest);
    }

    private TimeSpan ResolveTimelineCadence()
    {
        var timestamps = GetRowsForSelectedOrLatestDate()
            .OrderBy(row => row.RecordedAt)
            .Select(row => row.RecordedAt)
            .ToList();
        var gaps = timestamps
            .Zip(timestamps.Skip(1), (left, right) => right - left)
            .Where(gap => gap > TimeSpan.Zero)
            .OrderBy(gap => gap)
            .ToList();

        return gaps.Count == 0 ? TimeSpan.Zero : gaps[gaps.Count / 2];
    }

    private static List<ChartValues<MetricPoint>> SplitTimelineValuesAtGaps(
        ChartValues<MetricPoint> values,
        TimeSpan observedCadence)
    {
        var segments = new List<ChartValues<MetricPoint>>();
        if (values.Count == 0)
            return segments;

        var gapThreshold = observedCadence > TimeSpan.Zero
            ? TimeSpan.FromTicks(observedCadence.Ticks * 3)
            : TimeSpan.MaxValue;
        var current = new ChartValues<MetricPoint>();

        foreach (var point in values.OrderBy(point => point.Timestamp))
        {
            if (current.Count > 0 &&
                point.Timestamp - current[^1].Timestamp > gapThreshold)
            {
                segments.Add(current);
                current = new ChartValues<MetricPoint>();
            }

            current.Add(point);
        }

        if (current.Count > 0)
            segments.Add(current);

        return segments;
    }

    private static ChartValues<MetricPoint> InsertTimelineBreaks(
        ChartValues<MetricPoint> values,
        TimeSpan observedCadence)
    {
        var result = new ChartValues<MetricPoint>();
        if (values.Count == 0)
            return result;

        var gapThreshold = observedCadence > TimeSpan.Zero
            ? TimeSpan.FromTicks(observedCadence.Ticks * 3)
            : TimeSpan.MaxValue;

        MetricPoint? previous = null;
        foreach (var point in values.OrderBy(point => point.Timestamp))
        {
            if (previous is not null && point.Timestamp - previous.Timestamp > gapThreshold)
            {
                var breakIndex = previous.Index + Math.Max(0.001d, (point.Index - previous.Index) / 2d);
                result.Add(previous with { Index = breakIndex, Value = double.NaN });
            }

            result.Add(point);
            previous = point;
        }

        return result;
    }

    private bool IsStepNameFilterActive()
        => OnlineStepNameOptions.Any(option => !option.IsAll && option.IsChecked);

    private double DateTimeToAxisValue(DateTime value)
        => Math.Max(0d, (value - _timelineAxisOrigin).TotalSeconds);

    private DateTime AxisValueToDateTime(double value)
        => _timelineAxisOrigin == DateTime.MinValue
            ? DateTime.MinValue.AddSeconds(Math.Max(0d, value))
            : _timelineAxisOrigin.AddSeconds(Math.Max(0d, value));

    private void AddAnomalyBoundarySeries(SeriesCollection target, DateTime axisStart, DateTime axisEnd)
    {
        var finiteValues = target
            .Where(series => series.Values is not null)
            .SelectMany(series => series.Values!.Cast<object>())
            .OfType<MetricPoint>()
            .Where(point => !double.IsNaN(point.Value) && !double.IsInfinity(point.Value))
            .Select(point => point.Value)
            .ToList();
        if (finiteValues.Count == 0)
            return;

        var yMin = finiteValues.Min();
        var yMax = finiteValues.Max();
        if (Math.Abs(yMax - yMin) < 0.001d)
        {
            yMin -= 1d;
            yMax += 1d;
        }

        var intervals = BuildMlAnomalyIntervals(IsOnline ? _onlineMlPredictionRows : _offlineMlPredictionRows);

        // Collect all visible intervals and draw only ONE start line (earliest) and ONE end line (latest)
        // so exactly 2 dashed red lines appear regardless of how many anomaly bursts exist.
        DateTime? overallStart = null;
        DateTime? overallEnd = null;

        foreach (var interval in intervals)
        {
            if (interval.End < axisStart || interval.Start > axisEnd)
                continue;

            var clampedStart = interval.Start < axisStart ? axisStart : interval.Start;
            var clampedEnd = interval.End > axisEnd ? axisEnd : interval.End;

            if (overallStart == null || clampedStart < overallStart)
                overallStart = clampedStart;
            if (overallEnd == null || clampedEnd > overallEnd)
                overallEnd = clampedEnd;
        }

        if (overallStart.HasValue && overallEnd.HasValue)
        {
            AddAnomalyBoundarySeriesLine(target, overallStart.Value, yMin, yMax);
            AddAnomalyBoundarySeriesLine(target, overallEnd.Value, yMin, yMax);
        }
    }

    private void AddAnomalyBoundarySeriesLine(SeriesCollection target, DateTime timestamp, double yMin, double yMax)
    {
        var x = DateTimeToAxisValue(timestamp);
        var values = new ChartValues<MetricPoint>
        {
            new MetricPoint(x, timestamp, yMin, string.Empty, string.Empty, "Anomaly boundary", string.Empty),
            new MetricPoint(x, timestamp, yMax, string.Empty, string.Empty, "Anomaly boundary", string.Empty)
        };

        // Title = string.Empty tells LiveCharts to skip this series in the legend renderer,
        // so no stray red dot appears. IsSeriesVisible is read-only (reflects Visibility),
        // so we cannot set it directly.
        target.Add(new LineSeries
        {
            Title = string.Empty,
            Values = values,
            Configuration = Mappers.Xy<MetricPoint>().X(point => point.Index).Y(point => point.Value),
            Stroke = System.Windows.Media.Brushes.Red,
            Fill = System.Windows.Media.Brushes.Transparent,
            StrokeThickness = 2,
            StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 4 },
            PointGeometrySize = 0,
            LineSmoothness = 0,
            LabelPoint = _ => string.Empty
        });
    }

    private string BuildTimelineTooltipLabel(ChartPoint chartPoint)
    {
        if (chartPoint.Instance is not MetricPoint point || point.Timestamp == DateTime.MinValue)
            return $"{chartPoint.Y:0.##}";

        var baseLabel = $"{point.MetricName}  {point.Timestamp:HH:mm:ss}  {chartPoint.Y:0.##}";
        var reasonText = ResolvePointAnomalyTooltip(point);
        return string.IsNullOrWhiteSpace(reasonText)
            ? baseLabel
            : $"{baseLabel}{Environment.NewLine}{reasonText}";
    }

    private string ResolvePointAnomalyTooltip(MetricPoint point)
    {
        if (string.IsNullOrWhiteSpace(point.MetricName))
            return string.Empty;

        var metricName = point.MetricName.Trim();
        var metricCode = GetSensorCode(metricName);
        var rows = (IsOnline ? _onlineMlPredictionRows : _offlineMlPredictionRows)
            .Where(row => row.IsScored && row.Anomaly && row.Timestamp.HasValue)
            .Where(row => Math.Abs((row.Timestamp!.Value - point.Timestamp).TotalSeconds) <= 1.0)
            .ToList();
        if (rows.Count == 0)
            return string.Empty;

        var matchingReasons = rows
            .SelectMany(row => row.Reasons.Count > 0
                ? row.Reasons
                : string.IsNullOrWhiteSpace(row.AnomalySummary)
                    ? Enumerable.Empty<AnomalyReason>()
                    : [new AnomalyReason("OTHER", metricName, null, string.Empty, row.AnomalySummary)])
            .Where(reason => SensorMatchesMetric(reason.Sensor, metricName, metricCode))
            .Select(reason => string.IsNullOrWhiteSpace(reason.Detail)
                ? FormatConciseAnomalyReason(reason)
                : reason.Detail.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        return matchingReasons.Count == 0
            ? string.Empty
            : "ML: " + string.Join(Environment.NewLine + "ML: ", matchingReasons);
    }

    private static bool SensorMatchesMetric(string? sensor, string metricName, string metricCode)
    {
        if (string.IsNullOrWhiteSpace(sensor))
            return false;

        var normalizedSensor = sensor.Trim();
        return string.Equals(normalizedSensor, metricName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalizedSensor, metricCode, StringComparison.OrdinalIgnoreCase)
               || normalizedSensor.Contains(metricName, StringComparison.OrdinalIgnoreCase)
               || metricName.Contains(normalizedSensor, StringComparison.OrdinalIgnoreCase)
               || (!string.IsNullOrWhiteSpace(metricCode) && normalizedSensor.Contains(metricCode, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateXAxisAnomalySections(DateTime axisStart, DateTime axisEnd)
    {
        var sections = new SectionsCollection();
        var intervals = BuildMlAnomalyIntervals(IsOnline ? _onlineMlPredictionRows : _offlineMlPredictionRows);
        foreach (var interval in intervals)
        {
            if (interval.End < axisStart || interval.Start > axisEnd)
                continue;

            AddAnomalyBoundarySection(sections, interval.Start < axisStart ? axisStart : interval.Start);
            AddAnomalyBoundarySection(sections, interval.End > axisEnd ? axisEnd : interval.End);
        }

        if (IsOnline)
        {
            OnlineXAxisSections = sections;
            OfflineXAxisSections = new SectionsCollection();
        }
        else
        {
            OfflineXAxisSections = sections;
            OnlineXAxisSections = new SectionsCollection();
        }

        OnPropertyChanged(nameof(OfflineXAxisSections));
        OnPropertyChanged(nameof(OnlineXAxisSections));
    }

    private void AddAnomalyBoundarySection(SectionsCollection sections, DateTime timestamp)
    {
        sections.Add(new AxisSection
        {
            Value = DateTimeToAxisValue(timestamp),
            Stroke = System.Windows.Media.Brushes.Red,
            StrokeThickness = 2,
            StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 4 }
        });
    }

    private static bool IsInsideMlAnomalyWindow(
        DateTime timestamp,
        IReadOnlyList<(DateTime Start, DateTime End)> intervals)
        => intervals.Any(interval => timestamp >= interval.Start && timestamp < interval.End);

    private static IReadOnlyList<(DateTime Start, DateTime End)> BuildMlAnomalyIntervals(
        IEnumerable<AnomalyPredictionRow> predictions)
    {
        var scored = predictions
            .Where(row => row.IsScored && row.Timestamp.HasValue)
            .OrderBy(row => row.Timestamp)
            .ToList();
        if (scored.Count == 0)
            return [];

        var observedGaps = scored
            .Zip(scored.Skip(1), (left, right) => right.Timestamp!.Value - left.Timestamp!.Value)
            .Where(gap => gap > TimeSpan.Zero)
            .OrderBy(gap => gap)
            .ToList();
        var observedCadence = observedGaps.Count == 0
            ? TimeSpan.FromTicks(1)
            : observedGaps[observedGaps.Count / 2];

        var anomalyTimes = scored
            .Where(row => row.Anomaly)
            .Select(row => row.Timestamp!.Value)
            .OrderBy(timestamp => timestamp)
            .ToList();
        if (anomalyTimes.Count == 0)
            return [];

        var mergeGap = TimeSpan.FromTicks(Math.Max(observedCadence.Ticks * 3, TimeSpan.TicksPerSecond));
        var intervals = new List<(DateTime Start, DateTime End)>();
        var activeStart = anomalyTimes[0];
        var activeEnd = anomalyTimes[0] + observedCadence;

        foreach (var anomalyTime in anomalyTimes.Skip(1))
        {
            if (anomalyTime - activeEnd <= mergeGap)
            {
                activeEnd = anomalyTime + observedCadence;
                continue;
            }

            intervals.Add((activeStart, activeEnd));
            activeStart = anomalyTime;
            activeEnd = anomalyTime + observedCadence;
        }

        intervals.Add((activeStart, activeEnd));

        return intervals;
    }

    private void BuildGoodVsFailedEnvelopeSeries()
    {
        var metrics = GetTemperatureMetrics().ToList();
        var failedAttempts = GetReviewAttemptsForComparison();
        var baselineAttempts = GetBaselineAttemptsForComparison();

        if (HasExplicitCycleFilter())
        {
            failedAttempts = ApplyPinnedCycleFilter(failedAttempts);
            baselineAttempts = ApplyPinnedCycleFilter(baselineAttempts);
        }

        if (metrics.Count == 0 || (baselineAttempts.Count == 0 && failedAttempts.Count == 0))
        {
            ApplyChartState(new SeriesCollection(), Array.Empty<string>());
            return;
        }

        var goodRowIds = baselineAttempts.SelectMany(attempt => attempt.Rows).Select(row => row.Id).ToHashSet();
        var failedRowIds = failedAttempts.SelectMany(attempt => attempt.Rows).Select(row => row.Id).ToHashSet();

        var palette = GetPalette();
        var newSeries = new SeriesCollection();

        // -- BAR CHART MODE --
        if (IsBarChartRepresentation)
        {
            // Show 4 summary bars: Good Min, Good Max, Failed Min, Failed Max
            // Use VisibleCycles (full dataset) so we compute statistics over all dates
            var goodRows = VisibleCycles.Where(c => goodRowIds.Contains(c.Id)).ToList();
            var failedRows = VisibleCycles.Where(c => failedRowIds.Contains(c.Id)).ToList();
            var goodVals = GetValidMetricValues(goodRows, metrics);
            var failedVals = GetValidMetricValues(failedRows, metrics);

            var barLabels = new List<string> { "Good Min", "Good Max", "Failed Min", "Failed Max" };
            var barData = new[]
            {
                (idx: 0, val: goodVals.Count   == 0 ? double.NaN : Math.Round(goodVals.Min(),   2), brush: palette[2]),
                (idx: 1, val: goodVals.Count   == 0 ? double.NaN : Math.Round(goodVals.Max(),   2), brush: palette[0]),
                (idx: 2, val: failedVals.Count == 0 ? double.NaN : Math.Round(failedVals.Min(), 2), brush: palette[3]),
                (idx: 3, val: failedVals.Count == 0 ? double.NaN : Math.Round(failedVals.Max(), 2), brush: palette[5])
            };
            foreach (var (idx, val, brush) in barData)
            {
                if (double.IsNaN(val)) continue;
                var pts = new ChartValues<MetricPoint>
                {
                    new MetricPoint(idx, DateTime.MinValue, val, barLabels[idx], barLabels[idx], "Envelope", ImportedWorkbookName)
                };
                newSeries.Add(new ColumnSeries
                {
                    Title = barLabels[idx],
                    Values = pts,
                    Configuration = Mappers.Xy<MetricPoint>().X(p => p.Index).Y(p => p.Value),
                    LabelPoint = TooltipLabelPoint,
                    Fill = brush,
                    Stroke = brush,
                    StrokeThickness = 0.8,
                    MaxColumnWidth = 42,
                    ColumnPadding = 6
                });
            }
            ApplyChartState(newSeries, barLabels);
            return;
        }

        // -- LINE / ENVELOPE MODE --
        var allAttempts = baselineAttempts.Concat(failedAttempts).ToList();
        var buckets = BuildRelativeTimeBucketSlices(allAttempts);
        if (buckets.Count == 0)
        {
            ApplyChartState(new SeriesCollection(), Array.Empty<string>());
            return;
        }

        var goodMin = new ChartValues<MetricPoint>();
        var goodMax = new ChartValues<MetricPoint>();
        var failedMin = new ChartValues<MetricPoint>();
        var failedMax = new ChartValues<MetricPoint>();

        for (var i = 0; i < buckets.Count; i++)
        {
            var bucket = buckets[i];
            var goodRows = bucket.Rows.Where(row => goodRowIds.Contains(row.Id)).ToList();
            var failedRows = bucket.Rows.Where(row => failedRowIds.Contains(row.Id)).ToList();
            var goodValues = GetValidMetricValues(goodRows, metrics);
            var failedValues = GetValidMetricValues(failedRows, metrics);

            goodMin.Add(new MetricPoint(i, bucket.Timestamp,
                goodValues.Count == 0 ? double.NaN : Math.Round(goodValues.Min(), 2),
                "Good cycle min", CurrentRangeLabel, "Window", ImportedWorkbookName));
            goodMax.Add(new MetricPoint(i, bucket.Timestamp,
                goodValues.Count == 0 ? double.NaN : Math.Round(goodValues.Max(), 2),
                "Good cycle max", CurrentRangeLabel, "Window", ImportedWorkbookName));
            failedMin.Add(new MetricPoint(i, bucket.Timestamp,
                failedValues.Count == 0 ? double.NaN : Math.Round(failedValues.Min(), 2),
                "Failed cycle min", CurrentRangeLabel, "Window", ImportedWorkbookName));
            failedMax.Add(new MetricPoint(i, bucket.Timestamp,
                failedValues.Count == 0 ? double.NaN : Math.Round(failedValues.Max(), 2),
                "Failed cycle max", CurrentRangeLabel, "Window", ImportedWorkbookName));
        }

        if (goodMin.Any(value => !double.IsNaN(value.Value))) AddIndexedMetricSeries(newSeries, "Good cycle min", goodMin, palette[2]);
        if (goodMax.Any(value => !double.IsNaN(value.Value))) AddIndexedMetricSeries(newSeries, "Good cycle max", goodMax, palette[0]);
        if (failedMin.Any(value => !double.IsNaN(value.Value))) AddIndexedMetricSeries(newSeries, "Failed cycle min", failedMin, palette[3]);
        if (failedMax.Any(value => !double.IsNaN(value.Value))) AddIndexedMetricSeries(newSeries, "Failed cycle max", failedMax, palette[5]);
        ApplyChartState(newSeries, BuildBucketLabels(buckets));
    }
    private int GetTimelinePointBudget(int metricCount)
    {
        if (IsBarChartRepresentation)
        {
            return metricCount >= 10 ? 60 : metricCount >= 6 ? 84 : 120;
        }

        return metricCount >= 10 ? 140 : metricCount >= 6 ? 180 : 240;
    }

    private static List<SterilizationCycle> DownsampleTimelineCycles(IReadOnlyList<SterilizationCycle> source, int maxPoints)
    {
        if (source.Count <= maxPoints || maxPoints <= 0)
        {
            return source.ToList();
        }

        var sampled = new List<SterilizationCycle>(maxPoints);
        var bucketSize = source.Count / (double)maxPoints;
        for (var bucketIndex = 0; bucketIndex < maxPoints; bucketIndex++)
        {
            var start = (int)Math.Floor(bucketIndex * bucketSize);
            var endExclusive = (int)Math.Floor((bucketIndex + 1) * bucketSize);
            if (bucketIndex == maxPoints - 1)
            {
                endExclusive = source.Count;
            }

            if (endExclusive <= start)
            {
                endExclusive = Math.Min(source.Count, start + 1);
            }

            sampled.Add(source[endExclusive - 1]);
        }

        return sampled;
    }

    private void BuildCyclesInfoSeries()
    {
        var attempts = GetAttemptsForComparison();
        var renderCycles = GetComparisonRenderCycles();

        if (attempts.Count == 0 || renderCycles.Count == 0)
        {
            ApplyChartState(new SeriesCollection(), Array.Empty<string>());
            return;
        }

        var goodRowIds = attempts.SelectMany(attempt => attempt.Rows).Select(row => row.Id).ToHashSet();
        var activeCycles = renderCycles.Where(c => goodRowIds.Contains(c.Id)).ToList();
        var buckets = BuildTimeBucketSlices(activeCycles);

        if (buckets.Count == 0)
        {
            ApplyChartState(new SeriesCollection(), Array.Empty<string>());
            return;
        }

        var attemptByRowId = attempts
            .SelectMany(attempt => attempt.Rows.Select(row => new { row.Id, Attempt = attempt }))
            .GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.First().Attempt);
        var durationValues = new ChartValues<MetricPoint>();
        var maxStepValues = new ChartValues<MetricPoint>();
        var processSpanValues = new ChartValues<MetricPoint>();

        for (var i = 0; i < buckets.Count; i++)
        {
            var bucket = buckets[i];
            var contextAttempt = ResolveContextAttemptForBucket(bucket, attempts);
            var averageDuration = contextAttempt is null ? double.NaN : contextAttempt.DurationMinutes;
            var maxStep = StepHeaderKey is null
                ? double.NaN
                : bucket.Rows.Select(row => row.GetNumericValue(StepHeaderKey) ?? double.NaN)
                    .Where(value => !double.IsNaN(value))
                    .DefaultIfEmpty(double.NaN)
                    .Max();
            var processChanges = CountProcessPhaseChanges(bucket.Rows, GetProcessPhaseLabel);

            durationValues.Add(new MetricPoint(i, bucket.Timestamp, averageDuration, "Cycle duration (min)", contextAttempt?.Name ?? CurrentRangeLabel, contextAttempt?.Status ?? "Window", contextAttempt?.Recipe ?? ImportedWorkbookName));
            maxStepValues.Add(new MetricPoint(i, bucket.Timestamp, double.IsNaN(maxStep) ? double.NaN : Math.Round(maxStep, 0), "Highest process step", contextAttempt?.Name ?? CurrentRangeLabel, contextAttempt?.Status ?? "Window", contextAttempt?.Recipe ?? ImportedWorkbookName));
            processSpanValues.Add(new MetricPoint(i, bucket.Timestamp, processChanges, "Process-state changes", contextAttempt?.Name ?? CurrentRangeLabel, contextAttempt?.Status ?? "Window", contextAttempt?.Recipe ?? ImportedWorkbookName));
        }

        var palette = GetPalette();
        var newSeries = new SeriesCollection();

        // -- BAR CHART MODE --
        if (IsBarChartRepresentation)
        {
            // Use per-attempt data instead of time buckets so bars have meaningful labels
            var barAttempts = ApplyPinnedCycleFilter(GetAttemptSummariesForAnalysis())
                .OrderBy(a => a.Start).ToList();
            if (barAttempts.Count == 0)
            {
                ApplyChartState(new SeriesCollection(), Array.Empty<string>());
                return;
            }
            var durVals = new ChartValues<MetricPoint>();
            var stepVals = new ChartValues<MetricPoint>();
            var stageVals = new ChartValues<MetricPoint>();
            for (var i = 0; i < barAttempts.Count; i++)
            {
                var a = barAttempts[i];
                var stageCount = a.Rows
                    .Select(GetProcessPhaseLabel)
                    .Where(p => !string.IsNullOrWhiteSpace(p) && !string.Equals(p, "Unknown", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                durVals.Add(new MetricPoint(i, a.Start, a.DurationMinutes, "Cycle duration (min)", a.Name, a.Status, a.Recipe));
                stepVals.Add(new MetricPoint(i, a.Start, a.MaxStep, "Highest process step", a.Name, a.Status, a.Recipe));
                stageVals.Add(new MetricPoint(i, a.Start, stageCount, "Distinct process stages", a.Name, a.Status, a.Recipe));
            }
            AddIndexedMetricSeries(newSeries, "Cycle duration (min)", durVals, palette[2]);
            AddIndexedMetricSeries(newSeries, "Highest process step", stepVals, palette[0]);
            AddIndexedMetricSeries(newSeries, "Distinct process stages", stageVals, palette[5]);
            ApplyChartState(newSeries, barAttempts.Select(a => a.Name).ToArray());
            return;
        }

        // -- LINE / LIVE CHART MODE --
        AddIndexedMetricSeries(newSeries, "Highest process step", maxStepValues, palette[0]);
        AddIndexedMetricSeries(newSeries, "Process-state changes", processSpanValues, palette[5]);
        ApplyChartState(newSeries, BuildBucketLabels(buckets));
    }

    private void BuildCycleDurationAnalyticsSeries()
    {
        var attempts = ApplyPinnedCycleFilter(GetAttemptSummariesForAnalysis())
            .OrderBy(attempt => attempt.Start)
            .ToList();
        if (attempts.Count == 0)
        {
            ApplyChartState(new SeriesCollection(), Array.Empty<string>());
            return;
        }

        var completeValues = new ChartValues<MetricPoint>();
        var reviewValues = new ChartValues<MetricPoint>();
        var stepValues = new ChartValues<MetricPoint>();

        for (var index = 0; index < attempts.Count; index++)
        {
            var attempt = attempts[index];
            var maxStep = attempt.MaxStep;

            var durationPoint = new MetricPoint(index, attempt.Start, attempt.DurationMinutes, "Cycle duration (min)", attempt.Name, attempt.Status, attempt.Recipe);
            if (attempt.Status == "Complete")
            {
                completeValues.Add(durationPoint);
            }
            else
            {
                reviewValues.Add(durationPoint);
            }

            stepValues.Add(new MetricPoint(index, attempt.Start, maxStep, "Highest process step", attempt.Name, attempt.Status, attempt.Recipe));
        }

        var labels = attempts.Select(a => a.Name).ToArray();

        var palette = GetPalette();
        var series = new SeriesCollection();

        // -- BAR CHART MODE --
        if (IsBarChartRepresentation)
        {
            // One ColumnSeries per attempt for duration, coloured by status.
            // A second ColumnSeries for MaxStep shares the same X indices.
            var durValues = new ChartValues<MetricPoint>();
            var barstepValues = new ChartValues<MetricPoint>();
            for (var i = 0; i < attempts.Count; i++)
            {
                var a = attempts[i];
                durValues.Add(new MetricPoint(i, a.Start, a.DurationMinutes, "Cycle duration (min)", a.Name, a.Status, a.Recipe));
                barstepValues.Add(new MetricPoint(i, a.Start, a.MaxStep, "Highest process step", a.Name, a.Status, a.Recipe));
            }
            AddIndexedMetricSeries(series, "Cycle duration (min)", durValues, palette[2]);
            AddIndexedMetricSeries(series, "Highest step", barstepValues, palette[1]);
            ApplyChartState(series, labels);
            return;
        }

        // -- LINE / LIVE CHART MODE --
        if (completeValues.Count > 0) AddIndexedMetricSeries(series, "Complete attempts", completeValues, palette[2]);
        if (reviewValues.Count > 0) AddIndexedMetricSeries(series, "Review attempts", reviewValues, palette[3]);
        AddIndexedMetricSeries(series, "Highest step", stepValues, palette[1]);
        ApplyChartState(series, labels);
    }

    private void BuildSensorProfileSeries(SensorFamily family, string goodTitle, string failedTitle)
    {
        var metrics = MetricOptions.Where(metric => GetSensorFamily(metric.PropertyName) == family).ToList();
        var failedAttempts = GetReviewAttemptsForComparison();
        var baselineAttempts = GetBaselineAttemptsForComparison();

        if (metrics.Count == 0 || (baselineAttempts.Count == 0 && failedAttempts.Count == 0))
        {
            ApplyChartState(new SeriesCollection(), Array.Empty<string>());
            return;
        }

        var palette = GetPalette();
        var series = new SeriesCollection();

        // -- BAR CHART MODE --
        if (IsBarChartRepresentation)
        {
            if (HasExplicitCycleFilter())
            {
                var selAttempts = ApplyPinnedCycleFilter(GetAttemptSummaries(applyTimeFilter: false, bypassDateFilter: true))
                    .OrderBy(a => a.Start)
                    .Take(6)
                    .ToList();
                for (var i = 0; i < selAttempts.Count; i++)
                {
                    var a = selAttempts[i];
                    var vals = GetValidMetricValues(a.Rows, metrics);
                    var avg = vals.Count == 0 ? 0d : Math.Round(vals.Average(), 2);
                    var brush = palette[i % palette.Length];
                    var pt = new ChartValues<MetricPoint>
                    {
                        new MetricPoint(i, a.Start, avg, a.Name, a.Name, a.Status, a.Recipe)
                    };
                    series.Add(new ColumnSeries
                    {
                        Title = a.Name,
                        Values = pt,
                        Configuration = Mappers.Xy<MetricPoint>().X(p => p.Index).Y(p => p.Value),
                        LabelPoint = TooltipLabelPoint,
                        Fill = brush,
                        Stroke = brush,
                        StrokeThickness = 0.8,
                        MaxColumnWidth = 42,
                        ColumnPadding = 6
                    });
                }
                ApplyChartState(series, selAttempts.Select(a => a.Name).ToArray());
            }
            else
            {
                var goodRowIds2 = baselineAttempts.SelectMany(a => a.Rows).Select(r => r.Id).ToHashSet();
                var failedRowIds2 = failedAttempts.SelectMany(a => a.Rows).Select(r => r.Id).ToHashSet();
                var goodRows2 = VisibleCycles.Where(c => goodRowIds2.Contains(c.Id)).ToList();
                var failedRows2 = VisibleCycles.Where(c => failedRowIds2.Contains(c.Id)).ToList();
                var goodVals2 = GetValidMetricValues(goodRows2, metrics);
                var failedVals2 = GetValidMetricValues(failedRows2, metrics);
                var barLabels2 = new List<string> { goodTitle, failedTitle };
                if (goodVals2.Count > 0)
                {
                    var pt = new ChartValues<MetricPoint>
                    {
                        new MetricPoint(0, DateTime.MinValue, Math.Round(goodVals2.Average(), 2), goodTitle, goodTitle, "Good", ImportedWorkbookName)
                    };
                    series.Add(new ColumnSeries
                    {
                        Title = goodTitle,
                        Values = pt,
                        Configuration = Mappers.Xy<MetricPoint>().X(p => p.Index).Y(p => p.Value),
                        LabelPoint = TooltipLabelPoint,
                        Fill = palette[2],
                        Stroke = palette[2],
                        StrokeThickness = 0.8,
                        MaxColumnWidth = 42,
                        ColumnPadding = 6
                    });
                }
                if (failedVals2.Count > 0)
                {
                    var pt = new ChartValues<MetricPoint>
                    {
                        new MetricPoint(1, DateTime.MinValue, Math.Round(failedVals2.Average(), 2), failedTitle, failedTitle, "Failed", ImportedWorkbookName)
                    };
                    series.Add(new ColumnSeries
                    {
                        Title = failedTitle,
                        Values = pt,
                        Configuration = Mappers.Xy<MetricPoint>().X(p => p.Index).Y(p => p.Value),
                        LabelPoint = TooltipLabelPoint,
                        Fill = palette[3],
                        Stroke = palette[3],
                        StrokeThickness = 0.8,
                        MaxColumnWidth = 42,
                        ColumnPadding = 6
                    });
                }
                ApplyChartState(series, barLabels2.ToArray());
            }
            return;
        }

        // -- LINE / LIVE CHART MODE --
        if (HasExplicitCycleFilter())
        {
            var selectedAttempts = ApplyPinnedCycleFilter(GetAttemptSummaries(applyTimeFilter: false, bypassDateFilter: true))
                .OrderBy(attempt => attempt.Start)
                .Take(6)
                .ToList();
            var buckets = BuildRelativeTimeBucketSlices(selectedAttempts);
            if (buckets.Count == 0)
            {
                ApplyChartState(new SeriesCollection(), Array.Empty<string>());
                return;
            }

            foreach (var indexedAttempt in selectedAttempts.Select((attempt, index) => new { attempt, index }))
            {
                var rowIds = indexedAttempt.attempt.Rows.Select(row => row.Id).ToHashSet();
                var values = BuildBucketAverageSeries(
                    buckets,
                    rowIds,
                    metrics,
                    indexedAttempt.attempt.Name,
                    indexedAttempt.attempt.Name,
                    indexedAttempt.attempt.Status,
                    indexedAttempt.attempt.Recipe);
                AddIndexedMetricSeries(series, indexedAttempt.attempt.Name, values, palette[indexedAttempt.index % palette.Length]);
            }
            ApplyChartState(series, BuildBucketLabels(buckets));
        }
        else
        {
            var goodRowIds = baselineAttempts.SelectMany(attempt => attempt.Rows).Select(row => row.Id).ToHashSet();
            var failedRowIds = failedAttempts.SelectMany(attempt => attempt.Rows).Select(row => row.Id).ToHashSet();

            var allAttempts = baselineAttempts.Concat(failedAttempts).ToList();
            var buckets = BuildRelativeTimeBucketSlices(allAttempts);
            if (buckets.Count == 0)
            {
                ApplyChartState(new SeriesCollection(), Array.Empty<string>());
                return;
            }

            var goodValues = BuildBucketAverageSeries(buckets, goodRowIds, metrics, goodTitle, goodTitle, "Window", ImportedWorkbookName);
            var failedValues = BuildBucketAverageSeries(buckets, failedRowIds, metrics, failedTitle, failedTitle, "Window", ImportedWorkbookName);

            AddIndexedMetricSeries(series, goodTitle, goodValues, palette[2]);
            AddIndexedMetricSeries(series, failedTitle, failedValues, palette[3]);
            ApplyChartState(series, BuildBucketLabels(buckets));
        }
    }

    private ChartValues<MetricPoint> BuildBucketAverageSeries(
        IReadOnlyList<TimeBucketSlice> buckets,
        IReadOnlySet<int> rowIds,
        IReadOnlyCollection<MetricOption> metrics,
        string metricName,
        string attemptName,
        string status,
        string recipe)
    {
        var values = new ChartValues<MetricPoint>();
        for (var index = 0; index < buckets.Count; index++)
        {
            var bucket = buckets[index];
            var bucketRows = bucket.Rows.Where(row => rowIds.Contains(row.Id)).ToList();
            var metricValues = GetValidMetricValues(bucketRows, metrics);
            values.Add(new MetricPoint(
                index,
                bucket.Timestamp,
                metricValues.Count == 0 ? double.NaN : Math.Round(metricValues.Average(), 2),
                metricName,
                attemptName,
                status,
                recipe));
        }

        return values;
    }

    private void BuildF0ExposureSeries()
    {
        var f0Metrics = MetricOptions.Where(metric => GetSensorFamily(metric.PropertyName) == SensorFamily.Lethality).Take(2).ToList();
        var attempts = ApplyPinnedCycleFilter(GetAttemptSummariesForAnalysis())
            .OrderBy(attempt => attempt.Start)
            .ToList();
        if (attempts.Count == 0 || f0Metrics.Count == 0)
        {
            ApplyChartState(new SeriesCollection(), Array.Empty<string>());
            return;
        }

        var series = new SeriesCollection();
        var palette = GetPalette();
        for (var metricIndex = 0; metricIndex < f0Metrics.Count; metricIndex++)
        {
            var metric = f0Metrics[metricIndex];
            var values = new ChartValues<MetricPoint>();
            for (var attemptIndex = 0; attemptIndex < attempts.Count; attemptIndex++)
            {
                var attempt = attempts[attemptIndex];
                var peakValue = attempt.Rows
                    .Select(metric.GetValue)
                    .Where(value => value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value))
                    .Select(value => value!.Value)
                    .DefaultIfEmpty(0d)
                    .Max();

                values.Add(new MetricPoint(attemptIndex, attempt.Start, Math.Round(peakValue, 2), metric.DisplayName, attempt.Name, attempt.Status, attempt.Recipe));
            }

            AddIndexedMetricSeries(series, GetSensorCode(metric), values, palette[metricIndex % palette.Length]);
        }

        var labels = attempts.Select(a => a.Name).ToArray();
        ApplyChartState(series, labels);
    }

    private void BuildLevelConductivitySeries()
    {
        var metrics = MetricOptions
            .Where(metric =>
                GetSensorFamily(metric.PropertyName) == SensorFamily.Level ||
                GetSensorFamily(metric.PropertyName) == SensorFamily.Flow)
            .Take(4)
            .ToList();
        var attempts = ApplyPinnedCycleFilter(GetAttemptSummariesForAnalysis())
            .OrderBy(attempt => attempt.Start)
            .ToList();
        if (attempts.Count == 0 || metrics.Count == 0)
        {
            ApplyChartState(new SeriesCollection(), Array.Empty<string>());
            return;
        }

        var series = new SeriesCollection();
        var palette = GetPalette();
        for (var metricIndex = 0; metricIndex < metrics.Count; metricIndex++)
        {
            var metric = metrics[metricIndex];
            var values = new ChartValues<MetricPoint>();
            for (var attemptIndex = 0; attemptIndex < attempts.Count; attemptIndex++)
            {
                var attempt = attempts[attemptIndex];
                var validValues = attempt.Rows
                    .Select(metric.GetValue)
                    .Where(value => value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value))
                    .Select(value => value!.Value)
                    .Where(value => !IsImpossibleSensorValue(metric, value))
                    .ToList();

                var aggregateValue = validValues.Count == 0 ? 0d : Math.Round(validValues.Average(), 2);
                values.Add(new MetricPoint(attemptIndex, attempt.Start, aggregateValue, metric.DisplayName, attempt.Name, attempt.Status, attempt.Recipe));
            }

            AddIndexedMetricSeries(series, GetSensorCode(metric), values, palette[metricIndex % palette.Length]);
        }

        var labels = attempts.Select(a => a.Name).ToArray();
        ApplyChartState(series, labels);
    }

    private void BuildRecipeStepMapSeries()
    {
        var attempts = ApplyPinnedCycleFilter(GetAttemptSummariesForAnalysis())
            .OrderBy(attempt => attempt.Start)
            .ToList();
        if (attempts.Count == 0)
        {
            ApplyChartState(new SeriesCollection(), Array.Empty<string>());
            return;
        }

        var maxStepValues = new ChartValues<MetricPoint>();
        var stageCountValues = new ChartValues<MetricPoint>();
        var preIdleValues = new ChartValues<MetricPoint>();

        for (var index = 0; index < attempts.Count; index++)
        {
            var attempt = attempts[index];
            var maxStep = attempt.MaxStep;
            var stageCount = attempt.Rows
                .Select(GetProcessPhaseLabel)
                .Where(label => !string.IsNullOrWhiteSpace(label) && !string.Equals(label, "Unknown", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var preIdleCount = string.IsNullOrWhiteSpace(attempt.PreIdleSteps)
                ? 0
                : attempt.PreIdleSteps.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

            maxStepValues.Add(new MetricPoint(index, attempt.Start, maxStep, "Highest process step", attempt.Name, attempt.Status, attempt.Recipe));
            stageCountValues.Add(new MetricPoint(index, attempt.Start, stageCount, "Distinct process stages", attempt.Name, attempt.Status, attempt.Recipe));
            preIdleValues.Add(new MetricPoint(index, attempt.Start, preIdleCount, "Pre-idle steps", attempt.Name, attempt.Status, attempt.Recipe));
        }

        var palette = GetPalette();
        var series = new SeriesCollection();
        AddIndexedMetricSeries(series, "Highest step", maxStepValues, palette[0]);
        AddIndexedMetricSeries(series, "Distinct stages", stageCountValues, palette[2]);
        AddIndexedMetricSeries(series, "Pre-idle steps", preIdleValues, palette[5]);

        var labels = attempts.Select(a => a.Name).ToArray();
        ApplyChartState(series, labels);
    }

    private IEnumerable<MetricOption> GetTemperatureMetrics()
        => MetricOptions.Where(m => GetSensorFamily(m.PropertyName) == SensorFamily.Temperature).OrderBy(m => m.DisplayName);

    private IEnumerable<MetricOption> GetPressureMetrics()
        => MetricOptions.Where(m => GetSensorFamily(m.PropertyName) == SensorFamily.Pressure).OrderBy(m => m.DisplayName);

    private double GetAveragePeak(IEnumerable<AttemptSummary> attempts, IEnumerable<MetricOption> metrics)
    {
        var peaks = new List<double>();
        foreach (var metric in metrics)
        {
            var values = attempts.SelectMany(a => a.Rows)
                .Select(metric.GetValue).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (values.Count > 0) peaks.Add(values.Max());
        }
        return peaks.Count == 0 ? 0d : Math.Round(peaks.Average(), 2);
    }

    private double GetAverageRowValue(SterilizationCycle row, IEnumerable<MetricOption> metrics)
    {
        var values = metrics.Select(m => m.GetValue(row)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return values.Count == 0 ? 0d : Math.Round(values.Average(), 2);
    }

    private AttemptSummary? GetRepresentativeGoodAttempt()
    {
        var attempts = GetBaselineAttemptsForAnalysis().OrderBy(a => a.DurationMinutes).ToList();
        return attempts.Skip(Math.Max(0, attempts.Count / 2)).FirstOrDefault();
    }

    private AttemptSummary? GetRepresentativeFailedAttempt()
        => GetReviewAttemptsForAnalysis().OrderByDescending(a => a.DurationMinutes).FirstOrDefault();

    private double GetProcessSpan(AttemptSummary attempt)
        => attempt.Rows.Select(GetProcessPhaseLabel)
            .Where(v => !string.IsNullOrWhiteSpace(v) && !string.Equals(v, "Unknown", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private ChartValues<MetricPoint> BuildAverageProfilePoints(AttemptSummary attempt, IReadOnlyCollection<MetricOption> metrics, string label)
    {
        var values = new ChartValues<MetricPoint>();
        if (metrics.Count == 0) return values;
        foreach (var row in attempt.Rows.OrderBy(r => r.RecordedAt))
        {
            var rawValues = metrics.Select(m => m.GetValue(row)).Where(v => v.HasValue && v.Value >= -200d).Select(v => v!.Value).ToList();
            if (rawValues.Count == 0) continue;
            var avg = Math.Round(rawValues.Average(), 2);
            var minute = Math.Max(0, (int)Math.Round((row.RecordedAt - attempt.Start).TotalMinutes, MidpointRounding.AwayFromZero));
            values.Add(new MetricPoint(minute, row.RecordedAt, avg, label, attempt.Name, attempt.Status, attempt.Recipe));
        }
        return values;
    }

    private ChartValues<MetricPoint> BuildAverageProfilePoints(IEnumerable<AttemptSummary> attempts, IReadOnlyCollection<MetricOption> metrics, string label)
    {
        var rowsByMinute = new Dictionary<int, List<double>>();
        foreach (var attempt in attempts)
        {
            foreach (var row in attempt.Rows.OrderBy(item => item.RecordedAt))
            {
                var rawValues = metrics
                    .Select(metric => metric.GetValue(row))
                    .Where(value => value.HasValue && !double.IsNaN(value.Value))
                    .Select(value => value!.Value)
                    .Where(value => value >= -200d)
                    .ToList();
                if (rawValues.Count == 0)
                {
                    continue;
                }

                var minute = Math.Max(0, (int)Math.Round((row.RecordedAt - attempt.Start).TotalMinutes, MidpointRounding.AwayFromZero));
                if (!rowsByMinute.TryGetValue(minute, out var bucketValues))
                {
                    bucketValues = [];
                    rowsByMinute[minute] = bucketValues;
                }

                bucketValues.Add(Math.Round(rawValues.Average(), 2));
            }
        }

        var points = new ChartValues<MetricPoint>();
        foreach (var minute in rowsByMinute.Keys.OrderBy(key => key))
        {
            points.Add(new MetricPoint(minute, DateTime.MinValue.AddMinutes(minute), Math.Round(rowsByMinute[minute].Average(), 2), label, label, "Aggregate", label));
        }

        return points;
    }

    private static int GetElapsedMinutes(AttemptSummary attempt, SterilizationCycle row)
        => Math.Max(0, (int)Math.Round((row.RecordedAt - attempt.Start).TotalMinutes, MidpointRounding.AwayFromZero));

    private static double GetProfileAverage(AttemptSummary attempt, IReadOnlyCollection<MetricOption> metrics)
    {
        if (metrics.Count == 0) return 0d;
        var values = attempt.Rows.SelectMany(r => metrics.Select(m => m.GetValue(r)))
            .Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return values.Count == 0 ? 0d : Math.Round(values.Average(), 2);
    }

    private static double GetProfilePeak(AttemptSummary attempt, IReadOnlyCollection<MetricOption> metrics)
    {
        if (metrics.Count == 0) return 0d;
        var values = attempt.Rows.SelectMany(r => metrics.Select(m => m.GetValue(r)))
            .Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return values.Count == 0 ? 0d : Math.Round(values.Max(), 2);
    }

    private static string GetSensorCode(MetricOption metric) => GetSensorCode(metric.PropertyName);

    private static string GetSensorCode(string propertyName)
    {
        var normalized = propertyName.ToUpperInvariant();
        if (normalized.StartsWith("STR34", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[5..];
        return normalized.TrimStart('_');
    }

    private static SensorFamily GetSensorFamily(string propertyName)
    {
        var code = GetSensorCode(propertyName);
        if (code.StartsWith("T", StringComparison.OrdinalIgnoreCase)) return SensorFamily.Temperature;
        if (code.StartsWith("P", StringComparison.OrdinalIgnoreCase)) return SensorFamily.Pressure;
        if (code.StartsWith("F0", StringComparison.OrdinalIgnoreCase)) return SensorFamily.Lethality;
        if (code.StartsWith("L", StringComparison.OrdinalIgnoreCase)) return SensorFamily.Level;
        if (code.StartsWith("Q", StringComparison.OrdinalIgnoreCase)) return SensorFamily.Flow;
        return SensorFamily.Other;
    }

    private void AddIndexedMetricSeries(
        SeriesCollection target,
        string? title,
        ChartValues<MetricPoint> values,
        System.Windows.Media.Brush brush,
        bool showPeakMarkers = false)
    {
        if (!HasRenderableValues(values))
        {
            return;
        }

        var mapper = Mappers.Xy<MetricPoint>().X(p => p.Index).Y(p => p.Value);

        var filteredValues = new ChartValues<MetricPoint>();
        foreach (var val in values)
        {
            if (val != null && !double.IsInfinity(val.Value))
            {
                filteredValues.Add(val);
            }
        }
        if (filteredValues.Count == 0) return;

        if (IsBarChartRepresentation)
        {
            target.Add(new ColumnSeries
            {
                Title = title,
                Values = filteredValues,
                Configuration = mapper,
                LabelPoint = TooltipLabelPoint,
                Fill = brush,
                Stroke = brush,
                StrokeThickness = 0.8,
                MaxColumnWidth = 34,
                ColumnPadding = 6
            });
            return;
        }
        if (filteredValues.Count == 1)
        {
            target.Add(new ScatterSeries
            {
                Title = title,
                Values = filteredValues,
                Configuration = mapper,
                LabelPoint = TooltipLabelPoint,
                Fill = brush,
                Stroke = System.Windows.Media.Brushes.White,
                StrokeThickness = 1,
                MinPointShapeDiameter = 5,
                MaxPointShapeDiameter = 5
            });
            return;
        }

        target.Add(new LineSeries
        {
            Title = title,
            Values = filteredValues,
            Configuration = mapper,
            LabelPoint = TooltipLabelPoint,
            Stroke = brush,
            Fill = System.Windows.Media.Brushes.Transparent,
            PointGeometrySize = 0,
            StrokeThickness = 2.4,
            LineSmoothness = 0
        });

        if (!showPeakMarkers)
            return;

        var peakValues = FindPeakPoints(filteredValues);
        if (peakValues.Count == 0)
            return;

        target.Add(new ScatterSeries
        {
            Title = null,
            Values = peakValues,
            Configuration = mapper,
            LabelPoint = TooltipLabelPoint,
            Fill = brush,
            Stroke = System.Windows.Media.Brushes.White,
            StrokeThickness = 1,
            MinPointShapeDiameter = 5,
            MaxPointShapeDiameter = 5
        });
    }

    private static ChartValues<MetricPoint> FindPeakPoints(ChartValues<MetricPoint> values)
    {
        var peaks = new ChartValues<MetricPoint>();
        if (values.Count < 3)
            return peaks;

        var finiteValues = values
            .Select(point => point.Value)
            .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
            .OrderBy(value => value)
            .ToList();
        if (finiteValues.Count < 3)
            return peaks;

        var valueRange = finiteValues[^1] - finiteValues[0];
        if (valueRange <= 0)
            return peaks;

        var deltas = values
            .Zip(values.Skip(1), (left, right) => Math.Abs(right.Value - left.Value))
            .Where(delta => !double.IsNaN(delta) && !double.IsInfinity(delta) && delta > 0)
            .OrderBy(delta => delta)
            .ToList();

        var medianDelta = deltas.Count == 0 ? 0d : deltas[deltas.Count / 2];
        var prominenceThreshold = Math.Max(valueRange * 0.08, medianDelta * 6);

        for (var index = 1; index < values.Count - 1; index++)
        {
            var previous = values[index - 1].Value;
            var current = values[index].Value;
            var next = values[index + 1].Value;
            var prominence = Math.Min(Math.Abs(current - previous), Math.Abs(current - next));
            var isPeak = (current > previous && current >= next) ||
                         (current < previous && current <= next);
            if (isPeak && prominence >= prominenceThreshold)
                peaks.Add(values[index]);
        }

        return peaks;
    }

    private void AddCategorySeries(SeriesCollection target, string title, ChartValues<double> values, System.Windows.Media.Brush brush)
    {
        if (!HasRenderableValues(values))
        {
            return;
        }

        if (IsBarChartRepresentation)
        {
            target.Add(new ColumnSeries
            {
                Title = title,
                Values = values,
                LabelPoint = TooltipLabelPoint,
                Fill = brush,
                Stroke = brush,
                StrokeThickness = 0.8,
                MaxColumnWidth = 42,
                ColumnPadding = 10
            });
            return;
        }
        target.Add(new LineSeries
        {
            Title = title,
            Values = values,
            LabelPoint = TooltipLabelPoint,
            Stroke = brush,
            Fill = System.Windows.Media.Brushes.Transparent,
            PointGeometrySize = 0,
            StrokeThickness = 2.6,
            LineSmoothness = 0
        });
    }

    private void AddColorPerCategoryBarSeries(SeriesCollection target, IReadOnlyList<string> labels, IReadOnlyList<double> values)
    {
        var palette = GetPalette();
        for (var index = 0; index < labels.Count; index++)
        {
            var pointValues = new ChartValues<double>();
            for (var valueIndex = 0; valueIndex < labels.Count; valueIndex++)
            {
                pointValues.Add(valueIndex == index ? values[index] : double.NaN);
            }

            AddCategorySeries(target, labels[index], pointValues, palette[index % palette.Length]);
        }
    }

    private static bool HasRenderableValues(IEnumerable values)
    {
        foreach (var value in values)
        {
            switch (value)
            {
                case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                    return true;
                case MetricPoint metricPoint when !double.IsNaN(metricPoint.Value) && !double.IsInfinity(metricPoint.Value):
                    return true;
            }
        }

        return false;
    }

    private void ApplyChartState(
        SeriesCollection series,
        IReadOnlyList<string> labels,
        Func<double, string>? xFormatter = null,
        double? numericXAxisMin = null,
        double? numericXAxisMax = null)
    {
        var renderableSeries = series.Where(SeriesHasRenderableValues).ToList();
        var activeChartSeries = IsOnline ? OnlineMainChartSeries : OfflineMainChartSeries;

        // Update only the active mode's series collection.
        // The inactive mode's series is left intact so switching back restores it immediately.
        MainChartSeries.Clear();
        MainChartSeries.AddRange(renderableSeries);
        activeChartSeries.Clear();
        activeChartSeries.AddRange(renderableSeries);

        var allValues = new List<double>();
        foreach (var s in activeChartSeries)
        {
            if (s.Values == null) continue;
            foreach (var v in s.Values)
            {
                if (v is double d && !double.IsNaN(d) && d > -200) allValues.Add(d);
                else if (v is MetricPoint mp && !double.IsNaN(mp.Value) && mp.Value > -200) allValues.Add(mp.Value);
            }
        }

        if (allValues.Count > 0)
        {
            var min = allValues.Min();
            var max = allValues.Max();
            var diff = max - min;
            if (diff == 0) diff = Math.Max(Math.Abs(max) * 0.1, 0.2);
            var padding = diff * (IsBarChartRepresentation ? 0.12 : 0.08);
            var rawMin = Math.Round(min - padding, 2);
            // Bar charts showing non-negative data must start at 0 so bars have a proper baseline.
            YAxisMin = IsBarChartRepresentation && min >= 0 ? 0 : rawMin;
            YAxisMax = Math.Round(max + padding, 2);
        }
        else
        {
            YAxisMin = double.NaN;
            YAxisMax = double.NaN;
        }

        if (xFormatter is null)
        {
            OfflineXAxisSections = new SectionsCollection();
            OnlineXAxisSections = new SectionsCollection();
            XLabels = labels.ToArray();
            // Index-based x-axis (bar / offline label charts).
            // Step sized to show ~10 ticks; clamp so the label array is never overrun.
            XAxisSeparatorStep = ComputeXAxisSeparatorStep(labels);
            // Pad the axis by half a step on both sides so the first and last
            // labels are never clipped against the chart edge.
            var labelPad = XAxisSeparatorStep * 0.5;
            XAxisMin = -labelPad;
            XAxisMax = Math.Max(0, labels.Count - 1) + labelPad;
        }
        else
        {
            // Timeline points already carry elapsed-second X coordinates.
            // Category labels would reinterpret those coordinates as array
            // indexes and cause missing or completely blank time labels.
            XLabels = Array.Empty<string>();
            // Numeric elapsed-seconds x-axis (live sensor timeline).
            if (numericXAxisMax.HasValue)
            {
                XAxisMin = numericXAxisMin ?? 0d;
                XAxisMax = numericXAxisMax.Value <= XAxisMin
                    ? XAxisMin + 1d
                    : numericXAxisMax.Value;
                XAxisSeparatorStep = Math.Max(1d, (XAxisMax - XAxisMin) / 10d);
            }
            else
            {
                XAxisSeparatorStep = ComputeNumericXAxisSeparatorStep(activeChartSeries, out var xMin, out var xMax);
                XAxisMin = double.IsNaN(xMin) ? double.NaN : Math.Min(0d, xMin);
                XAxisMax = double.IsNaN(xMax)
                    ? double.NaN
                    : Math.Max(XAxisSeparatorStep, Math.Ceiling(xMax / XAxisSeparatorStep) * XAxisSeparatorStep);
            }
        }

        YAxisFormatter = value => value.ToString("0.00", CultureInfo.InvariantCulture);
        // Only update XAxisFormatter when we have something meaningful to show.
        // When clearing (renderableSeries is empty / xFormatter is null and no labels),
        // preserve whatever formatter was previously set so the online chart axis
        // labels don't revert to the dead index-based lambda between data-arrival ticks.
        var isEmptyClear = renderableSeries.Count == 0 && xFormatter is null && labels.Count == 0;
        if (!isEmptyClear)
        {
            XAxisFormatter = xFormatter ?? (value =>
            {
                var idx = (int)Math.Round(value);
                return XLabels is not null && idx >= 0 && idx < XLabels.Length
                    ? XLabels[idx]
                    : string.Empty;
            });
        }
        OnPropertyChanged(nameof(XLabels));
        OnPropertyChanged(nameof(OfflineXAxisSections));
        OnPropertyChanged(nameof(OnlineXAxisSections));
        OnPropertyChanged(nameof(XAxisSeparatorStep));
        OnPropertyChanged(nameof(XAxisMin));
        OnPropertyChanged(nameof(XAxisMax));
        OnPropertyChanged(nameof(YAxisFormatter));
        OnPropertyChanged(nameof(XAxisFormatter));
        OnPropertyChanged(nameof(HasRenderableSeries));
        OnPropertyChanged(nameof(EmptyStateMessage));
        OnPropertyChanged(nameof(ActiveMainChartSeries));
    }

    private static bool SeriesHasRenderableValues(ISeriesView series)
    {
        if (series.Values is null)
        {
            return false;
        }

        foreach (var value in series.Values)
        {
            switch (value)
            {
                case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                    return true;
                case MetricPoint metricPoint when !double.IsNaN(metricPoint.Value) && !double.IsInfinity(metricPoint.Value):
                    return true;
                case ObservablePoint point when !double.IsNaN(point.Y) && !double.IsInfinity(point.Y):
                    return true;
            }
        }

        return false;
    }

    private double ComputeXAxisSeparatorStep(IReadOnlyList<string> labels)
    {
        if (labels.Count <= 1) return 1d;

        // Target tick counts: 10 for bar, 12 for line.
        // Use Floor (not Ceiling) so the step is never larger than necessary,
        // which ensures a tick always falls at or near the last label index.
        var targetTicks = IsBarChartRepresentation ? 10 : 12;
        if (labels.Count <= targetTicks) return 1d;
        return Math.Max(1d, Math.Floor((double)(labels.Count - 1) / targetTicks));
    }

    private static double ComputeNumericXAxisSeparatorStep(
        IEnumerable<ISeriesView> series, out double xMin, out double xMax)
    {
        var xs = new List<double>();
        foreach (var item in series)
        {
            if (item.Values is null) continue;
            foreach (var value in item.Values)
            {
                if (value is MetricPoint point && !double.IsNaN(point.Index) && !double.IsInfinity(point.Index))
                    xs.Add(point.Index);
                else if (value is ObservablePoint observable && !double.IsNaN(observable.X) && !double.IsInfinity(observable.X))
                    xs.Add(observable.X);
            }
        }

        if (xs.Count <= 1)
        {
            xMin = xs.Count == 1 ? xs[0] : double.NaN;
            xMax = xMin;
            return 1d;
        }

        xMin = xs.Min();
        xMax = xs.Max();

        var span = Math.Max(1d, xMax - xMin);
        var distinctPointCount = xs.Distinct().Count();
        var intervalCount = Math.Max(1, Math.Min(10, distinctPointCount - 1));
        return span / intervalCount;
    }

    private static double NiceAxisStep(double rawStep)
    {
        if (rawStep <= 0 || double.IsNaN(rawStep) || double.IsInfinity(rawStep))
            return 1d;

        var magnitude = Math.Pow(10d, Math.Floor(Math.Log10(rawStep)));
        var normalized = rawStep / magnitude;
        var niceNormalized = normalized switch
        {
            <= 1d => 1d,
            <= 2d => 2d,
            <= 5d => 5d,
            _ => 10d
        };
        return niceNormalized * magnitude;
    }

    private static System.Windows.Media.Brush[] GetPalette() =>
    [
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 177, 28)),
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(82, 171, 245)),
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(111, 210, 129)),
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 111, 111)),
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(190, 102, 229)),
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 170, 56)),
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 212, 232)),
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(166, 132, 111)),
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 194, 177)),
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 116, 72)),
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(150, 214, 72)),
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(122, 142, 232)),
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 200, 52)),
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(49, 196, 229)),
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 126, 168)),
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(186, 224, 112))
    ];

    // -- bar charts --
    private void RefreshAttemptMetricBars()
    {
        BvMetricBars.Clear(); LpcMetricBars.Clear();
        foreach (var a in GetAttemptSummaries(applyTimeFilter: true))
        {
            BvMetricBars.Add(new TopMetricBar(a.Name, "Peak", a.PeakSelectedMetric, a.Status, a.Recipe, a.StatusColor));
            LpcMetricBars.Add(new TopMetricBar(a.Name, "Average", a.AverageSelectedMetric, a.Status, a.Recipe, a.StatusColor));
        }
    }

    private void RefreshDashboardCards()
    {
        DashboardStatCards.Clear();
        var filteredAttempts = GetAttemptSummaries(applyTimeFilter: true);
        var filteredCycles = GetFilteredCycles();
        var selVals = SelectedMetric is null ? [] :
            filteredCycles.Select(c => SelectedMetric.GetValue(c)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        var alarmCount = filteredAttempts.Count(a => a.PeakAlarm > 0);
        var dominant = filteredAttempts.GroupBy(a => a.Recipe).OrderByDescending(g => g.Count())
                                .Select(g => $"{g.First().Recipe}  -  {g.Count()} attempts").FirstOrDefault() ?? "No recipe profile";
        var avgDuration = filteredAttempts.Count == 0 ? 0 : filteredAttempts.Average(a => a.DurationMinutes);

        DashboardStatCards.Add(new DashboardStatCard("Active Attempts", filteredAttempts.Count.ToString(CultureInfo.InvariantCulture), CurrentRangeLabel, "#3A6FAA", $"{filteredCycles.Count} samples in view"));
        DashboardStatCards.Add(new DashboardStatCard(SelectedMetric?.DisplayName ?? "Metric",
            selVals.Count == 0 ? "0" : selVals.Average().ToString("0.##", CultureInfo.InvariantCulture),
            "Avg value", "#3A8A6A",
            selVals.Count == 0 ? "No numeric points" : $"Peak {selVals.Max():0.##} across series"));
        DashboardStatCards.Add(new DashboardStatCard("Alarmed Attempts",
            alarmCount.ToString(CultureInfo.InvariantCulture),
            filteredAttempts.Count == 0 ? "0%" : $"{(alarmCount * 100d / filteredAttempts.Count):0.#}%",
            "#AA4A2A", alarmCount == 0 ? "No alarm spikes" : "Derived from workbook alarm columns"));
        DashboardStatCards.Add(new DashboardStatCard("Cycle Rhythm",
            avgDuration == 0 ? "0 min" : $"{avgDuration:0} min",
            "Avg duration", "#6A3BAA", dominant));
    }

    private void RefreshStatusDistribution()
    {
        AttemptStatusBars.Clear();
        foreach (var g in GetAttemptSummaries(applyTimeFilter: true)
            .GroupBy(a => a.Status).OrderByDescending(g => g.Count()))
        {
            var accent = g.Key switch { "Complete" => "#3BCB78", "Short Run" => "#F4B740", "Standby Only" => "#6A86A8", _ => "#FF6A6A" };
            AttemptStatusBars.Add(new TopMetricBar(g.Key, "Count", g.Count(), g.Key, $"{g.Count()} attempts", accent));
        }
    }

    private void RefreshTopDefects()
    {
        TopDefectBars.Clear();
        foreach (var a in GetAttemptSummaries(applyTimeFilter: true)
            .Where(a => a.PeakAlarm > 0).OrderByDescending(a => a.PeakAlarm).Take(10))
        {
            TopDefectBars.Add(new TopMetricBar(a.Name, "PeakAlarm", a.PeakAlarm, a.Status, a.Recipe, a.StatusColor));
        }
    }

    // -- alert tiles --
    private void RefreshAlertTiles()
    {
        AlertTiles.Clear();
        UpdateMlDetectionStatusForCurrentView();
        AddLatestMlPredictionAlerts();
        // ML-only by design: normal/unscored rows do not create alert cards.
    }

    private void UpdateMlDetectionStatusForCurrentView()
    {
        var predictions = GetVisibleMlPredictionRows();
        if (predictions.Count == 0)
        {
            MlDetectionStatus = "No inference in current view";
            MlDetectionStatusColor = "#777777";
            return;
        }

        var latest = predictions
            .OrderBy(row => row.Timestamp ?? row.InferredAt ?? DateTime.MinValue)
            .ThenBy(row => row.RowIndex ?? -1)
            .Last();
        var timeText = latest.Timestamp?.ToString("HH:mm:ss", CultureInfo.CurrentCulture) ?? "latest";

        if (!latest.IsScored)
        {
            MlDetectionStatus = string.Equals(latest.RiskLevel, "NO_MODEL", StringComparison.OrdinalIgnoreCase)
                ? $"No model - {timeText}"
                : $"Monitoring only - {timeText}";
            MlDetectionStatusColor = string.Equals(latest.RiskLevel, "NO_MODEL", StringComparison.OrdinalIgnoreCase)
                ? "#D68A00"
                : "#777777";
            return;
        }

        MlDetectionStatus = latest.Anomaly
            ? $"Anomaly - {timeText}"
            : $"Normal - {timeText}";
        MlDetectionStatusColor = latest.Anomaly ? "#E53935" : "#32C766";
    }

    private List<AnomalyPredictionRow> GetVisibleMlPredictionRows()
    {
        var predictionRows = (IsOnline ? _onlineMlPredictionRows : _offlineMlPredictionRows).ToList();
        if (predictionRows.Count == 0)
            return predictionRows;

        // -- ONLINE MODE --
        // ML inference runs completely independently of any UI filter (step name,
        // time range, etc.).  We show ALL anomaly alerts whose timestamps fall inside
        // the current live sliding window, regardless of which step names the user
        // has checked in the dropdown.  Filtering the alerts by step name here was
        // the root cause of "inference appears to stop when I change the step filter":
        // no new rows were arriving so the inference queue was idle, and the previous
        // step-key filter was hiding the already-computed results.
        if (IsOnline)
        {
            if (VisibleCycles.Count > 0)
            {
                var windowStart = VisibleCycles.Min(row => row.RecordedAt);
                var windowEnd = VisibleCycles.Max(row => row.RecordedAt);
                predictionRows = predictionRows
                    .Where(row => !row.Timestamp.HasValue
                                  || (row.Timestamp.Value >= windowStart && row.Timestamp.Value <= windowEnd))
                    .ToList();
            }
            return predictionRows;
        }

        // -- OFFLINE MODE --
        if (!_showRepresentations)
            return predictionRows;

        // -- OFFLINE MODE --
        // In offline mode we scope alerts to timestamps that actually appear in the
        // current date/time-range-filtered cycle view so the alert panel stays in
        // sync with the chart.  However, if the filtered view is empty (e.g. the
        // selected date is today but all data is from a historical date) we fall
        // back to matching against the full offline dataset so pre-loaded predictions
        // (e.g. live_predictions.json) are never silently discarded just because the
        // default "today" date filter has no rows.
        var visibleRows = GetFilteredCycles();
        if (visibleRows.Count > 0)
        {
            var windowStart = visibleRows.Min(row => row.RecordedAt);
            var windowEnd = visibleRows.Max(row => row.RecordedAt);
            var windowMatchedRows = predictionRows
                .Where(row => !row.Timestamp.HasValue ||
                              (row.Timestamp.Value >= windowStart && row.Timestamp.Value <= windowEnd))
                .ToList();

            if (windowMatchedRows.Count > 0)
                return windowMatchedRows;

            // Some offline prediction sources carry timestamps that are close to,
            // but not exactly equal to, rendered workbook row timestamps. If the
            // precise window match finds nothing, keep same-date predictions visible
            // so valid ML alerts are not hidden while the chart itself has data.
            if (SelectedChartDate.HasValue)
            {
                var selectedDate = SelectedChartDate.Value.Date;
                var dateMatchedRows = predictionRows
                    .Where(row => !row.Timestamp.HasValue || row.Timestamp.Value.Date == selectedDate)
                    .ToList();
                if (dateMatchedRows.Count > 0)
                    return dateMatchedRows;
            }

            predictionRows = windowMatchedRows;
        }
        else
        {
            if (SelectedChartDate.HasValue)
            {
                var selectedDate = SelectedChartDate.Value.Date;
                predictionRows = predictionRows
                    .Where(row => !row.Timestamp.HasValue || row.Timestamp.Value.Date == selectedDate)
                    .ToList();
            }
        }

        return predictionRows;
    }

    private void AddLatestMlPredictionAlerts()
    {
        var predictionRows = GetVisibleMlPredictionRows();
        if (predictionRows.Count == 0)
            return;

        List<AnomalyPredictionRow> rowsToShow;
        if (IsOnline)
        {
            var latest = predictionRows
                .Where(row => row.IsScored)
                .OrderBy(row => row.Timestamp ?? row.InferredAt ?? DateTime.MinValue)
                .ThenBy(row => row.RowIndex ?? -1)
                .LastOrDefault();

            rowsToShow = latest is { IsScored: true, Anomaly: true }
                ? new List<AnomalyPredictionRow> { latest }
                : new List<AnomalyPredictionRow>();
        }
        else
        {
            rowsToShow = predictionRows
                .Where(row => row.IsScored && row.Anomaly)
                .OrderByDescending(row => row.Timestamp ?? row.InferredAt ?? DateTime.MinValue)
                .ThenByDescending(row => row.RowIndex ?? -1)
                .Take(20)
                .ToList();
        }

        foreach (var row in rowsToShow)
        {
            var stepText = FormatMlStepText(row);
            var cycleText = string.IsNullOrWhiteSpace(row.CycleId) ? string.Empty : row.CycleId;
            var reasons = row.Reasons.Count > 0
                ? row.Reasons.Cast<AnomalyReason?>()
                : new AnomalyReason?[] { null };

            foreach (var reason in reasons)
            {
                var classCode = !string.IsNullOrWhiteSpace(reason?.Category)
                    ? reason.Category.Trim().ToUpperInvariant()
                    : ResolveMlAlertClassCode(row, reason);
                var summary = !string.IsNullOrWhiteSpace(reason?.Detail)
                    ? reason.Detail.Trim()
                    : string.IsNullOrWhiteSpace(row.AnomalySummary)
                        ? BuildConciseMlAlertSummary(row, classCode)
                        : row.AnomalySummary.Trim();
                var sensor = reason?.Sensor ?? ResolvePrimaryMlSensor(row);
                var category = reason?.Category ?? AlertLabelForClassCode(classCode);

                AlertTiles.Add(new EvAlertRow(
                    classCode: classCode,
                    isAnomaly: true,
                    priority: AlertPriorityForClassCode(classCode),
                    summary: summary,
                    timestamp: row.Timestamp,
                    step: stepText,
                    sensor: sensor,
                    value: reason?.CurrentValue,
                    unit: reason?.Unit ?? string.Empty,
                    category: category,
                    detail: reason?.Detail ?? row.AnomalySummary)
                {
                    RiskLabel = row.RiskLevel,
                    Score = row.EnsembleScore,
                    ModelSignals = string.Empty,
                    CycleId = cycleText
                });
            }
        }
    }

    private static string FormatMlStepText(AnomalyPredictionRow row)
    {
        if (row.Step.HasValue)
        {
            var stepName = string.IsNullOrWhiteSpace(row.StepName) ? string.Empty : $" ({row.StepName.Trim()})";
            return $"Step {row.Step.Value}{stepName}";
        }

        return string.IsNullOrWhiteSpace(row.StepName) ? string.Empty : row.StepName.Trim();
    }

    private static string ResolveMlAlertClassCode(AnomalyPredictionRow row, AnomalyReason? primaryReason)
    {
        var text = string.Join(" ", new[]
        {
            row.RiskLevel,
            row.AnomalySummary,
            primaryReason?.Category,
            primaryReason?.Sensor,
            primaryReason?.Detail,
            string.Join(" ", row.Reasons.Select(reason => $"{reason.Category} {reason.Sensor} {reason.Detail}"))
        }.Where(part => !string.IsNullOrWhiteSpace(part))).ToUpperInvariant();

        if (ContainsAny(text, "LOW", "BELOW", "UNDER", "DROP", "DROPPED", "DECREASE", "DECREASED", "FALL"))
            return "LOW";
        if (ContainsAny(text, "TEMP", "TEMPERATURE"))
            return "TEMP";
        if (ContainsAny(text, "HIGH", "ABOVE", "OVER", "SPIKE", "INCREASE", "INCREASED", "EXCEED", "EXCEEDED"))
            return "HIGH";

        return "HIGH";
    }

    private static bool ContainsAny(string text, params string[] tokens)
        => tokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static int AlertPriorityForClassCode(string classCode) => classCode switch
    {
        "HIGH" => 3,
        "TEMP" => 2,
        "LOW" => 1,
        _ => 2
    };

    private static string AlertLabelForClassCode(string classCode) => classCode switch
    {
        "HIGH" => "High",
        "TEMP" => "Temperature",
        "LOW" => "Low",
        _ => "Anomaly"
    };

    private static string BuildConciseMlAlertSummary(AnomalyPredictionRow row, string classCode)
    {
        var reasons = row.Reasons
            .Take(2)
            .Select(FormatConciseAnomalyReason)
            .Where(reason => reason.Length > 0)
            .ToList();

        var reasonText = reasons.Count > 0
            ? string.Join(" | ", reasons)
            : CleanReasonText(row.AnomalySummary);

        return $"{AlertLabelForClassCode(classCode)} anomaly: {reasonText}";
    }

    private static string FormatConciseAnomalyReason(AnomalyReason reason)
    {
        var sensor = string.IsNullOrWhiteSpace(reason.Sensor) ? "signal" : reason.Sensor.Trim();
        var valueText = reason.CurrentValue.HasValue
            ? " = " + reason.CurrentValue.Value.ToString("0.###", CultureInfo.InvariantCulture) + reason.Unit
            : string.Empty;
        var detail = CleanReasonText(reason.Detail);

        if (string.IsNullOrWhiteSpace(detail))
            return sensor + valueText;
        if (detail.StartsWith(sensor, StringComparison.OrdinalIgnoreCase))
            return detail;

        return $"{sensor}{valueText}: {detail}";
    }

    private static string CleanReasonText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "anomaly detected";

        var cleaned = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        foreach (var prefix in new[] { "Anomaly detected:", "Anomaly:" })
        {
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[prefix.Length..].Trim();
                break;
            }
        }

        while (cleaned.Contains("  ", StringComparison.Ordinal))
            cleaned = cleaned.Replace("  ", " ");

        const int maxLength = 220;
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength].TrimEnd() + "...";
    }

    private static string ResolvePrimaryMlSensor(AnomalyPredictionRow row)
    {
        if (row.Temperature100.HasValue) return "STR34_T100";
        if (row.Temperature150.HasValue) return "STR34_T150";
        if (row.Pressure101P102.HasValue) return "STR34_P101_P102";
        if (row.F0Value.HasValue) return "STR34_F0_1";
        return "ML inference";
    }

    private static bool IsImpossibleSensorValue(MetricOption metric, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return true;
        var family = GetSensorFamily(metric.PropertyName);
        if (family == SensorFamily.Temperature) return value < -50d || value > 160d;
        if (family == SensorFamily.Pressure) return value < -5d || value > 100d;
        return Math.Abs(value) > 500d;
    }
    private static string FormatProbability(double? value)
        => value.HasValue ? (value.Value * 100d).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "n/a";

    private void RefreshSummaryCollections()
    {
        CycleRuns.Clear(); CycleAttempts.Clear();
        foreach (var a in GetAttemptSummaries())
        {
            CycleRuns.Add(new CycleRunCard(
                a.Name, a.LeadText,
                $"{a.Start:dd MMM yyyy HH:mm} - {a.End:dd MMM yyyy HH:mm}",
                a.SampleCount, a.StatusColor));

            string statusIcon = "OK";
            if (a.Status == "Complete") statusIcon = "DONE";
            else if (a.PeakAlarm > 0) statusIcon = "ALERT";
            else if (a.Status == "Short Run") statusIcon = "WARN";

            CycleAttempts.Add(new CycleAttemptRow(
                a.Name, a.Recipe, a.Date,
                a.Start.ToString("HH:mm"), a.End.ToString("HH:mm"),
                a.DurationMinutes, Math.Round(a.PeakAlarm, 0),
                string.IsNullOrWhiteSpace(a.PreIdleSteps) ? "-" : a.PreIdleSteps,
                a.RecipeLoadStep, a.Status, statusIcon));
        }
    }

    // -- computed properties --
    public string SummaryHeaderTitle => $"Cycle Intelligence - {GetAttemptSummaries().Count} Attempts";
    public int GoodCycleCount => GetAttemptSummaries().Count(a => a.Status == "Complete");
    public double AverageGoodDurationMinutes
    {
        get
        {
            var d = GetAttemptSummaries().Where(a => a.Status == "Complete").Select(a => (double)a.DurationMinutes).ToList();
            return d.Count == 0 ? 0 : Math.Round(d.Average(), 0);
        }
    }
    public int FailedAttemptCount => GetAttemptSummaries().Count(a => a.Status != "Complete");
    public double PeakFailedDurationMinutes
    {
        get
        {
            var d = GetAttemptSummaries().Where(a => a.Status != "Complete").Select(a => (double)a.DurationMinutes).ToList();
            return d.Count == 0 ? 0 : d.Max();
        }
    }
    public string PeakFailedDurationLabel => $"{PeakFailedDurationMinutes} min";
    public string PeakFailedAttemptName
    {
        get
        {
            return GetAttemptSummaries().Where(a => a.Status != "Complete")
                .OrderByDescending(a => a.DurationMinutes).FirstOrDefault()?.Name ?? "Failed";
        }
    }
    public string PeakFailedAttemptLabel => $"{PeakFailedAttemptName} Peak Duration";
    public string AverageGoodDurationLabel => $"{AverageGoodDurationMinutes} min";

    public string SummaryFooterInsight1 => $"Good cycles average {AverageGoodDurationMinutes} min with zero alarms.";
    public string SummaryFooterInsight2
    {
        get
        {
            var peak = GetAttemptSummaries().Where(a => a.Status != "Complete")
                        .OrderByDescending(a => a.DurationMinutes).FirstOrDefault();
            if (peak == null || AverageGoodDurationMinutes == 0) return string.Empty;
            var ratio = Math.Round(peak.DurationMinutes / AverageGoodDurationMinutes, 1);
            return $"{peak.Name} ran {ratio:0.#}x longer due to overrun and accumulated {peak.PeakAlarm} critical alarms.";
        }
    }
    public string SummaryFooterInsight3
    {
        get
        {
            var standby = GetAttemptSummaries().Where(a => a.Status == "Standby Only" || a.SampleCount < 3).ToList();
            if (standby.Count == 0) return string.Empty;
            return $"{standby.First().Name} never progressed past idle - no active steps detected.";
        }
    }

    public string ImportedWorkbookName => VisibleCycles.Select(c => c.SourceWorkbookName).FirstOrDefault() ?? "No workbook imported";
    public int VisibleCycleCount => VisibleCycles.Count;
    public int DistinctRecipeCount => GetAttemptSummaries().Select(a => a.Recipe).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public int DistinctStepCount => HeaderCatalog.Count;
    public string DominantRecipeSummary => GetAttemptSummaries().GroupBy(a => a.Recipe).OrderByDescending(g => g.Count())
        .Select(g => $"{g.Key} leads with {g.Count()} attempt(s).").FirstOrDefault() ?? "Recipe profile visible after import.";
    public string LiveAlertSummary => AlertTiles.FirstOrDefault()?.SuggestedAction ?? "No live alert summary.";
    public string VisibleDateRangeSummary
    {
        get
        {
            if (VisibleCycles.Count == 0) return "No rows loaded";
            return $"{VisibleCycles.Min(c => c.RecordedAt):dd MMM yyyy HH:mm}  ->  {VisibleCycles.Max(c => c.RecordedAt):dd MMM yyyy HH:mm}";
        }
    }
    public string SummaryInsight
    {
        get
        {
            var a = GetAttemptSummaries();
            if (a.Count == 0) return "Import a workbook to build cycle-level summary data.";
            var failed = a.Where(x => x.Status != "Complete").ToList();
            var s = $"Dynamic attempt boundaries derived from imported step data - {DistinctRecipeCount} recipe families detected.";
            if (failed.Count > 0) s += $"  {failed.Count} attempt(s) flagged for review.";
            return s;
        }
    }

    public string TrendChartTitle => SelectedRepresentation?.DisplayName switch
    {
        "Good vs Failed Min/Max" => "Temperature sensor min/max: good vs failed workbook cycles",
        "Cycles Info" => "Cycle timing, duration, and process-state flow",
        "Cycle Duration Analytics" => "Cycle duration analytics across imported attempts",
        "Temperature Sensor Profile" => "Good vs failed temperature trend profile",
        "Pressure Sensor Profile" => "Good vs failed pressure trend profile",
        "F0 Score & Exposure" => "F0 lethality comparison across cycle attempts",
        "Chamber Level & Conductivity" => "Level and conductivity summary by cycle attempt",
        "Recipe Loading & Steps" => "Recipe loading pattern and step progression summary",
        "Live Sensor Timeline" => SelectedMetric is null ? "Live sensor timeline" : $"Live {SelectedMetric.DisplayName} series",
        _ => "Live sterilization data"
    };
    public string PeakBarChartTitle => $"Peak {SelectedMetric?.DisplayName ?? "Metric"} by attempt";
    public string AverageBarChartTitle => $"Average {SelectedMetric?.DisplayName ?? "Metric"} by attempt";
    public string TopDefectChartTitle => "Highest-Risk Attempts (by Alarm)";
    public string StatusChartTitle => "Attempt health mix";

    public string CurrentRangeLabel => TimeRangeOptions.FirstOrDefault(o => o.IsSelected)?.Label ?? "All";
    public int FilteredCycleCount => GetFilteredCycles().Count;
    public string ActiveWindowSummary => VisibleCycles.Count == 0
        ? "No data loaded. Import workbook."
        : $"Showing {GetAttemptSummaries(applyTimeFilter: true).Count} attempts  -  {FilteredCycleCount} samples  -  {HeaderCatalog.Count} headers.";
    public string ActiveSeriesSummary => ActiveWindowSummary;

    public string SelectedMetricsSummary
    {
        get
        {
            if (ComparisonMetricOptions.Any(option => option.IsSelectAll && option.IsSelected))
                return "All sensor headers";

            var parts = new List<string>();
            if (SelectedMetric is not null) parts.Add(SelectedMetric.DisplayName);
            parts.AddRange(ComparisonMetricOptions.Where(o => o.IsSelected && !o.IsSelectAll).Select(o => o.DisplayName)
                .Where(n => !parts.Contains(n, StringComparer.OrdinalIgnoreCase)));
            return parts.Count == 0 ? "Select elements" : string.Join(", ", parts);
        }
    }

    private enum SensorFamily { Other, Temperature, Pressure, Lethality, Level, Flow }

    private List<MetricOption> GetActiveMetrics()
    {
        var selected = ComparisonMetricOptions
            .Where(o => o.IsSelected && !o.IsSelectAll && o.Metric is not null)
            .Select(o => o.Metric!)
            .ToList();

        if (selected.Count > 0)
            return selected;

        // - FIX: In online mode with no explicit selection, auto-pick first 8
        // sensor metrics regardless of whether the user is online or offline.
        // The old `|| !IsOnline` guard meant offline with no selection returned
        // an empty list instead of falling through to the auto-pick below.
        if (IsOnline || MetricOptions.Any())
        {
            return MetricOptions
                .Where(metric => GetSensorFamily(metric.PropertyName) is not SensorFamily.Other)
                .Take(8)
                .ToList();
        }

        return selected; // empty — offline with no headers loaded yet
    }

    // -- time range selection --
    private void BuildTimeRanges()
    {
        TimeRangeOptions.Clear();
        TimeRangeOptions.Add(new TimeRangeOption("5m", TimeSpan.FromMinutes(5), isSelected: true));
        TimeRangeOptions.Add(new TimeRangeOption("10m", TimeSpan.FromMinutes(10)));
        TimeRangeOptions.Add(new TimeRangeOption("30m", TimeSpan.FromMinutes(30)));
        TimeRangeOptions.Add(new TimeRangeOption("60m", TimeSpan.FromHours(1)));
        TimeRangeOptions.Add(new TimeRangeOption("3h", TimeSpan.FromHours(3)));
        TimeRangeOptions.Add(new TimeRangeOption("6h", TimeSpan.FromHours(6)));
        TimeRangeOptions.Add(new TimeRangeOption("12h", TimeSpan.FromHours(12)));
        TimeRangeOptions.Add(new TimeRangeOption("24h", TimeSpan.FromHours(24)));
    }

    private void SelectTimeRange(TimeRangeOption? option)
    {
        if (option is null) return;
        foreach (var r in TimeRangeOptions) r.IsSelected = ReferenceEquals(r, option);
        RefreshAllVisuals();
    }

    // -- export --
    private void ExportCsv()
    {
        if (VisibleCycles.Count == 0) { LastActionMessage = "No data to export."; return; }
        var path = new ExportService().ExportCsv(_exportDirectory, VisibleCycles);
        LastActionMessage = $"CSV exported -> {path}";
    }
    private void ExportJson()
    {
        if (VisibleCycles.Count == 0) { LastActionMessage = "No data to export."; return; }
        var path = new ExportService().ExportJson(_exportDirectory, VisibleCycles);
        LastActionMessage = $"JSON exported -> {path}";
    }

    // -- metric options --
    private void BuildMetricOptions()
    {
        var prevKey = _selectedMetric?.PropertyName;
        var hadMetricOptions = ComparisonMetricOptions.Any(option => !option.IsSelectAll);
        var selectAllWasEnabled = ComparisonMetricOptions.FirstOrDefault(option => option.IsSelectAll)?.IsSelected == true;
        var selComps = ComparisonMetricOptions.Where(o => o.IsSelected && !o.IsSelectAll).Select(o => o.PropertyName)
                           .ToHashSet(StringComparer.OrdinalIgnoreCase);
        MetricOptions.Clear(); ComparisonMetricOptions.Clear();
        SensorMetricGroups.Clear();

        ComparisonMetricOptions.Add(new MetricSelectionOption(
            metric: null,
            displayName: "All sensor headers",
            onChanged: ApplyMetricSelectionChange,
            canApply: AllowAll,
            isSelectAll: true));

        foreach (var header in HeaderCatalog.Where(h => h.IsNumeric && IsSensorHeader(h)))
        {
            var metric = new MetricOption(header);
            MetricOptions.Add(metric);
            var comparison = new MetricSelectionOption(metric, metric.DisplayName, ApplyMetricSelectionChange, AllowAll);
            comparison.SetSelectedSilently(selectAllWasEnabled || selComps.Contains(metric.PropertyName) || !hadMetricOptions);
            ComparisonMetricOptions.Add(comparison);
        }

        SyncSelectAllMetricOption();
        RebuildSensorMetricGroups();

        var chosen = MetricOptions.FirstOrDefault(o => o.PropertyName == prevKey);
        if (chosen is null)
            chosen = ComparisonMetricOptions.FirstOrDefault(option => option.IsSelected && option.Metric is not null)?.Metric;
        if (!ReferenceEquals(_selectedMetric, chosen)) _selectedMetric = chosen;
    }

    private static bool AllowAll(MetricSelectionOption _, bool __) => true;

    private void ApplyMetricSelectionChange(MetricSelectionOption changedOption, bool isSelected)
    {
        if (changedOption.IsSelectAll)
        {
            foreach (var option in ComparisonMetricOptions.Where(option => !option.IsSelectAll))
            {
                option.SetSelectedSilently(isSelected);
            }
        }

        SyncSelectAllMetricOption();
        foreach (var group in SensorMetricGroups)
            group.NotifySelectionChanged();

        var selectedMetric = ComparisonMetricOptions
            .Where(option => option.IsSelected && !option.IsSelectAll && option.Metric is not null)
            .Select(option => option.Metric)
            .FirstOrDefault();
        if (!ReferenceEquals(_selectedMetric, selectedMetric))
        {
            _selectedMetric = selectedMetric;
            OnPropertyChanged(nameof(SelectedMetric));
        }

        RefreshAllVisuals();
    }

    private void RebuildSensorMetricGroups()
    {
        SensorMetricGroups.Clear();

        foreach (var group in ComparisonMetricOptions
            .Where(option => !option.IsSelectAll && option.Metric is not null)
            .GroupBy(option => GetSensorFamily(option.PropertyName))
            .Where(group => group.Key is not SensorFamily.Other)
            .OrderBy(group => SensorFamilySort(group.Key)))
        {
            SensorMetricGroups.Add(new SensorMetricGroup(
                SensorFamilyDisplayName(group.Key),
                SensorFamilyDescription(group.Key),
                group.OrderBy(option => option.DisplayName).ToList()));
        }

        OnPropertyChanged(nameof(SensorMetricGroups));
    }

    private static int SensorFamilySort(SensorFamily family) => family switch
    {
        SensorFamily.Temperature => 0,
        SensorFamily.Pressure => 1,
        SensorFamily.Lethality => 2,
        SensorFamily.Level => 3,
        SensorFamily.Flow => 4,
        _ => 99
    };

    private static string SensorFamilyDisplayName(SensorFamily family) => family switch
    {
        SensorFamily.Temperature => "Temperature",
        SensorFamily.Pressure => "Pressure",
        SensorFamily.Lethality => "F0 / Lethality",
        SensorFamily.Level => "Level",
        SensorFamily.Flow => "Conductivity / Flow",
        _ => "Other"
    };

    private static string SensorFamilyDescription(SensorFamily family) => family switch
    {
        SensorFamily.Temperature => "Temperature sensors",
        SensorFamily.Pressure => "Pressure sensors",
        SensorFamily.Lethality => "F0 and exposure values",
        SensorFamily.Level => "Level transmitters",
        SensorFamily.Flow => "Conductivity/flow values",
        _ => "Sensor values"
    };

    private void SyncSelectAllMetricOption()
    {
        var selectAllOption = ComparisonMetricOptions.FirstOrDefault(option => option.IsSelectAll);
        if (selectAllOption is null)
            return;

        var selectableOptions = ComparisonMetricOptions.Where(option => !option.IsSelectAll).ToList();
        var areAllSelected = selectableOptions.Count > 0 && selectableOptions.All(option => option.IsSelected);
        selectAllOption.SetSelectedSilently(areAllSelected);
    }

    // -- drill-down --
    private bool _isDrillDownOpen;
    public bool IsDrillDownOpen
    {
        get => _isDrillDownOpen;
        set => SetProperty(ref _isDrillDownOpen, value);
    }

    private string _drillDownTitle = "";
    public string DrillDownTitle
    {
        get => _drillDownTitle;
        set => SetProperty(ref _drillDownTitle, value);
    }

    private string _drillDownSubtitle = "";
    public string DrillDownSubtitle
    {
        get => _drillDownSubtitle;
        set => SetProperty(ref _drillDownSubtitle, value);
    }

    public ObservableCollection<DashboardStatCard> DrillDownStatRows { get; } = new();

    public SeriesCollection DrillDownSeries { get; } = new();
    public string[] DrillDownXLabels { get; protected set; } = Array.Empty<string>();
    public Func<double, string> DrillDownXFormatter { get; } = value => value.ToString("0.##");

    public ICommand CloseDrillDownCommand { get; protected set; } = null!;

    public void OnChartDataClick(ChartPoint chartPoint)
    {
        if (string.Equals(SelectedRepresentation?.Key, "timeline", StringComparison.OrdinalIgnoreCase) && IsBarChartRepresentation)
        {
            var metricTitle = chartPoint.SeriesView?.Title;
            if (!string.IsNullOrWhiteSpace(metricTitle))
            {
                OpenTimelineMetricDrillDown(metricTitle, chartPoint.Y);
                return;
            }
        }

        if (chartPoint.Instance is MetricPoint metricPoint
            && !string.IsNullOrWhiteSpace(metricPoint.AttemptName)
            && HasAttemptDrillDown(metricPoint.AttemptName))
        {
            OpenDrillDownForAttempt(metricPoint.AttemptName, chartPoint.SeriesView?.Title, metricPoint);
            return;
        }

        var pointIndex = (int)Math.Round(chartPoint.X);

        if (XLabels != null && pointIndex >= 0 && pointIndex < XLabels.Length)
        {
            var barAttemptName = XLabels[pointIndex];
            if (AllAttemptSummaries.Any(a => string.Equals(a.Name, barAttemptName, StringComparison.OrdinalIgnoreCase)))
            {
                OpenDrillDownForAttempt(barAttemptName, chartPoint.SeriesView?.Title);
                return;
            }
        }

        if (chartPoint.SeriesView?.Values is not null)
        {
            var clickedPoint = chartPoint.SeriesView.Values
                .OfType<MetricPoint>()
                .FirstOrDefault(value => (int)Math.Round(value.Index) == pointIndex);
            if (clickedPoint is not null
                && !string.IsNullOrWhiteSpace(clickedPoint.AttemptName)
                && HasAttemptDrillDown(clickedPoint.AttemptName))
            {
                OpenDrillDownForAttempt(clickedPoint.AttemptName, chartPoint.SeriesView.Title, clickedPoint);
                return;
            }
        }

        if (XLabels != null && chartPoint.Key >= 0 && chartPoint.Key < XLabels.Length)
        {
            var labelAttemptName = XLabels[(int)chartPoint.Key];
            if (AllAttemptSummaries.Any(a => string.Equals(a.Name, labelAttemptName, StringComparison.OrdinalIgnoreCase)))
            {
                OpenDrillDownForAttempt(labelAttemptName, chartPoint.SeriesView?.Title);
                return;
            }
        }

        OpenGenericPointDrillDown(chartPoint);
    }

    private bool HasAttemptDrillDown(string attemptName)
        => AllAttemptSummaries.Any(a => string.Equals(a.Name, attemptName, StringComparison.OrdinalIgnoreCase));

    private MetricOption? ResolveMetricByDisplayOrCode(string title)
    {
        return MetricOptions.FirstOrDefault(metric =>
            string.Equals(metric.DisplayName, title, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(GetSensorCode(metric), title, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(GetSensorCode(metric.PropertyName), title, StringComparison.OrdinalIgnoreCase));
    }

    private void OpenTimelineMetricDrillDown(string metricTitle, double selectedValue)
    {
        var metric = ResolveMetricByDisplayOrCode(metricTitle);
        if (metric is null)
        {
            return;
        }

        var rows = GetFilteredCycles().OrderBy(row => row.RecordedAt).ToList();
        var metricRows = rows
            .Select(row => new { Row = row, Value = metric.GetValue(row) })
            .Where(item => item.Value.HasValue && !double.IsNaN(item.Value.Value))
            .Where(item => !IsImpossibleSensorValue(metric, item.Value!.Value))
            .ToList();

        if (metricRows.Count == 0)
        {
            return;
        }

        DrillDownTitle = $"{GetSensorCode(metric)} detailed trace";
        DrillDownSubtitle = $"{SelectedRepresentation?.DisplayName ?? "Representation"} on {(SelectedChartDate?.ToString("dd-MM-yyyy") ?? "selected date")}";
        DrillDownPointSummary = $"Selected bar value = {selectedValue:0.##}. The chart below shows every workbook row contributing to that sensor summary.";
        DrillDownYAxisTitle = metric.DisplayName;
        DrillDownXAxisTitle = "Time";

        var values = new ChartValues<MetricPoint>();
        for (var index = 0; index < metricRows.Count; index++)
        {
            values.Add(new MetricPoint(
                index,
                metricRows[index].Row.RecordedAt,
                Math.Round(metricRows[index].Value!.Value, 2),
                metric.DisplayName,
                RowIdToAttemptName.TryGetValue(metricRows[index].Row.Id, out var attemptName) ? attemptName : string.Empty,
                "Timeline",
                ResolveRecipeName([metricRows[index].Row])));
        }

        DrillDownSeries.Clear();
        var mapper = LiveCharts.Configurations.Mappers.Xy<MetricPoint>().X(point => point.Index).Y(point => point.Value);
        DrillDownSeries.Add(new LineSeries
        {
            Title = metric.DisplayName,
            Values = values,
            Configuration = mapper,
            PointGeometrySize = 7,
            StrokeThickness = 2.4,
            LineSmoothness = 0,
            Stroke = GetPalette()[0],
            Fill = System.Windows.Media.Brushes.Transparent
        });

        DrillDownXLabels = metricRows.Select(item => item.Row.RecordedAt.ToString("HH:mm:ss")).ToArray();
        OnPropertyChanged(nameof(DrillDownXLabels));

        var numericValues = metricRows.Select(item => item.Value!.Value).ToList();
        var cycleNames = metricRows
            .Select(item => RowIdToAttemptName.TryGetValue(item.Row.Id, out var attemptName) ? attemptName : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        DrillDownStatRows.Clear();
        DrillDownStatRows.Add(new DashboardStatCard("Sensor", metric.DisplayName, "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Average", numericValues.Average().ToString("0.##", CultureInfo.InvariantCulture), "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Minimum", numericValues.Min().ToString("0.##", CultureInfo.InvariantCulture), "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Maximum", numericValues.Max().ToString("0.##", CultureInfo.InvariantCulture), "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Rows", metricRows.Count.ToString(CultureInfo.InvariantCulture), "#CCCCCC", "", ""));
        if (cycleNames.Count > 0)
        {
            DrillDownStatRows.Add(new DashboardStatCard("Cycles", string.Join(", ", cycleNames), "#CCCCCC", "", ""));
        }

        IsDrillDownOpen = true;
    }

    private void OpenDrillDownForAttempt(string attemptName, string? metricName = null, MetricPoint? clickedPoint = null)
    {
        var attempt = AllAttemptSummaries.FirstOrDefault(a => a.Name == attemptName);
        if (attempt == null) return;

        DrillDownTitle = attempt.Name;
        DrillDownPointSummary = clickedPoint is null
            ? "Showing the full workbook attempt trace."
            : $"Selected point from workbook: X = {clickedPoint.Timestamp:dd-MM-yyyy HH:mm:ss}, Y = {clickedPoint.Value:0.##}, Metric = {clickedPoint.MetricName}";
        DrillDownSubtitle = $"{attempt.Date}  -  {attempt.DurationMinutes} min  -  {attempt.Status}";
        DrillDownXAxisTitle = "Elapsed Time (Minutes)";

        var metricsToPlot = new List<MetricOption>();

        var matchedSensor = MetricOptions.FirstOrDefault(m => m.DisplayName == metricName || m.DisplayName == metricName?.Replace(" avg", "")?.Replace(" peak", ""));

        double peakAlert = attempt.PeakAlarm;
        double peakMetric = attempt.PeakSelectedMetric;
        double avgMetric = attempt.AverageSelectedMetric;

        if (matchedSensor != null)
        {
            var validVals = attempt.Rows.Select(matchedSensor.GetValue).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (validVals.Count > 0)
            {
                peakMetric = validVals.Max();
                avgMetric = validVals.Average();
            }
        }

        DrillDownStatRows.Clear();
        if (clickedPoint is not null)
        {
            var selectedStepName = ResolveClickedPointStepName(attempt, clickedPoint);
            DrillDownStatRows.Add(new DashboardStatCard("Selected X", clickedPoint.Timestamp.ToString("dd-MM-yyyy HH:mm:ss"), "#CCCCCC", "", ""));
            DrillDownStatRows.Add(new DashboardStatCard("Selected Y", clickedPoint.Value.ToString("0.##"), "#CCCCCC", "", ""));
            DrillDownStatRows.Add(new DashboardStatCard("Selected Metric", clickedPoint.MetricName, "#CCCCCC", "", ""));
            DrillDownStatRows.Add(new DashboardStatCard("Step Name", selectedStepName, "#CCCCCC", "", ""));
        }
        DrillDownStatRows.Add(new DashboardStatCard("Status", attempt.Status, attempt.StatusColor, "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Duration", $"{attempt.DurationMinutes} min", "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Highest Step", attempt.MaxStep.ToString(), "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Pre-idle Steps", attempt.PreIdleSteps, "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Recipe Load Step", attempt.RecipeLoadStep, "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Avg Metric", avgMetric.ToString("0.##"), "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Peak Metric", peakMetric.ToString("0.##"), "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Peak Alarm", peakAlert.ToString(), "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Recipe", attempt.Recipe, "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Samples", attempt.SampleCount.ToString(), "#CCCCCC", "", ""));

        DrillDownYAxisTitle = "Sensor Values";

        if (IsStepMetricName(metricName) && StepHeaderKey != null)
        {
            metricsToPlot.Add(MetricOptions.FirstOrDefault(m => m.PropertyName == StepHeaderKey)!);
            DrillDownYAxisTitle = "Process Step";
        }
        else if (metricName == "PeakAlarm" && AlarmHeaderKey != null)
        {
            metricsToPlot.Add(MetricOptions.FirstOrDefault(m => m.PropertyName == AlarmHeaderKey)!);
            DrillDownYAxisTitle = "Alarm State";
        }
        else
        {
            if (matchedSensor != null)
            {
                metricsToPlot.Add(matchedSensor);
            }
            else
            {
                var selected = ComparisonMetricOptions
                    .Where(o => o.IsSelected && !o.IsSelectAll && o.Metric is not null)
                    .Select(o => o.Metric!)
                    .ToList();
                if (selected.Count > 0)
                    metricsToPlot.AddRange(selected);
                else
                {
                    metricsToPlot.AddRange(GetTemperatureMetrics().Take(3));
                    metricsToPlot.AddRange(GetPressureMetrics().Take(3));
                }
            }
        }

        DrillDownSeries.Clear();
        var mapper = LiveCharts.Configurations.Mappers.Xy<MetricPoint>().X(p => p.Index).Y(p => p.Value);
        var palette = GetPalette();
        int colorIdx = 0;

        foreach (var m in metricsToPlot.Where(m => m != null))
        {
            var values = new ChartValues<MetricPoint>();
            foreach (var row in attempt.Rows.OrderBy(r => r.RecordedAt))
            {
                var val = m.GetValue(row);
                if (val.HasValue)
                {
                    var minute = Math.Max(0, (int)Math.Round((row.RecordedAt - attempt.Start).TotalMinutes, MidpointRounding.AwayFromZero));
                    values.Add(new MetricPoint(minute, row.RecordedAt, Math.Round(val.Value, 2), m.DisplayName, attempt.Name, attempt.Status, attempt.Recipe));
                }
            }
            if (values.Count > 0)
            {
                DrillDownSeries.Add(new LineSeries
                {
                    Title = m.DisplayName,
                    Values = values,
                    Configuration = mapper,
                    PointGeometrySize = 0,
                    StrokeThickness = 2,
                    LineSmoothness = 0,
                    Stroke = palette[colorIdx % palette.Length],
                    Fill = System.Windows.Media.Brushes.Transparent
                });
                colorIdx++;
            }
        }

        var maxMinute = attempt.Rows.Count > 0 ? GetElapsedMinutes(attempt, attempt.Rows.Last()) : 0;
        DrillDownXLabels = Enumerable.Range(0, maxMinute + 1).Select(m => m.ToString()).ToArray();
        OnPropertyChanged(nameof(DrillDownXLabels));

        IsDrillDownOpen = true;
    }

    private void OpenEnvelopeSensorDrillDown(string sensorCode, string? seriesTitle)
    {
        var metric = GetTemperatureMetrics()
            .FirstOrDefault(option => string.Equals(GetSensorCode(option), sensorCode, StringComparison.OrdinalIgnoreCase));
        if (metric is null)
        {
            return;
        }

        var goodAttempts = GetBaselineAttemptsForAnalysis();
        var failedAttempts = GetReviewAttemptsForAnalysis();
        var phases = GetOrderedProcessPhases(goodAttempts.Concat(failedAttempts)).ToList();
        if (phases.Count == 0)
        {
            return;
        }

        DrillDownTitle = $"{metric.DisplayName} process profile";
        DrillDownSubtitle = $"Workbook process stages for {seriesTitle ?? sensorCode}";
        DrillDownPointSummary = $"X = process stage, Y = average {metric.DisplayName} value from workbook rows.";
        DrillDownYAxisTitle = metric.DisplayName;

        DrillDownStatRows.Clear();
        var goodValues = goodAttempts.SelectMany(a => a.Rows).Select(metric.GetValue).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        var failedValues = failedAttempts.SelectMany(a => a.Rows).Select(metric.GetValue).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        DrillDownStatRows.Add(new DashboardStatCard("Sensor", metric.DisplayName, "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Good Min/Max", goodValues.Count == 0 ? "-" : $"{goodValues.Min():0.##} / {goodValues.Max():0.##}", "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Failed Min/Max", failedValues.Count == 0 ? "-" : $"{failedValues.Min():0.##} / {failedValues.Max():0.##}", "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Stages", phases.Count.ToString(CultureInfo.InvariantCulture), "#CCCCCC", "", ""));

        DrillDownSeries.Clear();
        var mapper = LiveCharts.Configurations.Mappers.Xy<MetricPoint>().X(p => p.Index).Y(p => p.Value);
        var palette = GetPalette();
        var goodSeries = BuildProcessPhaseSeries(metric, goodAttempts, phases, "Good cycles");
        var failedSeries = BuildProcessPhaseSeries(metric, failedAttempts, phases, "Failed cycles");

        if (goodSeries.Count > 0)
        {
            DrillDownSeries.Add(new LineSeries
            {
                Title = "Good cycles",
                Values = goodSeries,
                Configuration = mapper,
                PointGeometrySize = 8,
                StrokeThickness = 2,
                LineSmoothness = 0,
                Stroke = palette[2],
                Fill = System.Windows.Media.Brushes.Transparent
            });
        }

        if (failedSeries.Count > 0)
        {
            DrillDownSeries.Add(new LineSeries
            {
                Title = "Failed cycles",
                Values = failedSeries,
                Configuration = mapper,
                PointGeometrySize = 8,
                StrokeThickness = 2,
                LineSmoothness = 0,
                Stroke = palette[3],
                Fill = System.Windows.Media.Brushes.Transparent
            });
        }

        DrillDownXLabels = phases.ToArray();
        OnPropertyChanged(nameof(DrillDownXLabels));
        IsDrillDownOpen = true;
    }

    private void OpenGenericPointDrillDown(ChartPoint chartPoint)
    {
        var pointIndex = Math.Max(0, (int)Math.Round(chartPoint.X));
        var label = XLabels != null && pointIndex >= 0 && pointIndex < XLabels.Length
            ? XLabels[pointIndex]
            : chartPoint.X.ToString("0.##", CultureInfo.InvariantCulture);
        var seriesTitle = chartPoint.SeriesView?.Title ?? "Selected series";
        var pointValue = chartPoint.Y;

        DrillDownTitle = string.IsNullOrWhiteSpace(seriesTitle)
            ? (SelectedRepresentation?.DisplayName ?? "Point details")
            : seriesTitle;
        DrillDownSubtitle = $"{SelectedRepresentation?.DisplayName ?? "Representation"} on {(SelectedChartDate?.ToString("dd-MM-yyyy") ?? "selected date")}";
        DrillDownPointSummary = $"Selected point from workbook representation: X = {label}, Y = {pointValue:0.##}, Series = {seriesTitle}";
        DrillDownYAxisTitle = "Workbook value";
        DrillDownXAxisTitle = AllAttemptSummaries.Any(a => string.Equals(a.Name, label, StringComparison.OrdinalIgnoreCase))
            ? "Cycle Attempts"
            : "Time";

        DrillDownStatRows.Clear();
        DrillDownStatRows.Add(new DashboardStatCard("Representation", SelectedRepresentation?.DisplayName ?? "-", "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Selected X", label, "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Selected Y", pointValue.ToString("0.##", CultureInfo.InvariantCulture), "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Series", seriesTitle, "#CCCCCC", "", ""));
        DrillDownStatRows.Add(new DashboardStatCard("Date", SelectedChartDate?.ToString("dd-MM-yyyy") ?? "-", "#CCCCCC", "", ""));
        if (!string.Equals(SelectedRepresentation?.Key, "timeline", StringComparison.OrdinalIgnoreCase))
        {
            AppendBucketWorkbookDetails(GetCurrentComparisonBucket(pointIndex), seriesTitle, label, pointValue);
        }
        else
        {
            if (IsBarChartRepresentation)
            {
                AppendWorkbookRowDetails(new List<SterilizationCycle>(), seriesTitle, label, pointValue);
            }
            else
            {
                var sampled = GetSampledTimelineCycles();
                var rows = (pointIndex >= 0 && pointIndex < sampled.Count)
                    ? new List<SterilizationCycle> { sampled[pointIndex] }
                    : new List<SterilizationCycle>();
                AppendWorkbookRowDetails(rows, seriesTitle, label, pointValue);
            }
        }

        DrillDownSeries.Clear();
        DrillDownXLabels = Array.Empty<string>();
        OnPropertyChanged(nameof(DrillDownXLabels));

        var palette = GetPalette();
        var categoryLabels = new List<string>();
        var categoryValues = new ChartValues<double>();
        var colorIndex = 0;

        foreach (var series in MainChartSeries)
        {
            if (series.Values is null || pointIndex >= series.Values.Count)
            {
                continue;
            }

            var rawValue = series.Values[pointIndex];
            var numericValue = TryGetChartValue(rawValue);
            if (!numericValue.HasValue)
            {
                continue;
            }

            categoryLabels.Add(series.Title ?? $"Series {categoryLabels.Count + 1}");
            categoryValues.Add(Math.Round(numericValue.Value, 2));
        }

        if (categoryValues.Count > 0)
        {
            DrillDownTitle = $"{label} snapshot";
            DrillDownSubtitle = $"Series values at {label}";
            DrillDownPointSummary = $"X = {label}. Each bar shows the exact workbook-backed Y value for one visible series at that selected point.";
            DrillDownYAxisTitle = "Workbook value";
            AddColorPerCategoryBarSeries(DrillDownSeries, categoryLabels, categoryValues.ToList());
            DrillDownXLabels = categoryLabels.ToArray();
            OnPropertyChanged(nameof(DrillDownXLabels));
            IsDrillDownOpen = true;
            return;
        }

        var mapper = Mappers.Xy<MetricPoint>().X(p => p.Index).Y(p => p.Value);
        var singlePointSeries = new ChartValues<MetricPoint>
        {
            new MetricPoint(0, SelectedChartDate?.Date ?? DateTime.MinValue, Math.Round(pointValue, 2), seriesTitle, string.Empty, string.Empty, string.Empty)
        };

        DrillDownSeries.Add(new LineSeries
        {
            Title = seriesTitle,
            Values = singlePointSeries,
            Configuration = mapper,
            LabelPoint = TooltipLabelPoint,
            Stroke = palette[colorIndex % palette.Length],
            Fill = System.Windows.Media.Brushes.Transparent,
            PointGeometrySize = 12,
            StrokeThickness = 2,
            LineSmoothness = 0
        });
        DrillDownXLabels = new[] { label };
        OnPropertyChanged(nameof(DrillDownXLabels));
        IsDrillDownOpen = true;
    }

    private static double? TryGetChartValue(object? rawValue)
    {
        return rawValue switch
        {
            null => null,
            double value when !double.IsNaN(value) && !double.IsInfinity(value) => value,
            int value => value,
            decimal value => (double)value,
            MetricPoint metricPoint when !double.IsNaN(metricPoint.Value) && !double.IsInfinity(metricPoint.Value) => metricPoint.Value,
            ObservablePoint observablePoint when !double.IsNaN(observablePoint.Y) && !double.IsInfinity(observablePoint.Y) => observablePoint.Y,
            _ => null
        };
    }

    private static bool IsStepMetricName(string? metricName)
    {
        if (string.IsNullOrWhiteSpace(metricName))
        {
            return false;
        }

        return metricName.Contains("step", StringComparison.OrdinalIgnoreCase) ||
               metricName.Contains("process-state", StringComparison.OrdinalIgnoreCase) ||
               metricName.Contains("stage", StringComparison.OrdinalIgnoreCase);
    }

    private ChartValues<MetricPoint> BuildProcessPhaseSeries(MetricOption metric, IEnumerable<AttemptSummary> attempts, IReadOnlyList<string> phases, string label)
    {
        var values = new ChartValues<MetricPoint>();
        for (var index = 0; index < phases.Count; index++)
        {
            var phase = phases[index];
            var phaseValues = attempts
                .SelectMany(attempt => attempt.Rows)
                .Where(row => string.Equals(GetProcessPhaseLabel(row), phase, StringComparison.OrdinalIgnoreCase))
                .Select(metric.GetValue)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToList();
            if (phaseValues.Count == 0)
            {
                continue;
            }

            values.Add(new MetricPoint(index, DateTime.MinValue, Math.Round(phaseValues.Average(), 2), metric.DisplayName, label, label, phase));
        }

        return values;
    }

    private string ResolveClickedPointStepName(AttemptSummary attempt, MetricPoint clickedPoint)
    {
        if (attempt.Rows.Count == 0)
            return "-";

        if (clickedPoint.Timestamp != DateTime.MinValue)
        {
            var nearestRow = attempt.Rows
                .OrderBy(row => Math.Abs((row.RecordedAt - clickedPoint.Timestamp).Ticks))
                .FirstOrDefault();
            if (nearestRow is not null)
                return GetRawProcessPhaseLabel(nearestRow);
        }

        if (!string.IsNullOrWhiteSpace(clickedPoint.Recipe))
            return NormalizeStepName(clickedPoint.Recipe);

        return GetAttemptBoundaryStepSummary(attempt);
    }

    private IReadOnlyList<string> GetExpectedPhaseSequence(IReadOnlyList<AttemptSummary> attempts)
    {
        return attempts
            .Select(GetCompressedPhaseSequence)
            .Where(sequence => sequence.Count > 0)
            .GroupBy(sequence => string.Join("|", GetDistinctPhaseSequence(sequence)), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key.Length)
            .Select(group => (IReadOnlyList<string>)GetDistinctPhaseSequence(group.First()))
            .FirstOrDefault() ?? Array.Empty<string>();
    }

    private List<string> GetCompressedPhaseSequence(AttemptSummary attempt)
    {
        // Return the sequence pre-computed at build time -- no per-call row iteration.
        return attempt.CompressedPhaseSequence.ToList();
    }

    private static List<string> GetDistinctPhaseSequence(IEnumerable<string> phases)
    {
        var distinct = new List<string>();
        foreach (var phase in phases)
        {
            if (!distinct.Contains(phase, StringComparer.OrdinalIgnoreCase))
                distinct.Add(phase);
        }

        return distinct;
    }

    private string GetAttemptBoundaryStepSummary(AttemptSummary attempt)
    {
        var compressedSequence = GetCompressedPhaseSequence(attempt);
        if (compressedSequence.Count == 0)
            return "-";

        return compressedSequence.Count == 1
            ? compressedSequence[0]
            : $"{compressedSequence.First()} -> {compressedSequence.Last()}";
    }

    private string GetRawProcessPhaseLabel(SterilizationCycle row)
    {
        var raw = StepNameHeaderKey is null ? string.Empty : row.GetText(StepNameHeaderKey);
        return NormalizeStepName(raw);
    }

    private static string NormalizeStepName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unknown";

        var cleaned = raw.Trim().Replace("_", " ");
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleaned.ToLowerInvariant());
    }

    private IEnumerable<string> GetOrderedProcessPhases(IEnumerable<AttemptSummary> attempts)
    {
        return attempts
            .SelectMany(attempt => attempt.Rows)
            .Select(GetProcessPhaseLabel)
            .Where(phase => !string.IsNullOrWhiteSpace(phase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetProcessPhaseOrder)
            .ThenBy(phase => phase)
            .ToList();
    }

    private string GetProcessPhaseLabel(SterilizationCycle row)
    {
        var raw = GetRawProcessPhaseLabel(row);
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown";
        }

        var normalized = raw.Trim().ToUpperInvariant();
        if (normalized.Contains("PRECONDITION")) return "Preconditioning";
        if (normalized.Contains("FILL")) return "Filling";
        if (normalized.Contains("HEATING")) return "Heating";
        if (normalized.Contains("EXPOSURE")) return "Exposure";
        if (normalized.Contains("COOLING")) return "Cooling";
        if (normalized.Contains("DRAIN")) return "Draining";
        if (normalized.Contains("EXHAUST")) return "Exhaust";
        return raw.Trim();
    }

    private static int GetProcessPhaseOrder(string phase) => phase switch
    {
        "Preconditioning" => 0,
        "Filling" => 1,
        "Heating" => 2,
        "Exposure" => 3,
        "Cooling" => 4,
        "Draining" => 5,
        "Exhaust" => 6,
        "Unknown" => 7,
        _ => 8
    };

    protected void InitDrillDown()
    {
        CloseDrillDownCommand = new RelayCommand<object>(_ => IsDrillDownOpen = false);
    }

    // -- data transfer records --
    public sealed record MetricPoint(double Index, DateTime Timestamp, double Value,
        string MetricName, string AttemptName, string Status, string Recipe)
    {
        public string TimestampLabel => Timestamp.ToString("dd MMM yyyy HH:mm");
        public string ValueLabel => Value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    public sealed record TopMetricBar(string Name, string PropertyName, double Value,
        string Status, string Detail, string Accent)
    {
        public string ValueLabel => Value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    public sealed record DashboardStatCard(string Title, string Value, string Badge, string Accent, string Detail);
    public sealed record ChartRepresentationOption(string Key, string DisplayName, bool UsesSensorSelection)
    {
        public override string ToString() => DisplayName;
    }
    public sealed record ProjectionPoint(DateTime Timestamp, double Value, string Label, string WindowText, string Detail)
    {
        public string ValueLabel => Value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    // -- NEW: expanded EvAlertRow with badge colour and priority label --
    public sealed class EvAlertRow
    {
        public EvAlertRow(
            string classCode,
            bool isAnomaly,
            int priority,
            string summary,
            DateTime? timestamp = null,
            string step = "",
            string sensor = "",
            double? value = null,
            string unit = "",
            string category = "",
            string detail = "")
        {
            ClassCode = classCode;
            IsAnomaly = isAnomaly;
            Priority = priority;
            Summary = summary;
            Timestamp = timestamp;
            Step = step;
            Sensor = sensor;
            Value = value;
            Unit = unit;
            Category = category;
            Detail = detail;
        }

        public EvAlertRow(
            string classCode,
            bool sameMTSU,
            bool samePosition,
            bool sameUVBay,
            bool sameExtTube,
            bool sameCuvette,
            int repeatMinutes,
            int repeatCount,
            int awardPoints,
            int priority,
            string suggestedAction,
            bool actionTaken)
            : this(classCode, priority >= 3, priority, suggestedAction, sensor: classCode, category: classCode, detail: suggestedAction)
        {
            RepeatMinutes = repeatMinutes;
            RepeatCount = repeatCount;
            AwardPoints = awardPoints;
            ActionTaken = actionTaken;
        }

        public string ClassCode { get; init; }
        public bool IsAnomaly { get; init; }
        public string DotColor => IsAnomaly ? "#E53935" : "#32C766";
        public string Sensor { get; init; }
        public double? Value { get; init; }
        public string Unit { get; init; }
        public string Category { get; init; }
        public string Detail { get; init; }
        public string Summary { get; init; }
        public DateTime? Timestamp { get; init; }
        public string Step { get; init; }
        public int Priority { get; init; }
        public double? Score { get; init; }
        public string ModelSignals { get; init; } = string.Empty;
        public string CycleId { get; init; } = string.Empty;
        public string RiskLabel { get; init; } = string.Empty;
        public int RepeatMinutes { get; init; }
        public int RepeatCount { get; init; }
        public int AwardPoints { get; init; }
        public bool ActionTaken { get; init; }
        public string SuggestedAction => Summary;
        public string ValueLabel => Value.HasValue
            ? Value.Value.ToString("0.###", CultureInfo.InvariantCulture) + Unit
            : string.Empty;
        public string TimestampLabel => Timestamp.HasValue
            ? Timestamp.Value.ToString("dd MMM HH:mm:ss", CultureInfo.CurrentCulture)
            : string.Empty;

        /// <summary>Background colour for the class-code badge in the warning tile.</summary>
        public string BadgeColor => ClassCode switch
        {
            "SNS" => "#8B1A1A",   // dark red   - sensor hardware fault
            "TEMP" => "#B85C00",   // amber      - temperature deviation
            "PRESS" => "#1A5C8B",   // blue       - pressure deviation
            "DUR" => "#7A5C00",   // dark gold  - duration overrun
            "SHORT" => "#5C5C00",   // olive      - short / aborted cycle
            "STEP" => "#3D5C00",   // dark green - step abort
            "IDLE" => "#444466",   // muted blue - standby only
            "HIGH" => "#8B1A1A",   // dark red   - value too high
            "LOW" => "#5A2D82",   // purple    - value too low
            "DRIFT" => "#5A2D82",   // purple     - slow drift
            "MISS" => "#6B3E00",   // brown      - missed sterilisation window
            "VAR" => "#1A5C5C",   // teal       - variability / recipe mix
            "ALM" => "#8B1A1A",   // dark red   - alarm-count
            "ML" => "#5A2D82",    // purple     - model-detected anomaly
            _ => "#444444"
        };

        public string EffectiveBadgeColor => IsAnomaly ? "#E53935" : BadgeColor;

        /// <summary>Human-readable priority label shown beside the badge.</summary>
        public string PriorityLabel => !string.IsNullOrWhiteSpace(RiskLabel)
            ? RiskLabel
            : Priority switch
            {
                1 => "Low",
                2 => "Medium",
                3 => "High",
                4 => "Critical",
                _ => "Info"
            };
    }

    // -- NEW: CycleAttemptSelectOption with bindable IsSelected for checkbox multi-select --
    public sealed class CycleAttemptSelectOption : ObservableObject
    {
        private readonly SterilizationDashboardViewModel? _owner;

        // Ctor used for standalone (non-view-model) instances
        public CycleAttemptSelectOption(string key, string displayName, bool isAll = false)
        {
            Key = key;
            DisplayName = displayName;
            IsAll = isAll;
            _owner = null;
        }

        // Ctor for unified AllCycleOptions (bound to the view model)
        public CycleAttemptSelectOption(string key, string displayName, bool isAll, SterilizationDashboardViewModel owner)
        {
            Key = key;
            DisplayName = displayName;
            IsAll = isAll;
            _owner = owner;
        }

        public string Key { get; }
        public string DisplayName { get; }
        public bool IsAll { get; }

        /// <summary>Checkbox binding: reads current selection state from the owner VM; toggling triggers ToggleCycleSelection.</summary>
        public bool IsSelected
        {
            get => _owner?.IsCycleOptionSelected(Key) ?? false;
            set
            {
                if (_owner is not null)
                    _owner.ToggleCycleSelection(this, value);
            }
        }

        /// <summary>Forces a property-changed notification without triggering the setter logic (used internally).</summary>
        public void NotifyIsSelected() => OnPropertyChanged(nameof(IsSelected));

        public override string ToString() => DisplayName;
    }

    public sealed record CycleRunCard(string Name, string Label, string DateSpan, int RowCount, string StatusColor);
    public sealed record CycleAttemptRow(
        string Attempt, string Recipe, string Date,
        string Start, string End, int DurationMinutes,
        double MaxAlarm, string PreIdleSteps, string RecipeLoadStep, string Status, string StatusIcon);

    protected sealed record AttemptSummary(
        string Name, string SheetName, string Recipe, string Status, string StatusColor,
        int SampleCount, int DurationMinutes, DateTime Start, DateTime End,
        double PeakAlarm, double PeakSelectedMetric, double AverageSelectedMetric,
        double MaxStep, string PreIdleSteps, string RecipeLoadStep,
        string Date, string LeadText, IReadOnlyList<SterilizationCycle> Rows,
        IReadOnlyList<string> CompressedPhaseSequence);

    protected sealed record TimeBucketSlice(int Index, DateTime Timestamp, IReadOnlyList<SterilizationCycle> Rows);

    public sealed class MetricOption
    {
        public MetricOption(CycleHeaderDefinition h) { PropertyName = h.NormalizedName; DisplayName = h.Name; }
        public string PropertyName { get; }
        public string DisplayName { get; }
        public double? GetValue(SterilizationCycle c)
        {
            var v = c.GetNumericValue(PropertyName);
            if (v.HasValue && v.Value < -200) return double.NaN;
            return v;
        }
        public override string ToString() => DisplayName;
    }

    public sealed class SensorMetricGroup : ObservableObject
    {
        public SensorMetricGroup(string displayName, string description, IReadOnlyList<MetricSelectionOption> options)
        {
            DisplayName = displayName;
            Description = description;
            foreach (var option in options)
                Options.Add(option);
        }

        public string DisplayName { get; }
        public string Description { get; }
        public ObservableCollection<MetricSelectionOption> Options { get; } = new();
        public string SelectionSummary
        {
            get
            {
                var selected = Options.Count(option => option.IsSelected);
                return selected == 0 ? $"{Options.Count} sensors" : $"{selected}/{Options.Count} selected";
            }
        }

        public bool? IsSelected
        {
            get
            {
                if (Options.Count == 0) return false;
                var selected = Options.Count(option => option.IsSelected);
                if (selected == 0) return false;
                if (selected == Options.Count) return true;
                return null;
            }
            set
            {
                var target = value == true;
                foreach (var option in Options)
                    option.SetSelectedSilently(target);
                if (Options.Count > 0)
                    Options[0].ApplySelectionChange();
                NotifySelectionChanged();
            }
        }

        public void NotifySelectionChanged()
        {
            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(SelectionSummary));
        }
    }

    public sealed class MetricSelectionOption : ObservableObject
    {
        private readonly Action<MetricSelectionOption, bool> _onChanged;
        private readonly Func<MetricSelectionOption, bool, bool> _canApply;
        private bool _isSelected;
        public MetricSelectionOption(
            MetricOption? metric,
            string displayName,
            Action<MetricSelectionOption, bool> onChanged,
            Func<MetricSelectionOption, bool, bool> canApply,
            bool isSelectAll = false)
        {
            Metric = metric;
            DisplayName = displayName;
            IsSelectAll = isSelectAll;
            _onChanged = onChanged;
            _canApply = canApply;
        }

        public MetricOption? Metric { get; }
        public string PropertyName => Metric?.PropertyName ?? string.Empty;
        public string DisplayName { get; }
        public bool IsSelectAll { get; }
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (!_canApply(this, value)) return;
                if (SetProperty(ref _isSelected, value)) _onChanged(this, value);
            }
        }
        public void SetSelectedSilently(bool value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        public void ApplySelectionChange() => _onChanged(this, _isSelected);
    }

    public sealed class TimeRangeOption : ObservableObject
    {
        private bool _isSelected;
        public TimeRangeOption(string label, TimeSpan duration, bool isSelected = false)
        { Label = label; Duration = duration; _isSelected = isSelected; }
        public string Label { get; }
        public TimeSpan Duration { get; }
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    }
}

// Generic RelayCommand<T>
public sealed class RelayCommand<T> : System.Windows.Input.ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;
    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null) { _execute = execute; _canExecute = canExecute; }
    public event EventHandler? CanExecuteChanged
    {
        add => System.Windows.Input.CommandManager.RequerySuggested += value;
        remove => System.Windows.Input.CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? p) => _canExecute?.Invoke((T?)p) ?? true;
    public void Execute(object? p) => _execute((T?)p);
}