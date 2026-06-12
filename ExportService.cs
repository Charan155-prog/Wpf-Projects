using SterilizationGenie.Models;
using NPOI.Util;
using NPOI.XWPF.UserModel;
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

    public string ExportRepresentationReport(string exportDirectory, RepresentationExportRequest request)
    {
        Directory.CreateDirectory(exportDirectory);
        var stem = SanitizeFileName(request.Title);
        var path = Path.Combine(exportDirectory, $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss}.docx");

        using var document = new XWPFDocument();

        var titleParagraph = document.CreateParagraph();
        titleParagraph.SpacingAfter = 180;
        var titleRun = titleParagraph.CreateRun();
        titleRun.IsBold = true;
        titleRun.FontSize = 18;
        titleRun.SetText(request.Title);

        if (!string.IsNullOrWhiteSpace(request.Subtitle))
        {
            var subtitleParagraph = document.CreateParagraph();
            subtitleParagraph.SpacingAfter = 120;
            var subtitleRun = subtitleParagraph.CreateRun();
            subtitleRun.FontSize = 11;
            subtitleRun.SetText(request.Subtitle);
        }

        if (!string.IsNullOrWhiteSpace(request.Summary))
        {
            var summaryParagraph = document.CreateParagraph();
            summaryParagraph.SpacingAfter = 180;
            var summaryRun = summaryParagraph.CreateRun();
            summaryRun.FontSize = 11;
            summaryRun.SetText(request.Summary);
        }

        AddImageSection(document, "Main Representation", request.MainImageBytes, request.MainImageWidth, request.MainImageHeight);

        if (request.DetailImageBytes is { Length: > 0 })
        {
            AddImageSection(document, "Selected Item Detail", request.DetailImageBytes, request.DetailImageWidth, request.DetailImageHeight);
        }

        if (request.Stats.Count > 0)
        {
            var heading = document.CreateParagraph();
            heading.SpacingBefore = 120;
            heading.SpacingAfter = 90;
            var headingRun = heading.CreateRun();
            headingRun.IsBold = true;
            headingRun.FontSize = 13;
            headingRun.SetText("Detailed Info");

            var table = document.CreateTable(request.Stats.Count + 1, 2);
            table.Width = 9000;
            table.GetRow(0).GetCell(0).SetText("Field");
            table.GetRow(0).GetCell(1).SetText("Value");

            for (var index = 0; index < request.Stats.Count; index++)
            {
                var row = request.Stats[index];
                table.GetRow(index + 1).GetCell(0).SetText(row.Label);
                table.GetRow(index + 1).GetCell(1).SetText(row.Value);
            }
        }

        using var stream = File.Create(path);
        document.Write(stream);
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

    private static void AddImageSection(XWPFDocument document, string heading, byte[] imageBytes, int pixelWidth, int pixelHeight)
    {
        var headingParagraph = document.CreateParagraph();
        headingParagraph.SpacingBefore = 120;
        headingParagraph.SpacingAfter = 80;
        var headingRun = headingParagraph.CreateRun();
        headingRun.IsBold = true;
        headingRun.FontSize = 13;
        headingRun.SetText(heading);

        var imageParagraph = document.CreateParagraph();
        imageParagraph.SpacingAfter = 160;
        var imageRun = imageParagraph.CreateRun();
        using var stream = new MemoryStream(imageBytes, writable: false);
        imageRun.AddPicture(
            stream,
            (int)PictureType.PNG,
            $"{heading}.png",
            Units.PixelToEMU(Math.Max(320, pixelWidth)),
            Units.PixelToEMU(Math.Max(180, pixelHeight)));
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalidChars.Contains(ch) ? '_' : ch);
        }

        return string.IsNullOrWhiteSpace(builder.ToString()) ? "RepresentationExport" : builder.ToString().Trim();
    }

}

public sealed record RepresentationExportRequest(
    string Title,
    string Subtitle,
    string Summary,
    byte[] MainImageBytes,
    int MainImageWidth,
    int MainImageHeight,
    byte[]? DetailImageBytes,
    int DetailImageWidth,
    int DetailImageHeight,
    IReadOnlyList<RepresentationStatRow> Stats);

public sealed record RepresentationStatRow(string Label, string Value);
