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

    public void Reset() { lock (_indexGate) _indexCache.Clear(); }

    public Task<IReadOnlyList<DlngReviewItem>> ExpandAsync(
        WeldingMachine machine,
        DlngReviewItem candidate,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? cropFolders = null)
    {
        return Task.Run<IReadOnlyList<DlngReviewItem>>(() =>
        {
            candidate = candidate with { RawImages = candidate.RawImages ?? candidate.Images };
            if (candidate.Inspection?.Issue is { } issue)
            {
                progress?.Report($"{candidate.CellId}: {issue}");
                candidate = candidate with { Images = [], RawImages = [], SourceFolder = "", ResolutionMessage = issue };
            }
            var mappings = DlngRules.FindMappings(candidate.JudgeDefect, _settings.DlngRules);
            var results = new List<DlngReviewItem>();
            foreach (var mapping in mappings)
            foreach (var folder in mapping.CropFolders.Where(folder =>
                         cropFolders is null || cropFolders.Contains(folder)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? failure = candidate.ResolutionMessage;
                void Diagnose(string reason)
                {
                    failure ??= reason;
                    progress?.Report($"{machine.OutputFolderName} {candidate.CellId} {folder} {candidate.Side}: {reason}");
                }
                var pairs = mapping.ModelKind == DlngModelKind.Classification
                    ? FindClassificationPairs(machine, candidate, mapping, folder, progress, cancellationToken, Diagnose)
                    : FindSegmentationPairs(machine, candidate, mapping, folder, progress, cancellationToken, Diagnose);
                if (pairs.Count == 0)
                {
                    results.Add(FallbackItem(candidate, folder, failure ?? "No usable source crop for the selected defect/side."));
                    continue;
                }

                for (var index = 0; index < pairs.Count; index++)
                {
                    var pair = pairs[index];
                    results.Add(candidate with
                    {
                        Key = $"{candidate.Key}|{folder}|PAIR:{InspectionIdentity.Hash(InspectionIdentity.PairKey(pair.Images[0].Path))}",
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
        CancellationToken cancellationToken,
        Action<string> diagnose)
    {
        var matches = FindCropFiles(machine, candidate, folder, progress, cancellationToken, diagnose)
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
        CancellationToken cancellationToken,
        Action<string> diagnose)
    {
        var matches = FindCropFiles(machine, candidate, folder, progress, cancellationToken, diagnose)
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
        CancellationToken cancellationToken,
        Action<string> diagnose)
    {
        var imageTime = candidate.Inspection is not null ? candidate.Inspection.ImageAt : InspectionIdentity.ImageTime(
            (candidate.RawImages ?? candidate.Images).Select(x => x.Path).Append(candidate.SourceFolder));
        if (imageTime is null)
        {
            diagnose("Missing or conflicting image timestamp in CSV paths.");
            return [];
        }
        var matches = new List<string>();
        var cellFiles = new List<string>();
        var issues = new List<string>();
        foreach (var index in CropFolderIndexes(machine, candidate, folder, progress, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index.Issue is not null) issues.Add(index.Issue);
            var files = index.Find(candidate.CellId);
            cellFiles.AddRange(files);
            matches.AddRange(files.Where(path =>
                candidate.Inspection is { } inspection
                    ? InspectionIdentity.MatchesCrop(path, machine, inspection, InspectionIdentity.CropPathDate(path))
                    : InspectionIdentity.MatchesCrop(path, machine, candidate.CellId, imageTime.Value)));
        }

        if (matches.Count == 0)
        {
            if (candidate.Inspection is { } context && cellFiles.Any(path =>
                    InspectionIdentity.MatchesCrop(path, machine, context with { CropConflicts = [] }, InspectionIdentity.CropPathDate(path))))
                diagnose("Ambiguous crop time: another CSV inspection overlaps this crop.");
            else if (cellFiles.Count > 0)
            {
                var times = cellFiles.Select(path => System.Text.RegularExpressions.Regex.Match(
                    FileName(path), @"_(\d{6})_(?:UPPER|LOWER)_", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    .Where(match => match.Success).Select(match => match.Groups[1].Value)
                    .Distinct().OrderBy(value => value).Take(8);
                var expected = candidate.Inspection is { CropConflicts: not null } checkedContext
                    ? $"{checkedContext.CropWindow.Start:HH:mm:ss}–{checkedContext.CropWindow.End:HH:mm:ss}"
                    : $"{imageTime:HH:mm:ss}";
                diagnose($"No matching crop window: expected {machine.OutputFolderName} {expected}; cell crop times: {string.Join(", ", times)}.");
            }
            else if (issues.Count > 0) diagnose(string.Join(" | ", issues.Distinct()));
            else diagnose($"No crop files for this cell in {folder} on {imageTime:yyyy-MM-dd}.");
        }
        else
        {
            var sideFiles = matches.Where(path => MatchesSide(path, candidate.Side)).ToArray();
            if (sideFiles.Length == 0) diagnose($"Exact inspection crops exist, but none for {candidate.Side}.");
            else
            {
                var mapping = DlngRules.FindMapping(candidate.JudgeDefect, _settings.DlngRules);
                if (mapping is not null && !sideFiles.Any(path => MatchesToken(path, mapping, folder)))
                    diagnose($"Exact {candidate.Side} crops exist, but none match defect {candidate.JudgeDefect} in {folder}.");
            }
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
            progress?.Report($"Indexing DLNG crop folder: {root}");
            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsUsefulCropFile(file)) continue;
                files.Add(file);
            }
            progress?.Report($"Indexed DLNG crop folder: {root} ({files.Count:N0} useful file(s)).");
        }
        catch (DirectoryNotFoundException)
        {
            return new([], $"Crop folder missing or unavailable: {root}");
        }
        catch (IOException ex)
        {
            return new([], $"Crop folder read failed: {root} ({ex.Message})");
        }
        catch (UnauthorizedAccessException)
        {
            return new([], $"Crop folder access denied: {root}");
        }

        return new(files);
    }

    private IEnumerable<string> MavinRoots(WeldingMachine machine, DlngReviewItem candidate, string folder)
    {
        var imageTime = candidate.Inspection is not null ? candidate.Inspection.ImageAt : InspectionIdentity.ImageTime(
            (candidate.RawImages ?? candidate.Images).Select(x => x.Path).Append(candidate.SourceFolder));
        if (imageTime is null) yield break;
        var model = candidate.Inspection?.Model ?? candidate.Model;
        var dates = candidate.Inspection is { } context ? InspectionIdentity.CropDates(context) : new[] { imageTime.Value.Date };
        foreach (var date in dates)
        {
            var relative = Path.Combine(
                _settings.ProductionPaths.ImageSegments
                    .Concat([model, date.ToString("yyyy"), date.ToString("MM"), date.ToString("dd"), _settings.ProductionPaths.MavinFolderName, folder])
                    .ToArray());
            foreach (var drive in CropSearchDrives(machine, candidate))
                yield return Path.Combine(_shares.GetRoot(machine, drive), relative);
        }
    }

    private static IReadOnlyList<char> CropSearchDrives(
        WeldingMachine machine,
        DlngReviewItem candidate)
    {
        var rawDrives = (candidate.RawImages ?? candidate.Images)
            .Select(image => ProductionPathMapper.TryGetImageDrive(image.Path))
            .Where(drive => drive is not null)
            .Select(drive => drive!.Value)
            .Distinct()
            .ToArray();
        return rawDrives.Length > 0 ? rawDrives : machine.ImageDrives;
    }

    private static bool MatchesSide(string path, string side) =>
        FileName(path).Contains("_" + side + "_", StringComparison.OrdinalIgnoreCase);

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
            ;
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

    private static DlngReviewItem FallbackItem(DlngReviewItem candidate, string folder, string reason) =>
        candidate with
        {
            Key = $"{candidate.Key}|{folder}|FALLBACK",
            CropFolder = folder,
            SourceClass = "NEED_TO_SIMULATE",
            ModelKind = DlngModelKind.FallbackRaw,
            Images = candidate.RawImages ?? candidate.Images,
            ResolutionMessage = candidate.ResolutionMessage ?? reason
        };

    private sealed record CropPair(string SourceClass, IReadOnlyList<DlngImage> Images);

    private sealed class CropFolderIndex
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _filesByCell;
        private readonly IReadOnlyList<string> _files;

        public string? Issue { get; }

        public CropFolderIndex(IReadOnlyList<string> files, string? issue = null)
        {
            Issue = issue;
            _files = files;
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
            return _filesByCell.TryGetValue(cellId, out var paths) ? paths :
                _files.Where(path => FileName(path).StartsWith(cellId + "_", StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        private static IEnumerable<string> CellTokens(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var first = name.Split('_', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first)) yield return first;
        }
    }
}
