using System.Text.Json;
using System.Text.Json.Serialization;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class JsonReviewStore : IReviewStore
{
    private readonly AppStorage _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, ReviewEntry>? _cache;
    private DateTime _cacheWriteTimeUtc;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonReviewStore(AppStorage storage)
    {
        _storage = storage;
    }

    public async Task<IReadOnlyDictionary<string, ReviewEntry>> LoadAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return new Dictionary<string, ReviewEntry>(
                await LoadUnlockedAsync(cancellationToken),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ReviewEntry entry, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await LoadUnlockedAsync(cancellationToken);
            entries[entry.CandidateKey] = entry;
            Directory.CreateDirectory(Path.GetDirectoryName(_storage.ReviewFile)!);
            var temporary = _storage.ReviewFile + ".writing";
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
                    entries.Values.OrderBy(x => x.UpdatedAt).ToArray(),
                    _json,
                    cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }
            File.Move(temporary, _storage.ReviewFile, true);
            _cacheWriteTimeUtc = File.GetLastWriteTimeUtc(_storage.ReviewFile);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, ReviewEntry>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storage.ReviewFile))
        {
            _cache ??= new Dictionary<string, ReviewEntry>(StringComparer.OrdinalIgnoreCase);
            _cacheWriteTimeUtc = DateTime.MinValue;
            return _cache;
        }

        var writeTime = File.GetLastWriteTimeUtc(_storage.ReviewFile);
        if (_cache is not null && writeTime == _cacheWriteTimeUtc)
        {
            return _cache;
        }

        await using var stream = new FileStream(
            _storage.ReviewFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous);
        var entries = await JsonSerializer.DeserializeAsync<List<ReviewEntry>>(
            stream,
            _json,
            cancellationToken) ?? [];
        _cache = entries.ToDictionary(x => x.CandidateKey, StringComparer.OrdinalIgnoreCase);
        _cacheWriteTimeUtc = writeTime;
        return _cache;
    }
}
