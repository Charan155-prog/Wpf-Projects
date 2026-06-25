using ExcelDataReader;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using SterilizationGenie.Infrastructure;
using SterilizationGenie.Models;
using System.Globalization;
using System.IO;

namespace SterilizationGenie.Services;

public sealed class WorkbookImportResult
{
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public List<CycleHeaderDefinition> ImportedHeaders { get; set; } = new List<CycleHeaderDefinition>();
    public List<SterilizationCycle> ImportedCycles { get; set; } = new List<SterilizationCycle>();
    public List<string> SourceFiles { get; set; } = new List<string>();
    public Dictionary<string, int> ImportedSheetRowPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WorkbookImportService
{
    public WorkbookImportResult ImportFiles(IEnumerable<string> filePaths)
    {
        var distinctPaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctPaths.Count == 0)
        {
            return new WorkbookImportResult
            {
                Success = false,
                Error = "No valid workbook files were selected."
            };
        }

        var result = new WorkbookImportResult();

        try
        {
            var partialResults = distinctPaths
                // Bug #1C - NPOI's XSSFWorkbook is not thread-safe; process files sequentially
                // to avoid race conditions in the shared-strings table or formula evaluator.
                .Select(path => ImportSingleFile(path))
                .ToList();

            var failedImport = partialResults.FirstOrDefault(partial => !partial.Success);
            if (failedImport is not null)
            {
                return failedImport;
            }

            foreach (var partial in partialResults)
            {
                result.ImportedCycles.AddRange(partial.ImportedCycles);
            }

            result.ImportedHeaders = result.ImportedCycles
                .SelectMany(cycle => cycle.Values)
                .Where(value => value.Header is not null)
                .Select(value => value.Header!)
                .GroupBy(header => header.NormalizedName, StringComparer.OrdinalIgnoreCase)
                .Select((group, index) => new CycleHeaderDefinition
                {
                    Name = group.OrderBy(header => header.DisplayOrder).First().Name,
                    NormalizedName = group.Key,
                    DisplayOrder = index,
                    IsNumeric = group.Any(header => header.IsNumeric)
                })
                .OrderBy(header => header.DisplayOrder)
                .ThenBy(header => header.Name)
                .ToList();
            result.SourceFiles = distinctPaths
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()!;
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    public WorkbookImportResult ImportFile(string filePath)
    {
        return ImportSingleFile(filePath);
    }

    public WorkbookImportResult ImportNewRows(string filePath, IReadOnlyDictionary<string, int> priorSheetRowPositions)
    {
        // Prefer the cheap streaming delta path for live mode. Some Excel writers
        // briefly produce a package state ExcelDataReader cannot open, so keep a
        // bounded NPOI delta fallback instead of letting live mode go silent.
        try
        {
            return ImportNewRowsStreaming(filePath, priorSheetRowPositions);
        }
        catch (Exception streamingEx)
        {
            var fallback = ImportSingleFile(filePath, priorSheetRowPositions);
            if (!fallback.Success)
            {
                fallback.ImportedSheetRowPositions = new Dictionary<string, int>(priorSheetRowPositions, StringComparer.OrdinalIgnoreCase);
                fallback.Error = $"Live workbook read failed. Streaming: {streamingEx.Message}; fallback: {fallback.Error}";
            }
            return fallback;
        }
    }
    private WorkbookImportResult ImportNewRowsStreaming(string filePath, IReadOnlyDictionary<string, int> priorSheetRowPositions)
    {
        var result = new WorkbookImportResult();
        var headerCatalog = new Dictionary<string, CycleHeaderDefinition>(StringComparer.OrdinalIgnoreCase);
        var nextDisplayOrder = 0;

        // FileShare.ReadWrite + FileShare.Delete matches the existing NPOI
        // path's sharing mode so this can read a file the generator still
        // has open for writing.
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = ExcelReaderFactory.CreateReader(fs);

        var sheetIndex = 0;
        do
        {
            var sheetName = reader.Name;
            var sheetKey = BuildSheetPositionKey(sheetIndex, sheetName);
            var priorLastRowIndex = -1;
            if (priorSheetRowPositions is not null &&
                !priorSheetRowPositions.TryGetValue(sheetKey, out priorLastRowIndex))
            {
                priorLastRowIndex = -1;
            }
            var minRowIndexInclusive = priorLastRowIndex + 1;

            var sheetNameUpper = sheetName.ToUpperInvariant();
            var isMetadataSheet = sheetNameUpper.Contains("TAG") ||
                                   sheetNameUpper.Contains("DESCRIPTION") ||
                                   sheetNameUpper.Contains("METADATA") ||
                                   sheetNameUpper.Contains("LEGEND");

            var (cycles, lastRowIndex) = isMetadataSheet
                ? ([], -1)
                : StreamCycleSheet(reader, sheetName, sheetIndex, Path.GetFileName(filePath),
                    headerCatalog, ref nextDisplayOrder, minRowIndexInclusive);

            result.ImportedCycles.AddRange(cycles);
            // Preserve whatever the prior position was if this sheet had no
            // rows at all yet (mirrors NPOI path's sheet.LastRowNum, which is
            // -1 for an empty sheet) so the cursor doesn't regress.
            result.ImportedSheetRowPositions[sheetKey] = Math.Max(lastRowIndex, priorLastRowIndex);

            sheetIndex++;
        } while (reader.NextResult());

        result.ImportedHeaders = headerCatalog.Values
            .OrderBy(header => header.DisplayOrder)
            .ThenBy(header => header.Name)
            .ToList();
        result.SourceFiles = [Path.GetFileName(filePath)];
        result.Success = true;
        return result;
    }

    /// <summary>
    /// Streams a single sheet via ExcelDataReader, mirroring
    /// ReadDynamicCycleSheet's header-detection / column-mapping /
    /// timestamp-resolution rules but operating on raw row values instead of
    /// NPOI IRow/ICell, and skipping rows below minRowIndexInclusive without
    /// allocating ColumnHeader/cell objects for them.
    /// </summary>
    private (List<SterilizationCycle> Cycles, int LastRowIndex) StreamCycleSheet(
        IExcelDataReader reader,
        string sheetName,
        int sheetIndex,
        string workbookSource,
        IDictionary<string, CycleHeaderDefinition> headerCatalog,
        ref int nextDisplayOrder,
        int minRowIndexInclusive)
    {
        var cycles = new List<SterilizationCycle>();
        List<ColumnHeader>? columns = null;
        var timestampColumn = -1;
        var perDateOffsets = new Dictionary<DateOnly, int>();
        var rowIndex = -1;
        var lastRowIndex = -1;

        while (reader.Read())
        {
            rowIndex++;
            lastRowIndex = rowIndex;

            // ExcelDataReader must advance through earlier rows, but it does not need to
            // materialize every cell again once the header has already been resolved.
            if (columns is not null && rowIndex < minRowIndexInclusive)
            {
                continue;
            }

            var fieldCount = reader.FieldCount;
            var values = new object?[fieldCount];
            for (var i = 0; i < fieldCount; i++)
            {
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            if (columns is null)
            {
                // Still hunting for the header row (first 20 rows only, same
                // bound as FindHeaderRow). Once headerRowIndex's data rows
                // would already be behind minRowIndexInclusive on subsequent
                // ticks, this loop is skipped entirely below.
                if (rowIndex > 20)
                {
                    // No header found within the scan window - same outcome
                    // as FindHeaderRow returning null.
                    return (cycles, lastRowIndex);
                }

                var textHeaders = values
                    .Select(v => v?.ToString()?.Trim())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .ToList();

                if (textHeaders.Count < 5)
                {
                    continue;
                }

                var str34Count = textHeaders.Count(v => HeaderNameHelper.Normalize(v!).Contains("STR34", StringComparison.OrdinalIgnoreCase));
                var namedMetricCount = textHeaders.Count(v =>
                    HeaderNameHelper.Normalize(v!).Contains("RECIPE", StringComparison.OrdinalIgnoreCase) ||
                    HeaderNameHelper.Normalize(v!).Contains("STEP", StringComparison.OrdinalIgnoreCase) ||
                    HeaderNameHelper.Normalize(v!).Contains("EXP", StringComparison.OrdinalIgnoreCase));

                if (str34Count < 5 && !(str34Count >= 3 && namedMetricCount >= 2))
                {
                    continue;
                }

                columns = BuildColumnHeadersFromValues(values, headerCatalog, ref nextDisplayOrder);
                if (columns.Count == 0)
                {
                    return (cycles, lastRowIndex);
                }
                timestampColumn = ResolveTimestampColumn(columns);
                continue;
            }

            if (RowIsEmptyValues(values))
            {
                continue;
            }

            if (!TryResolveRecordedAtFromValues(values, timestampColumn, columns, perDateOffsets, out var recordedAt))
            {
                continue;
            }

            var matchCount = 0;
            var totalChecked = 0;
            foreach (var column in columns)
            {
                var rawValue = GetValueString(SafeGet(values, column.ColumnIndex));
                if (!string.IsNullOrWhiteSpace(rawValue))
                {
                    totalChecked++;
                    var trimmedVal = rawValue.Trim();
                    if (string.Equals(trimmedVal, column.Header.Name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(trimmedVal, column.Header.NormalizedName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchCount++;
                    }
                }
            }
            if (totalChecked >= 2 && (double)matchCount / totalChecked >= 0.5)
            {
                continue;
            }

            var cycle = new SterilizationCycle
            {
                SourceWorkbookName = string.IsNullOrWhiteSpace(workbookSource) ? string.Empty : Path.GetFileName(workbookSource).Trim(),
                SheetName = sheetName,
                SheetIndex = sheetIndex,
                RecordedAt = recordedAt
            };

            foreach (var column in columns)
            {
                var rawCellValue = SafeGet(values, column.ColumnIndex);
                var rawValue = GetValueString(rawCellValue);

                double? numericValue = TryGetNumericFromValue(rawCellValue, rawValue, out var number) ? number : null;
                if (numericValue.HasValue)
                {
                    column.Header.IsNumeric = true;
                }

                cycle.AddValue(column.Header, rawValue, numericValue);
            }

            cycle.ResetLookup();
            cycles.Add(cycle);
        }

        return (cycles, lastRowIndex);
    }

    private WorkbookImportResult ImportSingleFile(string filePath, IReadOnlyDictionary<string, int>? priorSheetRowPositions = null)
    {
        var result = new WorkbookImportResult();

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var workbook = new XSSFWorkbook(fs);

            var headerCatalog = new Dictionary<string, CycleHeaderDefinition>(StringComparer.OrdinalIgnoreCase);
            var nextDisplayOrder = 0;

            for (var sheetIndex = 0; sheetIndex < workbook.NumberOfSheets; sheetIndex++)
            {
                var sheet = workbook.GetSheetAt(sheetIndex);
                var sheetKey = BuildSheetPositionKey(sheetIndex, sheet.SheetName);
                var priorLastRowIndex = -1;
                if (priorSheetRowPositions is not null &&
                    !priorSheetRowPositions.TryGetValue(sheetKey, out priorLastRowIndex))
                {
                    priorLastRowIndex = -1;
                }
                result.ImportedSheetRowPositions[sheetKey] = sheet.LastRowNum;
                var cycles = ReadDynamicCycleSheet(
                    sheet,
                    Path.GetFileName(filePath),
                    sheetIndex,
                    headerCatalog,
                    ref nextDisplayOrder,
                    priorSheetRowPositions is null ? null : priorLastRowIndex + 1);
                result.ImportedCycles.AddRange(cycles);
            }

            result.ImportedHeaders = headerCatalog.Values
                .OrderBy(header => header.DisplayOrder)
                .ThenBy(header => header.Name)
                .ToList();
            result.SourceFiles = [Path.GetFileName(filePath)];
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    private List<SterilizationCycle> ReadDynamicCycleSheet(
        ISheet sheet,
        string workbookSource,
        int sheetIndex,
        IDictionary<string, CycleHeaderDefinition> headerCatalog,
        ref int nextDisplayOrder,
        int? minRowIndexInclusive)
    {
        var cycles = new List<SterilizationCycle>();
        var headerInfo = FindHeaderRow(sheet);

        if (headerInfo is null)
        {
            return cycles;
        }

        var (headerRowIndex, headerRow) = headerInfo.Value;
        var columns = BuildColumnHeaders(headerRow, headerCatalog, ref nextDisplayOrder);
        if (columns.Count == 0)
        {
            return cycles;
        }

        var timestampColumn = ResolveTimestampColumn(columns);
        var perDateOffsets = new Dictionary<DateOnly, int>();
        var sampleIndex = 0;

        var startRowIndex = Math.Max(headerRowIndex + 1, minRowIndexInclusive ?? headerRowIndex + 1);
        for (var rowIndex = startRowIndex; rowIndex <= sheet.LastRowNum; rowIndex++)
        {
            var row = sheet.GetRow(rowIndex);
            if (row is null || RowIsEmpty(row))
            {
                continue;
            }

            if (!TryResolveRecordedAt(row, timestampColumn, columns, perDateOffsets, out var recordedAt))
            {
                continue;
            }

            // Check if this row is actually a duplicate header row in the middle of the data.
            // A row is a header row if several of its key columns contain their own header names.
            var matchCount = 0;
            var totalChecked = 0;
            foreach (var column in columns)
            {
                var cell = row.GetCell(column.ColumnIndex);
                var rawValue = GetCellString(cell);
                if (!string.IsNullOrWhiteSpace(rawValue))
                {
                    totalChecked++;
                    var trimmedVal = rawValue.Trim();
                    if (string.Equals(trimmedVal, column.Header.Name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(trimmedVal, column.Header.NormalizedName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchCount++;
                    }
                }
            }
            if (totalChecked >= 2 && (double)matchCount / totalChecked >= 0.5)
            {
                continue;
            }

            var cycle = new SterilizationCycle
            {
                SourceWorkbookName = string.IsNullOrWhiteSpace(workbookSource) ? string.Empty : Path.GetFileName(workbookSource).Trim(),
                SheetName = sheet.SheetName,
                SheetIndex = sheetIndex,
                RecordedAt = recordedAt
            };

            foreach (var column in columns)
            {
                var cell = row.GetCell(column.ColumnIndex);
                var rawValue = GetCellString(cell) ?? string.Empty;

                double? numericValue = TryGetNumeric(cell, rawValue, out var number) ? number : null;
                if (numericValue.HasValue)
                {
                    column.Header.IsNumeric = true;
                }

                cycle.AddValue(column.Header, rawValue, numericValue);
            }

            cycle.ResetLookup();
            cycles.Add(cycle);
            sampleIndex++;
        }

        return cycles;
    }

    private static (int RowIndex, IRow HeaderRow)? FindHeaderRow(ISheet sheet)
    {
        // Bug #1A - skip sheets that are metadata / tag-description sheets; they have no cycle data
        // and their rows would otherwise be misidentified as header candidates.
        var sheetNameUpper = sheet.SheetName.ToUpperInvariant();
        if (sheetNameUpper.Contains("TAG") ||
            sheetNameUpper.Contains("DESCRIPTION") ||
            sheetNameUpper.Contains("METADATA") ||
            sheetNameUpper.Contains("LEGEND"))
        {
            return null;
        }

        for (var rowIndex = sheet.FirstRowNum; rowIndex <= Math.Min(sheet.LastRowNum, sheet.FirstRowNum + 20); rowIndex++)
        {
            var row = sheet.GetRow(rowIndex);
            if (row is null)
            {
                continue;
            }

            var textHeaders = Enumerable.Range(0, Math.Max((int)row.LastCellNum, 0))
                .Select(index => row.GetCell(index)?.ToString()?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

            if (textHeaders.Count < 5)
            {
                continue;
            }

            var str34Count = textHeaders.Count(value => HeaderNameHelper.Normalize(value!).Contains("STR34", StringComparison.OrdinalIgnoreCase));
            var namedMetricCount = textHeaders.Count(value => HeaderNameHelper.Normalize(value!).Contains("RECIPE", StringComparison.OrdinalIgnoreCase)
                                                          || HeaderNameHelper.Normalize(value!).Contains("STEP", StringComparison.OrdinalIgnoreCase)
                                                          || HeaderNameHelper.Normalize(value!).Contains("EXP", StringComparison.OrdinalIgnoreCase));

            if (str34Count >= 5 || (str34Count >= 3 && namedMetricCount >= 2))
            {
                return (rowIndex, row);
            }
        }

        return null;
    }

    private static List<ColumnHeader> BuildColumnHeaders(
        IRow headerRow,
        IDictionary<string, CycleHeaderDefinition> headerCatalog,
        ref int nextDisplayOrder)
    {
        var columns = new List<ColumnHeader>();

        for (var columnIndex = 0; columnIndex < headerRow.LastCellNum; columnIndex++)
        {
            var rawHeader = headerRow.GetCell(columnIndex)?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(rawHeader))
            {
                continue;
            }

            var normalizedName = HeaderNameHelper.Normalize(rawHeader);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                continue;
            }

            if (!headerCatalog.TryGetValue(normalizedName, out var header))
            {
                header = new CycleHeaderDefinition
                {
                    Name = rawHeader,
                    NormalizedName = normalizedName,
                    DisplayOrder = nextDisplayOrder++
                };

                headerCatalog[normalizedName] = header;
            }

            columns.Add(new ColumnHeader(columnIndex, header));
        }

        return columns;
    }

    /// <summary>
    /// Same logic as BuildColumnHeaders, operating on raw row values read via
    /// ExcelDataReader instead of an NPOI IRow, for the streaming delta path.
    /// </summary>
    private static List<ColumnHeader> BuildColumnHeadersFromValues(
        object?[] headerRowValues,
        IDictionary<string, CycleHeaderDefinition> headerCatalog,
        ref int nextDisplayOrder)
    {
        var columns = new List<ColumnHeader>();

        for (var columnIndex = 0; columnIndex < headerRowValues.Length; columnIndex++)
        {
            var rawHeader = headerRowValues[columnIndex]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(rawHeader))
            {
                continue;
            }

            var normalizedName = HeaderNameHelper.Normalize(rawHeader);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                continue;
            }

            if (!headerCatalog.TryGetValue(normalizedName, out var header))
            {
                header = new CycleHeaderDefinition
                {
                    Name = rawHeader,
                    NormalizedName = normalizedName,
                    DisplayOrder = nextDisplayOrder++
                };

                headerCatalog[normalizedName] = header;
            }

            columns.Add(new ColumnHeader(columnIndex, header));
        }

        return columns;
    }

    private static int ResolveTimestampColumn(IEnumerable<ColumnHeader> columns)
    {
        var colList = columns.ToList();

        var exactMatch = colList.FirstOrDefault(column =>
            column.Header.NormalizedName.Contains("TIMESTAMP", StringComparison.OrdinalIgnoreCase) ||
            column.Header.NormalizedName.Contains("RECORDEDAT", StringComparison.OrdinalIgnoreCase));

        if (exactMatch is not null)
        {
            return exactMatch.ColumnIndex;
        }

        var dateMatch = colList.FirstOrDefault(column =>
            column.Header.NormalizedName.Contains("DATE", StringComparison.OrdinalIgnoreCase) ||
            column.Header.NormalizedName.Contains("TIME", StringComparison.OrdinalIgnoreCase));

        if (dateMatch is not null)
        {
            return dateMatch.ColumnIndex;
        }

        // Bug #1B - none of the workbook's 27 named headers contain TIMESTAMP / DATE / TIME
        // (the timestamp lives in column 0 with no label).  Fall back to the first column so
        // TryResolveRecordedAt can always locate a timestamp without relying on Strategy 3 alone.
        return colList.FirstOrDefault()?.ColumnIndex ?? 0;
    }

    private static bool TryResolveRecordedAt(
        IRow row,
        int timestampColumn,
        IEnumerable<ColumnHeader> columns,
        IDictionary<DateOnly, int> perDateOffsets,
        out DateTime recordedAt)
    {
        // Check for separate DATE and TIME headers
        var dateCol = columns.FirstOrDefault(c => c.Header.NormalizedName.Equals("DATE", StringComparison.OrdinalIgnoreCase) || c.Header.NormalizedName.EndsWith("DATE", StringComparison.OrdinalIgnoreCase));
        var timeCol = columns.FirstOrDefault(c => c.Header.NormalizedName.Equals("TIME", StringComparison.OrdinalIgnoreCase) || c.Header.NormalizedName.EndsWith("TIME", StringComparison.OrdinalIgnoreCase));

        if (dateCol != null && timeCol != null)
        {
            if (TryGetTimestampInfo(row.GetCell(dateCol.ColumnIndex), out var dVal, out _) &&
                TryGetTimestampInfo(row.GetCell(timeCol.ColumnIndex), out var tVal, out _))
            {
                recordedAt = dVal.Date + tVal.TimeOfDay;
                return true;
            }
        }

        if (timestampColumn >= 0 && TryGetTimestampInfo(row.GetCell(timestampColumn), out var directTimestamp, out var directHasTime))
        {
            recordedAt = directHasTime
                ? directTimestamp
                : directTimestamp.Date.AddMinutes(NextMinuteOffset(perDateOffsets, DateOnly.FromDateTime(directTimestamp)));
            return true;
        }

        if (TryGetTimestampInfo(row.GetCell(0), out var firstCellTimestamp, out var firstCellHasTime))
        {
            recordedAt = firstCellHasTime
                ? firstCellTimestamp
                : firstCellTimestamp.Date.AddMinutes(NextMinuteOffset(perDateOffsets, DateOnly.FromDateTime(firstCellTimestamp)));
            return true;
        }

        foreach (var column in columns)
        {
            if (TryGetTimestampInfo(row.GetCell(column.ColumnIndex), out var columnTimestamp, out var columnHasTime))
            {
                recordedAt = columnHasTime
                    ? columnTimestamp
                    : columnTimestamp.Date.AddMinutes(NextMinuteOffset(perDateOffsets, DateOnly.FromDateTime(columnTimestamp)));
                return true;
            }
        }

        recordedAt = default;
        return false;
    }

    private static int NextMinuteOffset(IDictionary<DateOnly, int> perDateOffsets, DateOnly date)
    {
        if (!perDateOffsets.TryGetValue(date, out var offset))
        {
            perDateOffsets[date] = 1;
            return 0;
        }

        perDateOffsets[date] = offset + 1;
        return offset;
    }

    private static string BuildSheetPositionKey(int sheetIndex, string sheetName)
        => $"{sheetIndex}:{(sheetName ?? string.Empty).Trim()}";

    private static bool RowIsEmpty(IRow row)
    {
        for (var index = row.FirstCellNum; index < row.LastCellNum; index++)
        {
            if (!string.IsNullOrWhiteSpace(row.GetCell(index)?.ToString()))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Same logic as TryResolveRecordedAt, operating on raw row values from
    /// ExcelDataReader instead of an NPOI IRow.
    /// </summary>
    private static bool TryResolveRecordedAtFromValues(
        object?[] values,
        int timestampColumn,
        IEnumerable<ColumnHeader> columns,
        IDictionary<DateOnly, int> perDateOffsets,
        out DateTime recordedAt)
    {
        var columnList = columns as IList<ColumnHeader> ?? columns.ToList();

        var dateCol = columnList.FirstOrDefault(c => c.Header.NormalizedName.Equals("DATE", StringComparison.OrdinalIgnoreCase) || c.Header.NormalizedName.EndsWith("DATE", StringComparison.OrdinalIgnoreCase));
        var timeCol = columnList.FirstOrDefault(c => c.Header.NormalizedName.Equals("TIME", StringComparison.OrdinalIgnoreCase) || c.Header.NormalizedName.EndsWith("TIME", StringComparison.OrdinalIgnoreCase));

        if (dateCol != null && timeCol != null)
        {
            if (TryGetTimestampInfoFromValue(SafeGet(values, dateCol.ColumnIndex), out var dVal, out _) &&
                TryGetTimestampInfoFromValue(SafeGet(values, timeCol.ColumnIndex), out var tVal, out _))
            {
                recordedAt = dVal.Date + tVal.TimeOfDay;
                return true;
            }
        }

        if (timestampColumn >= 0 && TryGetTimestampInfoFromValue(SafeGet(values, timestampColumn), out var directTimestamp, out var directHasTime))
        {
            recordedAt = directHasTime
                ? directTimestamp
                : directTimestamp.Date.AddMinutes(NextMinuteOffset(perDateOffsets, DateOnly.FromDateTime(directTimestamp)));
            return true;
        }

        if (TryGetTimestampInfoFromValue(SafeGet(values, 0), out var firstCellTimestamp, out var firstCellHasTime))
        {
            recordedAt = firstCellHasTime
                ? firstCellTimestamp
                : firstCellTimestamp.Date.AddMinutes(NextMinuteOffset(perDateOffsets, DateOnly.FromDateTime(firstCellTimestamp)));
            return true;
        }

        foreach (var column in columnList)
        {
            if (TryGetTimestampInfoFromValue(SafeGet(values, column.ColumnIndex), out var columnTimestamp, out var columnHasTime))
            {
                recordedAt = columnHasTime
                    ? columnTimestamp
                    : columnTimestamp.Date.AddMinutes(NextMinuteOffset(perDateOffsets, DateOnly.FromDateTime(columnTimestamp)));
                return true;
            }
        }

        recordedAt = default;
        return false;
    }

    private static object? SafeGet(object?[] values, int index)
        => index >= 0 && index < values.Length ? values[index] : null;

    private static bool RowIsEmptyValues(object?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value?.ToString()))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Same logic as TryGetTimestampInfo, operating on a raw cell value from
    /// ExcelDataReader instead of an NPOI ICell. ExcelDataReader already
    /// returns date-formatted .xlsx cells as DateTime (unlike NPOI, which
    /// requires checking DateUtil.IsCellDateFormatted against a raw double),
    /// so that case is checked first; numeric/string fallbacks mirror the
    /// NPOI version exactly.
    /// </summary>
    private static bool TryGetTimestampInfoFromValue(object? cellValue, out DateTime value, out bool hasTime)
    {
        value = DateTime.MinValue;
        hasTime = false;

        if (cellValue is null) return false;

        bool ValidateOrAdjustRange(DateTime dt, out DateTime v, out bool ht)
        {
            v = dt;
            ht = dt.TimeOfDay.TotalSeconds > 0;
            if (dt.Year < 1900) return false;
            return dt.Year is >= 2000 and <= 2100;
        }

        if (cellValue is DateTime dtValue)
        {
            return ValidateOrAdjustRange(dtValue, out value, out hasTime);
        }

        if (cellValue is double numericValue)
        {
            try
            {
                return ValidateOrAdjustRange(DateTime.FromOADate(numericValue), out value, out hasTime);
            }
            catch
            {
                return false;
            }
        }

        if (cellValue is int or long or float or decimal)
        {
            try
            {
                return ValidateOrAdjustRange(DateTime.FromOADate(Convert.ToDouble(cellValue, CultureInfo.InvariantCulture)), out value, out hasTime);
            }
            catch
            {
                return false;
            }
        }

        var raw = cellValue.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return false;

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var parsed1))
            return ValidateOrAdjustRange(parsed1, out value, out hasTime);

        if (DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var parsed2))
            return ValidateOrAdjustRange(parsed2, out value, out hasTime);

        string[] formats =
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd H:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd H:mm",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss.fff",
            "dd/MM/yyyy",
            "d/M/yyyy",
            "dd-MM-yyyy",
            "d-M-yyyy",
            "dd/MM/yyyy HH:mm:ss",
            "d/M/yyyy HH:mm:ss",
            "dd-MM-yyyy HH:mm:ss",
            "d-M-yyyy HH:mm:ss",
            "dd/MM/yyyy HH:mm",
            "d/M/yyyy HH:mm",
            "dd-MM-yyyy HH:mm",
            "d-M-yyyy HH:mm",
            "HH:mm:ss",
            "H:mm:ss",
            "HH:mm",
            "H:mm"
        };
        if (DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var parsed3))
            return ValidateOrAdjustRange(parsed3, out value, out hasTime);

        return false;
    }

    /// <summary>
    /// Same logic as GetCellString, operating on a raw ExcelDataReader value.
    /// </summary>
    private static string GetValueString(object? cellValue)
    {
        if (cellValue is null)
        {
            return string.Empty;
        }

        return cellValue switch
        {
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            bool b => b ? "True" : "False",
            double d => d.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            int or long => Convert.ToString(cellValue, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => cellValue.ToString()?.Trim() ?? string.Empty
        };
    }

    /// <summary>
    /// Same logic as TryGetNumeric, operating on a raw ExcelDataReader value.
    /// </summary>
    private static bool TryGetNumericFromValue(object? cellValue, string rawValue, out double value)
    {
        value = default;

        switch (cellValue)
        {
            case double d:
                value = d;
                return true;
            case float f:
                value = f;
                return true;
            case decimal m:
                value = (double)m;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case DateTime:
                // Date-typed cells are handled via GetValueString/timestamp
                // resolution, not treated as a plain numeric measurement.
                return false;
        }

        return double.TryParse(rawValue, NumberStyles.Any, CultureInfo.InvariantCulture, out value) ||
               double.TryParse(rawValue, NumberStyles.Any, CultureInfo.CurrentCulture, out value);
    }

    private static bool TryGetTimestampInfo(ICell? cell, out DateTime value, out bool hasTime)
    {
        value = DateTime.MinValue;
        hasTime = false;

        if (cell is null) return false;

        bool ValidateOrAdjustRange(DateTime dt, out DateTime v, out bool ht)
        {
            v = dt;
            ht = dt.TimeOfDay.TotalSeconds > 0;
            if (dt.Year < 1900) return false;
            return dt.Year is >= 2000 and <= 2100;
        }

        if (cell.CellType == CellType.Numeric)
        {
            if (DateUtil.IsCellDateFormatted(cell))
            {
                return ValidateOrAdjustRange(cell.DateCellValue.GetValueOrDefault(), out value, out hasTime);
            }

            try
            {
                return ValidateOrAdjustRange(DateTime.FromOADate(cell.NumericCellValue), out value, out hasTime);
            }
            catch
            {
                return false;
            }
        }

        var raw = cell.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return false;

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var parsed1))
            return ValidateOrAdjustRange(parsed1, out value, out hasTime);

        if (DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var parsed2))
            return ValidateOrAdjustRange(parsed2, out value, out hasTime);

