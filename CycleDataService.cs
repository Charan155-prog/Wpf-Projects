using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SterilizationGenie.Data;
using SterilizationGenie.Models;
using System.IO;
using System.Diagnostics;

namespace SterilizationGenie.Services;

public sealed class CycleDataService
{
    private string _databasePath;

    public CycleDataService(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task EnsureDatabaseAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

        await using var db = new SterilizationDbContext(_databasePath);

        if (await RequiresRebuildAsync())
        {
            await db.Database.EnsureDeletedAsync();
        }

        await db.Database.EnsureCreatedAsync();
    }

    public async Task<List<SterilizationCycle>> LoadExistingCyclesAsync()
    {
        await using var db = new SterilizationDbContext(_databasePath);
        var cycles = await db.Cycles
            .Include(cycle => cycle.Values)
            .ThenInclude(value => value.Header)
            .OrderBy(cycle => cycle.RecordedAt)
            .ToListAsync();

        foreach (var cycle in cycles)
        {
            cycle.ResetLookup();
        }

        return cycles;
    }

    public async Task ReplaceAllCyclesAsync(IEnumerable<SterilizationCycle> cycles)
    {
        await using var db = new SterilizationDbContext(_databasePath);
        var importedCycles = PrepareCycleGraph(cycles).ToList();

        var existingCycles = await db.Cycles.ToListAsync();
        if (existingCycles.Count > 0)
        {
            db.Cycles.RemoveRange(existingCycles);
        }

        var existingHeaders = await db.CycleHeaders.ToListAsync();
        if (existingHeaders.Count > 0)
        {
            db.CycleHeaders.RemoveRange(existingHeaders);
        }

        await db.Cycles.AddRangeAsync(importedCycles);
        await db.SaveChangesAsync();
    }

    public async Task SaveCycleAsync(SterilizationCycle cycle)
    {
        await using var db = new SterilizationDbContext(_databasePath);
        var preparedCycle = PrepareCycleGraph([cycle]).Single();
        await AttachExistingHeadersAsync(db, [preparedCycle]);
        await db.Cycles.AddAsync(preparedCycle);
        await db.SaveChangesAsync();
    }

    public async Task SaveCyclesAsync(IEnumerable<SterilizationCycle> cycles)
    {
        var importedCycles = PrepareCycleGraph(cycles).ToList();
        if (importedCycles.Count == 0)
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        await using var db = new SterilizationDbContext(_databasePath);
        await AttachExistingHeadersAsync(db, importedCycles);
        await db.Cycles.AddRangeAsync(importedCycles);
        await db.SaveChangesAsync();
        sw.Stop();
        try { Debug.WriteLine($"[Diag] SaveCyclesAsync inserted {importedCycles.Count} rows in {sw.ElapsedMilliseconds}ms"); } catch { }
        try { SterilizationGenie.Services.DiagLogger.Write($"SaveCyclesAsync inserted {importedCycles.Count} rows in {sw.ElapsedMilliseconds}ms"); } catch { }
    }

    private async Task<bool> RequiresRebuildAsync()
    {
        if (!File.Exists(_databasePath))
        {
            return false;
        }

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();

        return !await TableExistsAsync(connection, "Cycles") ||
               !await TableExistsAsync(connection, "CycleHeaders") ||
               !await TableExistsAsync(connection, "CycleValues");
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task AttachExistingHeadersAsync(SterilizationDbContext db, IEnumerable<SterilizationCycle> cycles)
    {
        var incomingHeaders = cycles
            .SelectMany(cycle => cycle.Values)
            .Where(value => value.Header is not null)
            .Select(value => value.Header!)
            .GroupBy(header => header.NormalizedName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (incomingHeaders.Count == 0)
        {
            return;
        }

        var incomingNames = incomingHeaders
            .Select(header => header.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingHeaders = await db.CycleHeaders
            .Where(header => incomingNames.Contains(header.NormalizedName))
            .ToListAsync();

        var existingByName = existingHeaders.ToDictionary(
            header => header.NormalizedName,
            StringComparer.OrdinalIgnoreCase);

        foreach (var value in cycles.SelectMany(cycle => cycle.Values))
        {
            if (value.Header is null)
            {
                continue;
            }

            if (existingByName.TryGetValue(value.Header.NormalizedName, out var existingHeader))
            {
                value.Header = existingHeader;
            }
        }
    }

    private static IEnumerable<SterilizationCycle> PrepareCycleGraph(IEnumerable<SterilizationCycle> cycles)
    {
        var headerMap = new Dictionary<string, CycleHeaderDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var cycle in cycles)
        {
            foreach (var value in cycle.Values)
            {
                if (value.Header is null)
                {
                    continue;
                }

                var normalizedName = value.Header.NormalizedName;
                if (!headerMap.TryGetValue(normalizedName, out var canonicalHeader))
                {
                    canonicalHeader = value.Header;
                    headerMap[normalizedName] = canonicalHeader;
                }
                else
                {
                    canonicalHeader.IsNumeric |= value.Header.IsNumeric || value.NumericValue.HasValue;
                    canonicalHeader.DisplayOrder = Math.Min(canonicalHeader.DisplayOrder, value.Header.DisplayOrder);
                }

                value.Header = canonicalHeader;
            }

            cycle.ResetLookup();
            yield return cycle;
        }
    }
}
