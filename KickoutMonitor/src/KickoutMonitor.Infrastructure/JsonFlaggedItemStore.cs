using System.Text.Json;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class JsonFlaggedItemStore : IFlaggedItemStore
{
    private readonly AppStorage _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonFlaggedItemStore(AppStorage storage)
    {
        _storage = storage;
    }

    public async Task<IReadOnlyDictionary<string, FlaggedItem>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadUnlockedAsync(cancellationToken);
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
            var items = await LoadUnlockedAsync(cancellationToken);
            var mutable = new Dictionary<string, FlaggedItem>(items, StringComparer.OrdinalIgnoreCase);
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
            var updated = items.Values
                .Select(item => keySet.Contains(item.Key)
                    ? item with { SummarizedAt = summarizedAt, UpdatedAt = summarizedAt }
                    : item)
                .ToArray();
            await SaveUnlockedAsync(updated, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, FlaggedItem>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storage.FlaggedItemFile))
        {
            return new Dictionary<string, FlaggedItem>(StringComparer.OrdinalIgnoreCase);
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
        return records.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
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
    }
}
