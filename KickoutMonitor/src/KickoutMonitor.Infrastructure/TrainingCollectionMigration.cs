using System.Text.Json;

namespace KickoutMonitor.Infrastructure;

public sealed partial class TrainingCollectionService
{
    private void RelocatePreviousCollection()
    {
        var previousManifest = Path.Combine(_previousRoot, "batches.json");
        if (File.Exists(_manifest) || !File.Exists(previousManifest)) return;
        var batches = JsonSerializer.Deserialize<List<TrainingBatch>>(File.ReadAllText(previousManifest))
            ?? throw new IOException("Cannot read previous DLNG collection.");
        string Relocate(string path)
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(Path.GetFullPath(_previousRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Previous collection contains an external ownership path.");
            return Path.Combine(_root, Path.GetRelativePath(_previousRoot, full));
        }
        // Copy before publishing rewritten references. Leave all old data intact.
        foreach (var source in Directory.EnumerateFiles(_previousRoot, "*", SearchOption.AllDirectories))
        {
            if (source == previousManifest || Path.GetFileName(source) == "migration-complete.txt" || source.EndsWith("_ActiveMap.jpg", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Relocate(source);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target) || Digest(source) != Digest(target))
            {
                File.Copy(source, target + ".relocating", true);
                if (Digest(source) != Digest(target + ".relocating")) throw new IOException("Collection relocation verification failed.");
                File.Move(target + ".relocating", target, true);
            }
        }
        foreach (var sample in batches.SelectMany(b => b.Samples))
        {
            sample.Files = sample.Files.Select(Relocate).ToList();
            sample.ObsoleteFiles = sample.ObsoleteFiles.Select(Relocate).ToList();
            sample.Error = sample.Error.Replace(_previousRoot, _root, StringComparison.OrdinalIgnoreCase);
        }
        Write(batches);
        if (File.Exists(Path.Combine(_previousRoot, "migration-complete.txt")) && !batches.SelectMany(b => b.Samples).Any(s => s.MigrationPending))
            File.Copy(Path.Combine(_previousRoot, "migration-complete.txt"), Path.Combine(_root, "migration-complete.txt"), true);
    }
    // Publish the new manifest only after every retained local file is verified.
    // The old collection is a read-only backup and can be removed by the user afterward.
    private void MigrateLegacyCollection()
    {
        var legacyManifest = Path.Combine(_legacyRoot, "batches.json");
        if (File.Exists(_manifest) || !File.Exists(legacyManifest)) return;
        var batches = JsonSerializer.Deserialize<List<TrainingBatch>>(File.ReadAllText(legacyManifest))
            ?? throw new IOException("Cannot read legacy collection.");
        string CopyOwned(string source, string sourceRoot, string targetRoot)
        {
            var full = Path.GetFullPath(source);
            if (!full.StartsWith(Path.GetFullPath(sourceRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Legacy collection contains an external ownership path.");
            var target = Path.Combine(targetRoot, Path.GetRelativePath(sourceRoot, full));
            if (File.Exists(full))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target) || Digest(full) != Digest(target))
                {
                    File.Copy(full, target + ".migrating", true);
                    if (Digest(full) != Digest(target + ".migrating")) throw new IOException("Collection migration verification failed.");
                    File.Move(target + ".migrating", target, true);
                }
            }
            return target;
        }
        foreach (var batch in batches)
        {
            foreach (var sample in batch.Samples)
            {
                sample.LegacyFiles = TrainingFiles(sample.Review, sample.Files).ToList();
                sample.Files = TrainingFiles(sample.Review, sample.Files).Select(p => CopyOwned(p, _legacyRoot, _root)).ToList();
                sample.ObsoleteFiles = TrainingFiles(sample.Review, sample.ObsoleteFiles).Select(p => CopyOwned(p, _legacyRoot, _root)).ToList();
                foreach (var missing in sample.Files.Where(p => !File.Exists(p)))
                {
                    var recovery = sample.ObsoleteFiles.FirstOrDefault(p => Path.GetFileName(p) == Path.GetFileName(missing) && File.Exists(p));
                    if (recovery is not null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(missing)!);
                        File.Copy(recovery, missing, true);
                    }
                }
                if (sample.State == "Ready")
                {
                    try { ValidateTrainingFiles(sample.Review, sample.Files); }
                    catch (IOException e)
                    {
                        sample.State = "Failed";
                        sample.MigrationPending = true;
                        sample.Error = $"Batch {batch.Id}, cell {sample.Review.CellId}, crop {batch.Crop}: {e.Message} Files: {string.Join("; ", sample.Files)}";
                    }
                }
            }
            // Keep historical exports at their old location; local verified sources can recreate
            // them in DLNG. Snapshot hashes retain immutable trained labels and contents.
            if (batch.ExportSnapshot is { } snapshot)
                batch.ExportSnapshot = snapshot with { Files = snapshot.Files.Where(f =>
                    TrainingFiles(batch.Samples.Single(s => s.Id == f.SampleId).Review, new[] { f.RelativePath }).Any()).ToArray() };
            batch.DatasetFolder = null;
        }
        var legacyStage = Path.Combine(_legacyRoot, ".staging");
        if (Directory.Exists(legacyStage))
            foreach (var file in Directory.EnumerateFiles(legacyStage, "*", SearchOption.AllDirectories)
                .Where(p => !p.EndsWith("_ActiveMap.jpg", StringComparison.OrdinalIgnoreCase)))
                CopyOwned(file, _legacyRoot, _root);
        Write(batches);
        if (!batches.SelectMany(b => b.Samples).Any(s => s.MigrationPending))
            File.WriteAllText(Path.Combine(_root, "migration-complete.txt"),
                "Legacy Training collection copied and verified. Original Training folder was not modified. " + DateTimeOffset.Now);
    }
    // Retry only exact legacy-owned paths; a missing sample must not block unrelated collection.
    private void RetryLegacySamples(List<TrainingBatch> batches)
    {
        var changed = false;
        foreach (var sample in batches.SelectMany(b => b.Samples).Where(s => s.MigrationPending))
        {
            try
            {
                foreach (var target in sample.Files)
                {
                    if (File.Exists(Owned(target)) && new FileInfo(target).Length > 0) continue;
                    var source = sample.LegacyFiles.FirstOrDefault(p =>
                        Path.GetFileName(p).Equals(Path.GetFileName(target), StringComparison.OrdinalIgnoreCase));
                    if (source is null) continue;
                    if (!Path.GetFullPath(source).StartsWith(Path.GetFullPath(_legacyRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Legacy source is outside Training.");
                    if (!File.Exists(source) || new FileInfo(source).Length == 0) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target + ".migrating", true);
                    if (Digest(source) != Digest(target + ".migrating")) throw new IOException("Legacy retry verification failed.");
                    File.Move(target + ".migrating", target, true);
                }
                ValidateTrainingFiles(sample.Review, sample.Files);
                sample.State = "Ready";
                sample.MigrationPending = false;
                sample.Error = "";
                changed = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { /* Keep the durable sample error and allow unrelated batches to proceed. */ }
        }
        if (changed) Write(batches);
        if (batches.SelectMany(b => b.Samples).Any(s => s.LegacyFiles.Count > 0)
            && !batches.SelectMany(b => b.Samples).Any(s => s.MigrationPending)
            && !File.Exists(Path.Combine(_root, "migration-complete.txt")))
            File.WriteAllText(Path.Combine(_root, "migration-complete.txt"),
                "Legacy sample migration resolved. Original Training was not modified. " + DateTimeOffset.Now);
    }

}
