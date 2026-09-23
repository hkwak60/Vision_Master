using KickoutMonitor.Domain;
namespace KickoutMonitor.Infrastructure;

public static class ReviewCompatibility
{
    public static void Backup(string path)
    {
        if (File.Exists(path) && !File.Exists(path + ".rework-v1.bak"))
            File.Copy(path, path + ".rework-v1.bak", false);
    }

    public static bool SavedImagesMatch(IrsReviewCandidate candidate, IrsReviewRecord record)
    {
        var context = candidate.Inspection;
        if (context?.ImageAt is not { } imageAt) return false;
        if (record.Inspection is not null && record.Inspection.Identity != context.Identity) return false;
        var files = (record.SavedPaths ?? []).SelectMany(path => File.Exists(path) ? new[] { path } :
            Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) : []);
        var images = files.Where(p => new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(Path.GetExtension(p).ToLowerInvariant())).ToArray();
        if (images.Length == 0) return false;
        var rawNames = context.ImagePaths.Select(InspectionIdentity.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var machine = new WeldingMachine(context.MachineId, candidate.LinePolarity.Split('(')[0],
            candidate.LinePolarity.Contains("(+)") ? Polarity.Cathode : Polarity.Anode, "", []);
        return images.All(path => rawNames.Contains(InspectionIdentity.FileName(path))
            || InspectionIdentity.MatchesCrop(path, machine, context));
    }
}
