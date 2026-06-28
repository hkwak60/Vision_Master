using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class DiskPreviewCache : IPreviewCache
{
    private readonly AppStorage _storage;

    public DiskPreviewCache(AppStorage storage)
    {
        _storage = storage;
    }

    public async Task<KickoutCandidate> EnsureCachedAsync(
        WeldingMachine machine,
        KickoutCandidate candidate,
        CancellationToken cancellationToken)
    {
        var cacheFolder = _storage.CandidateTempFolder(machine, candidate);
        Directory.CreateDirectory(cacheFolder);
        var cachedImages = new List<CandidateImage>(candidate.PreviewImages.Count);

        foreach (var image in candidate.PreviewImages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(image.NetworkPath);
            var cachedPath = Path.Combine(cacheFolder, fileName);

            if (!IsUsableCachedCopy(cachedPath, image.NetworkPath))
            {
                var temporary = cachedPath + ".copying";
                TryDeleteFile(temporary);
                var copied = await CopyStableFileAsync(
                    image.NetworkPath,
                    temporary,
                    cancellationToken);
                if (copied)
                {
                    File.Move(temporary, cachedPath, true);
                }
                else
                {
                    TryDeleteFile(temporary);
                    cachedPath = string.Empty;
                }
            }

            cachedImages.Add(image with
            {
                CachedPath = File.Exists(cachedPath) ? cachedPath : null
            });
        }

        return candidate with { PreviewImages = cachedImages };
    }

    public Task RemoveAsync(
        WeldingMachine machine,
        KickoutCandidate candidate,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = _storage.CandidateTempFolder(machine, candidate);
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                RemoveEmptyParents(Path.GetDirectoryName(folder), _storage.Temp);
            }
            catch (IOException)
            {
                // A preview can still be releasing its local file. Cleanup can
                // safely occur on the next load.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }, cancellationToken);
    }

    private static bool IsUsableCachedCopy(string cachedPath, string sourcePath)
    {
        try
        {
            if (!File.Exists(cachedPath)) return false;
            var cached = new FileInfo(cachedPath);
            var source = new FileInfo(sourcePath);
            return source.Exists
                && cached.Length == source.Length
                && HasCompleteImageEnding(cachedPath);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> CopyStableFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var before = new FileInfo(sourcePath);
            if (!before.Exists) return false;
            if (DateTime.UtcNow - before.LastWriteTimeUtc < TimeSpan.FromSeconds(2))
            {
                return false;
            }
            var originalLength = before.Length;
            var originalWrite = before.LastWriteTimeUtc;

            await using (var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                256 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                256 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[256 * 1024];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await destination.FlushAsync(cancellationToken);
            }

            var after = new FileInfo(sourcePath);
            return after.Exists
                && after.Length == originalLength
                && after.LastWriteTimeUtc == originalWrite
                && new FileInfo(destinationPath).Length == originalLength
                && HasCompleteImageEnding(destinationPath);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool HasCompleteImageEnding(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            if (stream.Length < 8) return false;
            stream.Seek(-8, SeekOrigin.End);
            Span<byte> ending = stackalloc byte[8];
            stream.ReadExactly(ending);
            ReadOnlySpan<byte> expected = [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];
            return ending.SequenceEqual(expected);
        }

        if (stream.Length < 2) return false;
        stream.Seek(-2, SeekOrigin.End);
        return stream.ReadByte() == 0xFF && stream.ReadByte() == 0xD9;
    }

    private static void RemoveEmptyParents(string? folder, string stopAt)
    {
        while (!string.IsNullOrWhiteSpace(folder)
               && folder.StartsWith(stopAt, StringComparison.OrdinalIgnoreCase)
               && !folder.Equals(stopAt, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.EnumerateFileSystemEntries(folder).Any()) return;
            Directory.Delete(folder);
            folder = Path.GetDirectoryName(folder);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}
