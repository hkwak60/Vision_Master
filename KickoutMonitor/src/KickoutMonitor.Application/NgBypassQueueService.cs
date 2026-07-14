using KickoutMonitor.Domain;

namespace KickoutMonitor.Application;

public sealed class NgBypassQueueService
{
    private readonly IDailyCsvLocator _locator;
    private readonly IReadOnlySnapshotService _snapshots;
    private readonly INgBypassCsvReader _reader;

    public NgBypassQueueService(
        IDailyCsvLocator locator,
        IReadOnlySnapshotService snapshots,
        INgBypassCsvReader reader)
    {
        _locator = locator;
        _snapshots = snapshots;
        _reader = reader;
    }

    public async Task<NgBypassLoadResult> LoadAsync(
        WeldingMachine machine,
        DateOnly date,
        NgBypassQuery query,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var sources = await _locator.FindAsync(machine, date, cancellationToken);
        if (sources.Count == 0)
        {
            throw new FileNotFoundException(
                $"No daily Welding CSV was found for {machine.DisplayName} on {date:yyyy-MM-dd}.");
        }

        var items = new List<NgBypassCandidate>();
        var warnings = new List<NgBypassHeaderWarning>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Creating read-only snapshot: {Path.GetFileName(source)}");
            var snapshot = await _snapshots.CreateAsync(
                source,
                date == DateOnly.FromDateTime(DateTime.Now),
                cancellationToken);

            try
            {
                await foreach (var item in _reader.ReadAsync(machine, snapshot, query, progress, cancellationToken))
                {
                    items.Add(item);
                }
            }
            catch (NgBypassMissingHeaderException exception)
            {
                foreach (var column in exception.MissingColumns)
                {
                    warnings.Add(new(machine.OutputFolderName, date, Path.GetFileName(source), column));
                }
            }
        }

        return new(
            items
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderBy(x => x.InspectedAt)
                .ThenBy(x => x.LinePolarity, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.CellId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Side, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            warnings);
    }
}

public sealed class NgBypassMissingHeaderException : Exception
{
    public NgBypassMissingHeaderException(IReadOnlyList<string> missingColumns)
        : base($"Missing measure column(s): {string.Join(", ", missingColumns)}")
    {
        MissingColumns = missingColumns;
    }

    public IReadOnlyList<string> MissingColumns { get; }
}
