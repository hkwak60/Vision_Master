using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class NgBypassClassifiedFolderService : INgBypassClassifiedFolderService
{
    private readonly AppStorage _storage;

    public NgBypassClassifiedFolderService(AppStorage storage)
    {
        _storage = storage;
    }

    public Task<CopyResult> ClassifyAsync(
        WeldingMachine machine,
        NgBypassCandidate candidate,
        ReviewDecision decision,
        CancellationToken cancellationToken)
    {
        if (decision is not (ReviewDecision.RealNg or ReviewDecision.Overkill))
        {
            return Task.FromResult(new CopyResult(CopyState.NotRequested, null, "No folder copy is required."));
        }
        if (!Directory.Exists(candidate.SourceFolder))
        {
            return Task.FromResult(new CopyResult(CopyState.MissingSource, null, "The production image folder is unavailable."));
        }

        var classFolder = decision == ReviewDecision.RealNg ? "REAL" : "OVERKILL";
        var originalFolderName = Path.GetFileName(
            candidate.SourceFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var destinationParent = Path.Combine(
            _storage.MachineRoot(machine),
            "NG_BYPASS_MONITOR",
            classFolder,
            SafeName(candidate.Measure));
        Directory.CreateDirectory(destinationParent);
        var destination = Path.Combine(destinationParent, originalFolderName);
        if (Directory.Exists(destination))
        {
            return Task.FromResult(new CopyResult(CopyState.Copied, destination, "Destination already exists."));
        }

        CopyDirectory(candidate.SourceFolder, destination, cancellationToken);
        return Task.FromResult(new CopyResult(CopyState.Copied, destination, "Complete cell folder copied successfully."));
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
