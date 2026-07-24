using System.Text.Json;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class IrsReviewCommitService : IIrsReviewCommitService
{
    private readonly AppStorage _storage;
    private readonly IDailyCsvLocator _csvs;
    private readonly ISharePathResolver _shares;
    private readonly VisionMasterSettings _settings;
    private readonly string _workflowFolder;
    private readonly string _reviewFile;

    public IrsReviewCommitService(
        AppStorage storage,
        IDailyCsvLocator csvs,
        ISharePathResolver shares,
        VisionMasterSettings? settings = null,
        string workflowFolder = "IRS_LEAK",
        string? reviewFile = null)
    {
        _storage = storage;
        _csvs = csvs;
        _shares = shares;
        _settings = settings ?? VisionMasterSettings.CreateDefault();
        _workflowFolder = workflowFolder;
        _reviewFile = reviewFile ?? Path.Combine(_storage.Root, "irs-reviews.json");
    }

    public async Task<IrsReviewCommitResult> CommitAsync(
        IrsReviewCommitRequest request,
        CancellationToken cancellationToken)
    {
        var machineRoot = _storage.MachineRoot(request.Machine);
        var destinationRoot = Path.Combine(machineRoot, _workflowFolder);
        Directory.CreateDirectory(destinationRoot);

        var records = await LoadRecordListAsync(cancellationToken);
        var previous = records.FirstOrDefault(x =>
            x.Key.Equals(request.Candidate.Key, StringComparison.OrdinalIgnoreCase));
        DeleteSavedPaths(previous?.SavedPaths);

        var crop = await CopyCropFilesAsync(request, destinationRoot, cancellationToken);
        var original = await CopyOriginalFolderAsync(
            request,
            destinationRoot,
            crop.MissingSimulationFolders,
            cancellationToken);
        var missing = original.Missing + crop.Missing;
        var savedPaths = original.SavedPaths.Concat(crop.SavedPaths).ToArray();

        await SaveRecordAsync(
            records,
            request,
            new IrsReviewCommitResult(
                original.Copied,
                crop.Copied,
                missing,
                destinationRoot,
                $"Saved originals: {original.Copied}, crops: {crop.Copied}, missing: {missing}."),
            savedPaths,
            cancellationToken);

        return new(
            original.Copied,
            crop.Copied,
            missing,
            destinationRoot,
            $"Saved originals: {original.Copied}, crops: {crop.Copied}, missing: {missing}.");
    }

    private async Task<CopyCounter> CopyOriginalFolderAsync(
        IrsReviewCommitRequest request,
        string destinationRoot,
        IReadOnlyList<string> missingSimulationFolders,
        CancellationToken cancellationToken)
    {
        var paths = await FindOriginalImagePathsAsync(request, cancellationToken);
        var first = paths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (first is null) return CopyCounter.Empty with { Missing = 1 };
        var sourceFolder = Path.GetDirectoryName(first);
        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
        {
            return CopyCounter.Empty with { Missing = 1 };
        }

        var folderName = Path.GetFileName(
            sourceFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var cropSelected = request.Selections.Any(x => x.Kind == IrsSelectionKind.Crop);
        var destinations = cropSelected
            ? new[] { Path.Combine(destinationRoot, "ORIGINAL", folderName) }
                .Concat(missingSimulationFolders
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(folder => Path.Combine(destinationRoot, "NEED_TO_SIMULATE", folder, folderName)))
                .ToArray()
            : request.Selections
                .Select(selection => Path.Combine(destinationRoot, selection.CategoryFolder, folderName))
                .ToArray();

        var total = CopyCounter.Empty;
        foreach (var destination in destinations)
        {
            total = total.Add(await CopyDirectoryAsync(sourceFolder, destination, cancellationToken));
        }

        return total;
    }

    private async Task<IReadOnlyList<string>> FindOriginalImagePathsAsync(
        IrsReviewCommitRequest request,
        CancellationToken cancellationToken)
    {
        var date = DateOnly.FromDateTime(request.Candidate.ProducedAt);
        var csvFiles = await _csvs.FindAsync(request.Machine, date, cancellationToken);
        foreach (var csv in csvFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var paths = await FindOriginalImagePathsInCsvAsync(request, csv, cancellationToken);
                if (paths.Count > 0) return paths;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return request.Candidate.RawImagePaths ?? [];
    }

    private async Task<IReadOnlyList<string>> FindOriginalImagePathsInCsvAsync(
        IrsReviewCommitRequest request,
        string csvFile,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            csvFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

        var headerLine = await reader.ReadLineAsync(cancellationToken);
        if (headerLine is null) return [];
        var headers = CsvSupport.UniqueHeaders(CsvSupport.ParseLine(headerLine));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = CsvSupport.ParseLine(line);
            if (values.Count < headers.Count) continue;
            var row = new CsvRow(headers, values);
            var cellId = row.Get("CELL-ID");
            if (!request.Candidate.CellId.Equals(cellId, StringComparison.OrdinalIgnoreCase)) continue;

            var paths = new List<string>(12);
            AddOriginalPaths(request.Machine, row, paths, "UPPER");
            AddOriginalPaths(request.Machine, row, paths, "LOWER");
            return paths;
        }

        return [];
    }

    private void AddOriginalPaths(
        WeldingMachine machine,
        CsvRow row,
        ICollection<string> paths,
        string side)
    {
        for (var index = 1; index <= 3; index++)
        {
            Add(row.Get($"{side}_IMAGE-PATH-{index}"));
            Add(row.Get($"{side}_OVERLAY-IMAGE-PATH-{index}"));
        }

        void Add(string? productionPath)
        {
            if (string.IsNullOrWhiteSpace(productionPath)) return;
            paths.Add(ProductionPathMapper.ToUnc(machine, productionPath, _shares));
        }
    }

    private async Task<CopyCounter> CopyCropFilesAsync(
        IrsReviewCommitRequest request,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var total = CopyCounter.Empty;
        foreach (var selection in request.Selections.Where(x => x.Kind == IrsSelectionKind.Crop))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = FindCropFiles(request, selection, cancellationToken);
            if (files.Count == 0)
            {
                total = total.Add(CopyCounter.Empty with
                {
                    Missing = 1,
                    MissingSimulationFolders = [selection.CategoryFolder]
                });
                continue;
            }

            foreach (var match in files)
            {
                var categoryRoot = Path.Combine(destinationRoot, match.DestinationFolder);
                Directory.CreateDirectory(categoryRoot);
                var destination = Path.Combine(categoryRoot, Path.GetFileName(match.Path));
                var copied = await CopyStableFileAsync(match.Path, destination, overwrite: true, cancellationToken);
                total = total.Add(copied
                    ? new(1, 0, [destination], [])
                    : CopyCounter.Empty with { Missing = 1 });
            }
        }

        return total;
    }

    private IReadOnlyList<CropMatch> FindCropFiles(
        IrsReviewCommitRequest request,
        IrsReviewSelection selection,
        CancellationToken cancellationToken)
    {
        if (selection.MavinFolder is null) return [];
        var roots = MavinRoots(request.Machine, request.Candidate, selection.MavinFolder);
        var matches = new List<CropMatch>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(
                             root,
                             $"*{request.Candidate.CellId}*",
                             SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(file);
                    if (!MatchesSide(name, request.Candidate.CameraLocation)) continue;
                    if (!MatchesCropToken(name, selection)) continue;
                    if (!IsUsefulCropFile(name)) continue;
                    matches.Add(new(file, MavinDestinationFolder(root, file, selection)));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return matches
            .DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(match => match.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string MavinDestinationFolder(
        string root,
        string file,
        IrsReviewSelection selection)
    => selection.CategoryFolder;

    private IEnumerable<string> MavinRoots(
        WeldingMachine machine,
        IrsReviewCandidate candidate,
        string folder)
    {
        var relative = Path.Combine(
            _settings.ProductionPaths.ImageSegments
                .Concat([machine.Model, candidate.ProducedAt.ToString("yyyy"), candidate.ProducedAt.ToString("MM"), candidate.ProducedAt.ToString("dd"), _settings.ProductionPaths.MavinFolderName, folder])
                .ToArray());
        foreach (var drive in CropSearchDrives(machine, candidate))
        {
            yield return Path.Combine(_shares.GetRoot(machine, drive), relative);
            if (folder.Equals("GAP_DL", StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.Combine(_shares.GetRoot(machine, drive), relative.Replace("GAP_DL", "Gap_DL"));
            }
        }
    }

    private static IReadOnlyList<char> CropSearchDrives(
        WeldingMachine machine,
        IrsReviewCandidate candidate)
    {
        var rawDrives = (candidate.RawImagePaths ?? [])
            .Select(ProductionPathMapper.TryGetImageDrive)
            .Where(drive => drive is not null)
            .Select(drive => drive!.Value)
            .Distinct()
            .ToArray();
        return rawDrives.Length > 0 ? rawDrives : machine.ImageDrives;
    }

    private static bool MatchesSide(string fileName, string cameraLocation)
    {
        var side = cameraLocation.Trim().Equals("BTM", StringComparison.OrdinalIgnoreCase)
            || cameraLocation.Trim().Equals("BOTTOM", StringComparison.OrdinalIgnoreCase)
            ? "LOWER"
            : "UPPER";
        return fileName.Contains(side, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesCropToken(string fileName, IrsReviewSelection selection)
    {
        if (selection.Token is null) return true;
        if (selection.Id is "GAP" or "SEPA") return true;
        if (selection.Id is "TABSIDE_L") return MatchesLooseSideToken(fileName, "L");
        if (selection.Id is "TABSIDE_R") return MatchesLooseSideToken(fileName, "R");
        return fileName.Contains(selection.Token, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesLooseSideToken(string fileName, string side)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var tokens = name.Split(['_', ' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Any(token => token.Equals(side, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUsefulCropFile(string fileName) =>
        fileName.Contains("SourceMap", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("ActiveMap", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("SourceImg", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("mask", StringComparison.OrdinalIgnoreCase);

    private static async Task<CopyCounter> CopyDirectoryAsync(
        string sourceFolder,
        string destinationFolder,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceFolder)) return CopyCounter.Empty with { Missing = 1 };
        var temporary = destinationFolder + $".copying-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(temporary);
            var copied = 0;
            var missing = 0;
            var sourceFiles = Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var source in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(sourceFolder, source);
                var destination = Path.Combine(temporary, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (await CopyStableFileAsync(source, destination, overwrite: false, cancellationToken))
                {
                    copied++;
                }
                else
                {
                    missing++;
                }
            }

            if (Directory.Exists(destinationFolder)) Directory.Delete(destinationFolder, true);
            Directory.Move(temporary, destinationFolder);
            return new(copied, missing, [destinationFolder], []);
        }
        catch (IOException)
        {
            TryDelete(temporary);
            return CopyCounter.Empty with { Missing = 1 };
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temporary);
            return CopyCounter.Empty with { Missing = 1 };
        }
    }

    private static async Task<bool> CopyStableFileAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var before = new FileInfo(sourcePath);
        if (!before.Exists) return false;
        if (DateTime.UtcNow - before.LastWriteTimeUtc < TimeSpan.FromSeconds(2)) return false;
        var originalLength = before.Length;
        var originalWrite = before.LastWriteTimeUtc;

        await using (var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var destination = new FileStream(
            destinationPath,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[128 * 1024];
            var bytesSinceYield = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                bytesSinceYield += read;
                if (bytesSinceYield >= 2 * 1024 * 1024)
                {
                    bytesSinceYield = 0;
                    await Task.Delay(2, cancellationToken);
                }
            }

            await destination.FlushAsync(cancellationToken);
        }

        var after = new FileInfo(sourcePath);
        return after.Exists
            && after.Length == originalLength
            && after.LastWriteTimeUtc == originalWrite
            && new FileInfo(destinationPath).Length == originalLength;
    }

    private async Task SaveRecordAsync(
        List<IrsReviewRecord> records,
        IrsReviewCommitRequest request,
        IrsReviewCommitResult result,
        IReadOnlyList<string> savedPaths,
        CancellationToken cancellationToken)
    {
        records.RemoveAll(x => x.Key.Equals(request.Candidate.Key, StringComparison.OrdinalIgnoreCase));
        records.Add(new(
            request.Candidate.Key,
            request.Machine.Id,
            request.Machine.OutputFolderName,
            request.Candidate.ProducedAt,
            request.Candidate.CellId,
            request.Candidate.CameraLocation,
            request.Candidate.SecondResult,
            request.Candidate.SecondReason,
            request.Selections.Select(x => x.Id).ToArray(),
            result.OriginalFilesCopied,
            result.CropFilesCopied,
            result.MissingFiles,
            result.DestinationRoot,
            DateTimeOffset.Now,
            savedPaths));

        var path = _reviewFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var write = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(
            write,
            records.OrderBy(x => x.ProducedAt).ToArray(),
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken);
    }

    public async Task<IReadOnlyList<IrsReviewRecord>> LoadRecordsAsync(CancellationToken cancellationToken) =>
        await LoadRecordListAsync(cancellationToken);

    private async Task<List<IrsReviewRecord>> LoadRecordListAsync(CancellationToken cancellationToken)
    {
        var path = _reviewFile;
        if (!File.Exists(path)) return [];
        await using var read = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<List<IrsReviewRecord>>(
            read,
            cancellationToken: cancellationToken) ?? [];
    }

    private static void DeleteSavedPaths(IReadOnlyList<string>? paths)
    {
        if (paths is null) return;
        foreach (var path in paths.OrderByDescending(x => x.Length))
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
        }
    }

    private sealed record CopyCounter(
        int Copied,
        int Missing,
        IReadOnlyList<string> SavedPaths,
        IReadOnlyList<string> MissingSimulationFolders)
    {
        public static CopyCounter Empty { get; } = new(0, 0, [], []);

        public CopyCounter Add(CopyCounter other) =>
            new(
                Copied + other.Copied,
                Missing + other.Missing,
                SavedPaths.Concat(other.SavedPaths).ToArray(),
                MissingSimulationFolders.Concat(other.MissingSimulationFolders).ToArray());
    }

    private sealed record CropMatch(string Path, string DestinationFolder);

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
