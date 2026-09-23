using System.Text.RegularExpressions;
namespace KickoutMonitor.Domain;

public static class ReviewSemantics
{
    public static string Tone(string? label)
    {
        var value = Regex.Replace((label ?? "").Trim(), @"^\d+[_ \-]*", "").ToUpperInvariant();
        if (value == "OVERKILL" || value.StartsWith("OK")) return "Overkill";
        if (value == "REAL" || value.StartsWith("NG") || value.StartsWith("QNG")) return "Real";
        return "Unknown";
    }
    public const string NotDlng = "Not DLNG";
    public static bool IsNotDlng(string? label) => string.Equals(label?.Trim(), NotDlng, StringComparison.OrdinalIgnoreCase);
    public static bool IsApplicableOutcome(string outcome) => outcome is not ("Unknown" or NotDlng);
    public static bool IsLegacyNoNeed(string? label) =>
        (label ?? "").Trim().StartsWith("No Need", StringComparison.OrdinalIgnoreCase);
    public static bool CanTrain(string? label) => !string.IsNullOrWhiteSpace(label) && !IsLegacyNoNeed(label)
        && !label.Equals("Unknown", StringComparison.OrdinalIgnoreCase) && !IsNotDlng(label);
    public static bool Known(string? label) => Tone(label) != "Unknown";
    public static string Outcome(DlngReviewRecord record)
    {
        if (IsNotDlng(record.FinalClass)) return NotDlng;
        var final = Tone(record.FinalClass);
        if (final == "Unknown") return "Unknown";
        if (record.ModelKind == DlngModelKind.Segmentation ||
            record.FinalClass.Equals("Real", StringComparison.OrdinalIgnoreCase) ||
            record.FinalClass.Equals("Overkill", StringComparison.OrdinalIgnoreCase)) return final;
        var source = Tone(record.SourceClass);
        if (source == "Unknown") return "Unknown";
        if (source == "Real" && final == "Overkill") return "Overkill";
        if (source == "Overkill" && final == "Real") return "Missed defect";
        if (source == "Real" && final == "Real" && !record.SourceClass.Equals(record.FinalClass, StringComparison.OrdinalIgnoreCase))
            return "Class correction";
        return "Unchanged";
    }
    public static string SampleId(DlngReviewRecord record) => InspectionIdentity.Hash(
        $"{record.ProductModel}|{record.Inspection?.Model}|{record.Inspection?.Identity ?? $"{record.MachineId}|{record.InspectedAt:O}|{record.CellId}"}|{record.CropFolder}|{record.Side}|{InspectionIdentity.Fingerprint(record.ImagePaths)}");
}
