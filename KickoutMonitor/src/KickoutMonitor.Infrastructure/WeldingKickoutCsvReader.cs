using System.Globalization;
using System.Runtime.CompilerServices;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class WeldingKickoutCsvReader : IKickoutCsvReader
{
    private readonly ISharePathResolver _shares;

    public WeldingKickoutCsvReader(ISharePathResolver shares)
    {
        _shares = shares;
    }

    public async IAsyncEnumerable<KickoutCandidate> ReadAsync(
        WeldingMachine machine,
        SnapshotResult snapshot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            snapshot.SnapshotPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

        var headerLine = await reader.ReadLineAsync(cancellationToken);
        if (headerLine is null) yield break;
        var headers = CsvSupport.UniqueHeaders(CsvSupport.ParseLine(headerLine));

        var rowNumber = 1;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            rowNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = CsvSupport.ParseLine(line);
            if (values.Count < headers.Count)
            {
                // A live CSV snapshot can end in the middle of its final row.
                continue;
            }

            var row = new CsvRow(headers, values);
            var cellId = row.Get("CELL-ID");
            if (!KickoutRules.IsEligible(row.Get("JUDGE"), cellId)) continue;

            var side = KickoutRules.GetNgSide(row.Get("UPPER_JUDGE"), row.Get("LOWER_JUDGE"));
            if (side == NgSide.None) continue;
            if (!TryTimestamp(row.Get("DATE"), row.Get("TIME"), out var inspectedAt)) continue;

            var images = ReadPreviewImages(machine, row, side);
            if (images.Count == 0) continue;
            var sourceFolder = Path.GetDirectoryName(images[0].NetworkPath) ?? string.Empty;
            var model = row.Get("MODEL-ID") ?? string.Empty;
            var lotId = row.Get("LOT-ID") ?? string.Empty;
            var key = string.Join(
                "|",
                machine.Id,
                inspectedAt.ToString("O", CultureInfo.InvariantCulture),
                lotId,
                cellId);

            yield return new(
                key,
                machine.Id,
                inspectedAt,
                model,
                lotId,
                cellId!,
                KickoutRules.NormalizeDefect(row.Get("JUDGE-DEFECT")),
                side,
                images,
                sourceFolder,
                snapshot.SourcePath,
                rowNumber);
        }
    }

    private IReadOnlyList<CandidateImage> ReadPreviewImages(
        WeldingMachine machine,
        CsvRow row,
        NgSide side)
    {
        var images = new List<CandidateImage>(side == NgSide.Both ? 12 : 6);
        if (side is NgSide.Upper or NgSide.Both)
        {
            AddSide(machine, row, images, "UPPER");
        }
        if (side is NgSide.Lower or NgSide.Both)
        {
            AddSide(machine, row, images, "LOWER");
        }
        return images;
    }

    private void AddSide(
        WeldingMachine machine,
        CsvRow row,
        ICollection<CandidateImage> target,
        string side)
    {
        for (var index = 0; index < 3; index++)
        {
            var csvIndex = index + 1;
            Add(row.Get($"{side}_IMAGE-PATH-{csvIndex}"), false);
            Add(row.Get($"{side}_OVERLAY-IMAGE-PATH-{csvIndex}"), true);

            void Add(string? productionPath, bool overlay)
            {
                if (string.IsNullOrWhiteSpace(productionPath)) return;
                target.Add(new(
                    side,
                    index,
                    overlay,
                    productionPath,
                    ProductionPathMapper.ToUnc(machine, productionPath, _shares)));
            }
        }
    }

    private static bool TryTimestamp(string? date, string? time, out DateTime timestamp) =>
        DateTime.TryParseExact(
            $"{date} {time}",
            ["yyyyMMdd HH:mm:ss", "yyyyMMdd HH:mm:ss.fff"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out timestamp);

    private sealed class CsvRow
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public CsvRow(IReadOnlyList<string> headers, IReadOnlyList<string> values)
        {
            for (var index = 0; index < headers.Count; index++)
            {
                _values[headers[index]] = values[index].Trim();
            }
        }

        public string? Get(string name) =>
            _values.TryGetValue(name, out var value) ? value : null;
    }
}
