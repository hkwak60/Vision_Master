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
            var records = await LoadUnlockedAsync(cancellationToken);
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
        _cacheWriteTimeUtc = writeTime;
        return _cache;
    }
}
