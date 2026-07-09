using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class DlngReportGenerator : IDlngReportService
{
    private readonly DlngQueueService _queue;
    private readonly IDlngReviewStore _reviews;
    private readonly AppStorage _storage;

    public DlngReportGenerator(
        DlngQueueService queue,
        IDlngReviewStore reviews,
        AppStorage storage)
    {
        _queue = queue;
        _reviews = reviews;
        _storage = storage;
    }

    public async Task<DlngReportResult> GenerateAsync(
        IReadOnlyList<WeldingMachine> machines,
        DateOnly reportDate,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var windowStart = reportDate.ToDateTime(new TimeOnly(6, 0));
        var windowEnd = reportDate.AddDays(1).ToDateTime(new TimeOnly(6, 0));
        var items = new List<DlngReviewItem>();
        foreach (var machine in machines)
        {
            foreach (var date in new[] { reportDate, reportDate.AddDays(1) })
            {
                try
                {
                    progress?.Report($"Loading DLNG queue for {machine.OutputFolderName} {date:yyyy-MM-dd}...");
                    items.AddRange(await _queue.LoadAsync(machine, date, progress, cancellationToken));
                }
                catch (FileNotFoundException)
                {
                    progress?.Report($"{machine.OutputFolderName} {date:yyyy-MM-dd}: no daily CSV found.");
                }
            }
        }

        var windowItems = items
            .Where(x => x.InspectedAt >= windowStart && x.InspectedAt < windowEnd)
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.InspectedAt)
            .ToArray();
        var decisions = await _reviews.LoadAsync(cancellationToken);
        var missing = windowItems.Count(x => !decisions.ContainsKey(x.Key));
        if (missing > 0)
        {
            throw new InvalidOperationException(
                $"Cannot generate DLNG report: {missing:N0} DLNG crop item(s) are not classified.");
        }

        var outputFolder = Path.Combine(_storage.DlngReport, $"DLNG_REPORT_{reportDate:yyyyMMdd}");
        if (Directory.Exists(outputFolder)) Directory.Delete(outputFolder, true);
        Directory.CreateDirectory(outputFolder);
        var datasetRoot = Path.Combine(outputFolder, "Dataset");
        Directory.CreateDirectory(datasetRoot);

        var rows = windowItems.Select(item => (Item: item, Decision: decisions[item.Key])).ToArray();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyDataset(row.Item, row.Decision, datasetRoot, cancellationToken);
        }

        var summaryRows = rows
            .GroupBy(x => new
            {
                x.Item.LinePolarity,
                x.Item.Judge,
                x.Item.JudgeDefect,
                x.Item.CropFolder,
                DatasetSection = DatasetSection(x.Item, x.Decision),
                x.Decision.SourceClass,
                x.Decision.FinalClass
            })
            .Select(group => new DlngReportRow(
                group.Key.DatasetSection,
                group.Key.LinePolarity,
                group.Key.Judge,
                group.Key.JudgeDefect,
                group.Key.CropFolder,
                group.Key.SourceClass,
                group.Key.FinalClass,
                group.Count(),
                group.Count(x => IsSwitch(x.Item, x.Decision))))
            .OrderBy(x => x.LinePolarity)
            .ThenBy(x => x.DatasetSection)
            .ThenBy(x => x.CropFolder)
            .ThenBy(x => x.SourceClass)
            .ThenBy(x => x.FinalClass)
            .ToArray();

        var workbook = Path.Combine(outputFolder, $"DLNG_REPORT_{reportDate:yyyyMMdd}.xlsx");
        WriteWorkbook(workbook, summaryRows, rows);
        return new(reportDate, outputFolder, workbook, summaryRows);
    }

    private static bool IsSwitch(DlngReviewItem item, DlngReviewRecord decision) =>
        item.ModelKind == DlngModelKind.Classification
        && !string.IsNullOrWhiteSpace(decision.SourceClass)
        && !decision.SourceClass.Equals(decision.FinalClass, StringComparison.OrdinalIgnoreCase);

    private static void CopyDataset(
        DlngReviewItem item,
        DlngReviewRecord decision,
        string datasetRoot,
        CancellationToken cancellationToken)
    {
        var destinationFolder = DatasetDestinationFolder(item, decision, datasetRoot);
        Directory.CreateDirectory(destinationFolder);
        var images = decision.IsFallbackRaw && Directory.Exists(item.SourceFolder)
            ? Directory.EnumerateFiles(item.SourceFolder)
                .Where(IsImageFile)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : decision.ImagePaths.Where(File.Exists).ToArray();
        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destinationFolder, Path.GetFileName(image));
            File.Copy(image, target, true);
        }
    }

    private static string DatasetDestinationFolder(
        DlngReviewItem item,
        DlngReviewRecord decision,
        string datasetRoot)
    {
        if (decision.IsFallbackRaw)
        {
            return Path.Combine(
                datasetRoot,
                "Segmentation",
                "NEED_TO_SIMULATE",
                SafeName(item.CropFolder),
                SafeName(item.CellId));
        }

        if (item.ModelKind == DlngModelKind.Classification)
        {
            return Path.Combine(
                datasetRoot,
                "Classification",
                SafeName(ClassificationCategory(decision.SourceClass, decision.FinalClass)),
                SafeName(item.CropFolder),
                SafeName(item.LinePolarity),
                SafeName(DatasetClassFolder(item, decision)));
        }

        return Path.Combine(
            datasetRoot,
            "Segmentation",
            SafeName(item.CropFolder),
            SafeName(DatasetClassFolder(item, decision)));
    }

    private static string DatasetSection(DlngReviewItem item, DlngReviewRecord decision)
    {
        if (item.ModelKind == DlngModelKind.Classification && !decision.IsFallbackRaw)
        {
            return $"Classification/{ClassificationCategory(decision.SourceClass, decision.FinalClass)}";
        }

        return "Segmentation";
    }

    private static string ClassificationCategory(string sourceClass, string finalClass)
    {
        if (sourceClass.Equals(finalClass, StringComparison.OrdinalIgnoreCase))
        {
            return "정상검출";
        }

        var sourceOk = IsOkClass(sourceClass);
        var finalOk = IsOkClass(finalClass);
        if (sourceOk && !finalOk) return "미검_오검";
        if (!sourceOk && finalOk) return "과검";
        return "미검_오검";
    }

    private static bool IsOkClass(string className)
    {
        var value = className.Trim();
        if (value.Length == 0) return false;
        var firstPart = value.Split('_', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? value;
        if (firstPart.All(char.IsDigit))
        {
            value = value[(firstPart.Length)..].TrimStart('_', ' ', '-');
        }

        return value.StartsWith("OK", StringComparison.OrdinalIgnoreCase);
    }

    private static string DatasetClassFolder(DlngReviewItem item, DlngReviewRecord decision) =>
        item.ModelKind == DlngModelKind.Segmentation
        && decision.FinalClass.Equals("No Need to Train", StringComparison.OrdinalIgnoreCase)
            ? "OVERKILL"
            : decision.FinalClass;

    private static void WriteWorkbook(
        string path,
        IReadOnlyList<DlngReportRow> summaryRows,
        IReadOnlyList<(DlngReviewItem Item, DlngReviewRecord Decision)> details)
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

        var summary = summaryRows
            .Select(x => new[]
            {
                x.DatasetSection, x.LinePolarity, x.Judge, x.JudgeDefect, x.CropFolder, x.SourceClass,
                x.FinalClass, x.Count.ToString(CultureInfo.InvariantCulture),
                x.SwitchedCount.ToString(CultureInfo.InvariantCulture)
            })
            .ToArray();
        WriteEntry(
            archive,
            "xl/worksheets/sheet1.xml",
            WorksheetXml(
                ["Dataset Section", "Line", "Judge", "Judge Defect", "Crop Folder", "Source Class", "Final Class", "Count", "Switched Count"],
                summary));

        var detailRows = details.Select(x => new[]
        {
            DatasetSection(x.Item, x.Decision),
            x.Item.LinePolarity,
            x.Item.InspectedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            x.Item.CellId,
            x.Item.Judge,
            x.Item.JudgeDefect,
            x.Item.Side,
            x.Item.CropFolder,
            x.Decision.SourceClass,
            x.Decision.FinalClass,
            x.Decision.IsFallbackRaw ? "YES" : "",
            string.Join(";", x.Decision.ImagePaths)
        }).ToArray();
        WriteEntry(
            archive,
            "xl/worksheets/sheet2.xml",
            WorksheetXml(
                ["Dataset Section", "Line", "Time", "Cell ID", "Judge", "Judge Defect", "Side", "Crop Folder", "Source Class", "Final Class", "Fallback Raw", "Images"],
                detailRows));
    }

    private static string WorksheetXml(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        builder.AppendLine("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        AppendRow(builder, 1, headers);
        for (var i = 0; i < rows.Count; i++) AppendRow(builder, i + 2, rows[i]);
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

    private static bool IsImageFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value) builder.Append(invalid.Contains(character) ? '_' : character);
        return builder.ToString();
    }
}
