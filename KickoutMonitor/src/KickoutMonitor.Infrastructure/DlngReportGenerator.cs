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
    private readonly TrainingCollectionService _collection;

    public DlngReportGenerator(
        DlngQueueService queue,
        IDlngReviewStore reviews,
        AppStorage storage)
    {
        _queue = queue;
        _reviews = reviews;
        _storage = storage;
        _collection = new(storage);
    }

    public async Task<DlngReportResult> GenerateAsync(
        IReadOnlyList<WeldingMachine> machines,
        DateOnly reportDate,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? cropFolders = null)
    {
        var windowStart = reportDate.ToDateTime(new TimeOnly(6, 0));
        var windowEnd = reportDate.AddDays(1).ToDateTime(new TimeOnly(6, 0));
        var cropFilter = cropFolders is { Count: > 0 }
            ? new HashSet<string>(cropFolders.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase)
            : null;
        var items = new List<DlngReviewItem>();
        foreach (var machine in machines)
        {
            foreach (var date in new[] { reportDate, reportDate.AddDays(1) })
            {
                try
                {
                    progress?.Report($"Loading DLNG queue for {machine.OutputFolderName} {date:yyyy-MM-dd}...");
                    items.AddRange(await _queue.LoadAsync(machine, date, progress, cancellationToken, cropFilter));
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
        return await GenerateReviewedReportAsync(windowItems, reportDate, progress, cancellationToken);
    }

    public Task<DlngReportResult> GenerateFromItemsAsync(
        IReadOnlyList<DlngReviewItem> items,
        DateOnly reportDate,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var windowStart = reportDate.ToDateTime(new TimeOnly(6, 0));
        var windowEnd = reportDate.AddDays(1).ToDateTime(new TimeOnly(6, 0));
        var windowItems = items
            .Where(x => x.InspectedAt >= windowStart && x.InspectedAt < windowEnd)
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.InspectedAt)
            .ToArray();
        return GenerateReviewedReportAsync(windowItems, reportDate, progress, cancellationToken);
    }

    public async Task<DlngDatasetExportResult> GenerateDatasetFromItemsAsync(
        IReadOnlyList<DlngReviewItem> items,
        DateOnly startDate,
        DateOnly endDate,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (endDate < startDate)
        {
            throw new InvalidOperationException("Dataset end date must be on or after dataset start date.");
        }

        var windowStart = startDate.ToDateTime(new TimeOnly(6, 0));
        var windowEnd = endDate.AddDays(1).ToDateTime(new TimeOnly(6, 0));
        var decisions = await _reviews.LoadAsync(cancellationToken);
        var rows = items
            .Where(x => x.InspectedAt >= windowStart && x.InspectedAt < windowEnd)
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .Where(x => decisions.ContainsKey(x.Key))
            .Select(item => (Item: item, Decision: decisions[item.Key]))
            .Where(x => !x.Decision.IsFallbackRaw && x.Item.ModelKind != DlngModelKind.FallbackRaw)
            .Where(x => !ShouldSkipDatasetExport(x.Item, x.Decision))
            .OrderBy(x => x.Item.CropFolder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Decision.FinalClass, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Item.InspectedAt)
            .ToArray();

        var datasetRoot = Path.Combine(_storage.DlngReport, "DATASET");
        foreach (var item in items.Where(i => i.InspectedAt >= windowStart && i.InspectedAt < windowEnd))
            if (decisions.TryGetValue(item.Key, out var d) && ShouldSkipDatasetExport(item, d)) RemoveExport(datasetRoot, item.Key);
        var dateRange = DateRangeFolder(startDate, endDate);
        var copied = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var local = await LocalDecisionAsync(row.Decision, cancellationToken);
            if (local is null) { RemoveExport(datasetRoot, row.Item.Key); progress?.Report($"Collection unavailable: {row.Item.CellId} / {row.Item.CropFolder}"); continue; }
            copied += CopyFlatDataset(row.Item, local, datasetRoot, dateRange, cancellationToken);
        }

        progress?.Report($"DLNG dataset generated: {copied:N0} image(s) copied.");
        return new(startDate, endDate, datasetRoot, copied);
    }
    private async Task<DlngReportResult> GenerateReviewedReportAsync(
        IReadOnlyList<DlngReviewItem> windowItems,
        DateOnly reportDate,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var decisions = await _reviews.LoadAsync(cancellationToken);
        var reviewedRows = windowItems
            .Where(x => decisions.ContainsKey(x.Key))
            .Select(item => (Item: item, Decision: decisions[item.Key]))
            .ToArray();
        var missing = windowItems.Count - reviewedRows.Length;
        if (missing > 0)
        {
            progress?.Report($"Skipped {missing:N0} unclassified DLNG crop item(s).");
        }

        var rows = reviewedRows;

        if (rows.Length == 0)
        {
            throw new InvalidOperationException("Cannot generate DLNG report: no reviewed DLNG crop item(s) were found for the selected model/date window.");
        }

        var modelSuffix = ModelSuffix(rows.Select(x => x.Item.CropFolder));
        var outputFolder = Path.Combine(_storage.DlngReport, "REPORT", $"DLNG_REPORT_{reportDate:yyyyMMdd}");
        Directory.CreateDirectory(outputFolder);
        var datasetRoot = Path.Combine(outputFolder, "Dataset");
        Directory.CreateDirectory(datasetRoot);

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldSkipDatasetExport(row.Item, row.Decision)) { RemoveExport(datasetRoot, row.Item.Key); continue; }
            var local = await LocalDecisionAsync(row.Decision, cancellationToken);
            if (local is not null) CopyDataset(row.Item, local, datasetRoot, cancellationToken);
            else RemoveExport(datasetRoot, row.Item.Key);
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

        var workbook = Path.Combine(outputFolder, $"DLNG_REPORT_{reportDate:yyyyMMdd}_{modelSuffix}.xlsx");
        if (File.Exists(workbook)) File.Delete(workbook);
        WriteWorkbook(workbook, summaryRows, rows);
        return new(reportDate, outputFolder, workbook, summaryRows);
    }

    private static bool IsSwitch(DlngReviewItem item, DlngReviewRecord decision) =>
        item.ModelKind == DlngModelKind.Classification
        && !string.IsNullOrWhiteSpace(decision.SourceClass)
        && !decision.SourceClass.Equals(decision.FinalClass, StringComparison.OrdinalIgnoreCase);

    private static void RemoveExport(string root, string key)
    {
        var manifest = Path.Combine(root, ".ownership", InspectionIdentity.Hash(key) + ".json");
        if (!File.Exists(manifest)) return;
        var paths = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifest)) ?? [];
        foreach (var path in paths)
            if (Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(path)) File.Delete(path);
        File.Delete(manifest);
    }
    private async Task<DlngReviewRecord?> LocalDecisionAsync(DlngReviewRecord decision, CancellationToken token)
    {
        var id = ReviewSemantics.SampleId(decision);
        var sample = (await _collection.LoadAsync(token)).SelectMany(b => b.Samples)
            .FirstOrDefault(s => s.Id == id && s.State == "Ready" && !s.Superseded && s.Review.FinalClass == decision.FinalClass);
        if (sample is not null && sample.Files.Count == (TrainingCollectionService.Segmentation(decision) ? 2 : 1) && sample.Files.All(File.Exists))
            return decision with { ImagePaths = sample.Files };
        // Export is not an implicit collection operation.
        return null;
    }
    private static void CopyDataset(DlngReviewItem item, DlngReviewRecord decision, string root, CancellationToken token)
    {
        var destination = DatasetDestinationFolder(item, decision, root);
        if (!item.CropFolder.Equals("SEPA", StringComparison.OrdinalIgnoreCase))
            destination = Path.Combine(destination, InspectionIdentity.Hash(item.Inspection?.Identity ?? item.Key));
        ExportPair(item, decision, root, destination, token, true);
    }
    private static int CopyFlatDataset(DlngReviewItem item, DlngReviewRecord decision, string root, string range, CancellationToken token)
    {
        var destination = Path.Combine(root, SafeName(item.CropFolder), range, SafeName(decision.FinalClass));
        if (!item.CropFolder.Equals("SEPA", StringComparison.OrdinalIgnoreCase))
            destination = Path.Combine(destination, InspectionIdentity.Hash(item.Inspection?.Identity ?? item.Key));
        return ExportPair(item, decision, root, destination, token);
    }
    private static int ExportPair(DlngReviewItem item, DlngReviewRecord decision, string root, string folder, CancellationToken token, bool report = false)
    {
        TrainingCollectionService.ValidateTrainingFiles(decision, decision.ImagePaths);
        var id = InspectionIdentity.Hash(item.Key);
        var manifestFolder = Path.Combine(root, ".ownership");
        Directory.CreateDirectory(manifestFolder);
        var manifest = Path.Combine(manifestFolder, id + ".json");
        var previous = File.Exists(manifest)
            ? System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifest)) ?? [] : [];
        Directory.CreateDirectory(folder);
        var targets = decision.ImagePaths.Select(path => Path.Combine(folder,
            item.CropFolder.Equals("SEPA", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(path) : report ? ModelSuffixedFileName(path, item.CropFolder) : Path.GetFileName(path))).ToArray();
        // Record both sets before publishing, so an interrupted export can clean up its own old files.
        File.WriteAllText(manifest + ".tmp", System.Text.Json.JsonSerializer.Serialize(previous.Concat(targets).Distinct()));
        File.Move(manifest + ".tmp", manifest, true);
        for (var i = 0; i < targets.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            File.Copy(decision.ImagePaths[i], targets[i] + ".tmp", true);
            File.Move(targets[i] + ".tmp", targets[i], true);
        }
        foreach (var old in previous.Except(targets, StringComparer.OrdinalIgnoreCase))
            if (Path.GetFullPath(old).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(old))
                File.Delete(old);
        File.WriteAllText(manifest + ".tmp", System.Text.Json.JsonSerializer.Serialize(targets));
        File.Move(manifest + ".tmp", manifest, true);
        return targets.Length;
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

    private static string DatasetClassFolder(DlngReviewItem item, DlngReviewRecord decision) => decision.FinalClass;

    private static string FlatDatasetClassFolder(DlngReviewItem item, DlngReviewRecord decision) => decision.FinalClass;

    private static bool ShouldSkipDatasetExport(DlngReviewItem item, DlngReviewRecord decision) =>
        !decision.IncludeInTraining || decision.IsFallbackRaw || item.ModelKind == DlngModelKind.FallbackRaw || !ReviewSemantics.CanTrain(decision.FinalClass);

    private static bool IsNoNeedToTrain(string finalClass) =>
        finalClass.Equals("No Need to Train", StringComparison.OrdinalIgnoreCase)
        || finalClass.Equals("No Need to Retrain", StringComparison.OrdinalIgnoreCase);

    private static string DateRangeFolder(DateOnly startDate, DateOnly endDate) =>
        startDate == endDate
            ? startDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : $"{startDate:yyyyMMdd}-{endDate:yyyyMMdd}";

    private static string UniqueTargetPath(string destinationFolder, string fileName)
    {
        var target = Path.Combine(destinationFolder, fileName);
        if (!File.Exists(target)) return target;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            target = Path.Combine(destinationFolder, $"{stem}_{index}{extension}");
            if (!File.Exists(target)) return target;
        }
    }

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

    private static string ModelSuffixedFileName(string path, string cropFolder)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        return $"{fileName}_{SafeName(cropFolder)}{extension}";
    }

    private static string ModelSuffix(IEnumerable<string> cropFolders)
    {
        var models = cropFolders
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(SafeName)
            .ToArray();
        return models.Length switch
        {
            0 => "DLNG",
            1 => models[0],
            _ => "MULTI"
        };
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value) builder.Append(invalid.Contains(character) ? '_' : character);
        return builder.ToString();
    }
}


