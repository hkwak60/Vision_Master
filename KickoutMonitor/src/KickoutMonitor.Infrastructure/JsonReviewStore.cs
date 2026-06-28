using System.Text.Json;
using System.Text.Json.Serialization;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class JsonReviewStore : IReviewStore
{
    private readonly AppStorage _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
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
            if (!File.Exists(_storage.ReviewFile))
            {
                return new Dictionary<string, ReviewEntry>(StringComparer.OrdinalIgnoreCase);
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
            return entries.ToDictionary(x => x.CandidateKey, StringComparer.OrdinalIgnoreCase);
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
            var entries = new Dictionary<string, ReviewEntry>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(_storage.ReviewFile))
            {
                await using var source = new FileStream(
                    _storage.ReviewFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous);
                var existing = await JsonSerializer.DeserializeAsync<List<ReviewEntry>>(
                    source,
                    _json,
                    cancellationToken) ?? [];
                foreach (var item in existing) entries[item.CandidateKey] = item;
            }

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
        }
        finally
        {
            _gate.Release();
        }
    }
}
