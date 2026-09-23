using System.Globalization;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

/// <summary>Resolves a single production inspection, never the first cell-ID match.</summary>
public sealed class ProductionInspectionResolver(IDailyCsvLocator csvs, ISharePathResolver shares)
{
    private readonly Dictionary<string, Task<IReadOnlyList<InspectionContext>>> _cache = new(StringComparer.OrdinalIgnoreCase);
    public void Reset() { lock (_cache) _cache.Clear(); }

    public async Task<IrsImageLookupResult> ResolveAsync(WeldingMachine machine, IrsReviewCandidate candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate.Inspection?.Issue is { } issue) return new([], issue);
        if (candidate.Inspection is { ImageAt: not null } known)
        {
            if (!known.MachineId.Equals(machine.Id, StringComparison.OrdinalIgnoreCase)
                || !known.CellId.Equals(candidate.CellId, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(candidate.LotId) && !known.LotId.Equals(candidate.LotId, StringComparison.OrdinalIgnoreCase))
                || InspectionIdentity.ImageTime(known.ImagePaths) != known.ImageAt)
                return new([], "Conflicting stored inspection context.");
            if (!string.IsNullOrWhiteSpace(known.SourceCsv))
            {
                try
                {
                    var contexts = await ReadAsync(machine, DateOnly.FromDateTime(known.JudgedAt), cancellationToken);
                    known = contexts.FirstOrDefault(x => x.Identity == known.Identity) ?? known with { CropConflicts = null };
                }
                catch (IOException) { known = known with { CropConflicts = null }; }
                catch (UnauthorizedAccessException) { known = known with { CropConflicts = null }; }
            }
            return Result(known, candidate.CameraLocation);
        }
        var date = DateOnly.FromDateTime(candidate.ProducedAt);
        var key = $"{machine.Id}|{date}";
        Task<IReadOnlyList<InspectionContext>> task;
        lock (_cache)
        {
            if (!_cache.TryGetValue(key, out task!))
                _cache[key] = task = ReadAsync(machine, date, cancellationToken);
        }
        IReadOnlyList<InspectionContext> rows;
        try { rows = await task; }
        catch (OperationCanceledException) { lock (_cache) _cache.Remove(key); throw; }
        catch (IOException ex) { lock (_cache) _cache.Remove(key); return new([], $"Missing: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { lock (_cache) _cache.Remove(key); return new([], $"Unavailable: {ex.Message}"); }
        var matches = rows.Where(x => x.CellId.Equals(candidate.CellId, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(candidate.LotId) || x.LotId.Equals(candidate.LotId, StringComparison.OrdinalIgnoreCase))).ToArray();
        var names = (candidate.RawImagePaths ?? []).Select(InspectionIdentity.FileName).ToList();
        if (!string.IsNullOrWhiteSpace(candidate.RawImageFileName)) names.Add(InspectionIdentity.FileName(candidate.RawImageFileName));
        if (names.Count > 0)
            matches = matches.Where(x => names.All(name => x.ImagePaths.Any(path =>
                InspectionIdentity.FileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)))).ToArray();
        else
            matches = matches.Where(x => x.JudgedAt == candidate.ProducedAt || x.ImageAt == candidate.ProducedAt).ToArray();
        matches = matches.DistinctBy(x => x.Identity + "|" + string.Join("|", x.ImagePaths), StringComparer.OrdinalIgnoreCase).ToArray();
        if (matches.Length != 1)
            return new([], matches.Length == 0 ? "Missing: no exact inspection match." : "Ambiguous: multiple inspections match.");
        if (matches[0].ImageAt is null)
            return new([], "Conflicting or missing image timestamps in the matching CSV row.");
        return Result(matches[0], candidate.CameraLocation);
    }

