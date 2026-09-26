using System.Text.Json;

namespace KickoutMonitor.Infrastructure;

public sealed partial class TrainingCollectionService
{
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
                        throw new IOException($"Batch {batch.Id}, cell {sample.Review.CellId}, crop {batch.Crop}: {e.Message} Files: {string.Join("; ", sample.Files)}", e);
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
        File.WriteAllText(Path.Combine(_root, "migration-complete.txt"),
            "Legacy Training collection copied and verified. Original Training folder was not modified. " + DateTimeOffset.Now);
    }
}
