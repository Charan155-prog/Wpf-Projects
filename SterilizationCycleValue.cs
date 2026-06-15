namespace SterilizationGenie.Models;

public class SterilizationCycleValue
{
    public int Id { get; set; }
    public int CycleId { get; set; }
    public SterilizationCycle? Cycle { get; set; }
    public int HeaderId { get; set; }
    public CycleHeaderDefinition? Header { get; set; }
    public string RawValue { get; set; } = string.Empty;
    public double? NumericValue { get; set; }
}
