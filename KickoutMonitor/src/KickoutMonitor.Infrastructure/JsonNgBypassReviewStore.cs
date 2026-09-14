using System.Text.Json;
using System.Text.Json.Serialization;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class JsonNgBypassReviewStore : INgBypassReviewStore
{
    private readonly AppStorage _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, NgBypassReviewRecord>? _cache;
    private DateTime _cacheWriteTimeUtc;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonNgBypassReviewStore(AppStorage storage)
    {
        _storage = storage;
    }

    public async Task<IReadOnlyDictionary<string, NgBypassReviewRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return new Dictionary<string, NgBypassReviewRecord>(
                await LoadUnlockedAsync(cancellationToken),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(NgBypassReviewRecord record, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await LoadUnlockedAsync(cancellationToken);
            records[record.CandidateKey] = record;
            Directory.CreateDirectory(Path.GetDirectoryName(_storage.NgBypassReviewFile)!);
            var temporary = _storage.NgBypassReviewFile + ".writing";
            await using (var destination = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    destination,
                    records.Values.OrderBy(x => x.UpdatedAt).ToArray(),
                    _json,
                    cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }
            File.Move(temporary, _storage.NgBypassReviewFile, true);
            _cacheWriteTimeUtc = File.GetLastWriteTimeUtc(_storage.NgBypassReviewFile);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, NgBypassReviewRecord>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storage.NgBypassReviewFile))
        {
            _cache ??= new Dictionary<string, NgBypassReviewRecord>(StringComparer.OrdinalIgnoreCase);
            _cacheWriteTimeUtc = DateTime.MinValue;
            return _cache;
        }

        var writeTime = File.GetLastWriteTimeUtc(_storage.NgBypassReviewFile);
        if (_cache is not null && writeTime == _cacheWriteTimeUtc)
        {
            return _cache;
        }

        await using var stream = new FileStream(
            _storage.NgBypassReviewFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous);
        var records = await JsonSerializer.DeserializeAsync<List<NgBypassReviewRecord>>(
            stream,
            _json,
            cancellationToken) ?? [];
        _cache = records.ToDictionary(x => x.CandidateKey, StringComparer.OrdinalIgnoreCase);
        _cacheWriteTimeUtc = writeTime;
        return _cache;
    }
}
