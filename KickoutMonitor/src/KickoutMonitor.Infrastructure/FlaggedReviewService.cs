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
        var resolver = new ProductionInspectionResolver(_csvs, _shares);
        foreach (var flag in flags)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = new IrsReviewCandidate(flag.Key, flag.SourceModule, "Flagged",
                flag.LinePolarity, flag.ProducedAt, flag.LotId, flag.CellId,
                CameraFromSide(NormalizeSide(flag.Side)), "", "FLAGGED", flag.SourceContext, 0,
                flag.RawImagePaths, flag.Inspection);
            var machine = _machines.All.FirstOrDefault(x => x.Id.Equals(flag.MachineId, StringComparison.OrdinalIgnoreCase));
            var resolved = machine is null ? new IrsImageLookupResult([], "Unknown machine.")
                : await resolver.ResolveAsync(machine, candidate, cancellationToken);
            candidates.Add(candidate with { RawImagePaths = resolved.NetworkPaths,
                Inspection = resolved.Inspection, ResolutionMessage = resolved.Message });
        }

        return candidates;
    }

    public Task UnflagAsync(string key, CancellationToken cancellationToken) =>
        _flags.DeleteAsync([key], cancellationToken);

    public Task ReflagAsync(string key, CancellationToken cancellationToken) =>
        _flags.MarkActiveAsync([key], DateTimeOffset.Now, cancellationToken);

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

    private static string CameraFromSide(string side) =>
        NormalizeSide(side).Equals("LOWER", StringComparison.OrdinalIgnoreCase)
            ? "BTM"
            : "TOP";

    private static string NormalizeSide(string side)
    {
        var value = side.Trim();
        return value.Equals("LOWER", StringComparison.OrdinalIgnoreCase)
            || value.Equals("BTM", StringComparison.OrdinalIgnoreCase)
            || value.Equals("BOTTOM", StringComparison.OrdinalIgnoreCase)
                ? "LOWER"
                : "UPPER";
    }

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

