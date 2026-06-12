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
                // Bug #1C – NPOI's XSSFWorkbook is not thread-safe; process files sequentially
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
        return ImportSingleFile(filePath, priorSheetRowPositions);
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
                priorSheetRowPositions?.TryGetValue(sheetKey, out priorLastRowIndex);
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
                var rawValue = GetCellString(cell);
                if (string.IsNullOrWhiteSpace(rawValue))
                {
                    continue;
                }

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
        // Bug #1A – skip sheets that are metadata / tag-description sheets; they have no cycle data
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

        // Bug #1B – none of the workbook's 27 named headers contain TIMESTAMP / DATE / TIME
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
        => $"{sheetIndex}:{sheetName}";

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