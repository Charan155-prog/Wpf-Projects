namespace SterilizationGenie.Infrastructure;

public static class HeaderNameHelper
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var buffer = new char[value.Length];
        var index = 0;

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[index++] = char.ToUpperInvariant(ch);
            }
        }

        return new string(buffer, 0, index);
    }
}
