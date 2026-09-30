using KickoutMonitor.Domain;
namespace KickoutMonitor.Infrastructure;

/// <summary>Origin is provenance, never a batch grouping or a review-statistics input.</summary>
public sealed record TrainingInput(DlngReviewRecord Review, string Origin, string ReviewKey)
{
    public static string Identity(DlngReviewRecord r) => InspectionIdentity.Hash(
        $"{TrainingCollectionService.Group(r)}|{r.LinePolarity}|{r.Inspection?.Identity ?? r.InspectedAt.ToString("O")}|{r.CellId}|" +
        string.Join("|", r.ImagePaths.Select(InspectionIdentity.PairKey).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)));

    public static TrainingInput? FromIrs(IrsDatasetItem item, IrsDatasetDecision decision, VisionMasterSettings settings)
    {
        if (item.IsNeedToSimulate || decision.NoNeedToRetrain || decision.FinalClasses.Count != 1 ||
            !ReviewSemantics.CanTrain(decision.FinalClasses[0])) return null;
        var mapping = settings.DlngRules.DefectMappings.FirstOrDefault(m =>
            m.CropFolders.Contains(item.SourceFolder, StringComparer.OrdinalIgnoreCase));
        if (mapping is null) return null; // Raw/rulebase/unknown folders are not training models.
        var machine = settings.Machines.FirstOrDefault(m => $"{m.Line}({(m.Polarity == Polarity.Cathode ? "+" : "-")})" == item.LinePolarity);
        var product = string.IsNullOrWhiteSpace(item.Inspection?.Model) ? machine?.Model : item.Inspection.Model;
        if (string.IsNullOrWhiteSpace(product)) throw new InvalidOperationException("IRS product model is unresolved: " + item.Key);
        var r = new DlngReviewRecord(item.Key, item.Inspection?.MachineId ?? machine?.Id ?? item.LinePolarity,
            item.LinePolarity, item.ProducedAt, item.CellId, "IRS", item.SecondReason, item.CameraLocation,
            mapping.CropFolders.First(f => f.Equals(item.SourceFolder, StringComparison.OrdinalIgnoreCase)),
            item.OriginalClass, decision.FinalClasses[0], false, item.ImagePaths, decision.UpdatedAt, item.Inspection,
            true, DateTimeOffset.Now, product, mapping.ModelKind,
            item.LinePolarity.Contains('+') ? "Cathode" : "Anode");
        return new(r, "IRS", item.SourceReviewKey + "|" + item.Key);
    }
}
