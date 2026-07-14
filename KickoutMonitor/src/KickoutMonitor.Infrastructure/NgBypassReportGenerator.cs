using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class NgBypassReportGenerator : INgBypassReportService
{
    private readonly NgBypassQueueService _queue;
    private readonly IDailyCsvLocator _locator;
    private readonly IReadOnlySnapshotService _snapshots;
    private readonly IInspectionSummaryCsvReader _summaryReader;
    private readonly INgBypassReviewStore _reviews;
    private readonly AppStorage _storage;
    private readonly TimeSpan _reportStartTime;

    public NgBypassReportGenerator(
        NgBypassQueueService queue,
        IDailyCsvLocator locator,
        IReadOnlySnapshotService snapshots,
        IInspectionSummaryCsvReader summaryReader,
        INgBypassReviewStore reviews,
        AppStorage storage,
        VisionMasterSettings? settings = null)
    {
        _queue = queue;
        _locator = locator;
        _snapshots = snapshots;
        _summaryReader = summaryReader;
        _reviews = reviews;
        _storage = storage;
        _reportStartTime = TimeSpan.TryParse(settings?.KickoutRules.ReportStartTime, out var configured)
            ? configured
            : TimeSpan.FromHours(6);
    }

    public async Task<NgBypassReportResult> GenerateAsync(
        IReadOnlyList<WeldingMachine> machines,
        NgBypassQuery query,
        DateOnly reportDate,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var windowStart = reportDate.ToDateTime(TimeOnly.FromTimeSpan(_reportStartTime));
        var windowEndExclusive = windowStart.AddDays(1);
        var items = new List<NgBypassCandidate>();
        var totalInspectedByMachine = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var machine in machines)
        {
            totalInspectedByMachine[machine.Id] = 0;
            for (var date = reportDate; date <= reportDate.AddDays(1); date = date.AddDays(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    progress?.Report($"Loading NG/Bypass queue for {machine.OutputFolderName} {date:yyyy-MM-dd}...");
                    var load = await _queue.LoadAsync(machine, date, query, progress, cancellationToken);
                    items.AddRange(load.Items);
                    foreach (var warning in load.HeaderWarnings)
                    {
                        progress?.Report($"Missing measure column: {warning.Machine} {warning.Date:yyyy-MM-dd} {warning.FileName} column={warning.ColumnName}");
                    }

                    totalInspectedByMachine[machine.Id] += await CountWindowRowsAsync(
                        machine,
                        date,
                        query,
                        windowStart,
                        windowEndExclusive,
                        cancellationToken);
                }
                catch (FileNotFoundException)
                {
                    progress?.Report($"{machine.OutputFolderName} {date:yyyy-MM-dd}: no daily CSV found.");
                }
            }
        }

        var windowItems = items
            .Where(x => x.InspectedAt >= windowStart && x.InspectedAt < windowEndExclusive)
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.LinePolarity, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.InspectedAt)
            .ThenBy(x => x.CellId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var reviews = await _reviews.LoadAsync(cancellationToken);
        var missing = windowItems.Count(x =>
            !reviews.TryGetValue(x.Key, out var review)
            || review.Decision is not (ReviewDecision.RealNg or ReviewDecision.Overkill));
        if (missing > 0)
        {
            throw new InvalidOperationException(
                $"Cannot generate NG/Bypass summary: {missing:N0} item(s) are not reviewed.");
        }

        var rows = BuildRows(windowItems, reviews, totalInspectedByMachine);
        var details = BuildDetails(windowItems, reviews);
        var outputFolder = Path.Combine(_storage.NgBypassSummary, $"NG_Bypass_Summary_{reportDate:yyyyMMdd}");
        if (Directory.Exists(outputFolder)) Directory.Delete(outputFolder, true);
        Directory.CreateDirectory(outputFolder);
        Directory.CreateDirectory(Path.Combine(outputFolder, "REAL"));
        Directory.CreateDirectory(Path.Combine(outputFolder, "OVERKILL"));
        var workbook = Path.Combine(outputFolder, $"NG_Bypass_Summary_{reportDate:yyyyMMdd}.xlsx");
        WriteWorkbook(workbook, reportDate, windowStart, windowEndExclusive, rows, details);
        CopyReviewedImages(outputFolder, details, cancellationToken);
        return new(reportDate, windowStart, windowEndExclusive, outputFolder, workbook, rows);
    }

    private async Task<int> CountWindowRowsAsync(
        WeldingMachine machine,
        DateOnly date,
        NgBypassQuery query,
        DateTime windowStart,
        DateTime windowEndExclusive,
        CancellationToken cancellationToken)
    {
        var sources = await _locator.FindAsync(machine, date, cancellationToken);
        var count = 0;
        var selectedColumns = NgBypassCsvReader.SelectedColumns(query).Select(x => x.ColumnName).ToArray();
        foreach (var source in sources)
        {
            var snapshot = await _snapshots.CreateAsync(
                source,
                date == DateOnly.FromDateTime(DateTime.Now),
                cancellationToken);
            await foreach (var record in _summaryReader.ReadAsync(machine, snapshot, cancellationToken))
            {
                if (record.InspectedAt < windowStart || record.InspectedAt >= windowEndExclusive) continue;
                if (query.Bypassed
                    && query.SkipNg
                    && record.Judge.Equals("NG", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!selectedColumns.All(column => record.Headers.Any(header => header.Equals(column, StringComparison.OrdinalIgnoreCase)))) continue;
                count++;
            }
        }

        return count;
    }

    private static IReadOnlyList<NgBypassReportRow> BuildRows(
        IReadOnlyList<NgBypassCandidate> items,
        IReadOnlyDictionary<string, NgBypassReviewRecord> reviews,
        IReadOnlyDictionary<string, int> totalInspectedByMachine)
    {
        return items
            .GroupBy(x => new { x.MachineId, x.LinePolarity, x.Measure, x.Side, x.TargetValue })
            .Select(group =>
            {
                var total = totalInspectedByMachine.TryGetValue(group.Key.MachineId, out var value) ? value : 0;
                var real = group.Count(x => reviews[x.Key].Decision == ReviewDecision.RealNg);
                var overkill = group.Count(x => reviews[x.Key].Decision == ReviewDecision.Overkill);
                var matched = group.Count();
                return new NgBypassReportRow(
                    group.Key.LinePolarity,
                    group.Key.Measure,
                    group.Key.Side,
                    group.Key.TargetValue,
                    total,
                    matched,
                    real,
                    overkill,
                    Rate(matched, total),
                    Rate(real, total),
                    Rate(overkill, total));
            })
            .OrderBy(x => x.LinePolarity, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Measure, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Side, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<NgBypassSummaryDetailRow> BuildDetails(
        IReadOnlyList<NgBypassCandidate> items,
        IReadOnlyDictionary<string, NgBypassReviewRecord> reviews) =>
        items.Select(item =>
        {
            var review = reviews[item.Key];
            return new NgBypassSummaryDetailRow(
                item.LinePolarity,
                item.Measure,
                item.Side,
                item.TargetValue,
                review.Decision,
                review.LocalFolder,
                item.Headers,
                item.Values);
        }).ToArray();

    private static double Rate(int numerator, int denominator) =>
        denominator == 0 ? 0 : (double)numerator / denominator;

    private static void CopyReviewedImages(
        string outputFolder,
        IReadOnlyList<NgBypassSummaryDetailRow> details,
        CancellationToken cancellationToken)
    {
        foreach (var detail in details)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(detail.LocalFolder) || !Directory.Exists(detail.LocalFolder)) continue;
            var classification = detail.Decision == ReviewDecision.Overkill ? "OVERKILL" : "REAL";
            var destinationParent = Path.Combine(
                outputFolder,
                classification,
                SafeName(detail.LinePolarity),
                SafeName(detail.Measure),
                SafeName(detail.Side));
            Directory.CreateDirectory(destinationParent);
            var destination = Path.Combine(destinationParent, Path.GetFileName(detail.LocalFolder));
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            CopyDirectory(detail.LocalFolder, destination, cancellationToken);
        }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static void WriteWorkbook(
        string path,
        DateOnly reportDate,
        DateTime windowStart,
        DateTime windowEndExclusive,
        IReadOnlyList<NgBypassReportRow> rows,
        IReadOnlyList<NgBypassSummaryDetailRow> details)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        WriteEntry(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
            </Relationships>
            """);
        WriteEntry(archive, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
                <sheet name="Summary" sheetId="1" r:id="rId1"/>
                <sheet name="Details" sheetId="2" r:id="rId2"/>
              </sheets>
            </workbook>
            """);

        var summaryRows = rows.Select(x => new[]
        {
            x.LinePolarity,
            x.Measure,
            x.Side,
            x.TargetValue,
            x.TotalInspected.ToString(CultureInfo.InvariantCulture),
            x.InitialMatched.ToString(CultureInfo.InvariantCulture),
            x.Real.ToString(CultureInfo.InvariantCulture),
            x.Overkill.ToString(CultureInfo.InvariantCulture),
            x.InitialRate.ToString("P2", CultureInfo.InvariantCulture),
            x.RealRate.ToString("P2", CultureInfo.InvariantCulture),
            x.OverkillRate.ToString("P2", CultureInfo.InvariantCulture)
        }).ToArray();
        WriteEntry(
            archive,
            "xl/worksheets/sheet1.xml",
            WorksheetXml(
                ["Report Date", reportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)],
                ["Window Start", windowStart.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)],
                ["Window End", windowEndExclusive.AddMinutes(-1).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)],
                ["Line", "Measure", "Side", "Target", "Total", "Initial", "Real", "Overkill", "Initial Rate", "Real Rate", "Overkill Rate"],
                summaryRows));

        var detailRows = details.Select(x => new[]
        {
            x.LinePolarity,
            x.Measure,
            x.Side,
            x.TargetValue,
            x.Decision.ToString(),
            string.Join(";", x.Values)
        }).ToArray();
        WriteEntry(
            archive,
            "xl/worksheets/sheet2.xml",
            WorksheetXml(
                ["Line", "Measure", "Side", "Target", "Decision", "CSV Values"],
                detailRows));
    }

    private static string WorksheetXml(
        IReadOnlyList<string> meta1,
        IReadOnlyList<string> meta2,
        IReadOnlyList<string> meta3,
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var allRows = new List<IReadOnlyList<string>> { meta1, meta2, meta3, Array.Empty<string>(), headers };
        allRows.AddRange(rows);
        return WorksheetXml(allRows);
    }

    private static string WorksheetXml(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var allRows = new List<IReadOnlyList<string>> { headers };
        allRows.AddRange(rows);
        return WorksheetXml(allRows);
    }

    private static string WorksheetXml(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        builder.AppendLine("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var index = 0; index < rows.Count; index++) AppendRow(builder, index + 1, rows[index]);
        builder.AppendLine("</sheetData></worksheet>");
        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, int rowNumber, IReadOnlyList<string> values)
    {
        builder.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowNumber}\">");
        for (var column = 0; column < values.Count; column++)
        {
            builder.Append("<c r=\"").Append(ColumnName(column)).Append(rowNumber.ToString(CultureInfo.InvariantCulture)).Append("\" t=\"inlineStr\"><is><t>")
                .Append(SecurityElement.Escape(values[column]) ?? string.Empty)
                .Append("</t></is></c>");
        }
        builder.AppendLine("</row>");
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        index++;
        while (index > 0)
        {
            var remainder = (index - 1) % 26;
            name = (char)('A' + remainder) + name;
            index = (index - 1) / 26;
        }
        return name;
    }

    private static void WriteEntry(ZipArchive archive, string name, string contents)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(contents);
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
