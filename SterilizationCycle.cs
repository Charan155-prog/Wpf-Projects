using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;

namespace SterilizationGenie.Models;

public class SterilizationCycle
{
    private Dictionary<string, SterilizationCycleValue>? _valueLookup;

    public int Id { get; set; }
    public DateTime RecordedAt { get; set; }
    public string SourceWorkbookName { get; set; } = string.Empty;
    public string SheetName { get; set; } = string.Empty;
    public int SheetIndex { get; set; }
    public List<SterilizationCycleValue> Values { get; set; } = [];

    [NotMapped]
    public IReadOnlyDictionary<string, SterilizationCycleValue> ValueLookup
    {
        get
        {
            _valueLookup ??= Values
                .Where(value => value.Header is not null)
                .GroupBy(value => value.Header!.NormalizedName)
                .ToDictionary(group => group.Key, group => group.Last());

            return _valueLookup;
        }
    }

    public void AddValue(CycleHeaderDefinition header, string rawValue, double? numericValue)
    {
        Values.Add(new SterilizationCycleValue
        {
            Header = header,
            RawValue = rawValue,
            NumericValue = numericValue
        });

        _valueLookup = null;
    }

    public void ResetLookup() => _valueLookup = null;

    public string GetText(string normalizedHeader, string fallback = "")
    {
        if (ValueLookup.TryGetValue(normalizedHeader, out var value))
        {
            return string.IsNullOrWhiteSpace(value.RawValue) ? fallback : value.RawValue;
        }

        return fallback;
    }

    public double? GetNumericValue(string normalizedHeader)
    {
        if (!ValueLookup.TryGetValue(normalizedHeader, out var value))
        {
            return null;
        }

        if (value.NumericValue.HasValue)
        {
            return value.NumericValue.Value;
        }

        if (double.TryParse(value.RawValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        if (double.TryParse(value.RawValue, NumberStyles.Any, CultureInfo.CurrentCulture, out parsed))
        {
            return parsed;
        }

        return null;
    }
}