    private static IrsImageLookupResult Result(InspectionContext context, string camera)
    {
        var side = ProductionImageConventions.SideIndex(camera);
        var paths = context.ImagePaths.Where(p => !ProductionImageConventions.IsOverlay(InspectionIdentity.FileName(p))
            && System.Text.RegularExpressions.Regex.IsMatch(InspectionIdentity.FileName(p),
                $@"_(?:EXT_)?{side}_[012]\.[^.]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .OrderBy(p => ProductionImageConventions.RawImageOrder(p, side)).ToArray();
        return new(paths, paths.Length == 0 ? "Missing: inspection has no raw paths for this side." : "Resolved exact inspection from production CSV paths.", context);
    }

    private async Task<IReadOnlyList<InspectionContext>> ReadAsync(WeldingMachine machine, DateOnly date, CancellationToken token)
    {
        var result = new List<InspectionContext>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var day in new[] { date.AddDays(-1), date, date.AddDays(1) })
            foreach (var csv in await csvs.FindAsync(machine, day, token))
                if (files.Add(csv))
                    result.AddRange(await InspectionPaths.ReadAsync(machine, csv, csv, shares, token));
        return InspectionIdentity.WithCropConflicts(result);
    }
}

public static class InspectionPaths
{
    public static async Task<IReadOnlyList<InspectionContext>> ReadAsync(WeldingMachine machine,
        string path, string provenance, ISharePathResolver shares, CancellationToken token)
    {
        var result = new List<InspectionContext>();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var header = await reader.ReadLineAsync(token);
        if (header is null) return result;
        var headers = CsvSupport.UniqueHeaders(CsvSupport.ParseLine(header));
        var row = 1;
        while (await reader.ReadLineAsync(token) is { } line)
        {
            row++;
            var values = CsvSupport.ParseLine(line);
            if (values.Count < headers.Count) continue;
            var fields = headers.Select((name, index) => (name, value: values[index].Trim()))
                .ToDictionary(x => x.name, x => x.value, StringComparer.OrdinalIgnoreCase);
            string Get(string name) => fields.GetValueOrDefault(name, "");
            if (!DateTime.TryParseExact($"{Get("DATE")} {Get("TIME")}", ["yyyyMMdd HH:mm:ss", "yyyyMMdd HH:mm:ss.fff"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time)) continue;
            result.Add(InspectionPaths.Create(machine, Get, time, provenance, row, shares));
        }
        return result;
    }

    public static InspectionContext Create(WeldingMachine machine, Func<string, string?> get, DateTime time,
        string csv, int row, ISharePathResolver shares)
    {
        var paths = new List<string>();
        foreach (var side in new[] { "UPPER", "LOWER" })
            for (var index = 1; index <= 3; index++)
                foreach (var column in new[] { ProductionCsvSchema.ImagePath(side, index), ProductionCsvSchema.OverlayPath(side, index) })
                    if (get(column) is { Length: > 0 } path)
                        paths.Add(ProductionPathMapper.ToUnc(machine, path, shares));
        var imageAt = InspectionIdentity.ImageTime(paths);
        var models = paths.Select(path => System.Text.RegularExpressions.Regex.Match(path.Replace('\\', '/'),
            @"/(?<model>[^/]+)/\d{4}/\d{2}/\d{2}/")).Where(match => match.Success)
            .Select(match => match.Groups["model"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var model = models.Length == 1 ? models[0] :
            string.IsNullOrWhiteSpace(get("MODEL-ID")) ? machine.Model : get("MODEL-ID")!;
        var lot = get("LOT-ID") ?? "";
        var cell = get("CELL-ID") ?? "";
        var structured = paths.Where(path => System.Text.RegularExpressions.Regex.IsMatch(
            InspectionIdentity.FileName(path), @"^\d{8}_\d{6}_")).ToArray();
        var conflicting = models.Length > 1 || (structured.Length > 0 && (imageAt is null ||
            structured.Any(path => !InspectionIdentity.FileName(path).StartsWith(
                $"{imageAt:yyyyMMdd_HHmmss}_{lot}_{cell}_", StringComparison.OrdinalIgnoreCase))));
        return new(machine.Id, model, lot, cell, time, conflicting ? null : imageAt, paths, csv, row,
            conflicting ? "Conflicting image timestamps, lot, cell, or model in CSV paths." : null);
    }
}
