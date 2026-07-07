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
        VisionMasterSettings settings)
    {
        var group = settings.IrsRules.FinalClassGroups.FirstOrDefault(x =>
            x.Folder.Equals(cropFolder, StringComparison.OrdinalIgnoreCase)
            && x.Polarity == polarity)
            ?? settings.IrsRules.FinalClassGroups.FirstOrDefault(x =>
                x.Folder.Equals(cropFolder, StringComparison.OrdinalIgnoreCase)
                && x.Polarity is null);

        return group?.Classes.ToArray()
            ?? settings.DlngRules.SegmentationClasses.ToArray();
    }
}
