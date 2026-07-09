using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class DlngCropLocator : IDlngCropLocator
{
    private readonly ISharePathResolver _shares;
    private readonly VisionMasterSettings _settings;
    private readonly Dictionary<string, Lazy<Task<CropFolderIndex>>> _indexCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _indexGate = new();

    public DlngCropLocator(ISharePathResolver shares, VisionMasterSettings? settings = null)
    {
        _shares = shares;
        _settings = settings ?? VisionMasterSettings.CreateDefault();
    }

    public Task<IReadOnlyList<DlngReviewItem>> ExpandAsync(
        WeldingMachine machine,
        DlngReviewItem candidate,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<DlngReviewItem>>(() =>
        {
            var mapping = DlngRules.FindMapping(candidate.JudgeDefect, _settings.DlngRules);
            if (mapping is null) return [];

            var results = new List<DlngReviewItem>();
            foreach (var folder in mapping.CropFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pairs = mapping.ModelKind == DlngModelKind.Classification
                    ? FindClassificationPairs(machine, candidate, mapping, folder, progress, cancellationToken)
                    : FindSegmentationPairs(machine, candidate, mapping, folder, progress, cancellationToken);
                if (pairs.Count == 0)
                {
                    results.Add(FallbackItem(candidate, folder));
                    continue;
                }

                for (var index = 0; index < pairs.Count; index++)
                {
                    var pair = pairs[index];
                    results.Add(candidate with
                    {
                        Key = $"{candidate.Key}|{folder}|{index}",
                        CropFolder = folder,
                        SourceClass = pair.SourceClass,
                        ModelKind = mapping.ModelKind,
                        Images = pair.Images
                    });
                }
            }

            return results;
        }, cancellationToken);
    }

    private IReadOnlyList<CropPair> FindClassificationPairs(
        WeldingMachine machine,
        DlngReviewItem candidate,
        DlngDefectMappingSetting mapping,
        string folder,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var matches = FindCropFiles(machine, candidate, folder, progress, cancellationToken)
            .Where(path => MatchesSide(path, candidate.Side))
            .Where(path => MatchesToken(path, mapping, folder))
            .Where(path => FileName(path).Contains("SourceMap", StringComparison.OrdinalIgnoreCase)
                || FileName(path).Contains("ActiveMap", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var keys = matches.Select(CropPairKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var pairs = new List<CropPair>();
        foreach (var key in keys)
        {
            var source = matches.FirstOrDefault(path =>
                CropPairKey(path).Equals(key, StringComparison.OrdinalIgnoreCase)
                && FileName(path).Contains("SourceMap", StringComparison.OrdinalIgnoreCase));
            var active = matches.FirstOrDefault(path =>
                CropPairKey(path).Equals(key, StringComparison.OrdinalIgnoreCase)
                && FileName(path).Contains("ActiveMap", StringComparison.OrdinalIgnoreCase));
            var images = new List<DlngImage>();
            if (source is not null) images.Add(new("SourceMap", source, false));
            if (active is not null) images.Add(new("ActiveMap", active, true));
            if (images.Count == 0) continue;
            var sourceClass = Path.GetFileName(Path.GetDirectoryName(source ?? active!) ?? folder);
            pairs.Add(new(sourceClass, images));
        }

        return pairs;
    }

    private IReadOnlyList<CropPair> FindSegmentationPairs(
        WeldingMachine machine,
        DlngReviewItem candidate,
        DlngDefectMappingSetting mapping,
        string folder,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var matches = FindCropFiles(machine, candidate, folder, progress, cancellationToken)
            .Where(path => MatchesSide(path, candidate.Side))
            .Where(path => MatchesToken(path, mapping, folder))
            .ToArray();
        var sources = matches
            .Where(path => FileName(path).Contains("SourceImg", StringComparison.OrdinalIgnoreCase)
                && !IsMask(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var masks = matches
            .Where(IsMask)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var pairs = new List<CropPair>();
        foreach (var source in sources)
        {
            var mask = FindMaskForSource(source, masks);
            var images = new List<DlngImage> { new("SourceImg", source, false) };
            if (mask is not null) images.Add(new("Mask", mask, true));
            pairs.Add(new("Segmentation", images));
        }

        return pairs;
    }

    private IReadOnlyList<string> FindCropFiles(
        WeldingMachine machine,
        DlngReviewItem candidate,
        string folder,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var matches = new List<string>();
        foreach (var index in CropFolderIndexes(machine, candidate, folder, progress, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            matches.AddRange(index.Find(candidate.CellId));
        }

        return matches
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IEnumerable<CropFolderIndex> CropFolderIndexes(
        WeldingMachine machine,
        DlngReviewItem candidate,
        string folder,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var root in MavinRoots(machine, candidate, folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return GetOrBuildIndex(
                    machine,
                    candidate.InspectedAt,
                    folder,
                    root,
                    progress,
                    cancellationToken)
                .GetAwaiter()
                .GetResult();
        }
    }

    private Task<CropFolderIndex> GetOrBuildIndex(
        WeldingMachine machine,
        DateTime inspectedAt,
        string folder,
        string root,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var key = string.Join(
            "|",
            machine.Id,
            inspectedAt.ToString("yyyyMMdd"),
            folder,
            root);
        Lazy<Task<CropFolderIndex>> lazy;
        lock (_indexGate)
        {
            if (!_indexCache.TryGetValue(key, out lazy!))
            {
                var displayRoot = root;
                lazy = new(() => Task.Run(
                    () => BuildIndex(displayRoot, progress, cancellationToken),
                    cancellationToken));
                _indexCache[key] = lazy;
            }
        }

        return lazy.Value;
    }

    private static CropFolderIndex BuildIndex(
        string root,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var files = new List<string>();
        try
        {
            if (!Directory.Exists(root)) return CropFolderIndex.Empty;
            progress?.Report($"Indexing DLNG crop folder: {root}");
            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsUsefulCropFile(file)) continue;
                files.Add(file);
            }
            progress?.Report($"Indexed DLNG crop folder: {root} ({files.Count:N0} useful file(s)).");
        }
        catch (IOException)
        {
            return CropFolderIndex.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return CropFolderIndex.Empty;
        }

        return new(files);
    }

    private IEnumerable<string> MavinRoots(WeldingMachine machine, DlngReviewItem candidate, string folder)
    {
        var relative = Path.Combine(
            _settings.ProductionPaths.ImageSegments
                .Concat([machine.Model, candidate.InspectedAt.ToString("yyyy"), candidate.InspectedAt.ToString("MM"), candidate.InspectedAt.ToString("dd"), _settings.ProductionPaths.MavinFolderName, folder])
                .ToArray());
        foreach (var drive in CropSearchDrives(machine, candidate))
        {
            yield return Path.Combine(_shares.GetRoot(machine, drive), relative);
            if (folder.Equals("Gap_DL", StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.Combine(_shares.GetRoot(machine, drive), relative.Replace("Gap_DL", "GAP_DL"));
            }
        }
    }

    private static IReadOnlyList<char> CropSearchDrives(
        WeldingMachine machine,
        DlngReviewItem candidate)
    {
        var rawDrives = candidate.Images
            .Select(image => ProductionPathMapper.TryGetImageDrive(image.Path))
            .Where(drive => drive is not null)
            .Select(drive => drive!.Value)
            .Distinct()
            .ToArray();
        return rawDrives.Length > 0 ? rawDrives : machine.ImageDrives;
    }

    private static bool MatchesSide(string path, string side) =>
        FileName(path).Contains(side, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesToken(string path, DlngDefectMappingSetting mapping, string folder)
    {
        var token = mapping.Token;
        if (string.IsNullOrWhiteSpace(token)) return true;
        var name = FileName(path);
        if (folder.Equals("HORNMARK", StringComparison.OrdinalIgnoreCase))
        {
            return SideTokenAliases(token).Any(alias =>
                name.Contains($"HORN MARK {alias}", StringComparison.OrdinalIgnoreCase)
                || name.Contains($"HORNMARK {alias}", StringComparison.OrdinalIgnoreCase)
                || name.Contains($"HORNMARK_{alias}", StringComparison.OrdinalIgnoreCase)
                || name.Contains($"HORN {alias}", StringComparison.OrdinalIgnoreCase)
                || name.Contains($"HORN_{alias}", StringComparison.OrdinalIgnoreCase));
        }
        if (folder.Equals("LEADEDGE", StringComparison.OrdinalIgnoreCase))
        {
            return SideTokenAliases(token).Any(alias =>
                name.Contains($"LEAD EDGE {alias}", StringComparison.OrdinalIgnoreCase)
                || name.Contains($"LEADEDGE {alias}", StringComparison.OrdinalIgnoreCase)
                || name.Contains($"LEADEDGE_{alias}", StringComparison.OrdinalIgnoreCase));
        }
        if (folder.Equals("SEPA", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("SEPA_SHOULDER", StringComparison.OrdinalIgnoreCase))
        {
            return name.Contains(token, StringComparison.OrdinalIgnoreCase)
                || name.Replace('_', ' ').Contains(token, StringComparison.OrdinalIgnoreCase);
        }
        if (folder.Equals("Crop_micro_tabside", StringComparison.OrdinalIgnoreCase)
            && token is "L" or "R")
        {
            return MatchesLooseSideToken(name, token)
                || name.Contains($"TABSIDE_{token}", StringComparison.OrdinalIgnoreCase)
                || name.Contains($"Tabside_{token}", StringComparison.OrdinalIgnoreCase);
        }

        return name.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> SideTokenAliases(string token) =>
        token.ToUpperInvariant() switch
        {
            "L" => ["L", "LEFT"],
            "R" => ["R", "RIGHT"],
            _ => [token]
        };

    private static bool MatchesLooseSideToken(string fileName, string side)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var tokens = name.Split(['_', ' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Any(token => token.Equals(side, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindMaskForSource(string source, IReadOnlyList<string> masks)
    {
        var sourceName = Path.GetFileNameWithoutExtension(source);
        return masks.FirstOrDefault(path =>
            Path.GetFileNameWithoutExtension(path).Equals(sourceName + "_mask", StringComparison.OrdinalIgnoreCase))
            ?? masks.FirstOrDefault(path =>
                Path.GetFileNameWithoutExtension(path).Equals(sourceName, StringComparison.OrdinalIgnoreCase))
            ?? masks.FirstOrDefault(path =>
                Path.GetFileNameWithoutExtension(path).StartsWith(sourceName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMask(string path)
    {
        var name = FileName(path);
        return name.Contains("mask", StringComparison.OrdinalIgnoreCase)
            || (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
                && name.Contains("SourceImg", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUsefulCropFile(string path)
    {
        var name = FileName(path);
        return name.Contains("SourceMap", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ActiveMap", StringComparison.OrdinalIgnoreCase)
            || name.Contains("SourceImg", StringComparison.OrdinalIgnoreCase)
            || name.Contains("mask", StringComparison.OrdinalIgnoreCase);
    }

    private static string CropPairKey(string path)
    {
        var name = FileName(path);
        foreach (var marker in new[] { "_SourceMap", "_ActiveMap" })
        {
            var index = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index > 0) return name[..index];
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    private static string FileName(string path) => Path.GetFileName(path);

    private static DlngReviewItem FallbackItem(DlngReviewItem candidate, string folder) =>
        candidate with
        {
            Key = $"{candidate.Key}|{folder}|FALLBACK",
            CropFolder = folder,
            SourceClass = "NEED_TO_SIMULATE",
            ModelKind = DlngModelKind.FallbackRaw
        };

    private sealed record CropPair(string SourceClass, IReadOnlyList<DlngImage> Images);

    private sealed class CropFolderIndex
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _filesByCell;

        public CropFolderIndex(IReadOnlyList<string> files)
        {
            _filesByCell = files
                .SelectMany(path => CellTokens(path).Select(cellId => (cellId, path)))
                .GroupBy(x => x.cellId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group
                        .Select(x => x.path)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase);
        }

        public static CropFolderIndex Empty { get; } = new([]);

        public IReadOnlyList<string> Find(string cellId)
        {
            if (string.IsNullOrWhiteSpace(cellId)) return [];
            return _filesByCell.TryGetValue(cellId, out var paths) ? paths : [];
        }

        private static IEnumerable<string> CellTokens(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var first = name.Split('_', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first)) yield return first;
        }
    }
}
