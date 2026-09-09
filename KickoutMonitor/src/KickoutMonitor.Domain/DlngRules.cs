namespace KickoutMonitor.Domain;

public static class DlngRules
{
    public static DlngDefectMappingSetting? FindMapping(
        string? defect,
        DlngRuleSettings rules)
    {
        var value = defect?.Trim();
        var exact = rules.DefectMappings.FirstOrDefault(x =>
            x.Defect.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        foreach (var alias in DefectAliases(value))
        {
            var mapped = rules.DefectMappings.FirstOrDefault(x =>
                x.Defect.Equals(alias, StringComparison.OrdinalIgnoreCase));
            if (mapped is not null) return mapped;
        }

        return null;
    }

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
        if (cropFolder.Equals("Crop_A", StringComparison.OrdinalIgnoreCase))
        {
            return classes.Where(klass => CropAClassMatchesSide(klass, side)).ToArray();
        }

        if (cropFolder.Equals("Crop_micro", StringComparison.OrdinalIgnoreCase))
        {
            return classes.Where(klass => CropMicroClassMatchesSide(klass, side)).ToArray();
        }

        return classes;
    }

    private static IEnumerable<string> DefectAliases(string? defect)
    {
        if (defect is null) yield break;
        if (defect.Equals("SEPA_SHOULDER_DL", StringComparison.OrdinalIgnoreCase)) yield return "SEPA_SHOULDER";
        if (defect.Equals("SEPA_SHOULDER", StringComparison.OrdinalIgnoreCase)) yield return "SEPA_SHOULDER_DL";
    }
    private static bool CropAClassMatchesSide(string klass, string? side)
    {
        var isUpper = side?.Equals("UPPER", StringComparison.OrdinalIgnoreCase) == true;
        var isLower = side?.Equals("LOWER", StringComparison.OrdinalIgnoreCase) == true;
        if (isUpper && klass.Contains("_OK_BACK_", StringComparison.OrdinalIgnoreCase)) return false;
        if (isLower && klass.Contains("_OK_TOP_", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool CropMicroClassMatchesSide(string klass, string? side)
    {
        var isUpper = side?.Equals("UPPER", StringComparison.OrdinalIgnoreCase) == true;
        var isLower = side?.Equals("LOWER", StringComparison.OrdinalIgnoreCase) == true;
        if (isUpper && klass.Equals("02_OK_BTM", StringComparison.OrdinalIgnoreCase)) return false;
        if (isLower && klass.Equals("01_OK_TAB", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
