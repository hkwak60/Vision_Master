using KickoutMonitor.Domain;

namespace KickoutMonitor.Application;

public sealed class IrsReviewQueueService
{
    private readonly IMachineRegistry _machines;
    private readonly IIrsWorkbookReader _reader;
    private readonly IIrsRawImageLocator _images;

    public IrsReviewQueueService(
        IMachineRegistry machines,
        IIrsWorkbookReader reader,
        IIrsRawImageLocator images)
    {
        _machines = machines;
        _reader = reader;
        _images = images;
    }

    public async Task<IReadOnlyList<IrsReviewCandidate>> LoadAsync(
        string workbookPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var records = await _reader.ReadRequestedAsync(workbookPath, cancellationToken);
        progress?.Report($"IRS workbook parsed: {records.Count:N0} requested row(s).");
        var results = new List<IrsReviewCandidate>(records.Count);
        var skipped = 0;
        var searched = 0;
        var found = 0;
        var missing = 0;
        progress?.Report("Locating raw images from production CSV paths first, then bounded hour folders when needed.");
        foreach (var record in records.OrderBy(x => x.ProducedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetWeldingMachine(record, out var machine))
            {
                skipped++;
                progress?.Report(
                    $"Skipped row {record.SourceRow}: unsupported equipment/vision " +
                    $"{record.Eqpt} / {record.VisionType}.");
                continue;
            }

            searched++;
            var lookup = await _images.FindAsync(machine, record, cancellationToken);
            if (lookup.NetworkPaths.Count == 0)
            {
                missing++;
            }
            else
            {
                found++;
            }

            if (searched == 1 || searched % 10 == 0 || searched == records.Count - skipped)
            {
                progress?.Report(
                    $"IRS image lookup progress: {searched:N0} searched, {found:N0} ready, {missing:N0} missing.");
            }
            results.Add(record with
            {
                LinePolarity = machine.OutputFolderName,
                RawImagePaths = lookup.NetworkPaths
            });
        }

        if (skipped > 0)
        {
            progress?.Report($"Skipped {skipped:N0} unsupported IRS row(s).");
        }
        if (missing > 0)
        {
            progress?.Report($"Raw image lookup complete with {missing:N0} missing row(s). Missing rows show as unavailable in the queue.");
        }
        else
        {
            progress?.Report("Raw image lookup complete: all requested rows have raw image paths.");
        }
        return results;
    }

    private bool TryGetWeldingMachine(
        IrsReviewCandidate candidate,
        out WeldingMachine machine)
    {
        machine = null!;
        var line = ParseLine(candidate.Eqpt);
        if (line is null) return false;
        var normalizedVision = candidate.VisionType.Trim();
        var polarity = normalizedVision switch
        {
            "Welding Plus" => Polarity.Cathode,
            "Welding Minus" => Polarity.Anode,
            _ => (Polarity?)null
        };
        if (polarity is null) return false;
        machine = _machines.All.FirstOrDefault(x =>
            x.Line.Equals(line, StringComparison.OrdinalIgnoreCase)
            && x.Polarity == polarity.Value)!;
        return machine is not null;
    }

    private static string? ParseLine(string eqpt)
    {
        var marker = eqpt.IndexOf('#');
        if (marker < 0 || marker + 1 >= eqpt.Length) return null;
        var candidate = eqpt[(marker + 1)..].Trim();
        var end = candidate.IndexOf(' ');
        if (end >= 0) candidate = candidate[..end];
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }
}
