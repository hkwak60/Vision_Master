using System.Security.Cryptography;
using System.Text;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class ReadOnlySnapshotService : IReadOnlySnapshotService
{
    private readonly AppStorage _storage;

    public ReadOnlySnapshotService(AppStorage storage)
    {
        _storage = storage;
    }

    public async Task<SnapshotResult> CreateAsync(
        string sourceCsv,
        bool currentDate,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_storage.Staging);
        var before = new FileInfo(sourceCsv);
        if (!before.Exists) throw new FileNotFoundException("Daily CSV was not found.", sourceCsv);

        var name = $"{StableName(sourceCsv)}_{before.LastWriteTimeUtc:yyyyMMddHHmmssfff}_{before.Length}.csv";
        var finalPath = Path.Combine(_storage.Staging, name);
        var temporaryPath = finalPath + ".copying";

        await using (var source = new FileStream(
            sourceCsv,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var destination = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await ThrottledCopyAsync(source, destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, finalPath, true);
        var after = new FileInfo(sourceCsv);
        var changed = before.Length != after.Length
            || before.LastWriteTimeUtc != after.LastWriteTimeUtc;
        return new(
            sourceCsv,
            finalPath,
            currentDate || changed,
            changed ? "CSV changed while copied. Complete rows were loaded from a provisional snapshot." : null);
    }

    private static async Task ThrottledCopyAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[256 * 1024];
        var bytesSinceYield = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            bytesSinceYield += read;
            if (bytesSinceYield >= 4 * 1024 * 1024)
            {
                bytesSinceYield = 0;
                await Task.Delay(2, cancellationToken);
            }
        }
    }

    private static string StableName(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 10));
    }
}
