using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class IrsDatasetService : IIrsDatasetService
{
    private static readonly HashSet<string> ExcludedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "ORIGINAL", "RULEBASE", "UNDETECTABLE"
    };

    private readonly AppStorage _storage;
    private readonly VisionMasterSettings _settings;

    public IrsDatasetService(AppStorage storage, VisionMasterSettings? settings = null)
    {
        _storage = storage;
        _settings = settings ?? VisionMasterSettings.CreateDefault();
    }

    public Task<IReadOnlyList<IrsDatasetItem>> BuildQueueAsync(
        IReadOnlyList<IrsReviewCandidate> candidates,
        IReadOnlyList<IrsReviewRecord> reviewRecords,
        CancellationToken cancellationToken)
    {
        var candidateKeys = candidates.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidatesByKey = candidates.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        var items = new List<IrsDatasetItem>();
        foreach (var record in reviewRecords.Where(x => candidateKeys.Contains(x.Key)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidatesByKey[record.Key];
            foreach (var savedPath in record.SavedPaths ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(savedPath)) continue;
                if (!Directory.Exists(savedPath)) continue;
                var normalizedPath = savedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var folderName = Path.GetFileName(normalizedPath);
                var parent = Directory.GetParent(normalizedPath);
                var parentName = parent?.Name ?? string.Empty;
                var grandParentName = parent?.Parent?.Name ?? string.Empty;
                if (ExcludedFolders.Contains(parentName)) continue;

                if (grandParentName.Equals("NEED_TO_SIMULATE", StringComparison.OrdinalIgnoreCase))
                {
                    var missingCropFolder = parentName;
                    var images = Directory.EnumerateFiles(savedPath, "*.*", SearchOption.AllDirectories)
                        .Where(IsImage)
                        .Where(path => IsProductionRawImage(path, candidate.CameraLocation))
                        .OrderBy(path => ProductionImageOrder(path))
                        .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .ToArray();
                    items.Add(new(
                        $"{record.Key}|SIM|{missingCropFolder}|{folderName}",
                        record.Key,
                        record.LinePolarity,
                        record.ProducedAt,
                        record.CellId,
                        record.CameraLocation,
                        candidate.SecondReason,
                        missingCropFolder,
                        "NEED_TO_SIMULATE",
                        images,
                        ClassesFor(missingCropFolder, record.LinePolarity),
                        true));
                    continue;
                }
            }

            var fileGroups = (record.SavedPaths ?? [])
                .Where(File.Exists)
                .GroupBy(path => Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            foreach (var group in fileGroups)
            {
                var folder = group.Key;
                if (ExcludedFolders.Contains(folder)) continue;
                var pairs = BuildImagePairs(group.ToArray());
                foreach (var pair in pairs)
                {
                    var originalClass = OriginalClassFromFiles(pair, folder, record.LinePolarity);
                    items.Add(new(
                        $"{record.Key}|{folder}|{items.Count}",
                        record.Key,
                        record.LinePolarity,
                        record.ProducedAt,
                        record.CellId,
                        record.CameraLocation,
                        candidate.SecondReason,
                        folder,
                        originalClass,
                        pair,
                        ClassesFor(folder, record.LinePolarity),
                        false));
                }
            }
        }

        return Task.FromResult<IReadOnlyList<IrsDatasetItem>>(items.OrderBy(x => x.ProducedAt).ThenBy(x => x.CellId).ToArray());
    }

    public async Task<IReadOnlyDictionary<string, IrsDatasetDecision>> LoadDecisionsAsync(CancellationToken cancellationToken)
    {
        var records = await LoadDecisionListAsync(cancellationToken);
        return records.ToDictionary(x => x.ItemKey, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SaveDecisionAsync(
        IrsDatasetItem item,
        IReadOnlyList<string> finalClasses,
        bool noNeedToRetrain,
        CancellationToken cancellationToken)
    {
        var records = await LoadDecisionListAsync(cancellationToken);
        records.RemoveAll(x => x.ItemKey.Equals(item.Key, StringComparison.OrdinalIgnoreCase));
        records.Add(new(
            item.Key,
            item.SourceReviewKey,
            item.LinePolarity,
            item.ProducedAt,
            item.CellId,
            item.SourceFolder,
            item.OriginalClass,
            finalClasses,
            noNeedToRetrain,
            DateTimeOffset.Now));
        await SaveDecisionListAsync(records.OrderBy(x => x.ProducedAt).ToArray(), cancellationToken);
    }

    public async Task<IrsSummaryResult> WriteSummaryAsync(
        IReadOnlyList<IrsReviewCandidate> candidates,
        IReadOnlyList<IrsReviewRecord> reviewRecords,
        IReadOnlyList<IrsDatasetItem> datasetItems,
        CancellationToken cancellationToken)
    {
        var decisions = await LoadDecisionsAsync(cancellationToken);
        var relevant = datasetItems
            .Where(item => decisions.ContainsKey(item.Key))
            .Select(item => (Item: item, Decision: decisions[item.Key]))
            .Where(x => !x.Decision.NoNeedToRetrain)
            .ToArray();
        if (candidates.Count == 0) throw new InvalidOperationException("No loaded IRS rows are available for summary.");

        var first = DateOnly.FromDateTime(candidates.Min(x => x.ProducedAt));
        var last = DateOnly.FromDateTime(candidates.Max(x => x.ProducedAt));
        var root = Path.Combine(_storage.Root, "IRS_Summary");
        Directory.CreateDirectory(root);
        var folder = Path.Combine(root, $"IRS_Summary_{first:yyyyMMdd}_{last:yyyyMMdd}");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        var datasetRoot = Path.Combine(folder, "Dataset");
        Directory.CreateDirectory(datasetRoot);

        foreach (var row in relevant)
        {
            if (row.Item.IsNeedToSimulate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyNeedToSimulateDataset(row.Item, datasetRoot, cancellationToken);
                continue;
            }

            foreach (var finalClass in row.Decision.FinalClasses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destinationFolder = Path.Combine(datasetRoot, row.Item.SourceFolder, SafeName(finalClass));
                Directory.CreateDirectory(destinationFolder);
                foreach (var image in row.Item.ImagePaths.Where(File.Exists))
                {
                    var target = Path.Combine(destinationFolder, Path.GetFileName(image));
                    File.Copy(image, target, true);
                }
            }
        }

        var summaryPath = Path.Combine(folder, $"IRS_Summary_{first:yyyyMMdd}_{last:yyyyMMdd}.xlsx");
        WriteSummaryWorkbook(summaryPath, relevant);
        var details = WriteDetailsWorkbooks(folder, candidates, reviewRecords, relevant);
        return new(folder, summaryPath, details);
    }

    private static void CopyNeedToSimulateDataset(
        IrsDatasetItem item,
        string datasetRoot,
        CancellationToken cancellationToken)
    {
        var sourceFolder = item.ImagePaths
            .Where(File.Exists)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder)) return;

        var destinationFolder = Path.Combine(
            datasetRoot,
            "NEED_TO_SIMULATE",
            item.SourceFolder,
            Path.GetFileName(sourceFolder));
        CopyDirectoryContents(sourceFolder, destinationFolder, cancellationToken);
    }

    private static void CopyDirectoryContents(
        string sourceFolder,
        string destinationFolder,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationFolder);
        foreach (var file in Directory.EnumerateFiles(sourceFolder, "*.*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceFolder, file);
            var target = Path.Combine(destinationFolder, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
    private IReadOnlyList<string> WriteDetailsWorkbooks(
        string folder,
        IReadOnlyList<IrsReviewCandidate> candidates,
        IReadOnlyList<IrsReviewRecord> reviewRecords,
        IReadOnlyList<(IrsDatasetItem Item, IrsDatasetDecision Decision)> relevant)
    {
        var outputs = new List<string>();
        var retrainKeys = relevant.Select(x => x.Item.SourceReviewKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rulebaseKeys = reviewRecords
            .Where(x => x.Selections.Contains("RULEBASE", StringComparer.OrdinalIgnoreCase))
            .Select(x => x.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates.GroupBy(x => x.LinePolarity, StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(folder, $"IRS_Details_{SafeName(group.Key)}.xlsx");
            var rows = group.Select(x => new[]
            {
                x.Eqpt, x.VisionType, x.LinePolarity, x.ProducedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                x.LotId, x.CellId, x.CameraLocation, x.RawImageFileName, x.SecondResult, x.SecondReason,
                rulebaseKeys.Contains(x.Key) ? "RULEBASE" : retrainKeys.Contains(x.Key) ? "RETRAIN" : ""
            }).ToArray();
            WriteSimpleWorkbook(path, "IRS_Details", ["Eqpt", "Vision Type", "Line", "Prod. date", "Lot ID", "Cell ID", "Camera Location", "Image", "2nd Result", "2nd Reason", "Review"], rows);
            outputs.Add(path);
        }

        return outputs;
    }

    private void WriteSummaryWorkbook(string path, IReadOnlyList<(IrsDatasetItem Item, IrsDatasetDecision Decision)> rows)
    {
        var data = new List<string[]> { new[] { "Folder", "Original Class", "Final Class", "Retrain Count", "DL_OK Count" } };
        foreach (var group in rows.SelectMany(x => x.Decision.FinalClasses.Select(final => (x.Item, Final: final)))
                     .GroupBy(x => new { x.Item.SourceFolder, x.Item.OriginalClass, x.Final }))
        {
            var retrain = group.Count(x => !x.Item.OriginalClass.Equals(x.Final, StringComparison.OrdinalIgnoreCase));
            var dlOk = group.Count(x => x.Item.OriginalClass.Contains("OK", StringComparison.OrdinalIgnoreCase)
                && !x.Item.OriginalClass.Equals(x.Final, StringComparison.OrdinalIgnoreCase));
            data.Add(new[] { group.Key.SourceFolder, group.Key.OriginalClass, group.Key.Final, retrain.ToString(CultureInfo.InvariantCulture), dlOk.ToString(CultureInfo.InvariantCulture) });
        }

        foreach (var group in rows.SelectMany(x => x.Decision.FinalClasses.Select(final => (x.Item, Final: final)))
                     .Where(x => x.Item.OriginalClass.Contains("OK", StringComparison.OrdinalIgnoreCase)
                         && !x.Item.OriginalClass.Equals(x.Final, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(x => x.Item.SourceFolder))
        {
            data.Add(new[] { $"DL_OK_{group.Key}", "", "", "", group.Count().ToString(CultureInfo.InvariantCulture) });
        }

        WriteSimpleWorkbook(path, "IRS_Summary", data[0], data.Skip(1).ToArray());
    }

    private async Task<List<IrsDatasetDecision>> LoadDecisionListAsync(CancellationToken cancellationToken)
    {
        var path = DecisionPath();
        if (!File.Exists(path)) return [];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<List<IrsDatasetDecision>>(stream, cancellationToken: cancellationToken) ?? [];
    }

    private async Task SaveDecisionListAsync(IReadOnlyList<IrsDatasetDecision> records, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_storage.Root);
        await using var stream = new FileStream(DecisionPath(), FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, records, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
    }

    private string DecisionPath() => Path.Combine(_storage.Root, "irs-dataset-reviews.json");

    private IReadOnlyList<string> ClassesFor(string folder, string linePolarity)
    {
        var polarity = linePolarity.Contains("+", StringComparison.Ordinal) ? Polarity.Cathode : Polarity.Anode;
        var match = _settings.IrsRules.FinalClassGroups.FirstOrDefault(group =>
            group.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)
            && group.Polarity == polarity)
            ?? _settings.IrsRules.FinalClassGroups.FirstOrDefault(group =>
                group.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)
                && group.Polarity is null);
        return match?.Classes.ToArray() ?? ["No Need to Retrain"];
    }

    private string OriginalClassFromFiles(IReadOnlyList<string> files, string folder, string linePolarity)
    {
        var classes = ClassesFor(folder, linePolarity).Where(x => !x.Equals("No Need to Retrain", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            foreach (var klass in classes)
            {
                var number = klass.Split('_')[0];
                if (name.Contains($"CL{number}", StringComparison.OrdinalIgnoreCase)) return klass;
            }
        }

        return folder;
    }


    private static IReadOnlyList<IReadOnlyList<string>> BuildImagePairs(IReadOnlyList<string> files)
    {
        var ordered = files.OrderBy(path => SourceFirstOrder(path)).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var sourceMaps = ordered.Where(path => Path.GetFileName(path).Contains("SourceMap", StringComparison.OrdinalIgnoreCase)).ToArray();
        var activeMaps = ordered.Where(path => Path.GetFileName(path).Contains("ActiveMap", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sourceMaps.Length > 0 || activeMaps.Length > 0)
        {
            return BuildMapPairs(sourceMaps, activeMaps);
        }

        var sourceImgs = ordered.Where(path => Path.GetFileName(path).Contains("SourceImg", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(path).Contains("mask", StringComparison.OrdinalIgnoreCase)).ToArray();
        var masks = ordered.Where(path => Path.GetFileName(path).Contains("mask", StringComparison.OrdinalIgnoreCase)).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (sourceImgs.Length > 0 && masks.Length > 0)
        {
            var pairs = new List<IReadOnlyList<string>>();
            foreach (var source in sourceImgs)
            {
                var side = SideToken(source);
                var mask = masks.FirstOrDefault(path => SideToken(path).Equals(side, StringComparison.OrdinalIgnoreCase))
                    ?? masks.FirstOrDefault();
                if (mask is not null) pairs.Add([source, mask]);
            }
            return pairs;
        }

        return ordered.Chunk(2).Select(chunk => (IReadOnlyList<string>)chunk).ToArray();
    }

    private static bool IsProductionRawImage(string path, string cameraLocation)
    {
        var name = Path.GetFileName(path);
        if (name.Contains("overlay", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ActiveMap", StringComparison.OrdinalIgnoreCase)
            || name.Contains("SourceMap", StringComparison.OrdinalIgnoreCase)
            || name.Contains("SourceImg", StringComparison.OrdinalIgnoreCase)
            || name.Contains("mask", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var top = !cameraLocation.StartsWith("BTM", StringComparison.OrdinalIgnoreCase);
        return top
            ? name.Contains("UPPER", StringComparison.OrdinalIgnoreCase) || HasIndexedSideToken(name, "0")
            : name.Contains("LOWER", StringComparison.OrdinalIgnoreCase) || HasIndexedSideToken(name, "1");
    }

    private static bool HasIndexedSideToken(string fileName, string sideToken) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            fileName,
            $@"(^|_){sideToken}_[0-9]+(\.|_)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static int ProductionImageOrder(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var indexed = System.Text.RegularExpressions.Regex.Match(name, @"(?:^|_)(?:0|1)_([0-9]+)(?:$|_)");
        if (indexed.Success && int.TryParse(indexed.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var indexedValue))
        {
            return indexedValue;
        }

        var matches = System.Text.RegularExpressions.Regex.Matches(name, @"(?<!\d)\d+(?!\d)");
        if (matches.Count == 0) return int.MaxValue;
        return int.TryParse(matches[matches.Count - 1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : int.MaxValue;
    }
    private static IReadOnlyList<IReadOnlyList<string>> BuildMapPairs(
        IReadOnlyList<string> sourceMaps,
        IReadOnlyList<string> activeMaps)
    {
        var keys = sourceMaps.Concat(activeMaps)
            .Select(CropPairKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var pairs = new List<IReadOnlyList<string>>();
        foreach (var key in keys)
        {
            var sources = sourceMaps
                .Where(path => CropPairKey(path).Equals(key, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var actives = activeMaps
                .Where(path => CropPairKey(path).Equals(key, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var count = Math.Max(sources.Length, actives.Length);
            for (var index = 0; index < count; index++)
            {
                var pair = new List<string>(2);
                if (index < sources.Length) pair.Add(sources[index]);
                if (index < actives.Length) pair.Add(actives[index]);
                if (pair.Count > 0) pairs.Add(pair);
            }
        }

        return pairs;
    }

    private static string CropPairKey(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var token in new[]
                 {
                     "A_L", "A_R", "B_L", "B_R", "Micro_LL", "Micro_LM", "Micro_MM", "Micro_MR", "Micro_RR",
                     "Tabside_L", "Tabside_R", "SHOULDER_L", "SHOULDER_R"
                 })
        {
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase)) return token;
        }

        return SideToken(path);
    }
    private static string SideToken(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Contains("UPPER", StringComparison.OrdinalIgnoreCase)) return "UPPER";
        if (name.Contains("LOWER", StringComparison.OrdinalIgnoreCase)) return "LOWER";
        if (name.Contains("_L_", StringComparison.OrdinalIgnoreCase)) return "L";
        if (name.Contains("_R_", StringComparison.OrdinalIgnoreCase)) return "R";
        return string.Empty;
    }
    private static int SourceFirstOrder(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Contains("SourceMap", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("SourceImg", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("ActiveMap", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains("mask", StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    private static bool IsImage(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteSimpleWorkbook(string path, string sheetName, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
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
            </Relationships>
            """);
        WriteEntry(archive, "xl/workbook.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="{SecurityElement.Escape(sheetName)}" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        WriteEntry(archive, "xl/worksheets/sheet1.xml", WorksheetXml(headers, rows));
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

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value) builder.Append(invalid.Contains(character) ? '_' : character);
        return builder.ToString();
    }
}
