using System.Text.Json;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class JsonFlaggedItemStore : IFlaggedItemStore
{
    private readonly AppStorage _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, FlaggedItem>? _cache;
    private DateTime _cacheWriteTimeUtc;

    public JsonFlaggedItemStore(AppStorage storage)
    {
        _storage = storage;
    }

    public async Task<IReadOnlyDictionary<string, FlaggedItem>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return new Dictionary<string, FlaggedItem>(
                await LoadUnlockedAsync(cancellationToken),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(FlaggedItem item, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var mutable = await LoadUnlockedAsync(cancellationToken);
            if (mutable.TryGetValue(item.Key, out var existing))
            {
                item = item with
                {
                    FlaggedAt = existing.FlaggedAt,
                    SummarizedAt = existing.SummarizedAt
                };
            }

            mutable[item.Key] = item;
            await SaveUnlockedAsync(mutable.Values, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var keySet = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var items = await LoadUnlockedAsync(cancellationToken);
            foreach (var key in keySet)
            {
                items.Remove(key);
            }

            await SaveUnlockedAsync(items.Values, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkActiveAsync(
        IReadOnlyList<string> keys,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var keySet = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var items = await LoadUnlockedAsync(cancellationToken);
            foreach (var key in keySet)
            {
                if (items.TryGetValue(key, out var item))
                {
                    items[key] = item with { SummarizedAt = null, UpdatedAt = updatedAt };
                }
            }

            await SaveUnlockedAsync(items.Values, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkSummarizedAsync(
        IReadOnlyList<string> keys,
        DateTimeOffset summarizedAt,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var keySet = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var items = await LoadUnlockedAsync(cancellationToken);
            foreach (var key in keySet)
            {
                if (items.TryGetValue(key, out var item))
                {
                    items[key] = item with { SummarizedAt = summarizedAt, UpdatedAt = summarizedAt };
                }
            }

            await SaveUnlockedAsync(items.Values, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, FlaggedItem>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storage.FlaggedItemFile))
        {
            _cache ??= new Dictionary<string, FlaggedItem>(StringComparer.OrdinalIgnoreCase);
            _cacheWriteTimeUtc = DateTime.MinValue;
            return _cache;
        }

        var writeTime = File.GetLastWriteTimeUtc(_storage.FlaggedItemFile);
        if (_cache is not null && writeTime == _cacheWriteTimeUtc)
        {
            return _cache;
        }

        await using var stream = new FileStream(
            _storage.FlaggedItemFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous);
        var records = await JsonSerializer.DeserializeAsync<List<FlaggedItem>>(
            stream,
            cancellationToken: cancellationToken) ?? [];
        _cache = records.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        _cacheWriteTimeUtc = writeTime;
        return _cache;
    }

    private async Task SaveUnlockedAsync(IEnumerable<FlaggedItem> items, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storage.FlaggedItemFile)!);
        var temporary = _storage.FlaggedItemFile + ".writing";
        await using (var stream = new FileStream(
                         temporary,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                items.OrderBy(x => x.ProducedAt).ThenBy(x => x.LinePolarity).ThenBy(x => x.CellId).ToArray(),
                new JsonSerializerOptions { WriteIndented = true },
                cancellationToken);
        }

        File.Move(temporary, _storage.FlaggedItemFile, true);
        _cacheWriteTimeUtc = File.GetLastWriteTimeUtc(_storage.FlaggedItemFile);
    }
}
