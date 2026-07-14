using System.Text.Json;
using System.Text.Json.Serialization;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class JsonNgBypassReviewStore : INgBypassReviewStore
{
    private readonly AppStorage _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
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
            if (!File.Exists(_storage.NgBypassReviewFile))
            {
                return new Dictionary<string, NgBypassReviewRecord>(StringComparer.OrdinalIgnoreCase);
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
            return records.ToDictionary(x => x.CandidateKey, StringComparer.OrdinalIgnoreCase);
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
            var records = new Dictionary<string, NgBypassReviewRecord>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(_storage.NgBypassReviewFile))
            {
                await using var source = new FileStream(
                    _storage.NgBypassReviewFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous);
                var existing = await JsonSerializer.DeserializeAsync<List<NgBypassReviewRecord>>(
                    source,
                    _json,
                    cancellationToken) ?? [];
                foreach (var item in existing) records[item.CandidateKey] = item;
            }

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
        }
        finally
        {
            _gate.Release();
        }
    }
}
