using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class IrsRawImageLocator : IIrsRawImageLocator
{
    private readonly ISharePathResolver _shares;
    private readonly IDailyCsvLocator _csvs;
    private readonly Dictionary<string, Task<CsvImageIndex>> _csvIndexCache = new(StringComparer.OrdinalIgnoreCase);

    public IrsRawImageLocator(ISharePathResolver shares, IDailyCsvLocator csvs)
    {
        _shares = shares;
        _csvs = csvs;
    }

    public async Task<IrsImageLookupResult> FindAsync(
        WeldingMachine machine,
        IrsReviewCandidate candidate,
        CancellationToken cancellationToken)
    {
        var csvPaths = await FindFromProductionCsvAsync(machine, candidate, cancellationToken);
        if (csvPaths.Count > 0)
        {
            return new IrsImageLookupResult(
                csvPaths,
                $"Found {csvPaths.Count} raw image(s) from production CSV image-path columns.");
        }

        return await Task.Run(() =>
        {
            var model = ModelFor(machine);
            var hourRelative = Path.Combine(
                "Files",
                "Image",
                model,
                candidate.ProducedAt.ToString("yyyy"),
                candidate.ProducedAt.ToString("MM"),
                candidate.ProducedAt.ToString("dd"),
                candidate.ProducedAt.ToString("HH"));
            var sideIndex = SideIndex(candidate.CameraLocation);
            foreach (var drive in machine.ImageDrives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hourRoot = Path.Combine(_shares.GetRoot(machine, drive), hourRelative);
                try
                {
                    if (!Directory.Exists(hourRoot)) continue;
                    var rawMatches = FindRawImages(hourRoot, candidate.CellId, sideIndex, cancellationToken);
                    if (rawMatches.Count > 0)
                    {
                        return new IrsImageLookupResult(
                            rawMatches,
                            $"Found {rawMatches.Count} raw image(s) in {drive}:\\{hourRelative}");
                    }

                    var fallback = FindExactIrsFile(hourRoot, candidate, cancellationToken);
                    if (fallback.Count > 0)
                    {
                        return new IrsImageLookupResult(
                            fallback,
                            $"Found IRS filename fallback in {drive}:\\{hourRelative}");
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return new IrsImageLookupResult(
                [],
                $"No raw image match under {hourRelative}.");
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> FindFromProductionCsvAsync(
        WeldingMachine machine,
        IrsReviewCandidate candidate,
        CancellationToken cancellationToken)
    {
        var date = DateOnly.FromDateTime(candidate.ProducedAt);
        var key = $"{machine.Id}|{date:yyyyMMdd}";
        Task<CsvImageIndex> indexTask;
        lock (_csvIndexCache)
        {
            if (!_csvIndexCache.TryGetValue(key, out indexTask!))
            {
                indexTask = BuildCsvIndexAsync(machine, date, cancellationToken);
                _csvIndexCache[key] = indexTask;
            }
        }

        var index = await indexTask;
        return index.Find(candidate.CellId, SideName(candidate.CameraLocation));
    }

    private async Task<CsvImageIndex> BuildCsvIndexAsync(
        WeldingMachine machine,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var csvFiles = await _csvs.FindAsync(machine, date, cancellationToken);
        if (csvFiles.Count == 0) return CsvImageIndex.Empty;

        var rows = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var csvFile in csvFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ReadCsvImagePathsAsync(machine, csvFile, rows, cancellationToken);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return new CsvImageIndex(rows);
    }

    private async Task ReadCsvImagePathsAsync(
        WeldingMachine machine,
        string csvFile,
        Dictionary<string, Dictionary<string, List<string>>> rows,
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
        if (headerLine is null) return;
        var headers = CsvSupport.UniqueHeaders(CsvSupport.ParseLine(headerLine));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = CsvSupport.ParseLine(line);
            if (values.Count < headers.Count) continue;
            var row = new CsvRow(headers, values);
            var cellId = row.Get("CELL-ID");
            if (string.IsNullOrWhiteSpace(cellId)) continue;
            AddSide(machine, row, rows, cellId, "UPPER");
            AddSide(machine, row, rows, cellId, "LOWER");
        }
    }

    private void AddSide(
        WeldingMachine machine,
        CsvRow row,
        Dictionary<string, Dictionary<string, List<string>>> rows,
        string cellId,
        string side)
    {
        for (var index = 1; index <= 3; index++)
        {
            var productionPath = row.Get($"{side}_IMAGE-PATH-{index}");
            if (string.IsNullOrWhiteSpace(productionPath)) continue;
            if (!rows.TryGetValue(cellId, out var bySide))
            {
                bySide = new(StringComparer.OrdinalIgnoreCase);
                rows[cellId] = bySide;
            }

            if (!bySide.TryGetValue(side, out var paths))
            {
                paths = [];
                bySide[side] = paths;
            }

            var networkPath = ProductionPathMapper.ToUnc(machine, productionPath, _shares);
            if (!paths.Contains(networkPath, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(networkPath);
            }
        }
    }

    private static IReadOnlyList<string> FindRawImages(
        string hourRoot,
        string cellId,
        int sideIndex,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cellId)) return [];
        var results = new List<string>();
        foreach (var file in Directory.EnumerateFiles(hourRoot, $"*{cellId}*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(file);
            if (IsOverlay(name)) continue;
            if (name.Contains($"_{sideIndex}_0", StringComparison.OrdinalIgnoreCase)
                || name.Contains($"_{sideIndex}_1", StringComparison.OrdinalIgnoreCase)
                || name.Contains($"_{sideIndex}_2", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(file);
            }
        }

        return results
            .OrderBy(path => RawImageOrder(path, sideIndex))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
    }

    private static IReadOnlyList<string> FindExactIrsFile(
        string hourRoot,
        IrsReviewCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate.RawImageFileName)) return [];
        var results = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     hourRoot,
                     candidate.RawImageFileName,
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(file);
        }

        return results
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(1)
            .ToArray();
    }

    private static int RawImageOrder(string path, int sideIndex)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        for (var index = 0; index <= 2; index++)
        {
            if (name.Contains($"_{sideIndex}_{index}", StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 99;
    }

    private static bool IsOverlay(string fileName) =>
        fileName.Contains("overlay", StringComparison.OrdinalIgnoreCase);

    private static int SideIndex(string cameraLocation) =>
        cameraLocation.Trim().Equals("BTM", StringComparison.OrdinalIgnoreCase)
            || cameraLocation.Trim().Equals("BOTTOM", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;

    private static string SideName(string cameraLocation) =>
        SideIndex(cameraLocation) == 1 ? "LOWER" : "UPPER";

    private static string ModelFor(WeldingMachine machine) =>
        machine.Line.Equals("2-2", StringComparison.OrdinalIgnoreCase)
            ? "E69B"
            : "E81C";

    private sealed class CsvImageIndex(
        IReadOnlyDictionary<string, Dictionary<string, List<string>>> rows)
    {
        public static CsvImageIndex Empty { get; } = new(
            new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase));

        public IReadOnlyList<string> Find(string cellId, string side)
        {
            if (!rows.TryGetValue(cellId, out var bySide)) return [];
            if (!bySide.TryGetValue(side, out var paths)) return [];
            return paths
                .OrderBy(path => RawImageOrder(path, side.Equals("LOWER", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToArray();
        }
    }

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
