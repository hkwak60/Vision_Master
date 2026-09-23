using KickoutMonitor.Domain;

namespace KickoutMonitor.Application;

public sealed class DlngQueueService
{
    private readonly IDailyCsvLocator _locator;
    private readonly IReadOnlySnapshotService _snapshots;
    private readonly IDlngCsvReader _reader;
    private readonly IDlngCropLocator _crops;

    public DlngQueueService(
        IDailyCsvLocator locator,
        IReadOnlySnapshotService snapshots,
        IDlngCsvReader reader,
        IDlngCropLocator crops)
    {
        _locator = locator;
        _snapshots = snapshots;
        _reader = reader;
        _crops = crops;
    }

    public async Task<IReadOnlyList<DlngReviewItem>> LoadAsync(
        WeldingMachine machine,
        DateOnly date,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? cropFolders = null,
        QueueTimeRange? timeRange = null)
    {
        if (timeRange is { IsValid: false }) throw new ArgumentException("Invalid queue timeframe.", nameof(timeRange));
        _crops.Reset();
        var sources = await _locator.FindAsync(machine, date, cancellationToken);
        if (sources.Count == 0)
        {
            throw new FileNotFoundException(
                $"No daily Welding CSV was found for {machine.DisplayName} on {date:yyyy-MM-dd}.");
        }

        var snapshots = new List<SnapshotResult>();
        var contexts = new List<InspectionContext>();
        foreach (var source in sources)
        {
            var snapshot = await _snapshots.CreateAsync(source,
                date == DateOnly.FromDateTime(DateTime.Now), cancellationToken);
            snapshots.Add(snapshot);
            contexts.AddRange(await _reader.ReadInspectionContextsAsync(machine, snapshot, cancellationToken));
        }
        var windowIndexAvailable = true;
        try
        {
            foreach (var neighbor in new[] { date.AddDays(-1), date.AddDays(1) })
                foreach (var source in await _locator.FindAsync(machine, neighbor, cancellationToken))
                {
                    if (sources.Contains(source, StringComparer.OrdinalIgnoreCase)) continue;
                    var snapshot = await _snapshots.CreateAsync(source,
                        neighbor == DateOnly.FromDateTime(DateTime.Now), cancellationToken);
                    contexts.AddRange(await _reader.ReadInspectionContextsAsync(machine, snapshot, cancellationToken));
                }
        }
        catch (IOException) { windowIndexAvailable = false; }
        catch (UnauthorizedAccessException) { windowIndexAvailable = false; }
        if (!windowIndexAvailable) progress?.Report("Neighboring CSV ownership check unavailable; keeping exact raw-time crop matching.");
        var indexed = (windowIndexAvailable ? InspectionIdentity.WithCropConflicts(contexts) : contexts)
            .DistinctBy(x => x.Identity).ToDictionary(x => x.Identity);
        var items = new List<DlngReviewItem>();
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = snapshot.SourcePath;
            var candidateCount = 0;
            var itemCount = 0;
            await foreach (var candidate in _reader.ReadAsync(machine, snapshot, progress, cancellationToken))
            {
                if (timeRange is not null && !timeRange.Contains(candidate.InspectedAt)) continue;
                candidateCount++;
                if (candidateCount == 1 || candidateCount % 25 == 0)
                {
                    progress?.Report(
                        $"{machine.OutputFolderName} {date:yyyy-MM-dd}: locating crops for {candidateCount:N0} matching row(s)...");
                }
                var contextual = candidate.Inspection is { } inspection && indexed.TryGetValue(inspection.Identity, out var checkedContext)
                    ? candidate with { Inspection = checkedContext } : candidate;
                var expanded = await _crops.ExpandAsync(machine, contextual, progress, cancellationToken, cropFolders);
                items.AddRange(expanded);
                itemCount += expanded.Count;
                if (itemCount > 0 && itemCount % 100 == 0)
                {
                    progress?.Report(
                        $"{machine.OutputFolderName} {date:yyyy-MM-dd}: queued {itemCount:N0} DLNG crop item(s)...");
                }
            }
            progress?.Report(
                $"{machine.OutputFolderName} {date:yyyy-MM-dd}: finished {Path.GetFileName(source)}; " +
                $"{candidateCount:N0} matching row(s), {itemCount:N0} crop item(s).");
        }

        var unique = items
            .SeparateCollisions(x => x.Key, x => InspectionIdentity.Fingerprint(x.Inspection?.ImagePaths ?? (x.RawImages ?? x.Images).Select(i => i.Path).ToArray()), (x, key) => x with { Key = key })
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.InspectedAt)
            .ThenBy(x => x.CellId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.CropFolder, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return InspectionIdentity.GroupQueue(unique,
            x => InspectionIdentity.Group(x.MachineId, x.LotId, x.CellId, x.Inspection?.Identity ?? x.Key),
            x => x.InspectedAt);

    }
}
