namespace KickoutMonitor.Infrastructure;

public static class SafeFileCopier
{
    public static async Task<bool> CopyStableFileAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        bool requireCompleteImageEnding,
        CancellationToken cancellationToken)
    {
        try
        {
            var before = new FileInfo(sourcePath);
            if (!before.Exists) return false;
            if (DateTime.UtcNow - before.LastWriteTimeUtc < TimeSpan.FromSeconds(2)) return false;
            var originalLength = before.Length;
            var originalWrite = before.LastWriteTimeUtc;

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using (var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                256 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                destinationPath,
                overwrite ? FileMode.Create : FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                256 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
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
                await destination.FlushAsync(cancellationToken);
            }

            var after = new FileInfo(sourcePath);
            var copied = new FileInfo(destinationPath);
            return after.Exists
                && after.Length == originalLength
                && after.LastWriteTimeUtc == originalWrite
                && copied.Exists
                && copied.Length == originalLength
                && (!requireCompleteImageEnding || HasCompleteImageEnding(destinationPath));
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

    public static bool HasCompleteImageEnding(string path)
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
            return ending.SequenceEqual(new byte[] { 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 });
        }

        if (stream.Length < 2) return false;
        stream.Seek(-2, SeekOrigin.End);
        return stream.ReadByte() == 0xFF && stream.ReadByte() == 0xD9;
    }
}


