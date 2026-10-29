using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using KickoutMonitor.Domain;
namespace KickoutMonitor.Infrastructure;

// Original paths remain the inspection/sample identity. Only the byte source changes.
public sealed class DlngImageCache(AppStorage storage)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Stamp(string Source, long Length, string Sha256);
    private string Target(string source) => Path.Combine(storage.Temp, "DLNG", InspectionIdentity.Hash(source.ToUpperInvariant()), Path.GetFileName(source));
    public string? Find(string source)
    {
        var path = Target(source);
        try
        {
            if (!File.Exists(path + ".json")) return null;
            var stamp = JsonSerializer.Deserialize<Stamp>(File.ReadAllText(path + ".json"));
            if (stamp is null || !stamp.Source.Equals(source, StringComparison.OrdinalIgnoreCase)
                || stamp.Length <= 0 || !File.Exists(path) || new FileInfo(path).Length != stamp.Length) return null;
            using var input = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(input)) == stamp.Sha256 ? path : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public Task<string> GetAsync(string source, CancellationToken token = default) => Task.Run(async () =>
    {
        var target = Target(source);
        var gate = Gates.GetOrAdd(target, _ => new(1, 1));
        await gate.WaitAsync(token);
        try
        {
            if (Find(source) is { } ready) return ready;
            var before = new FileInfo(source);
            if (!before.Exists || before.Length == 0) throw new IOException("Missing/empty crop: " + source);
            var length = before.Length; var time = before.LastWriteTimeUtc;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = target + ".copying";
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                { await input.CopyToAsync(output, token); output.Flush(true); }
                var after = new FileInfo(source);
                if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != time || new FileInfo(temp).Length != length)
                    throw new IOException("Crop changed during download: " + source);
                string digest;
                using (var stream = File.OpenRead(temp)) digest = Convert.ToHexString(SHA256.HashData(stream));
                File.Move(temp, target, true);
                File.WriteAllText(target + ".json.tmp", JsonSerializer.Serialize(new Stamp(source, length, digest)));
                File.Move(target + ".json.tmp", target + ".json", true);
                return target;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { gate.Release(); }
    }, token);
}
