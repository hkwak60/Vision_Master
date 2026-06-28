using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class ClassifiedFolderService : IClassifiedFolderService
{
    private readonly AppStorage _storage;

    public ClassifiedFolderService(AppStorage storage)
    {
        _storage = storage;
    }

    public async Task<CopyResult> ClassifyAsync(
        WeldingMachine machine,
        KickoutCandidate candidate,
        ReviewDecision decision,
        CancellationToken cancellationToken)
    {
        if (decision is not (
            ReviewDecision.RealNg
            or ReviewDecision.Overkill
            or ReviewDecision.MultiDefectNg))
        {
            return new(CopyState.NotRequested, null, "No folder copy is required.");
        }
        if (!Directory.Exists(candidate.SourceFolder))
        {
            return new(CopyState.MissingSource, null, "The production image folder is unavailable.");
        }

        var originalFolderName = Path.GetFileName(
            candidate.SourceFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var machineRoot = _storage.MachineRoot(machine);
        var defect = decision == ReviewDecision.MultiDefectNg
            ? SummaryReportService.MultiNgDefectName
            : KickoutRules.NormalizeDefect(candidate.Defect);
        var destinationParent = decision == ReviewDecision.Overkill
            ? Path.Combine(machineRoot, "OVERKILL", defect)
            : Path.Combine(machineRoot, "NG", defect);
        Directory.CreateDirectory(destinationParent);
        var destination = Path.Combine(destinationParent, originalFolderName);

        var existing = FindExistingLocalFolder(machineRoot, originalFolderName);
        if (existing is not null)
        {
            if (existing.Equals(destination, StringComparison.OrdinalIgnoreCase))
            {
                return new(CopyState.Copied, destination, "Folder was already classified.");
            }

            if (!Directory.Exists(destination))
            {
                Directory.Move(existing, destination);
                return new(CopyState.Copied, destination, "Local copy was moved to the new classification.");
            }
        }

        if (Directory.Exists(destination))
        {
            return new(CopyState.Copied, destination, "Destination already exists.");
        }

        var temporary = destination + $".copying-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(temporary);
            var sourceFiles = Directory.EnumerateFiles(
                    candidate.SourceFolder,
                    "*",
                    SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var sourceFile in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(candidate.SourceFolder, sourceFile);
                var destinationFile = Path.Combine(temporary, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
                var result = await CopyStableFileAsync(sourceFile, destinationFile, cancellationToken);
                if (!result)
                {
                    Directory.Delete(temporary, true);
                    return new(
                        CopyState.PendingRetry,
                        null,
                        $"A production file changed while being copied: {Path.GetFileName(sourceFile)}");
                }
            }

            var afterFiles = Directory.EnumerateFiles(
                    candidate.SourceFolder,
                    "*",
                    SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!sourceFiles.SequenceEqual(afterFiles, StringComparer.OrdinalIgnoreCase))
            {
                Directory.Delete(temporary, true);
                return new(
                    CopyState.PendingRetry,
                    null,
                    "The production folder changed while being copied.");
            }

            Directory.Move(temporary, destination);
            return new(CopyState.Copied, destination, "Complete cell folder copied successfully.");
        }
        catch (IOException exception)
        {
            TryDelete(temporary);
            return new(CopyState.PendingRetry, null, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            TryDelete(temporary);
            return new(CopyState.PendingRetry, null, exception.Message);
        }
    }

    private static async Task<bool> CopyStableFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var before = new FileInfo(sourcePath);
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
        return after.Exists
            && after.Length == originalLength
            && after.LastWriteTimeUtc == originalWrite
            && new FileInfo(destinationPath).Length == originalLength
            && HasCompleteImageEnding(destinationPath);
    }

    private static bool HasCompleteImageEnding(string path)
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

    private static string? FindExistingLocalFolder(string machineRoot, string folderName)
    {
        try
        {
            return Directory.EnumerateDirectories(machineRoot, folderName, SearchOption.AllDirectories)
                .FirstOrDefault(path => !path.Contains(".copying-", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
        }
    }
}
