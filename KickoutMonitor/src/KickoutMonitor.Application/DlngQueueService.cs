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
        CancellationToken cancellationToken)
    {
        var sources = await _locator.FindAsync(machine, date, cancellationToken);
        if (sources.Count == 0)
        {
            throw new FileNotFoundException(
                $"No daily Welding CSV was found for {machine.DisplayName} on {date:yyyy-MM-dd}.");
        }

        var items = new List<DlngReviewItem>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Creating read-only snapshot: {Path.GetFileName(source)}");
            var snapshot = await _snapshots.CreateAsync(
                source,
                date == DateOnly.FromDateTime(DateTime.Now),
                cancellationToken);
            var candidateCount = 0;
            var itemCount = 0;
            await foreach (var candidate in _reader.ReadAsync(machine, snapshot, progress, cancellationToken))
            {
                candidateCount++;
                if (candidateCount == 1 || candidateCount % 25 == 0)
                {
                    progress?.Report(
                        $"{machine.OutputFolderName} {date:yyyy-MM-dd}: locating crops for {candidateCount:N0} matching row(s)...");
                }
                var expanded = await _crops.ExpandAsync(machine, candidate, progress, cancellationToken);
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

        return items
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.InspectedAt)
            .ThenBy(x => x.CellId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.CropFolder, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