        string[] formats =
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd H:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd H:mm",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss.fff",
            "dd/MM/yyyy",
            "d/M/yyyy",
            "dd-MM-yyyy",
            "d-M-yyyy",
            "dd/MM/yyyy HH:mm:ss",
            "d/M/yyyy HH:mm:ss",
            "dd-MM-yyyy HH:mm:ss",
            "d-M-yyyy HH:mm:ss",
            "dd/MM/yyyy HH:mm",
            "d/M/yyyy HH:mm",
            "dd-MM-yyyy HH:mm",
            "d-M-yyyy HH:mm",
            "HH:mm:ss",
            "H:mm:ss",
            "HH:mm",
            "H:mm"
        };
        if (DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var parsed3))
            return ValidateOrAdjustRange(parsed3, out value, out hasTime);

        return false;
    }

    private static string GetCellString(ICell? cell)
    {
        if (cell is null)
        {
            return string.Empty;
        }

        return cell.CellType switch
        {
            CellType.Numeric when DateUtil.IsCellDateFormatted(cell) => cell.DateCellValue?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            CellType.Numeric => cell.NumericCellValue.ToString(CultureInfo.InvariantCulture),
            CellType.Boolean => cell.BooleanCellValue ? "True" : "False",
            CellType.Formula => cell.ToString()?.Trim() ?? string.Empty,
            _ => cell.ToString()?.Trim() ?? string.Empty
        };
    }

    private static bool TryGetNumeric(ICell? cell, string rawValue, out double value)
    {
        value = default;
        if (cell is not null && cell.CellType == CellType.Numeric && !DateUtil.IsCellDateFormatted(cell))
        {
            value = cell.NumericCellValue;
            return true;
        }

        return double.TryParse(rawValue, NumberStyles.Any, CultureInfo.InvariantCulture, out value) ||
               double.TryParse(rawValue, NumberStyles.Any, CultureInfo.CurrentCulture, out value);
    }

    private sealed record ColumnHeader(int ColumnIndex, CycleHeaderDefinition Header);
}



