using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class IrsWorkbookReader : IIrsWorkbookReader
{
    private static readonly XNamespace SheetNs =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly DateTime ExcelEpoch = new(1899, 12, 30);

    public Task<IReadOnlyList<IrsReviewCandidate>> ReadRequestedAsync(
        string workbookPath,
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<IrsReviewCandidate>>(() =>
        {
            if (!File.Exists(workbookPath))
            {
                throw new FileNotFoundException("IRS workbook was not found.", workbookPath);
            }

            using var archive = ZipFile.OpenRead(workbookPath);
            var sharedStrings = ReadSharedStrings(archive);
            var sheetEntry = archive.GetEntry("xl/worksheets/sheet1.xml")
                ?? throw new InvalidDataException("The IRS workbook does not contain sheet1.xml.");
            using var sheetStream = sheetEntry.Open();
            var sheet = XDocument.Load(sheetStream);
            var rows = sheet.Root!
                .Element(SheetNs + "sheetData")!
                .Elements(SheetNs + "row")
                .ToArray();
            var rowValues = rows.ToDictionary(
                row => int.Parse(row.Attribute("r")!.Value, CultureInfo.InvariantCulture),
                row => ReadRow(row, sharedStrings));
            if (!rowValues.TryGetValue(1, out var row1)
                || !rowValues.TryGetValue(2, out var row2))
            {
                throw new InvalidDataException("The IRS workbook must have two header rows.");
            }

            var headers = BuildHeaders(row1, row2);
            var requested = new List<IrsReviewCandidate>();
            foreach (var pair in rowValues.Where(x => x.Key >= 3).OrderBy(x => x.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = new IrsRow(headers, pair.Value);
                if (!row.Get("Request for NG Cell OUT").Equals(
                        "Request",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var producedAt = ParseDate(row.Get("Prod. date"));
                var cellId = row.Get("Cell ID");
                if (producedAt is null || string.IsNullOrWhiteSpace(cellId)) continue;
                var image = row.Get("Image");
                var key = string.Join(
                    "|",
                    row.Get("Eqpt"),
                    row.Get("Vision Type"),
                    producedAt.Value.ToString("O", CultureInfo.InvariantCulture),
                    cellId,
                    row.Get("Camera Location"),
                    image);
                requested.Add(new(
                    key,
                    row.Get("Eqpt"),
                    row.Get("Vision Type"),
                    string.Empty,
                    producedAt.Value,
                    row.Get("Lot ID"),
                    cellId,
                    row.Get("Camera Location"),
                    image,
                    row.Get("2nd judgment/Result"),
                    row.Get("2nd judgment/reason"),
                    pair.Key));
            }

            return requested;
        }, cancellationToken);
    }

    private static Dictionary<int, string> BuildHeaders(
        IReadOnlyDictionary<int, string> row1,
        IReadOnlyDictionary<int, string> row2)
    {
        var max = Math.Max(
            row1.Keys.DefaultIfEmpty(0).Max(),
            row2.Keys.DefaultIfEmpty(0).Max());
        var headers = new Dictionary<int, string>();
        var parent = string.Empty;
        for (var column = 1; column <= max; column++)
        {
            if (row1.TryGetValue(column, out var top)
                && !string.IsNullOrWhiteSpace(top))
            {
                parent = top.Trim();
            }

            row2.TryGetValue(column, out var child);
            headers[column] = string.IsNullOrWhiteSpace(child)
                ? parent
                : $"{parent}/{child.Trim()}";
        }

        return headers;
    }

    private static IReadOnlyDictionary<int, string> ReadRow(
        XElement row,
        IReadOnlyList<string> sharedStrings)
    {
        var values = new Dictionary<int, string>();
        foreach (var cell in row.Elements(SheetNs + "c"))
        {
            var reference = cell.Attribute("r")?.Value;
            if (reference is null) continue;
            var column = ColumnIndex(reference);
            values[column] = ReadCell(cell, sharedStrings);
        }

        return values;
    }

    private static string ReadCell(
        XElement cell,
        IReadOnlyList<string> sharedStrings)
    {
        var type = cell.Attribute("t")?.Value;
        if (type == "s"
            && int.TryParse(
                cell.Element(SheetNs + "v")?.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var sharedIndex)
            && sharedIndex >= 0
            && sharedIndex < sharedStrings.Count)
        {
            return sharedStrings[sharedIndex];
        }

        if (type == "inlineStr")
        {
            return string.Concat(cell.Descendants(SheetNs + "t").Select(x => x.Value));
        }

        return cell.Element(SheetNs + "v")?.Value ?? string.Empty;
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        return document.Root!
            .Elements(SheetNs + "si")
            .Select(si => string.Concat(si.Descendants(SheetNs + "t").Select(t => t.Value)))
            .ToArray();
    }

    private static int ColumnIndex(string reference)
    {
        var result = 0;
        foreach (var character in reference.TakeWhile(char.IsLetter))
        {
            result = result * 26 + char.ToUpperInvariant(character) - 'A' + 1;
        }
        return result;
    }

    private static DateTime? ParseDate(string value)
    {
        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var parsed))
        {
            return parsed;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var oa))
        {
            return ExcelEpoch.AddDays(oa);
        }

        return null;
    }

    private sealed class IrsRow
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public IrsRow(
            IReadOnlyDictionary<int, string> headers,
            IReadOnlyDictionary<int, string> values)
        {
            foreach (var (column, header) in headers)
            {
                _values[header] = values.TryGetValue(column, out var value)
                    ? value.Trim()
                    : string.Empty;
            }
        }

        public string Get(string header) =>
            _values.TryGetValue(header, out var value) ? value : string.Empty;
    }
}
