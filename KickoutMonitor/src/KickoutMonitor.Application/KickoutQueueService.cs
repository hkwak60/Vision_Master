using KickoutMonitor.Domain;

namespace KickoutMonitor.Application;

public sealed class KickoutQueueService
{
    private readonly IDailyCsvLocator _locator;
    private readonly IReadOnlySnapshotService _snapshots;
    private readonly IKickoutCsvReader _reader;

    public KickoutQueueService(
        IDailyCsvLocator locator,
        IReadOnlySnapshotService snapshots,
        IKickoutCsvReader reader)
    {
        _locator = locator;
        _snapshots = snapshots;
        _reader = reader;
    }

    public async Task<IReadOnlyList<KickoutCandidate>> LoadAsync(
        WeldingMachine machine,
        DateOnly date,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var sources = await _locator.FindAsync(machine, date, cancellationToken);
        if (sources.Count == 0)
        {
            throw new FileNotFoundException(
                $"No daily Welding CSV was found for {machine.DisplayName} on {date:yyyy-MM-dd}. " +
                $"Check network access to {machine.DataRoot()}.");
        }
        var results = new List<KickoutCandidate>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Creating read-only snapshot: {Path.GetFileName(source)}");
            var snapshot = await _snapshots.CreateAsync(
                source,
                date == DateOnly.FromDateTime(DateTime.Now),
                cancellationToken);
            await foreach (var candidate in _reader.ReadAsync(machine, snapshot, cancellationToken))
            {
                results.Add(candidate);
            }
        }

        return results
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.InspectedAt)
            .ToArray();
    }
}
