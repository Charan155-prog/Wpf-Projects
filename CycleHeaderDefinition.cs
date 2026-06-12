namespace SterilizationGenie.Models;

public class CycleHeaderDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public bool IsNumeric { get; set; }
    public List<SterilizationCycleValue> Values { get; set; } = [];
}
