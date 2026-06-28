using System.Globalization;
using System.Runtime.CompilerServices;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class InspectionSummaryCsvReader : IInspectionSummaryCsvReader
{
    public async IAsyncEnumerable<InspectionSummaryRecord> ReadAsync(
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

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = CsvSupport.ParseLine(line);
            if (values.Count < headers.Count) continue;

            var row = new CsvRow(headers, values);
            var cellId = row.Get("CELL-ID");
            if (string.IsNullOrWhiteSpace(cellId)
                || cellId.StartsWith("OCR", StringComparison.OrdinalIgnoreCase)
                || cellId.StartsWith("AGING", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryTimestamp(row.Get("DATE"), row.Get("TIME"), out var inspectedAt)) continue;
            var judge = row.Get("JUDGE")?.Trim() ?? string.Empty;
            var lotId = row.Get("LOT-ID")?.Trim() ?? string.Empty;
            var defect = KickoutRules.NormalizeDefect(row.Get("JUDGE-DEFECT"));
            var key = string.Join(
                "|",
                machine.Id,
                inspectedAt.ToString("O", CultureInfo.InvariantCulture),
                lotId,
                cellId.Trim());

            yield return new(
                machine.Id,
                inspectedAt,
                lotId,
                cellId.Trim(),
                judge,
                defect,
                KickoutRules.GetNgSide(row.Get("UPPER_JUDGE"), row.Get("LOWER_JUDGE")),
                key,
                headers,
                values.Take(headers.Count).Select(value => value.Trim()).ToArray(),
                snapshot.SourcePath);
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
