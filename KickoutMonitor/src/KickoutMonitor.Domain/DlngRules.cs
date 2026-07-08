namespace KickoutMonitor.Domain;

public static class DlngRules
{
    public static DlngDefectMappingSetting? FindMapping(
        string? defect,
        DlngRuleSettings rules) =>
        rules.DefectMappings.FirstOrDefault(x =>
            x.Defect.Equals(defect?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsEligibleJudge(string? judge, DlngRuleSettings rules) =>
        rules.EligibleJudges.Any(x =>
            x.Equals(judge?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> ClassesFor(
        string cropFolder,
        Polarity polarity,
        VisionMasterSettings settings,
        string? side = null)
    {
        var group = settings.IrsRules.FinalClassGroups.FirstOrDefault(x =>
            x.Folder.Equals(cropFolder, StringComparison.OrdinalIgnoreCase)
            && x.Polarity == polarity)
            ?? settings.IrsRules.FinalClassGroups.FirstOrDefault(x =>
                x.Folder.Equals(cropFolder, StringComparison.OrdinalIgnoreCase)
                && x.Polarity is null);

        var classes = group?.Classes.ToArray()
            ?? settings.DlngRules.SegmentationClasses.ToArray();
        return cropFolder.Equals("Crop_A", StringComparison.OrdinalIgnoreCase)
            ? classes.Where(klass => CropAClassMatchesSide(klass, side)).ToArray()
            : classes;
    }

    private static bool CropAClassMatchesSide(string klass, string? side)
    {
        var isUpper = side?.Equals("UPPER", StringComparison.OrdinalIgnoreCase) == true;
        var isLower = side?.Equals("LOWER", StringComparison.OrdinalIgnoreCase) == true;
        if (isUpper && klass.Contains("_OK_BACK_", StringComparison.OrdinalIgnoreCase)) return false;
        if (isLower && klass.Contains("_OK_TOP_", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
