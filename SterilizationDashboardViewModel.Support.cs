using SterilizationGenie.Infrastructure;
using SterilizationGenie.Models;
using SterilizationGenie.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LiveCharts;
using LiveCharts.Wpf;
using LiveCharts.Configurations;
using LiveCharts.Defaults;

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

            RefreshTrendSeries();
            RefreshAttemptMetricBars();
            RefreshDashboardCards();
            RefreshStatusDistribution();
            RefreshTopDefects();

            // ── NEW: rebuild cycle selectors before refreshing alerts ──
            RebuildCycleSelectOptions();

            RefreshAlertTiles();
            RefreshSummaryCollections();

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
                // ── NEW cycle selector notifications ──
                nameof(HasCycleData),
                nameof(SelectedFailedCycle),       nameof(SelectedGoodCycle),
                nameof(SelectedFailedCycleLabel),  nameof(SelectedGoodCycleLabel),
                nameof(CycleSelectionSummary)
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

    // ── header catalogue ──
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

        RecipeHeaderKey = FindHeaderKey("RECIPENAME", "RECIPE");
        StepHeaderKey = FindHeaderKey("STEP");
        StepNameHeaderKey = FindHeaderKey("STEPNAME");
        AlarmHeaderKey = FindHeaderKey("CRITICALALARM", "ALARM");
        DurationHeaderKey = FindHeaderKey("EXPTIME", "DURATION");
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

    // ── time-range filtering ──
    private List<SterilizationCycle> GetFilteredCycles()
    {
        var cacheKey = $"{AnalysisDataSignature}|{SelectedChartDate?.Date.Ticks ?? 0}|{CurrentRangeLabel}|{IsOnline}";
        if (string.Equals(cacheKey, _filteredCyclesCacheKey, StringComparison.Ordinal))
        {
            return _filteredCyclesCache;
        }

        _filteredCyclesCache = ApplyTimeRange(VisibleCycles).ToList();
        _filteredCyclesCacheKey = cacheKey;
        return _filteredCyclesCache;
    }

    private IEnumerable<SterilizationCycle> ApplyTimeRange(IEnumerable<SterilizationCycle> source)
    {
        var ordered = source.OrderBy(c => c.RecordedAt).ToList();
        if (ordered.Count == 0) return ordered;

        if (SelectedChartDate.HasValue)
        {
            ordered = ordered.Where(c => c.RecordedAt.Date == SelectedChartDate.Value.Date).ToList();
            if (ordered.Count == 0) return ordered;
        }

        var selected = TimeRangeOptions.FirstOrDefault(o => o.IsSelected);
        if (selected is null || selected.Label == "All") return ordered;

        if (IsOnline)
        {
            var anchor = ordered.Max(c => c.RecordedAt);
            return ordered.Where(c => c.RecordedAt >= anchor - selected.Duration).ToList();
        }

        var anchorStart = ordered.Min(c => c.RecordedAt);
        return ordered.Where(c => c.RecordedAt < anchorStart + selected.Duration).ToList();
    }

    // ── attempt summaries ──
    private List<AttemptSummary> GetAttemptSummaries(bool applyTimeFilter = false)
    {
        if (AllAttemptSummaries.Count == 0 || string.IsNullOrWhiteSpace(StepHeaderKey)) return [];

        var metricKey = SelectedMetric?.PropertyName ?? string.Empty;
        var dateKey = SelectedChartDate?.Date.Ticks ?? 0;
        if (!applyTimeFilter)
        {
            var cacheKey = $"{AnalysisDataSignature}|all|{dateKey}|{metricKey}";
            if (string.Equals(cacheKey, _attemptSummariesCacheKey, StringComparison.Ordinal))
            {
                return _attemptSummariesCache;
            }

            var currentAttempts = SelectedChartDate.HasValue
                ? AllAttemptSummaries.Where(a => a.Start.Date == SelectedChartDate.Value.Date).ToList()
                : AllAttemptSummaries.ToList();
            _attemptSummariesCache = ProjectAttemptMetrics(currentAttempts);
            _attemptSummariesCacheKey = cacheKey;
            return _attemptSummariesCache;
        }

        var filteredCacheKey = $"{AnalysisDataSignature}|filtered|{dateKey}|{CurrentRangeLabel}|{metricKey}|{IsOnline}";
        if (string.Equals(filteredCacheKey, _filteredAttemptSummariesCacheKey, StringComparison.Ordinal))
        {
            return _filteredAttemptSummariesCache;
        }

        var relevantAttempts = SelectedChartDate.HasValue
            ? AllAttemptSummaries.Where(a => a.Start.Date == SelectedChartDate.Value.Date).ToList()
            : AllAttemptSummaries.ToList();

        var sourceFiltered = GetFilteredCycles();
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
        var leadText = $"{attempt.Status} • {durationMinutes} min • Peak {SelectedMetric?.DisplayName ?? "metric"} {peakMetric:0.##}";

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

            var prelude = ordered.Skip(segStart).Take(aStart - segStart).ToList();
            var activeRows = ordered.Skip(aStart).Take(aEnd - aStart + 1).ToList();
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
        var leadText = $"{status} • {durMin} min • Peak {SelectedMetric?.DisplayName ?? "metric"} {peakMetric:0.##}";

        return new AttemptSummary(
            BuildAttemptName(sheetName, ordinal), sheetName,
            recipe, status, statusColor, rows.Count, durMin, start, end,
            peakAlarm, peakMetric, avgMetric, maxStep,
            string.Join("+", preIdle), recipeLoadStep,
            start.ToString("MMM dd", CultureInfo.InvariantCulture),
            leadText, rows);
    }

    private static string BuildAttemptName(string sheetName, int ordinal)
    {
        var tokens = sheetName
            .Split([' ', '_', '-', '.', '/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Take(3).Select(t => char.ToUpperInvariant(t[0])).ToArray();
        return $"{(tokens.Length == 0 ? "AT" : new string(tokens))}-{ordinal:00}";
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
        if (sampleCount < 3 || maxStep < 30) return "Short Run";
        return "Complete";
    }

    private bool IsActiveStep(SterilizationCycle c)
    {
        var step = StepHeaderKey is null ? null : c.GetNumericValue(StepHeaderKey);
        return step.HasValue && step.Value >= 26;
    }

    private List<AttemptSummary> GetBaselineAttempts(bool applyTimeFilter = false)
    {
        var attempts = GetAttemptSummaries(applyTimeFilter);
        var goodNamed = attempts.Where(a => a.SheetName.Contains("good", StringComparison.OrdinalIgnoreCase)).ToList();
        if (goodNamed.Count > 0) return goodNamed;
        return attempts.Where(a => a.Status == "Complete").ToList();
    }

    private List<AttemptSummary> GetReviewAttempts(bool applyTimeFilter = false)
    {
        var attempts = GetAttemptSummaries(applyTimeFilter);
        var failedNamed = attempts.Where(a => a.SheetName.Contains("fail", StringComparison.OrdinalIgnoreCase)).ToList();
        if (failedNamed.Count > 0) return failedNamed;
        return attempts.Where(a => a.Status != "Complete").ToList();
    }

    private List<AttemptSummary> GetBaselineAttemptsForComparison()
    {
        var filtered = GetBaselineAttempts(applyTimeFilter: true);
        return filtered.Count > 0 ? filtered : GetBaselineAttempts(applyTimeFilter: false);
    }

    private List<AttemptSummary> GetReviewAttemptsForComparison()
    {
        var filtered = GetReviewAttempts(applyTimeFilter: true);
        return filtered.Count > 0 ? filtered : GetReviewAttempts(applyTimeFilter: false);
    }

    private List<AttemptSummary> GetAttemptsForComparison()
    {
        var filtered = GetAttemptSummaries(applyTimeFilter: true);
        return filtered.Count > 0 ? filtered : GetAttemptSummaries(applyTimeFilter: false);
    }

    private List<AttemptSummary> GetMatchedAttemptsForDate()
    {
        var attempts = GetAttemptSummaries(applyTimeFilter: false);
        var selectedGood = SelectedGoodCycle?.Key;
        var selectedFailed = SelectedFailedCycle?.Key;
        var matchedAttempts = new List<AttemptSummary>();

        foreach (var attempt in attempts)
        {
            var isGood = attempt.Status == "Complete" || attempt.SheetName.Contains("good", StringComparison.OrdinalIgnoreCase);
            if (isGood)
            {
                if (selectedGood == "__ALL__" || selectedGood is null || attempt.Name == selectedGood)
                {
                    matchedAttempts.Add(attempt);
                }
            }
            else if (selectedFailed == "__ALL__" || selectedFailed is null || attempt.Name == selectedFailed)
            {
                matchedAttempts.Add(attempt);
            }
        }

        return matchedAttempts;
    }

    private List<SterilizationCycle> GetComparisonRenderCycles()
    {
        var filteredCycles = GetFilteredCycles().OrderBy(cycle => cycle.RecordedAt).ToList();
        if (filteredCycles.Count == 0)
        {
            return [];
        }

        if (!HasExplicitCycleFilter())
        {
            return filteredCycles;
        }

        var matchedRowIds = GetMatchedAttemptsForDate()
            .SelectMany(attempt => attempt.Rows)
            .Select(row => row.Id)
            .ToHashSet();

        return filteredCycles.Where(cycle => matchedRowIds.Contains(cycle.Id)).ToList();
    }

    private List<TimeBucketSlice> BuildTimeBucketSlices(IReadOnlyList<SterilizationCycle> cycles)
    {
        if (cycles.Count == 0)
        {
            return [];
        }

        var ordered = cycles.OrderBy(cycle => cycle.RecordedAt).ToList();
        var anchor = ordered.First().RecordedAt;

        return ordered
            .GroupBy(cycle => Math.Max(0, (int)Math.Round((cycle.RecordedAt - anchor).TotalMinutes, MidpointRounding.AwayFromZero)))
            .OrderBy(group => group.Key)
            .Select(group => new TimeBucketSlice(group.Key, anchor.AddMinutes(group.Key), group.ToList()))
            .ToList();
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
        return GetAttemptSummaries(applyTimeFilter: false);
    }

    private List<AttemptSummary> GetBaselineAttemptsForAnalysis()
    {
        var attempts = IsEnvelopeAggregateMode()
            ? AllAttemptSummaries.ToList()
            : GetAttemptSummaries(applyTimeFilter: false);
        var goodNamed = attempts.Where(a => a.SheetName.Contains("good", StringComparison.OrdinalIgnoreCase)).ToList();
        var result = goodNamed.Count > 0 ? goodNamed : attempts.Where(a => a.Status == "Complete").ToList();
        if (IsEnvelopeAggregateMode())
            return result;
        if (SelectedGoodCycle is not null && !SelectedGoodCycle.IsAll)
            result = result.Where(a => a.Name == SelectedGoodCycle.Key).ToList();
        return result;
    }

    private List<AttemptSummary> GetReviewAttemptsForAnalysis()
    {
        var attempts = IsEnvelopeAggregateMode()
            ? AllAttemptSummaries.ToList()
            : GetAttemptSummaries(applyTimeFilter: false);
        var failedNamed = attempts.Where(a => a.SheetName.Contains("fail", StringComparison.OrdinalIgnoreCase)).ToList();
        var result = failedNamed.Count > 0 ? failedNamed : attempts.Where(a => a.Status != "Complete").ToList();
        if (IsEnvelopeAggregateMode())
            return result;
        if (SelectedFailedCycle is not null && !SelectedFailedCycle.IsAll)
            result = result.Where(a => a.Name == SelectedFailedCycle.Key).ToList();
        return result;
    }

    private bool IsEnvelopeAggregateMode()
        => string.Equals(SelectedRepresentation?.Key, "good-failed-envelope", StringComparison.OrdinalIgnoreCase);

    private bool HasExplicitCycleFilter()
        => (SelectedFailedCycle is not null && !SelectedFailedCycle.IsAll) ||
           (SelectedGoodCycle is not null && !SelectedGoodCycle.IsAll);

    private bool HasMetricSelection()
        => ComparisonMetricOptions.Any(option => option.IsSelected);

    private bool RequiresMetricSelection()
        => SelectedRepresentation?.UsesSensorSelection == true;

    private bool CanRenderCurrentSelection()
    {
        if (SelectedRepresentation is null) return false;
        return !RequiresMetricSelection() || HasMetricSelection();
    }

    private static bool IsSensorHeader(CycleHeaderDefinition header)
        => GetSensorFamily(header.NormalizedName) is not SensorFamily.Other;

    // ── NEW: Rebuild cycle selector combo boxes ──
    private void RebuildCycleSelectOptions()
    {
        var currentSelectedDateAttempts = GetAttemptSummaries(applyTimeFilter: false);
        var failedAttempts = currentSelectedDateAttempts.Where(a => a.Status != "Complete").OrderBy(a => a.Start).ToList();
        var goodAttempts = currentSelectedDateAttempts.Where(a => a.Status == "Complete").OrderBy(a => a.Start).ToList();
        var globalGoodOrdinals = BuildGlobalGoodCycleOrdinals();

        var prevFailedKey = SelectedFailedCycle?.Key;
        var prevGoodKey = SelectedGoodCycle?.Key;

        FailedCycleOptions.Clear();
        FailedCycleOptions.Add(new CycleAttemptSelectOption("__ALL__", "All failed cycles", IsAll: true));
        foreach (var a in failedAttempts)
            FailedCycleOptions.Add(new CycleAttemptSelectOption(a.Name,
                BuildAttemptDisplayName(a)));

        GoodCycleOptions.Clear();
        GoodCycleOptions.Add(new CycleAttemptSelectOption("__ALL__", "All good cycles", IsAll: true));
        foreach (var indexedAttempt in goodAttempts.Select((attempt, index) => new { attempt, index }))
        {
            var globalOrdinal = globalGoodOrdinals.TryGetValue(indexedAttempt.attempt.Name, out var ordinal)
                ? ordinal
                : indexedAttempt.index + 1;
            GoodCycleOptions.Add(new CycleAttemptSelectOption(indexedAttempt.attempt.Name,
                BuildAttemptDisplayName(indexedAttempt.attempt, $"Good Cycle {globalOrdinal}")));
        }

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
        OnPropertyChanged(nameof(HasCycleData));
    }

    private static string BuildAttemptDisplayName(AttemptSummary attempt, string? labelOverride = null)
        => $"{labelOverride ?? attempt.SheetName} | {attempt.Start:dd-MM-yyyy HH:mm} -> {attempt.End:HH:mm} | {attempt.DurationMinutes} min";

    private Dictionary<string, int> BuildGlobalGoodCycleOrdinals()
    {
        return AllAttemptSummaries
            .Where(attempt => attempt.Status == "Complete")
            .OrderBy(attempt => attempt.Start)
            .ThenBy(attempt => attempt.End)
            .ThenBy(attempt => attempt.Name)
            .Select((attempt, index) => new { attempt.Name, Ordinal = index + 1 })
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Ordinal, StringComparer.OrdinalIgnoreCase);
    }

    // ── trend series ──
    private void RefreshTrendSeries()
    {
        if (!CanRenderCurrentSelection())
        {
            ApplyChartState(new SeriesCollection(), Array.Empty<string>());
            return;
        }

        switch (SelectedRepresentation?.Key)
        {
            case "temp-pressure":
                BuildTemperaturePressureSeries();
                break;
            case "good-failed-envelope":
                BuildGoodVsFailedEnvelopeSeries();
                break;
            case "cycles-info":
                BuildCyclesInfoSeries();
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
        
        var selectedGood = SelectedGoodCycle?.Key;
        var selectedFailed = SelectedFailedCycle?.Key;
        
        var matchedAttempts = new List<AttemptSummary>();
        foreach (var a in allDayAttempts)
        {
            var isGood = a.Status == "Complete" || a.SheetName.Contains("good", StringComparison.OrdinalIgnoreCase);
            if (isGood)
            {
                 if (selectedGood == "__ALL__" || selectedGood == null || a.Name == selectedGood)
                     matchedAttempts.Add(a);
            }
            else
            {
                 if (selectedFailed == "__ALL__" || selectedFailed == null || a.Name == selectedFailed)
                     matchedAttempts.Add(a);
            }
        }

        if (timelineCycles.Count == 0)
        {
            ApplyChartState(new SeriesCollection(), Array.Empty<string>());
            return;
        }

        var hasExplicitCycleFilter =
            (SelectedGoodCycle is not null && !SelectedGoodCycle.IsAll) ||
            (SelectedFailedCycle is not null && !SelectedFailedCycle.IsAll);

        var matchedRowIds = hasExplicitCycleFilter
            ? matchedAttempts.SelectMany(a => a.Rows).Select(r => r.Id).ToHashSet()
            : null;

        var renderCycles = matchedRowIds is null
            ? timelineCycles
            : timelineCycles.Where(cycle => matchedRowIds.Contains(cycle.Id)).ToList();
        if (renderCycles.Count == 0)
        {
            ApplyChartState(new SeriesCollection(), Array.Empty<string>());
            return;
        }

        var palette = GetPalette();
        if (IsBarChartRepresentation)
        {
            var averagedValues = new ChartValues<double>();
            foreach (var metric in metrics)
            {
                var visibleValues = renderCycles
                    .Select(metric.GetValue)
                    .Where(value => value.HasValue)
                    .Select(value => value!.Value)
                    .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
                    .Where(value => !IsImpossibleSensorValue(metric, value))
                    .ToList();

                averagedValues.Add(visibleValues.Count == 0
                    ? double.NaN
                    : Math.Round(visibleValues.Average(), 2));
            }

            var barSeries = new SeriesCollection();
            AddCategorySeries(barSeries, $"{CurrentRangeLabel} window average", averagedValues, palette[0]);
            ApplyChartState(barSeries, metrics.Select(GetSensorCode).ToArray());
            return;
        }

        var sampledCycles = DownsampleTimelineCycles(renderCycles, GetTimelinePointBudget(metrics.Count));
        var cycleAttemptMap = allDayAttempts
            .SelectMany(a => a.Rows.Select(r => new { r.Id, a.Name, a.Status, a.Recipe }))
            .ToDictionary(x => x.Id, x => (x.Name, x.Status, x.Recipe));

        var newSeries = new SeriesCollection();
        var labels = sampledCycles.Select(c => c.RecordedAt.ToString("HH:mm:ss")).ToArray();

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

                values.Add(new MetricPoint(index, cycle.RecordedAt, val.Value, metric.DisplayName, attempt.Name, attempt.Status, attempt.Recipe));
            }

            AddIndexedMetricSeries(newSeries, metric.DisplayName, values, palette[metricIdx % palette.Length]);
        }

        ApplyChartState(newSeries, labels);
    }

    private void BuildTemperaturePressureSeries()
    {
        var temperatureMetrics = GetTemperatureMetrics().ToList();
        var pressureMetrics = GetPressureMetrics().ToList();
        var renderCycles = GetComparisonRenderCycles();
        var buckets = BuildTimeBucketSlices(renderCycles);
        if ((temperatureMetrics.Count == 0 && pressureMetrics.Count == 0) || buckets.Count == 0)
        {
            ApplyChartState(new SeriesCollection(), Array.Empty<string>());
            return;
        }

        var palette = GetPalette();
        var temperatureAverageValues = new ChartValues<MetricPoint>();
        var temperaturePeakValues = new ChartValues<MetricPoint>();
        var pressureAverageValues = new ChartValues<MetricPoint>();
        var pressurePeakValues = new ChartValues<MetricPoint>();

        for (var i = 0; i < buckets.Count; i++)
        {
            var bucket = buckets[i];
            var temperatureValues = GetValidMetricValues(bucket.Rows, temperatureMetrics);
            var pressureValues = GetValidMetricValues(bucket.Rows, pressureMetrics);

            temperatureAverageValues.Add(new MetricPoint(i, bucket.Timestamp,
                temperatureValues.Count == 0 ? double.NaN : Math.Round(temperatureValues.Average(), 2),
                "Temperature avg", CurrentRangeLabel, "Window", ImportedWorkbookName));
            temperaturePeakValues.Add(new MetricPoint(i, bucket.Timestamp,
                temperatureValues.Count == 0 ? double.NaN : Math.Round(temperatureValues.Max(), 2),
                "Temperature peak", CurrentRangeLabel, "Window", ImportedWorkbookName));
            pressureAverageValues.Add(new MetricPoint(i, bucket.Timestamp,
                pressureValues.Count == 0 ? double.NaN : Math.Round(pressureValues.Average(), 2),
                "Pressure avg", CurrentRangeLabel, "Window", ImportedWorkbookName));
            pressurePeakValues.Add(new MetricPoint(i, bucket.Timestamp,
                pressureValues.Count == 0 ? double.NaN : Math.Round(pressureValues.Max(), 2),
                "Pressure peak", CurrentRangeLabel, "Window", ImportedWorkbookName));
        }

        var newSeries = new SeriesCollection();
        AddIndexedMetricSeries(newSeries, "Temperature avg", temperatureAverageValues, palette[2]);
        AddIndexedMetricSeries(newSeries, "Temperature peak", temperaturePeakValues, palette[0]);
        AddIndexedMetricSeries(newSeries, "Pressure avg", pressureAverageValues, palette[1]);
        AddIndexedMetricSeries(newSeries, "Pressure peak", pressurePeakValues, palette[5]);
        ApplyChartState(newSeries, BuildBucketLabels(buckets));
    }

    private void BuildGoodVsFailedEnvelopeSeries()
    {
        var metrics = GetTemperatureMetrics().ToList();
        var failedAttempts = GetReviewAttemptsForComparison();
        var baselineAttempts = GetBaselineAttemptsForComparison();
        var renderCycles = GetComparisonRenderCycles();
        var buckets = BuildTimeBucketSlices(renderCycles);

        if (metrics.Count == 0 || (baselineAttempts.Count == 0 && failedAttempts.Count == 0) || buckets.Count == 0)
        {
            ApplyChartState(new SeriesCollection(), Array.Empty<string>());
            return;
        }

        var goodRowIds = baselineAttempts.SelectMany(attempt => attempt.Rows).Select(row => row.Id).ToHashSet();
        var failedRowIds = failedAttempts.SelectMany(attempt => attempt.Rows).Select(row => row.Id).ToHashSet();
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

        var palette = GetPalette();
        var newSeries = new SeriesCollection();
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
        var buckets = BuildTimeBucketSlices(renderCycles);

        if (attempts.Count == 0 || buckets.Count == 0)
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
        AddIndexedMetricSeries(newSeries, "Cycle duration (min)", durationValues, palette[2]);
        AddIndexedMetricSeries(newSeries, "Highest process step", maxStepValues, palette[0]);
        AddIndexedMetricSeries(newSeries, "Process-state changes", processSpanValues, palette[5]);
        ApplyChartState(newSeries, BuildBucketLabels(buckets));
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

    private void AddIndexedMetricSeries(SeriesCollection target, string title, ChartValues<MetricPoint> values, System.Windows.Media.Brush brush)
    {
        var mapper = Mappers.Xy<MetricPoint>().X(p => p.Index).Y(p => p.Value);
        if (IsBarChartRepresentation)
        {
            target.Add(new ColumnSeries
            {
                Title = title,
                Values = values,
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
        target.Add(new LineSeries
        {
            Title = title,
            Values = values,
            Configuration = mapper,
            LabelPoint = TooltipLabelPoint,
            Stroke = brush,
            Fill = System.Windows.Media.Brushes.Transparent,
            PointGeometrySize = 7,
            StrokeThickness = 2.4,
            LineSmoothness = 0
        });
    }

    private void AddCategorySeries(SeriesCollection target, string title, ChartValues<double> values, System.Windows.Media.Brush brush)
    {
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
            PointGeometrySize = 8,
            StrokeThickness = 2.6,
            LineSmoothness = 0
        });
    }

    private void ApplyChartState(SeriesCollection series, IReadOnlyList<string> labels, Func<double, string>? xFormatter = null)
    {
        MainChartSeries.Clear();
        MainChartSeries.AddRange(series);

        var allValues = new List<double>();
        foreach (var s in series)
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
            YAxisMin = Math.Round(min - padding, 2);
            YAxisMax = Math.Round(max + padding, 2);
        }
        else
        {
            YAxisMin = double.NaN;
            YAxisMax = double.NaN;
        }

        XLabels = labels.ToArray();
        XAxisSeparatorStep = ComputeXAxisSeparatorStep(labels);
        YAxisFormatter = value => value.ToString("0.00", CultureInfo.InvariantCulture);
        XAxisFormatter = xFormatter ?? (value =>
        {
            var idx = (int)Math.Round(value);
            return idx >= 0 && idx < XLabels.Length ? XLabels[idx] : string.Empty;
        });
        OnPropertyChanged(nameof(XLabels));
        OnPropertyChanged(nameof(XAxisSeparatorStep));
        OnPropertyChanged(nameof(YAxisFormatter));
        OnPropertyChanged(nameof(XAxisFormatter));
    }

    private double ComputeXAxisSeparatorStep(IReadOnlyList<string> labels)
    {
        if (labels.Count <= 1)
        {
            return 1d;
        }

        if (IsBarChartRepresentation)
        {
            return labels.Count <= 12 ? 1d : Math.Max(1d, Math.Ceiling(labels.Count / 10d));
        }

        return labels.Count <= 14 ? 1d : Math.Max(1d, Math.Ceiling(labels.Count / 12d));
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

    // ── bar charts ──
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
                                .Select(g => $"{g.First().Recipe} • {g.Count()} attempts").FirstOrDefault() ?? "No recipe profile";
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

    // ── alert tiles ──
    private void RefreshAlertTiles()
    {
        AlertTiles.Clear();
        if (SelectedRepresentation is null) return;

        if (string.Equals(SelectedRepresentation?.Key, "timeline", StringComparison.OrdinalIgnoreCase))
        {
            if (HasMetricSelection())
            {
                var cycles = GetFilteredCycles().OrderBy(c => c.RecordedAt).ToList();
                if (cycles.Count > 0)
                {
                    var latestRow = cycles.Last();
                    var baselineRows = VisibleCycles
                        .Where(c => c.RecordedAt < latestRow.RecordedAt || c.Id != latestRow.Id)
                        .OrderBy(c => c.RecordedAt).ToList();

                    foreach (var metric in GetActiveMetrics().Take(4))
                    {
                        var currentValue = metric.GetValue(latestRow);
                        if (!currentValue.HasValue) continue;
                        var alert = BuildLiveMetricAlert(metric, currentValue.Value, latestRow, baselineRows);
                        if (alert is not null) AlertTiles.Add(alert);
                    }
                }
            }

            if (AlertTiles.Count == 0 || HasExplicitCycleFilter())
            {
                foreach (var alert in BuildWorkbookAnomalyAlerts())
                {
                    if (!AlertTiles.Any(existing =>
                            string.Equals(existing.ClassCode, alert.ClassCode, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(existing.SuggestedAction, alert.SuggestedAction, StringComparison.Ordinal)))
                    {
                        AlertTiles.Add(alert);
                    }
                }
            }
            return;
        }

        foreach (var alert in BuildWorkbookAnomalyAlerts())
            AlertTiles.Add(alert);
    }

    // ── NEW: expanded representation-aware anomaly alerts ──
    // Dynamically analyses baseline vs review attempts per the selected view.
    // All thresholds are derived from the real Excel data:
    //   Good cycles (2 Days): T-sensors peak ~123 °C, P-sensors peak ~77 bar
    //   Failed Cycle 1: peak alarm = 5, T151 has dropout sentinel −3276.8, L130 = −999.9
    //   Failed Cycle 2: Step stuck at 10, Exp_Time = 0, pure standby
    private IEnumerable<EvAlertRow> BuildWorkbookAnomalyAlerts()
    {
        // Resolve baseline / review sets, respecting user combo-box selection
        var baselineAttempts = GetBaselineAttemptsForAnalysis();
        var reviewAttempts = GetReviewAttemptsForAnalysis();

        // Apply user-pinned cycle filter if not "All"
        if (SelectedFailedCycle is not null && !SelectedFailedCycle.IsAll)
            reviewAttempts = reviewAttempts.Where(a => a.Name == SelectedFailedCycle.Key).ToList();
        if (SelectedGoodCycle is not null && !SelectedGoodCycle.IsAll)
            baselineAttempts = baselineAttempts.Where(a => a.Name == SelectedGoodCycle.Key).ToList();

        if (baselineAttempts.Count == 0 && reviewAttempts.Count == 0) yield break;

        var representationKey = SelectedRepresentation?.Key ?? "timeline";

        foreach (var stepAlert in BuildStepSequenceAlerts(baselineAttempts, reviewAttempts))
            yield return stepAlert;

        // ══ Cross-representation alerts (always evaluated) ══════════════════

        // SNS — impossible sensor sentinel value in any review attempt
        var tempMetric = GetTemperatureMetrics().FirstOrDefault();
        if (tempMetric is not null)
        {
            var impossible = reviewAttempts
                .SelectMany(a => a.Rows.Select(r => new { Attempt = a, Value = tempMetric.GetValue(r) }))
                .FirstOrDefault(x => x.Value.HasValue && IsImpossibleSensorValue(tempMetric, x.Value.Value));
            if (impossible is not null)
            {
                yield return new EvAlertRow("SNS", false, true, false, false, false,
                    impossible.Attempt.DurationMinutes, 1, 5, 3,
                    $"{impossible.Attempt.Name}: sensor '{tempMetric.DisplayName}' contains an impossible reading of " +
                    $"{impossible.Value:0.##}. This matches the −3276.8 dropout pattern seen in the workbook — likely a " +
                    $"wiring fault, RTD open-circuit, or PI historian fill-forward error. Verify sensor before next run.",
                    false);
            }
        }

        // SNS — L130 / L230 level sensor sentinel (−999.9 from workbook)
        var levelMetrics = MetricOptions.Where(m => GetSensorFamily(m.PropertyName) == SensorFamily.Level).ToList();
        foreach (var lm in levelMetrics.Take(2))
        {
            var lvlDrop = reviewAttempts
                .SelectMany(a => a.Rows.Select(r => new { Attempt = a, Value = lm.GetValue(r) }))
                .FirstOrDefault(x => x.Value.HasValue && x.Value.Value < -900d);
            if (lvlDrop is not null)
            {
                yield return new EvAlertRow("SNS", false, false, true, false, false,
                    lvlDrop.Attempt.DurationMinutes, 1, 5, 3,
                    $"{lvlDrop.Attempt.Name}: level sensor '{lm.DisplayName}' returned {lvlDrop.Value:0.##}. " +
                    $"This sentinel (≈ −999.9) was observed in the workbook for STR34_L130 during failed cycles. " +
                    $"Check instrument loop and wiring before accepting any level-based interlocks.",
                    false);
            }
        }

        // ALM — any review attempt with a non-zero peak alarm
        foreach (var alarmed in reviewAttempts.Where(a => a.PeakAlarm > 0)
                                              .OrderByDescending(a => a.PeakAlarm).Take(3))
        {
            yield return new EvAlertRow("ALM", true, true, false, false, false,
                alarmed.DurationMinutes, 1, 5, 3,
                $"{alarmed.Name} tripped a critical alarm reaching a peak count of {alarmed.PeakAlarm:0}. " +
                $"In the workbook, Failed Cycle 1 peaked at 5 critical alarms (STR34_CRITICAL_ALARM). " +
                $"Recipe: {alarmed.Recipe}. Raise a corrective action before re-running this recipe.",
                false);
        }

        // IDLE — standby-only attempt (step never exceeded 10 — matches Failed Cycle 2)
        foreach (var idle in reviewAttempts.Where(a => a.Status == "Standby Only").Take(2))
        {
            var idlePhase = GetAttemptBoundaryStepSummary(idle);
            yield return new EvAlertRow("IDLE", true, false, false, false, false,
                idle.DurationMinutes, 1, 4, 2,
                $"{idle.Name} stayed at process step 10 (pre-sterilisation standby) for its entire duration. " +
                $"This matches the 'Failed Cycle 2' pattern in the workbook where STR34_Step never rose above 10 " +
                $"and STR34_Exp_Time remained 0. Step name trace: {idlePhase}. Check interlock clearance and operator abort logs.",
                false);
        }

        // ══ Representation-specific alerts ══════════════════════════════════

        switch (representationKey)
        {
            // ── LIVE SENSOR TIMELINE ──────────────────────────────────────────
            case "timeline":
                {
                    // Report recipe mix for the selected day
                    var dayAttempts = GetAttemptSummariesForAnalysis();
                    var recipes = dayAttempts.Select(a => a.Recipe)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (recipes.Count > 1)
                    {
                        yield return new EvAlertRow("VAR", false, false, false, false, false,
                            0, 1, 3, 1,
                            $"Multiple recipes detected on the selected day: {string.Join(", ", recipes)}. " +
                            $"Mixing recipe types in the timeline view can distort baseline comparisons. " +
                            $"Filter to a single recipe for a clean sensor trace.",
                            false);
                    }

                    // HIGH — F0 lethality counter unusually high vs workbook baseline (good peak ~44)
                    var lethalityMetrics = MetricOptions
                        .Where(m => GetSensorFamily(m.PropertyName) == SensorFamily.Lethality).ToList();
                    foreach (var fm in lethalityMetrics.Take(2))
                    {
                        var maxF0 = reviewAttempts.SelectMany(a => a.Rows.Select(fm.GetValue))
                            .Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0d).Max();
                        if (maxF0 > 50d)
                        {
                            yield return new EvAlertRow("HIGH", false, false, false, false, false,
                                0, 1, 4, 2,
                                $"Lethality counter '{fm.DisplayName}' reached {maxF0:0.##} — above the workbook " +
                                $"good-cycle ceiling of ~44. An over-sterilisation event may have occurred, " +
                                $"risking product degradation. Cross-check with exposure time data.",
                                false);
                        }
                    }
                    break;
                }

            // ── TEMPERATURE vs PRESSURE ───────────────────────────────────────
            case "temp-pressure":
                {
                    if (baselineAttempts.Count == 0 || reviewAttempts.Count == 0) break;

                    // TEMP — failed cycle peak temperature vs good cycle peak
                    // Good cycles: T-sensors peak 123–125 °C; Failed Cycle 2: peak only ~19–21 °C
                    var coolestFailed = reviewAttempts
                        .Select(a => new {
                            Attempt = a,
                            Peak = GetTemperatureMetrics()
                            .SelectMany(m => a.Rows.Select(m.GetValue))
                            .Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0d).Max()
                        })
                        .OrderBy(x => x.Peak).FirstOrDefault();
                    var hottestGood = baselineAttempts
                        .Select(a => GetTemperatureMetrics()
                            .SelectMany(m => a.Rows.Select(m.GetValue))
                            .Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0d).Max())
                        .DefaultIfEmpty(0d).Max();

                    if (coolestFailed is not null && hottestGood > 0 && coolestFailed.Peak < hottestGood - 15)
                    {
                        yield return new EvAlertRow("TEMP", true, true, false, false, false,
                            coolestFailed.Attempt.DurationMinutes, 1, 4, 2,
                            $"{coolestFailed.Attempt.Name} peaked at {coolestFailed.Peak:0.##} °C while good cycles " +
                            $"reached {hottestGood:0.##} °C. The workbook shows good cycles consistently hitting " +
                            $"121–125 °C; this run is {hottestGood - coolestFailed.Peak:0.#} °C below threshold. " +
                            $"Verify heater circuit, steam supply pressure, and door-seal integrity.",
                            false);
                    }

                    // PRESS — failed cycle pressure gap (good cycles P101 peaks ~51 bar; Failed Cycle 2 stuck at ~14 bar)
                    var strongestGoodPressure = baselineAttempts
                        .Select(a => GetPressureMetrics()
                            .SelectMany(m => a.Rows.Select(m.GetValue))
                            .Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0d).Max())
                        .DefaultIfEmpty(0d).Max();
                    var weakestFailedPressure = reviewAttempts
                        .Select(a => new {
                            Attempt = a,
                            Peak = GetPressureMetrics()
                            .SelectMany(m => a.Rows.Select(m.GetValue))
                            .Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0d).Max()
                        })
                        .OrderBy(x => x.Peak).FirstOrDefault();

                    if (weakestFailedPressure is not null && strongestGoodPressure > 0 &&
                        weakestFailedPressure.Peak < strongestGoodPressure * 0.8)
                    {
                        yield return new EvAlertRow("PRESS", true, false, true, false, false,
                            weakestFailedPressure.Attempt.DurationMinutes, 1, 4, 2,
                            $"{weakestFailedPressure.Attempt.Name} reached only {weakestFailedPressure.Peak:0.##} on pressure " +
                            $"sensors, while good cycles peaked at {strongestGoodPressure:0.##}. " +
                            $"Gap of {strongestGoodPressure - weakestFailedPressure.Peak:0.#} bar suggests the chamber " +
                            $"never pressurised — inspect steam trap, condensate drain, and chamber door latch.",
                            false);
                    }

                    // DRIFT — avg peak temperature across failed cycles significantly below good baseline
                    var allTempMetrics = GetTemperatureMetrics().ToList();
                    if (allTempMetrics.Count >= 2)
                    {
                        var goodAvgPeak = baselineAttempts
                            .Select(a => allTempMetrics.Select(m =>
                                a.Rows.Select(m.GetValue).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0d).Max())
                                .Average())
                            .DefaultIfEmpty(0d).Average();
                        var failAvgPeak = reviewAttempts
                            .Select(a => allTempMetrics.Select(m =>
                                a.Rows.Select(m.GetValue).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0d).Max())
                                .Average())
                            .DefaultIfEmpty(0d).Average();
                        if (goodAvgPeak > 0 && failAvgPeak < goodAvgPeak * 0.9)
                        {
                            yield return new EvAlertRow("DRIFT", false, false, false, true, false,
                                0, 1, 3, 2,
                                $"Average peak temperature across failed cycles ({failAvgPeak:0.#} °C) is " +
                                $"{goodAvgPeak - failAvgPeak:0.#} °C below the good-cycle average ({goodAvgPeak:0.#} °C). " +
                                $"Consistent under-temperature across multiple runs points to heater calibration drift " +
                                $"or heat-exchanger fouling rather than a one-off fault.",
                                false);
                        }
                    }

                    // VAR — exposure time (Exp_Time) discrepancy between good and failed
                    // Good cycles: Exp_Time up to 1140 s; Failed Cycle 2: Exp_Time = 0 throughout
                    if (DurationHeaderKey is not null)
                    {
                        var goodMaxExp = baselineAttempts
                            .SelectMany(a => a.Rows.Select(r => r.GetNumericValue(DurationHeaderKey)))
                            .Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0d).Max();
                        var failedMaxExp = reviewAttempts
                            .SelectMany(a => a.Rows.Select(r => r.GetNumericValue(DurationHeaderKey)))
                            .Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0d).Max();

                        if (goodMaxExp > 0 && failedMaxExp < goodMaxExp * 0.5)
                        {
                            yield return new EvAlertRow("VAR", false, false, false, false, false,
                                0, 1, 4, 2,
                                $"Exposure time (Exp_Time) for failed runs peaked at {failedMaxExp:0} s vs " +
                                $"{goodMaxExp:0} s for good cycles. The workbook confirms Failed Cycle 2 logged " +
                                $"Exp_Time = 0 throughout — the sterilisation dwell phase was never entered. " +
                                $"Check recipe hold-time parameter and PLC exposure timer.",
                                false);
                        }
                    }
                    break;
                }

            // ── GOOD vs FAILED MIN/MAX ENVELOPE ──────────────────────────────
            case "good-failed-envelope":
                {
                    if (baselineAttempts.Count == 0 || reviewAttempts.Count == 0) break;

                    var allTempMetrics = GetTemperatureMetrics().ToList();

                    // MISS — failed-cycle sensor max falls entirely below the good-cycle minimum
                    foreach (var metric in allTempMetrics.Take(4))
                    {
                        var goodMin = baselineAttempts.SelectMany(a => a.Rows.Select(metric.GetValue))
                            .Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0d).Min();
                        var failedMax = reviewAttempts.SelectMany(a => a.Rows.Select(metric.GetValue))
                            .Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0d).Max();

                        if (goodMin > 0 && failedMax < goodMin)
                        {
                            yield return new EvAlertRow("MISS", false, true, false, false, false,
                                0, 1, 5, 3,
                                $"{metric.DisplayName}: failed cycles peaked at {failedMax:0.##} but the good-cycle minimum " +
                                $"was {goodMin:0.##}. This sensor never entered the sterilisation envelope. " +
                                $"The workbook shows Failed Cycle 2 T-sensors peaking at ~19–21 °C while good cycles " +
                                $"never dropped below ~19 °C at start — but the failed cycle never climbed to the 121 °C band.",
                                false);
                        }
                    }

                    // VAR — high variability within the failed envelope (σ > 15 % of mean)
                    foreach (var metric in allTempMetrics.Take(4))
                    {
                        var failedValues = reviewAttempts.SelectMany(a => a.Rows.Select(metric.GetValue))
                            .Where(v => v.HasValue).Select(v => v!.Value).ToList();
                        if (failedValues.Count < 3) continue;
                        var mean = failedValues.Average();
                        var stdDev = Math.Sqrt(failedValues.Average(v => Math.Pow(v - mean, 2)));
                        if (mean > 0 && stdDev > mean * 0.15)
                        {
                            yield return new EvAlertRow("VAR", false, false, true, false, false,
                                0, 1, 3, 2,
                                $"{metric.DisplayName} shows high variability across failed cycles (σ = {stdDev:0.##}, " +
                                $"mean = {mean:0.##}, CV = {stdDev / mean * 100:0.#}%). " +
                                $"Inconsistent heating may indicate an intermittent heater element, unstable steam supply, " +
                                $"or sensor noise from the STR34_T151 dropout seen in Failed Cycle 1.",
                                false);
                        }
                    }

                    // DRIFT — F0 lethality counter max in failed cycles vs good cycles
                    // Good cycles: F0_1 peaks at 43.2, F0_2 at 44.0; Failed Cycle 2: F0 = 0 throughout
                    var f0Metrics = MetricOptions.Where(m => GetSensorFamily(m.PropertyName) == SensorFamily.Lethality).ToList();
                    if (f0Metrics.Count > 0)
                    {
                        var goodF0Max = baselineAttempts.SelectMany(a => a.Rows.Select(f0Metrics[0].GetValue))
                            .Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0d).Max();
                        var failedF0Max = reviewAttempts.SelectMany(a => a.Rows.Select(f0Metrics[0].GetValue))
                            .Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0d).Max();

                        if (goodF0Max > 0 && failedF0Max < goodF0Max * 0.5)
                        {
                            yield return new EvAlertRow("DRIFT", false, false, false, true, false,
                                0, 1, 4, 2,
                                $"Lethality counter '{f0Metrics[0].DisplayName}' reached {failedF0Max:0.##} in failed cycles " +
                                $"vs {goodF0Max:0.##} in good cycles. The workbook confirms F0_1 and F0_2 remained at 0 " +
                                $"for Failed Cycle 2 — sterilisation lethality was never accumulated. " +
                                $"The batch is likely non-sterile and must not be released.",
                                false);
                        }
                    }
                    break;
                }

            // ── CYCLES INFO ───────────────────────────────────────────────────
            case "cycles-info":
                {
                    var allAttempts = GetAttemptSummariesForAnalysis();
                    if (allAttempts.Count == 0) break;

                    // DUR — duration overrun vs good-cycle average
                    var longestReview = reviewAttempts.OrderByDescending(a => a.DurationMinutes).FirstOrDefault();
                    if (longestReview is not null && baselineAttempts.Count > 0)
                    {
                        var avgGood = baselineAttempts.Average(a => a.DurationMinutes);
                        if (avgGood > 0 && longestReview.DurationMinutes > avgGood * 1.35)
                        {
                            yield return new EvAlertRow("DUR", true, true, false, false, false,
                                longestReview.DurationMinutes, 1, 5, 3,
                                $"{longestReview.Name} ran {longestReview.DurationMinutes / avgGood:0.#}× longer than the " +
                                $"good-cycle average of {avgGood:0} min. Duration overrun commonly indicates a stuck step, " +
                                $"failed interlock clearance, or an operator-extended hold. " +
                                $"In the workbook, Failed Cycle 1 shows a significantly higher mean Exp_Time (554 s) " +
                                $"than good cycles (142 s mean), indicating the cycle stayed active far longer than normal.",
                                false);
                        }
                    }

                    // SHORT — cycle significantly shorter than good-cycle average (Failed Cycle 2 pattern)
                    var shortestReview = reviewAttempts.OrderBy(a => a.DurationMinutes).FirstOrDefault();
                    if (shortestReview is not null && baselineAttempts.Count > 0)
                    {
                        var avgGood = baselineAttempts.Average(a => a.DurationMinutes);
                        if (avgGood > 0 && shortestReview.DurationMinutes < avgGood * 0.6)
                        {
                            yield return new EvAlertRow("SHORT", false, false, false, true, false,
                                shortestReview.DurationMinutes, 1, 3, 2,
                                $"{shortestReview.Name} ended after {shortestReview.DurationMinutes} min — well below the " +
                                $"good-cycle average of {avgGood:0} min. This matches the 'Failed Cycle 2' workbook pattern " +
                                $"where the cycle terminated at step 10 with zero exposure time. " +
                                $"Confirm whether this was a planned abort or an unexpected process halt.",
                                false);
                        }
                    }

                    // STEP — failed cycle did not reach expected process step (good cycles max step ~65)
                    var lowestStepFailed = reviewAttempts.OrderBy(a => a.MaxStep).FirstOrDefault();
                    var highestStepGood = baselineAttempts.Select(a => a.MaxStep).DefaultIfEmpty(0d).Max();
                    if (lowestStepFailed is not null && highestStepGood > 0 &&
                        lowestStepFailed.MaxStep < highestStepGood - 5)
                    {
                        yield return new EvAlertRow("STEP", false, true, false, false, false,
                            lowestStepFailed.DurationMinutes, 1, 3, 2,
                            $"{lowestStepFailed.Name} reached process step {lowestStepFailed.MaxStep:0} while good cycles " +
                            $"progressed to step {highestStepGood:0}. The workbook shows Failed Cycle 2 stuck at step 10 " +
                            $"(pre-load/standby), never advancing to the active sterilisation phase (steps 26+). " +
                            $"Review PLC logic for the step 10→26 transition and check all permissive conditions.",
                            false);
                    }

                    // Recipe mix across all cycle info data
                    var recipeCounts = allAttempts
                        .GroupBy(a => a.Recipe, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(g => g.Count()).ToList();
                    if (recipeCounts.Count > 1)
                    {
                        var dominant = recipeCounts.First();
                        var minor = recipeCounts.Skip(1).Sum(g => g.Count());
                        yield return new EvAlertRow("VAR", false, false, false, false, true,
                            0, 1, 2, 1,
                            $"Recipe mix detected: '{dominant.Key}' used in {dominant.Count()} attempt(s), " +
                            $"while {minor} attempt(s) ran other recipes. " +
                            $"Comparing cycles from different recipes inflates duration and step variance. " +
                            $"Confirm all cycles are from the same approved protocol before drawing conclusions.",
                            false);
                    }

                    // Q151 flow sensor: workbook shows Q151 jumped from 4 (good) to 5 (Failed Cycle 1)
                    var flowMetrics = MetricOptions.Where(m => GetSensorFamily(m.PropertyName) == SensorFamily.Flow).ToList();
                    if (flowMetrics.Count > 0)
                    {
                        var goodFlowAvg = baselineAttempts.SelectMany(a => a.Rows.Select(flowMetrics[0].GetValue))
                            .Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0d).Average();
                        var failedFlowAvg = reviewAttempts.SelectMany(a => a.Rows.Select(flowMetrics[0].GetValue))
                            .Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0d).Average();
                        if (goodFlowAvg > 0 && Math.Abs(failedFlowAvg - goodFlowAvg) > goodFlowAvg * 0.1)
                        {
                            yield return new EvAlertRow("VAR", false, false, true, false, false,
                                0, 1, 2, 1,
                                $"Flow sensor '{flowMetrics[0].DisplayName}' average shifted from {goodFlowAvg:0.##} (good) " +
                                $"to {failedFlowAvg:0.##} (failed). The workbook shows STR34_Q151 at 4 during good cycles " +
                                $"and 5 during Failed Cycle 1. An unexpected flow change can indicate a valve position error " +
                                $"or cooling-water flow irregularity.",
                                false);
                        }
                    }
                    break;
                }
        }
    }

    private EvAlertRow? BuildLiveMetricAlert(MetricOption metric, double currentValue, SterilizationCycle latestRow, IReadOnlyList<SterilizationCycle> baselineRows)
    {
        if (IsImpossibleSensorValue(metric, currentValue))
        {
            return CreateAlert("SNS", metric, currentValue, latestRow,
                $"The newest row has an impossible {metric.DisplayName} value of {currentValue:0.##}. This usually points to a sensor sentinel value, drop-out, or wiring fault.");
        }

        var history = baselineRows.Select(metric.GetValue).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (history.Count < 5) return null;

        var mean = history.Average();
        var standardDeviation = Math.Sqrt(history.Average(v => Math.Pow(v - mean, 2)));
        var min = history.Min();
        var max = history.Max();
        var tolerance = Math.Max(standardDeviation * 3d, Math.Abs(mean) * 0.12d);

        if (currentValue > max + tolerance)
            return CreateAlert("HIGH", metric, currentValue, latestRow,
                $"The newest row is above the normal {metric.DisplayName} range. Current {currentValue:0.##}, baseline max {max:0.##}, tolerance {tolerance:0.##}.");

        if (currentValue < min - tolerance)
            return CreateAlert("LOW", metric, currentValue, latestRow,
                $"The newest row is below the normal {metric.DisplayName} range. Current {currentValue:0.##}, baseline min {min:0.##}, tolerance {tolerance:0.##}.");

        if (metric.PropertyName.Contains("T", StringComparison.OrdinalIgnoreCase) &&
            currentValue < 100d && max > 118d)
            return CreateAlert("TEMP", metric, currentValue, latestRow,
                $"The newest row did not reach the expected sterilization temperature band. {metric.DisplayName} is {currentValue:0.##} while prior rows reached {max:0.##}.");

        return null;
    }

    private static bool IsImpossibleSensorValue(MetricOption metric, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return true;
        var family = GetSensorFamily(metric.PropertyName);
        if (family == SensorFamily.Temperature) return value < -50d || value > 160d;
        if (family == SensorFamily.Pressure) return value < -5d || value > 100d;
        return Math.Abs(value) > 500d;
    }

    private static EvAlertRow CreateAlert(string code, MetricOption metric, double currentValue, SterilizationCycle latestRow, string message)
        => new EvAlertRow(code, true, true, false, false, false, 1, 1, 5, 3,
            $"{metric.DisplayName}: {message} Latest sample at {latestRow.RecordedAt:dd MMM HH:mm}.", false);

    private void RefreshSummaryCollections()
    {
        CycleRuns.Clear(); CycleAttempts.Clear();
        foreach (var a in GetAttemptSummaries())
        {
            CycleRuns.Add(new CycleRunCard(
                a.Name, a.LeadText,
                $"{a.Start:dd MMM yyyy HH:mm} – {a.End:dd MMM yyyy HH:mm}",
                a.SampleCount, a.StatusColor));

            string statusIcon = "⚪";
            if (a.Status == "Complete") statusIcon = "✔️";
            else if (a.PeakAlarm > 0) statusIcon = "🔴";
            else if (a.Status == "Short Run") statusIcon = "⚠️";

            CycleAttempts.Add(new CycleAttemptRow(
                a.Name, a.Recipe, a.Date,
                a.Start.ToString("HH:mm"), a.End.ToString("HH:mm"),
                a.DurationMinutes, Math.Round(a.PeakAlarm, 0),
                string.IsNullOrWhiteSpace(a.PreIdleSteps) ? "-" : a.PreIdleSteps,
                a.RecipeLoadStep, a.Status, statusIcon));
        }
    }

    // ── computed properties ──
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
            return $"{standby.First().Name} never progressed past idle — no active steps detected.";
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
            return $"{VisibleCycles.Min(c => c.RecordedAt):dd MMM yyyy HH:mm}  →  {VisibleCycles.Max(c => c.RecordedAt):dd MMM yyyy HH:mm}";
        }
    }
    public string SummaryInsight
    {
        get
        {
            var a = GetAttemptSummaries();
            if (a.Count == 0) return "Import a workbook to build cycle-level summary data.";
            var failed = a.Where(x => x.Status != "Complete").ToList();
            var s = $"Dynamic attempt boundaries derived from imported step data — {DistinctRecipeCount} recipe families detected.";
            if (failed.Count > 0) s += $"  {failed.Count} attempt(s) flagged for review.";
            return s;
        }
    }

    public string TrendChartTitle => SelectedRepresentation?.DisplayName switch
    {
        "Temperature vs Pressure" => "Temperature / pressure sensor coordinates from workbook",
        "Good vs Failed Min/Max" => "Temperature sensor min/max: good vs failed workbook cycles",
        "Cycles Info" => "Cycle timing, duration, and process-state flow",
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
        : $"Showing {GetAttemptSummaries(applyTimeFilter: true).Count} attempts · {FilteredCycleCount} samples · {HeaderCatalog.Count} headers.";
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
        => ComparisonMetricOptions
            .Where(o => o.IsSelected && !o.IsSelectAll && o.Metric is not null)
            .Select(o => o.Metric!)
            .ToList();

    // ── time range selection ──
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

    // ── export ──
    private void ExportCsv()
    {
        if (VisibleCycles.Count == 0) { LastActionMessage = "No data to export."; return; }
        var path = new ExportService().ExportCsv(_exportDirectory, VisibleCycles);
        LastActionMessage = $"CSV exported → {path}";
    }
    private void ExportJson()
    {
        if (VisibleCycles.Count == 0) { LastActionMessage = "No data to export."; return; }
        var path = new ExportService().ExportJson(_exportDirectory, VisibleCycles);
        LastActionMessage = $"JSON exported → {path}";
    }

    // ── metric options ──
    private void BuildMetricOptions()
    {
        var prevKey = _selectedMetric?.PropertyName;
        var selectAllWasEnabled = ComparisonMetricOptions.FirstOrDefault(option => option.IsSelectAll)?.IsSelected == true;
        var selComps = ComparisonMetricOptions.Where(o => o.IsSelected && !o.IsSelectAll).Select(o => o.PropertyName)
                           .ToHashSet(StringComparer.OrdinalIgnoreCase);
        MetricOptions.Clear(); ComparisonMetricOptions.Clear();

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
            comparison.SetSelectedSilently(selectAllWasEnabled || selComps.Contains(metric.PropertyName));
            ComparisonMetricOptions.Add(comparison);
        }

        SyncSelectAllMetricOption();

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

    private void SyncSelectAllMetricOption()
    {
        var selectAllOption = ComparisonMetricOptions.FirstOrDefault(option => option.IsSelectAll);
        if (selectAllOption is null)
            return;

        var selectableOptions = ComparisonMetricOptions.Where(option => !option.IsSelectAll).ToList();
        var areAllSelected = selectableOptions.Count > 0 && selectableOptions.All(option => option.IsSelected);
        selectAllOption.SetSelectedSilently(areAllSelected);
    }

    // ── drill-down ──
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
        if (chartPoint.Instance is MetricPoint metricPoint
            && !string.IsNullOrWhiteSpace(metricPoint.AttemptName)
            && HasAttemptDrillDown(metricPoint.AttemptName))
        {
            OpenDrillDownForAttempt(metricPoint.AttemptName, chartPoint.SeriesView.Title, metricPoint);
            return;
        }

        if (chartPoint.SeriesView?.Values is not null)
        {
            var pointIndex = (int)Math.Round(chartPoint.X);
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

    private void OpenDrillDownForAttempt(string attemptName, string? metricName = null, MetricPoint? clickedPoint = null)
    {
        var attempt = AllAttemptSummaries.FirstOrDefault(a => a.Name == attemptName);
        if (attempt == null) return;

        DrillDownTitle = attempt.Name;
        DrillDownPointSummary = clickedPoint is null
            ? "Showing the full workbook attempt trace."
            : $"Selected point from workbook: X = {clickedPoint.Timestamp:dd-MM-yyyy HH:mm:ss}, Y = {clickedPoint.Value:0.##}, Metric = {clickedPoint.MetricName}";
        DrillDownSubtitle = $"{attempt.Date} • {attempt.DurationMinutes} min • {attempt.Status}";

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

        if ((metricName == "Highest process step" || metricName == "Process-state changes") && StepHeaderKey != null)
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
            AppendWorkbookRowDetails(GetTimelineRowsForLabel(label), seriesTitle, label, pointValue);
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
            DrillDownSeries.Add(new ColumnSeries
            {
                Title = "Visible series values",
                Values = categoryValues,
                LabelPoint = TooltipLabelPoint,
                Fill = palette[colorIndex % palette.Length],
                MaxColumnWidth = 42,
                ColumnPadding = 4
            });
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

    private IEnumerable<EvAlertRow> BuildStepSequenceAlerts(
        IReadOnlyList<AttemptSummary> baselineAttempts,
        IReadOnlyList<AttemptSummary> reviewAttempts)
    {
        if (string.IsNullOrWhiteSpace(StepNameHeaderKey) || baselineAttempts.Count == 0 || reviewAttempts.Count == 0)
            yield break;

        var expectedSequence = GetExpectedPhaseSequence(baselineAttempts);
        if (expectedSequence.Count == 0)
            yield break;

        var expectedSequenceText = string.Join(" -> ", expectedSequence);

        foreach (var attempt in reviewAttempts.Where(a => a.Rows.Count > 0 && a.Status != "Standby Only"))
        {
            var compressedSequence = GetCompressedPhaseSequence(attempt);
            if (compressedSequence.Count == 0)
                continue;

            var actualUniqueSequence = GetDistinctPhaseSequence(compressedSequence);
            var missingPhases = expectedSequence
                .Where(phase => !actualUniqueSequence.Contains(phase, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (missingPhases.Count > 0)
            {
                yield return new EvAlertRow("STEP", false, true, false, false, false,
                    attempt.DurationMinutes, missingPhases.Count, 4, 3,
                    $"{attempt.Name} skipped step name(s): {string.Join(", ", missingPhases)}. " +
                    $"Good cycles usually follow {expectedSequenceText}, while this attempt followed " +
                    $"{string.Join(" -> ", actualUniqueSequence)}.",
                    false);
            }

            var repeatedPhases = compressedSequence
                .GroupBy(phase => phase, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .OrderByDescending(group => group.Count())
                .ToList();

            if (repeatedPhases.Count > 0)
            {
                var repeatedSummary = string.Join(", ", repeatedPhases
                    .Select(group => $"{group.First()} x{group.Count()}")
                    .Take(3));
                yield return new EvAlertRow("STEP", false, true, false, false, false,
                    attempt.DurationMinutes, repeatedPhases.Sum(group => group.Count() - 1), 4, 2,
                    $"{attempt.Name} repeated step name(s): {repeatedSummary}. " +
                    $"Workbook phase trace: {string.Join(" -> ", compressedSequence)}. " +
                    $"Expected good-cycle order: {expectedSequenceText}.",
                    false);
            }
        }
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
        var sequence = new List<string>();
        string? lastPhase = null;

        foreach (var row in attempt.Rows)
        {
            var phase = GetProcessPhaseLabel(row);
            if (string.IsNullOrWhiteSpace(phase) || string.Equals(phase, "Unknown", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(phase, lastPhase, StringComparison.OrdinalIgnoreCase))
                continue;

            sequence.Add(phase);
            lastPhase = phase;
        }

        return sequence;
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

    // ── data transfer records ──
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

    // ── NEW: expanded EvAlertRow with badge colour and priority label ──
    public sealed record EvAlertRow(
        string ClassCode,
        bool SameMTSU,
        bool SamePosition,
        bool SameUVBay,
        bool SameExtTube,
        bool SameCuvette,
        int RepeatMinutes,
        int RepeatCount,
        int AwardPoints,
        int Priority,
        string SuggestedAction,
        bool ActionTaken
    )
    {
        /// <summary>Background colour for the class-code badge in the warning tile.</summary>
        public string BadgeColor => ClassCode switch
        {
            "SNS" => "#8B1A1A",   // dark red   — sensor hardware fault
            "TEMP" => "#B85C00",   // amber      — temperature deviation
            "PRESS" => "#1A5C8B",   // blue       — pressure deviation
            "DUR" => "#7A5C00",   // dark gold  — duration overrun
            "SHORT" => "#5C5C00",   // olive      — short / aborted cycle
            "STEP" => "#3D5C00",   // dark green — step abort
            "IDLE" => "#444466",   // muted blue — standby only
            "HIGH" => "#8B1A1A",   // dark red   — value too high
            "LOW" => "#1A5C8B",   // blue       — value too low
            "DRIFT" => "#5A2D82",   // purple     — slow drift
            "MISS" => "#6B3E00",   // brown      — missed sterilisation window
            "VAR" => "#1A5C5C",   // teal       — variability / recipe mix
            "ALM" => "#8B1A1A",   // dark red   — alarm-count
            _ => "#444444"
        };

        /// <summary>Human-readable priority label shown beside the badge.</summary>
        public string PriorityLabel => Priority switch
        {
            1 => "Low",
            2 => "Medium",
            3 => "High",
            4 => "Critical",
            _ => "Info"
        };
    }

    // ── NEW: CycleAttemptSelectOption record ──
    public sealed record CycleAttemptSelectOption(string Key, string DisplayName, bool IsAll = false)
    {
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
        string Date, string LeadText, IReadOnlyList<SterilizationCycle> Rows);

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
