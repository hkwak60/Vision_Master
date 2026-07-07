using System.Globalization;
using System.Runtime.CompilerServices;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class DlngCsvReader : IDlngCsvReader
{
    private readonly ISharePathResolver _shares;
    private readonly VisionMasterSettings _settings;

    public DlngCsvReader(ISharePathResolver shares, VisionMasterSettings? settings = null)
    {
        _shares = shares;
        _settings = settings ?? VisionMasterSettings.CreateDefault();
    }

    public async IAsyncEnumerable<DlngReviewItem> ReadAsync(
        WeldingMachine machine,
        SnapshotResult snapshot,
        IProgress<string>? progress,
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
        var eligibleRows = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            rowNumber++;
            if (rowNumber == 2 || rowNumber % 5000 == 0)
            {
                progress?.Report(
                    $"{machine.OutputFolderName}: scanning {Path.GetFileName(snapshot.SourcePath)} row {rowNumber:N0}; " +
                    $"{eligibleRows:N0} eligible row(s) found...");
            }
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = CsvSupport.ParseLine(line);
            if (values.Count < headers.Count) continue;

            var row = new CsvRow(headers, values);
            var cellId = row.Get(ProductionCsvSchema.CellId);
            if (IsIgnoredCell(cellId)) continue;
            var judge = row.Get(ProductionCsvSchema.Judge);
            var defect = row.Get(ProductionCsvSchema.JudgeDefect);
            var mapping = DlngRules.FindMapping(defect, _settings.DlngRules);
            if (mapping is null || !DlngRules.IsEligibleJudge(judge, _settings.DlngRules)) continue;
            if (!TryTimestamp(row.Get(ProductionCsvSchema.Date), row.Get(ProductionCsvSchema.Time), out var inspectedAt)) continue;

            var sides = MatchingSides(row, judge, mapping.Defect);
            if (sides.Count > 0) eligibleRows++;
            foreach (var side in sides)
            {
                var rawImages = RawImages(machine, row, side);
                var sourceFolder = rawImages
                    .Select(image => Path.GetDirectoryName(image.Path))
                    .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
                    ?? string.Empty;
                var key = string.Join(
                    "|",
                    machine.Id,
                    inspectedAt.ToString("O", CultureInfo.InvariantCulture),
                    row.Get(ProductionCsvSchema.LotId),
                    cellId,
                    judge,
                    mapping.Defect,
                    side);
                yield return new(
                    key,
                    machine.Id,
                    machine.OutputFolderName,
                    machine.Polarity,
                    inspectedAt,
                    row.Get(ProductionCsvSchema.ModelId),
                    row.Get(ProductionCsvSchema.LotId),
                    cellId,
                    judge,
                    mapping.Defect,
                    side,
                    string.Empty,
                    string.Empty,
                    DlngModelKind.FallbackRaw,
                    rawImages,
                    snapshot.SourcePath,
                    rowNumber,
                    sourceFolder);
            }
        }
        progress?.Report(
            $"{machine.OutputFolderName}: finished scanning {Path.GetFileName(snapshot.SourcePath)}; " +
            $"{rowNumber:N0} row(s), {eligibleRows:N0} eligible row(s).");
    }

    private bool IsIgnoredCell(string cellId) =>
        string.IsNullOrWhiteSpace(cellId)
        || _settings.KickoutRules.IgnoredCellPrefixes.Any(prefix =>
            cellId.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> MatchingSides(CsvRow row, string judge, string defect)
    {
        if (judge.Equals("NG", StringComparison.OrdinalIgnoreCase))
        {
            return SideList(
                row.Get(ProductionCsvSchema.UpperJudge).Equals("NG", StringComparison.OrdinalIgnoreCase),
                row.Get(ProductionCsvSchema.LowerJudge).Equals("NG", StringComparison.OrdinalIgnoreCase));
        }

        return SideList(
            IsBypassNg(row, "UPPER", defect),
            IsBypassNg(row, "LOWER", defect));
    }

    private static bool IsBypassNg(CsvRow row, string side, string defect) =>
        SideJudgeColumns(side, defect).Any(column =>
            row.Get(column).Equals("BYPASS_NG", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> SideJudgeColumns(string side, string defect)
    {
        yield return $"{side}_{defect}-JUDGE";
        if (defect.Equals("SEPA_SHOULDER", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"{side}_SEPA_SHOULDER_DL-JUDGE";
        }
    }

    private static IReadOnlyList<string> SideList(bool upper, bool lower) =>
        (upper, lower) switch
        {
            (true, true) => [ProductionImageConventions.Upper, ProductionImageConventions.Lower],
            (true, false) => [ProductionImageConventions.Upper],
            (false, true) => [ProductionImageConventions.Lower],
            _ => []
        };

    private IReadOnlyList<DlngImage> RawImages(
        WeldingMachine machine,
        CsvRow row,
        string side)
    {
        var images = new List<DlngImage>(ProductionImageConventions.ImagesPerSide);
        for (var index = 1; index <= ProductionImageConventions.ImagesPerSide; index++)
        {
            var productionPath = row.Get(ProductionCsvSchema.ImagePath(side, index));
            if (string.IsNullOrWhiteSpace(productionPath)) continue;
            images.Add(new(
                $"Raw {index}",
                ProductionPathMapper.ToUnc(machine, productionPath, _shares),
                false));
        }

        return images;
    }

    private static bool TryTimestamp(string date, string time, out DateTime timestamp) =>
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

        public string Get(string name) =>
            _values.TryGetValue(name, out var value) ? value : string.Empty;
    }
}
