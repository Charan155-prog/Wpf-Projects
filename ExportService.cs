using SterilizationGenie.Models;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SterilizationGenie.Services;

public sealed class ExportService
{
    public string ExportCsv(string exportDirectory, IEnumerable<SterilizationCycle> cycles)
    {
        Directory.CreateDirectory(exportDirectory);
        var cycleList = cycles.OrderBy(cycle => cycle.RecordedAt).ToList();
        var headers = GetOrderedHeaders(cycleList);
        var path = Path.Combine(exportDirectory, $"SterilizationExport_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        var csvHeaders = new List<string> { "SourceWorkbookName", "SheetName", "RecordedAt" };
        csvHeaders.AddRange(headers.Select(header => header.Name));
        writer.WriteLine(string.Join(",", csvHeaders.Select(EscapeCsv)));

        foreach (var cycle in cycleList)
        {
            var cells = new List<string>
            {
                EscapeCsv(cycle.SourceWorkbookName),
                EscapeCsv(cycle.SheetName),
                EscapeCsv(cycle.RecordedAt.ToString("O"))
            };

            foreach (var header in headers)
            {
                cells.Add(EscapeCsv(cycle.GetText(header.NormalizedName)));
            }

            writer.WriteLine(string.Join(",", cells));
        }

        return path;
    }

    public string ExportJson(string exportDirectory, IEnumerable<SterilizationCycle> cycles)
    {
        Directory.CreateDirectory(exportDirectory);
        var cycleList = cycles.OrderBy(cycle => cycle.RecordedAt).ToList();
        var path = Path.Combine(exportDirectory, $"SterilizationExport_{DateTime.Now:yyyyMMdd_HHmmss}.json");

        var payload = cycleList.Select(cycle => new
        {
            cycle.RecordedAt,
            cycle.SourceWorkbookName,
            cycle.SheetName,
            Headers = cycle.Values
                .Where(value => value.Header is not null)
                .OrderBy(value => value.Header!.DisplayOrder)
                .ToDictionary(
                    value => value.Header!.Name,
                    value => new
                    {
                        value.RawValue,
                        value.NumericValue
                    })
        });

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));

        return path;
    }

    private static List<CycleHeaderDefinition> GetOrderedHeaders(IEnumerable<SterilizationCycle> cycles)
    {
        return cycles
            .SelectMany(cycle => cycle.Values)
            .Where(value => value.Header is not null)
            .Select(value => value.Header!)
            .GroupBy(header => header.NormalizedName)
            .Select(group => new CycleHeaderDefinition
            {
                Name = group.OrderBy(header => header.DisplayOrder).First().Name,
                NormalizedName = group.Key,
                DisplayOrder = group.Min(header => header.DisplayOrder),
                IsNumeric = group.Any(header => header.IsNumeric)
            })
            .OrderBy(header => header.DisplayOrder)
            .ThenBy(header => header.Name)
            .ToList();
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
