namespace KickoutMonitor.Domain;

public static class KickoutRules
{
    public static bool IsEligible(string? judge, string? cellId) =>
        string.Equals(judge?.Trim(), "NG", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(cellId)
        && !cellId.TrimStart().StartsWith("OCR", StringComparison.OrdinalIgnoreCase)
        && !cellId.TrimStart().StartsWith("AGING", StringComparison.OrdinalIgnoreCase);

    public static NgSide GetNgSide(string? upperJudge, string? lowerJudge)
    {
        var upper = string.Equals(upperJudge?.Trim(), "NG", StringComparison.OrdinalIgnoreCase);
        var lower = string.Equals(lowerJudge?.Trim(), "NG", StringComparison.OrdinalIgnoreCase);
        return (upper, lower) switch
        {
            (true, true) => NgSide.Both,
            (true, false) => NgSide.Upper,
            (false, true) => NgSide.Lower,
            _ => NgSide.None
        };
    }

    public static string NormalizeDefect(string? defect)
    {
        var value = string.IsNullOrWhiteSpace(defect) ? "UNSPECIFIED" : defect.Trim();
        foreach (var character in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(character, '_');
        }

        return string.IsNullOrWhiteSpace(value) ? "UNSPECIFIED" : value;
    }
}
