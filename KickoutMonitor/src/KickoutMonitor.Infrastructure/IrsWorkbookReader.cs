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
            var headerRowNumber = FindHeaderRow(rowValues)
                ?? throw new InvalidDataException("The IRS workbook does not contain recognizable IRS headers.");
            if (!rowValues.TryGetValue(headerRowNumber, out var row1))
            {
                throw new InvalidDataException("The IRS workbook does not contain a readable header row.");
            }

            rowValues.TryGetValue(headerRowNumber + 1, out var possibleSubHeader);
            var hasSubHeader = possibleSubHeader is not null && LooksLikeSubHeader(possibleSubHeader);
            var headers = BuildHeaders(
                row1,
                hasSubHeader ? possibleSubHeader! : new Dictionary<int, string>());
            var firstDataRow = headerRowNumber + (hasSubHeader ? 2 : 1);
            var requested = new List<IrsReviewCandidate>();
            foreach (var pair in rowValues.Where(x => x.Key >= firstDataRow).OrderBy(x => x.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = new IrsRow(headers, pair.Value);
                if (!row.GetAny("Request for NG Cell OUT", "NG Out/Request", "Request").Equals(
                        "Request",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var producedAt = ParseDate(row.GetAny("Prod. date", "Prod date", "Production date"));
                var cellId = row.GetAny("Cell ID", "CellID", "Cell");
                if (producedAt is null || string.IsNullOrWhiteSpace(cellId)) continue;
                var image = row.GetAny("Image", "Image file", "Image filename");
                var eqpt = row.GetAny("Eqpt", "Equipment", "Machine");
                var visionType = row.GetAny("Vision Type", "VisionType", "Vision");
                var cameraLocation = row.GetAny("Camera Location", "CameraLocation", "Side");
                var key = string.Join(
                    "|",
                    eqpt,
                    visionType,
                    producedAt.Value.ToString("O", CultureInfo.InvariantCulture),
                    cellId,
                    cameraLocation,
                    image);
                requested.Add(new(
                    key,
                    eqpt,
                    visionType,
                    string.Empty,
                    producedAt.Value,
                    row.GetAny("Lot ID", "LotID", "Lot"),
                    cellId,
                    cameraLocation,
                    image,
                    row.GetAny("2nd judgment/Result", "Second judgment/Result", "2nd judgment result", "Second result"),
                    row.GetAny("2nd judgment/reason", "Second judgment/reason", "2nd judgment reason", "Second reason"),
                    pair.Key));
            }

        return requested;
        }, cancellationToken);
    }

    private static int? FindHeaderRow(IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>> rows)
    {
        foreach (var (rowNumber, values) in rows.Where(x => x.Key <= 10).OrderBy(x => x.Key))
        {
            var normalized = values.Values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(NormalizeHeader)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (normalized.Contains(NormalizeHeader("Eqpt"))
                && normalized.Contains(NormalizeHeader("Cell ID"))
                && normalized.Contains(NormalizeHeader("Prod. date")))
            {
                return rowNumber;
            }
        }

        return null;
    }

    private static bool LooksLikeSubHeader(IReadOnlyDictionary<int, string> row)
    {
        var values = row.Values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
        if (values.Length == 0) return false;
        return values.Any(value =>
            value.Equals("Result", StringComparison.OrdinalIgnoreCase)
            || value.Equals("reason", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Inspector", StringComparison.OrdinalIgnoreCase)
            || value.Equals("completion date", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Request", StringComparison.OrdinalIgnoreCase)
            || value.Equals("PKG ID", StringComparison.OrdinalIgnoreCase));
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
        private readonly Dictionary<string, string> _normalizedValues = new(StringComparer.OrdinalIgnoreCase);

        public IrsRow(
            IReadOnlyDictionary<int, string> headers,
            IReadOnlyDictionary<int, string> values)
        {
            foreach (var (column, header) in headers)
            {
                _values[header] = values.TryGetValue(column, out var value)
                    ? value.Trim()
                    : string.Empty;
                _normalizedValues[NormalizeHeader(header)] = _values[header];
            }
        }

        public string Get(string header) =>
            _values.TryGetValue(header, out var value) ? value : string.Empty;

        public string GetAny(params string[] headers)
        {
            foreach (var header in headers)
            {
                if (_values.TryGetValue(header, out var value)) return value;
                if (_normalizedValues.TryGetValue(NormalizeHeader(header), out value)) return value;
            }

            return string.Empty;
        }
    }

    private static string NormalizeHeader(string header) =>
        new(header
            .Where(character => !char.IsWhiteSpace(character)
                && character != '.'
                && character != '-'
                && character != '_'
                && character != '/')
            .Select(char.ToUpperInvariant)
            .ToArray());
}
