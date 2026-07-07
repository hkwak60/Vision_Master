using System.Text.Json;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class JsonDlngReviewStore : IDlngReviewStore
{
    private readonly AppStorage _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonDlngReviewStore(AppStorage storage)
    {
        _storage = storage;
    }

    public async Task<IReadOnlyDictionary<string, DlngReviewRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storage.DlngReviewFile))
        {
            return new Dictionary<string, DlngReviewRecord>(StringComparer.OrdinalIgnoreCase);
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
        return records.ToDictionary(x => x.ItemKey, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SaveAsync(DlngReviewRecord record, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = new Dictionary<string, DlngReviewRecord>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(_storage.DlngReviewFile))
            {
                await using var read = new FileStream(
                    _storage.DlngReviewFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous);
                var existing = await JsonSerializer.DeserializeAsync<List<DlngReviewRecord>>(
                    read,
                    cancellationToken: cancellationToken) ?? [];
                foreach (var entry in existing) records[entry.ItemKey] = entry;
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
        }
        finally
        {
            _gate.Release();
        }
    }
}
