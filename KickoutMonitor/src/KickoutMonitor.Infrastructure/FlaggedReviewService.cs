using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class FlaggedReviewService : IFlaggedReviewService
{
    private readonly IFlaggedItemStore _flags;
    private readonly IIrsDatasetService _dataset;
    private readonly IMachineRegistry _machines;
    private readonly IDailyCsvLocator _csvs;
    private readonly ISharePathResolver _shares;

    public FlaggedReviewService(
        IFlaggedItemStore flags,
        IIrsDatasetService dataset,
        IMachineRegistry machines,
        IDailyCsvLocator csvs,
        ISharePathResolver shares)
    {
        _flags = flags;
        _dataset = dataset;
        _machines = machines;
        _csvs = csvs;
        _shares = shares;
    }

    public async Task<IReadOnlyList<FlaggedItem>> LoadAsync(bool summarized, CancellationToken cancellationToken)
    {
        var items = await _flags.LoadAsync(cancellationToken);
        return items.Values
            .Where(x => x.IsSummarized == summarized)
            .OrderBy(x => x.ProducedAt)
            .ThenBy(x => x.LinePolarity, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.CellId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<IrsReviewCandidate>> BuildCandidatesAsync(
        IReadOnlyList<FlaggedItem> flags,
        CancellationToken cancellationToken)
    {
        var candidates = new List<IrsReviewCandidate>(flags.Count);
        foreach (var flag in flags)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawPaths = flag.RawImagePaths.Count >= 3
                ? flag.RawImagePaths.Take(3).ToArray()
                : await ResolveRawImagesAsync(flag, cancellationToken);
            candidates.Add(new(
                flag.Key,
                flag.SourceModule,
                "Flagged",
                flag.LinePolarity,
                flag.ProducedAt,
                flag.LotId,
                flag.CellId,
                CameraFromSide(flag.Side),
                Path.GetFileName(rawPaths.FirstOrDefault() ?? string.Empty),
                "FLAGGED",
                flag.SourceContext,
                0,
                rawPaths));
        }

        return candidates;
    }

    public async Task<FlaggedSummaryResult> WriteSummaryAsync(
        IReadOnlyList<FlaggedItem> flags,
        IReadOnlyList<IrsReviewCandidate> candidates,
        IReadOnlyList<IrsReviewRecord> reviewRecords,
        IReadOnlyList<IrsDatasetItem> datasetItems,
        CancellationToken cancellationToken)
    {
        var result = await _dataset.WriteSummaryAsync(candidates, reviewRecords, datasetItems, cancellationToken);
        await _flags.MarkSummarizedAsync(flags.Select(x => x.Key).ToArray(), DateTimeOffset.Now, cancellationToken);
        return new(result.OutputFolder, result.SummaryWorkbook, flags.Count);
    }

    private async Task<IReadOnlyList<string>> ResolveRawImagesAsync(FlaggedItem flag, CancellationToken cancellationToken)
    {
        var machine = _machines.All.FirstOrDefault(x => x.Id.Equals(flag.MachineId, StringComparison.OrdinalIgnoreCase));
        if (machine is null) return [];
        var date = DateOnly.FromDateTime(flag.ProducedAt);
        IReadOnlyList<string> csvFiles;
        try
        {
            csvFiles = await _csvs.FindAsync(machine, date, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return [];
        }

        foreach (var csv in csvFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var paths = await ResolveRawImagesFromCsvAsync(machine, flag, csv, cancellationToken);
                if (paths.Count > 0) return paths;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return [];
    }

    private async Task<IReadOnlyList<string>> ResolveRawImagesFromCsvAsync(
        WeldingMachine machine,
        FlaggedItem flag,
        string csvFile,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            csvFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var headerLine = await reader.ReadLineAsync(cancellationToken);
        if (headerLine is null) return [];
        var headers = CsvSupport.UniqueHeaders(CsvSupport.ParseLine(headerLine));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = CsvSupport.ParseLine(line);
            if (values.Count < headers.Count) continue;
            var row = new CsvRow(headers, values);
            if (!flag.CellId.Equals(row.Get(ProductionCsvSchema.CellId), StringComparison.OrdinalIgnoreCase)) continue;
            var paths = new List<string>(ProductionImageConventions.ImagesPerSide);
            for (var index = 1; index <= ProductionImageConventions.ImagesPerSide; index++)
            {
                var productionPath = row.Get(ProductionCsvSchema.ImagePath(flag.Side, index));
                if (!string.IsNullOrWhiteSpace(productionPath))
                {
                    paths.Add(ProductionPathMapper.ToUnc(machine, productionPath, _shares));
                }
            }

            return paths;
        }

        return [];
    }

    private static string CameraFromSide(string side) =>
        side.Equals("LOWER", StringComparison.OrdinalIgnoreCase)
        || side.Equals("BTM", StringComparison.OrdinalIgnoreCase)
        || side.Equals("BOTTOM", StringComparison.OrdinalIgnoreCase)
            ? "BTM"
            : "TOP";

    private sealed class CsvRow
    {
        private readonly IReadOnlyList<string> _headers;
        private readonly IReadOnlyList<string> _values;

        public CsvRow(IReadOnlyList<string> headers, IReadOnlyList<string> values)
        {
            _headers = headers;
            _values = values;
        }

        public string Get(string header)
        {
            for (var index = 0; index < _headers.Count && index < _values.Count; index++)
            {
                if (_headers[index].Equals(header, StringComparison.OrdinalIgnoreCase)) return _values[index];
            }

            return string.Empty;
        }
    }
}
