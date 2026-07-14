using System.Globalization;
using System.Runtime.CompilerServices;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class NgBypassCsvReader : INgBypassCsvReader
{
    private readonly ISharePathResolver _shares;
    private readonly VisionMasterSettings _settings;

    public NgBypassCsvReader(ISharePathResolver shares, VisionMasterSettings? settings = null)
    {
        _shares = shares;
        _settings = settings ?? VisionMasterSettings.CreateDefault();
    }

    public async IAsyncEnumerable<NgBypassCandidate> ReadAsync(
        WeldingMachine machine,
        SnapshotResult snapshot,
        NgBypassQuery query,
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
        var selectedColumns = SelectedColumns(query);
        var missingColumns = selectedColumns
            .Where(column => !headers.Any(header => header.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase)))
            .Select(column => column.ColumnName)
            .ToArray();
        if (missingColumns.Length > 0)
        {
            throw new NgBypassMissingHeaderException(missingColumns);
        }

        var rowNumber = 1;
        var matched = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            rowNumber++;
            if (rowNumber == 2 || rowNumber % 5000 == 0)
            {
                progress?.Report(
                    $"{machine.OutputFolderName}: scanning {Path.GetFileName(snapshot.SourcePath)} row {rowNumber:N0}; " +
                    $"{matched:N0} matching row(s) found...");
            }
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = CsvSupport.ParseLine(line);
            if (values.Count < headers.Count) continue;

            var row = new CsvRow(headers, values);
            var cellId = row.Get(ProductionCsvSchema.CellId);
            if (IsIgnoredCell(cellId)) continue;
            if (!TryTimestamp(row.Get(ProductionCsvSchema.Date), row.Get(ProductionCsvSchema.Time), out var inspectedAt)) continue;
            if (query.Bypassed
                && query.SkipNg
                && row.Get(ProductionCsvSchema.Judge).Equals("NG", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var column in selectedColumns)
            {
                if (!row.Get(column.ColumnName).Equals(query.TargetValue, StringComparison.OrdinalIgnoreCase)) continue;
                var images = RawImages(machine, row, column.Side);
                if (images.Count == 0) continue;
                matched++;
                var sourceFolder = images
                    .Select(image => Path.GetDirectoryName(image.Path))
                    .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
                    ?? string.Empty;
                var key = string.Join(
                    "|",
                    machine.Id,
                    inspectedAt.ToString("O", CultureInfo.InvariantCulture),
                    row.Get(ProductionCsvSchema.LotId),
                    cellId,
                    query.Measure.Trim(),
                    column.Side,
                    query.TargetValue);
                yield return new(
                    key,
                    machine.Id,
                    machine.OutputFolderName,
                    machine.Polarity,
                    inspectedAt,
                    row.Get(ProductionCsvSchema.ModelId),
                    row.Get(ProductionCsvSchema.LotId),
                    cellId,
                    query.Measure.Trim(),
                    column.Side,
                    column.ColumnName,
                    query.TargetValue,
                    images,
                    sourceFolder,
                    snapshot.SourcePath,
                    rowNumber,
                    headers,
                    values.Take(headers.Count).ToArray());
            }
        }
        progress?.Report(
            $"{machine.OutputFolderName}: finished scanning {Path.GetFileName(snapshot.SourcePath)}; " +
            $"{rowNumber:N0} row(s), {matched:N0} matching row(s).");
    }

    public static IReadOnlyList<(string Side, string ColumnName)> SelectedColumns(NgBypassQuery query)
    {
        var measure = query.Measure.Trim();
        var columns = new List<(string Side, string ColumnName)>();
        if (query.IncludeUpper) columns.Add((ProductionImageConventions.Upper, $"{ProductionImageConventions.Upper}_{measure}-OK/NG"));
        if (query.IncludeLower) columns.Add((ProductionImageConventions.Lower, $"{ProductionImageConventions.Lower}_{measure}-OK/NG"));
        return columns;
    }

    private bool IsIgnoredCell(string cellId) =>
        string.IsNullOrWhiteSpace(cellId)
        || _settings.KickoutRules.IgnoredCellPrefixes.Any(prefix =>
            cellId.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

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
