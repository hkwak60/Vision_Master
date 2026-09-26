using System.Security.Cryptography;
using System.Text.Json;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed record BatchExportFile(string SampleId, string FinalClass, string RelativePath, string Sha256);
public sealed record BatchExportManifest(string BatchId, IReadOnlyList<BatchExportFile> Files);
public sealed partial class TrainingCollectionService
{
    /// <summary>Export or restore a frozen batch without consuming its retained sample counts.</summary>
    public async Task<string> GenerateDatasetAsync(string id, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        string? stage = null;
        try
        {
            var batches = Read();
            var batch = batches.Single(b => b.Id == id);
            if (batch.Pending != 0)
                throw new InvalidOperationException("Retry or exclude pending/failed samples before generating a dataset.");
            var samples = batch.Samples.Where(s => s.State == "Ready" && (batch.TrainedAt is not null || !s.Superseded)).OrderBy(s => s.Id).ToArray();
            if (samples.Length == 0) throw new InvalidOperationException("This batch has no collected samples.");
            BatchExportManifest? previous = null;
            if (batch.DatasetFolder is not null && File.Exists(Path.Combine(ExportOwned(batch.DatasetFolder), ".batch-export.json")))
                previous = ReadExport(batch.DatasetFolder);
            if (previous is not null && previous.BatchId != batch.Id) throw new IOException("Dataset belongs to another batch.");
            var frozen = batch.TrainedAt is not null ? batch.ExportSnapshot ?? previous : null;
            BatchExportManifest manifest;
            if (frozen is not null)
            {
                manifest = new(batch.Id, frozen.Files.Where(f => TrainingFiles(samples.Single(s => s.Id == f.SampleId).Review, new[] { f.RelativePath }).Any()).Select(f => f with {
                    RelativePath = RelativeFile(samples.Single(s => s.Id == f.SampleId), f.RelativePath)
                }).ToArray());
            }
            else
            {
                foreach (var sample in samples)
                {
                    if (!sample.Review.IncludeInTraining || !ReviewSemantics.CanTrain(sample.Review.FinalClass) || sample.Review.IsFallbackRaw)
                        throw new InvalidOperationException("The batch contains an ineligible sample.");
                    if (batch.TrainedAt is null) RecoverFiles(sample);
                    ValidateTrainingFiles(sample.Review, TrainingFiles(sample.Review, sample.Files).ToArray());
                }
                manifest = new(batch.Id, samples.SelectMany(sample => TrainingFiles(sample.Review, sample.Files).Order().Select(source =>
                    new BatchExportFile(sample.Id, sample.Review.FinalClass, RelativeFile(sample, source), Digest(Owned(source))))).ToArray());
            }
            var dates = samples.Select(s => (s.Review.Inspection?.ImageAt ?? s.Review.InspectedAt).Date).ToArray();
            var groupFolder = Path.Combine(_datasetRoot, Safe(batch.Product), Safe(batch.Crop));
            if (!samples.All(s => Segmentation(s.Review))) groupFolder = Path.Combine(groupFolder, Safe(batch.Polarity));
            var baseFolder = ExportOwned(Path.Combine(groupFolder, dates.Min().Year.ToString(), $"{dates.Min():MMdd}_{dates.Max():MMdd}"));
            var folder = AvailableFolder(baseFolder, batch, manifest, batches);
            var complete = false;
            if (Directory.Exists(folder))
            {
                try { ValidateExport(folder, manifest); complete = true; }
                catch (IOException) { /* Restore missing or altered owned files from the frozen local pairs. */ }
            }
            if (!complete)
            {
                stage = ExportOwned(Path.Combine(_datasetRoot, ".staging", batch.Id + "_" + Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(stage);
                foreach (var file in manifest.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var sample = samples.Single(s => s.Id == file.SampleId);
                    var candidates = sample.Files.Select(Owned).ToList();
                    if (previous is not null && batch.DatasetFolder is not null)
                        candidates.AddRange(previous.Files.Where(f => f.SampleId == file.SampleId && f.Sha256 == file.Sha256)
                            .Select(f => ExportChild(batch.DatasetFolder, f.RelativePath)));
                    var source = candidates.FirstOrDefault(p => File.Exists(p) && Digest(p) == file.Sha256)
                        ?? throw new IOException("A frozen source image is unavailable; this batch was not changed.");
                    var target = ExportChild(stage, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, false);
                }
                ValidateExport(stage, manifest);
                await File.WriteAllTextAsync(Path.Combine(stage, ".batch-export.json"), JsonSerializer.Serialize(manifest), token);
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
                if (!Directory.Exists(folder))
                {
                    Directory.Move(stage, folder);
                    stage = null;
                }
                else
                {
                    // Repair only this manifest's owned outputs; never remove unrelated files.
                    foreach (var file in manifest.Files)
                    {
                        var target = ExportChild(folder, file.RelativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Move(ExportChild(stage, file.RelativePath), target, true);
                    }
                    File.Move(Path.Combine(stage, ".batch-export.json"), Path.Combine(folder, ".batch-export.json"), true);
                }
            }
            token.ThrowIfCancellationRequested();
            batch.DatasetFolder = folder;
            batch.ExportSnapshot = manifest;
            batch.TrainedAt ??= _now();
            Write(batches);
            return folder;
        }
        finally
        {
            try { if (stage is not null && Directory.Exists(stage)) Directory.Delete(ExportOwned(stage), true); }
            finally { _gate.Release(); }
        }
    }
    private static string RelativeFile(TrainingSample sample, string source)
    {
        var name = Path.GetFileName(source);
        if (!name.StartsWith(sample.Id + "_", StringComparison.OrdinalIgnoreCase)) name = sample.Id + "_" + name;
        return Path.Combine(Safe(sample.Review.FinalClass), name);
    }
    private string AvailableFolder(string root, TrainingBatch batch, BatchExportManifest manifest, IReadOnlyList<TrainingBatch> batches)
    {
        bool Matches(string candidate)
        {
            if (batches.Any(b => b.Id != batch.Id && string.Equals(b.DatasetFolder, candidate, StringComparison.OrdinalIgnoreCase))) return false;
            if (!Directory.Exists(candidate)) return true;
            var path = Path.Combine(candidate, ".batch-export.json");
            if (File.Exists(path)) return JsonSerializer.Serialize(ReadExport(candidate)) == JsonSerializer.Serialize(manifest);
            return batch.DatasetFolder == candidate && batch.ExportSnapshot is not null;
        }
        if (batch.DatasetFolder is { } known && (known == root ||
            (known.StartsWith(root + "_", StringComparison.OrdinalIgnoreCase) && int.TryParse(known[(root.Length + 1)..], out _))) && Matches(known))
            return known;
        for (var suffix = 1; ; suffix++)
        {
            var candidate = suffix == 1 ? root : root + "_" + suffix;
            if (Matches(candidate)) return candidate;
        }
    }
    private string ExportOwned(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(_datasetRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(Path.GetFullPath(_previousDatasetRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Dataset path is outside the dataset root.");
        return full;
    }
    private string ExportChild(string folder, string relative)
    {
        var full = ExportOwned(Path.Combine(folder, relative));
        if (!full.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Dataset file escapes its batch folder.");
        return full;
    }
    private BatchExportManifest ReadExport(string folder) =>
        JsonSerializer.Deserialize<BatchExportManifest>(File.ReadAllText(Path.Combine(ExportOwned(folder), ".batch-export.json")))
        ?? throw new IOException("Dataset manifest is missing.");
    private void ValidateExport(string folder, BatchExportManifest manifest)
    {
        foreach (var file in manifest.Files)
        {
            var path = ExportChild(folder, file.RelativePath);
            if (!File.Exists(path) || Digest(path) != file.Sha256) throw new IOException("A dataset file is missing or incomplete.");
        }
    }
    private static string Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
