using System.Text.Json;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class JsonDlngReviewStore : IDlngReviewStore
{
    private readonly AppStorage _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, DlngReviewRecord>? _cache;
    private DateTime _cacheWriteTimeUtc;

    public JsonDlngReviewStore(AppStorage storage)
    {
        _storage = storage;
    }

    public async Task<IReadOnlyDictionary<string, DlngReviewRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return new Dictionary<string, DlngReviewRecord>(
                await LoadUnlockedAsync(cancellationToken),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(DlngReviewRecord record, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = new Dictionary<string, DlngReviewRecord>(await LoadUnlockedAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);
            if (File.Exists(_storage.DlngReviewFile) && !File.Exists(_storage.DlngReviewFile + ".overkill-v1.bak"))
                File.Copy(_storage.DlngReviewFile, _storage.DlngReviewFile + ".overkill-v1.bak");
            ReviewCompatibility.Backup(_storage.DlngReviewFile);
            // Legacy ordinal and stable keys can describe the exact same saved pair.
            // Replace those representations together so re-review cannot leave a stale duplicate in history.
            if (record.ImagePaths.Count > 0)
            {
                var paths = record.ImagePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
                var aliases = records.Where(p => p.Key != record.ItemKey
                    && p.Value.MachineId == record.MachineId && p.Value.InspectedAt == record.InspectedAt
                    && p.Value.CellId == record.CellId && p.Value.CropFolder == record.CropFolder && p.Value.Side == record.Side
                    && p.Value.ImagePaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).SequenceEqual(paths, StringComparer.OrdinalIgnoreCase))
                    .Select(p => p.Key).ToArray();
                foreach (var alias in aliases) records.Remove(alias);
            }
            records[record.ItemKey] = record;
            Directory.CreateDirectory(Path.GetDirectoryName(_storage.DlngReviewFile)!);
            var temporary = _storage.DlngReviewFile + ".writing";
            await using (var write = new FileStream(
                             temporary,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    write,
                    records.Values.OrderBy(x => x.InspectedAt).ToArray(),
                    new JsonSerializerOptions { WriteIndented = true },
                    cancellationToken);
            }

            File.Move(temporary, _storage.DlngReviewFile, true);
            _cache = records;
            _cacheWriteTimeUtc = File.GetLastWriteTimeUtc(_storage.DlngReviewFile);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, DlngReviewRecord>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storage.DlngReviewFile))
        {
            _cache ??= new Dictionary<string, DlngReviewRecord>(StringComparer.OrdinalIgnoreCase);
            _cacheWriteTimeUtc = DateTime.MinValue;
            return _cache;
        }

        var writeTime = File.GetLastWriteTimeUtc(_storage.DlngReviewFile);
        if (_cache is not null && writeTime == _cacheWriteTimeUtc)
        {
            return _cache;
        }

        await using var stream = new FileStream(
            _storage.DlngReviewFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous);
        var records = await JsonSerializer.DeserializeAsync<List<DlngReviewRecord>>(
            stream,
            cancellationToken: cancellationToken) ?? [];
        _cache = records.ToDictionary(x => x.ItemKey, StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.Where(x => !x.IsFallbackRaw))
        {
            var split = record.ItemKey.LastIndexOf('|');
            if (split < 0 || !int.TryParse(record.ItemKey[(split + 1)..], out _)) continue;
            var pairKeys = record.ImagePaths.Select(InspectionIdentity.PairKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (pairKeys.Length != 1) continue;
            // Require the production filename prefix; the current queue key provides the exact inspection check.
            if (!pairKeys[0].StartsWith(record.CellId + "_", StringComparison.OrdinalIgnoreCase)) continue;
            var stable = record.ItemKey[..split] + "|PAIR:" + InspectionIdentity.Hash(pairKeys[0]);
            _cache.TryAdd(stable, record with { ItemKey = stable });
        }
        _cacheWriteTimeUtc = writeTime;
        return _cache;
    }
}
