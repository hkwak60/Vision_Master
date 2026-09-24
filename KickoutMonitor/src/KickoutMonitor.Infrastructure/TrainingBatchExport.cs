using System.Security.Cryptography;
using System.Text.Json;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed record BatchExportFile(string SampleId, string FinalClass, string RelativePath, string Sha256);
public sealed record BatchExportManifest(string BatchId, IReadOnlyList<BatchExportFile> Files);
public sealed partial class TrainingCollectionService
{
    /// <summary>Publish a complete local dataset before freezing the collection batch.</summary>
    public async Task<string> GenerateDatasetAsync(string id, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        string? stage = null;
        try
        {
            var batches = Read();
            var batch = batches.Single(b => b.Id == id);
            if (batch.TrainedAt is not null)
            {
                if (batch.DatasetFolder is null) throw new InvalidOperationException("This legacy trained batch has no generated dataset.");
                var existing = ReadExport(batch.DatasetFolder);
                ValidateExport(batch.DatasetFolder, existing);
                if (batch.Samples.Any(s => Segmentation(s.Review)) &&
                    existing.Files.Any(f => Path.GetDirectoryName(f.RelativePath) != Safe(f.FinalClass)))
                {
                    // Preserve the frozen export; publish an independently verified flat copy.
                    batch.DatasetFolder = await FlattenDatasetAsync(batch.DatasetFolder, existing, token);
                    Write(batches);
                }
                return batch.DatasetFolder;
            }
            if (batch.Pending != 0) throw new InvalidOperationException("Retry or exclude pending/failed samples before generating a dataset.");
            var samples = batch.Samples.Where(s => s.State == "Ready" && !s.Superseded).OrderBy(s => s.Id).ToArray();
            if (samples.Length == 0) throw new InvalidOperationException("This batch has no collected samples.");
            foreach (var sample in samples)
            {
                if (!sample.Review.IncludeInTraining || !ReviewSemantics.CanTrain(sample.Review.FinalClass) || sample.Review.IsFallbackRaw)
                    throw new InvalidOperationException("The batch contains an ineligible sample; retry collection first.");
                RecoverFiles(sample);
                ValidatePair(sample.Files);
            }
            var dates = samples.Select(s => (s.Review.Inspection?.ImageAt ?? s.Review.InspectedAt).Date).ToArray();
            var folder = ExportOwned(Path.Combine(_datasetRoot, Safe(batch.Product), Safe(batch.Crop), Safe(batch.Polarity),
                dates.Min().Year.ToString(), batch.Id, $"{dates.Min():MMdd}_{dates.Max():MMdd}"));
            var files = samples.SelectMany(s => s.Files.Order().Select(source => new BatchExportFile(s.Id, s.Review.FinalClass,
                Segmentation(s.Review) || batch.Crop.Equals("SEPA", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(Safe(s.Review.FinalClass), Path.GetFileName(source).StartsWith(s.Id + "_") ? Path.GetFileName(source) : s.Id + "_" + Path.GetFileName(source))
                    : Path.Combine(Safe(s.Review.FinalClass), s.Id, Path.GetFileName(source)),
                Digest(source)))).ToArray();
            var manifest = new BatchExportManifest(batch.Id, files);
            if (Directory.Exists(folder))
            {
                // Recover a publication that completed before the trained-state write.
                var prior = ReadExport(folder);
                if (JsonSerializer.Serialize(prior) != JsonSerializer.Serialize(manifest))
                    throw new IOException("An existing dataset differs from this batch; it was left unchanged.");
                ValidateExport(folder, prior);
            }
            else
            {
                stage = ExportOwned(Path.Combine(_datasetRoot, ".staging", batch.Id + "_" + Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(stage);
                var index = 0;
                foreach (var sample in samples)
                foreach (var source in sample.Files.Order())
                {
                    token.ThrowIfCancellationRequested();
                    var target = ExportChild(stage, files[index++].RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Owned(source), target, false);
                }
                ValidateExport(stage, manifest);
                await File.WriteAllTextAsync(Path.Combine(stage, ".batch-export.json"), JsonSerializer.Serialize(manifest), token);
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
                Directory.Move(stage, folder);
                stage = null;
            }
            token.ThrowIfCancellationRequested();
            batch.DatasetFolder = folder;
            batch.TrainedAt = _now();
            Write(batches);
            return folder;
        }
        finally
        {
            // Only this invocation's checked, private staging directory is removable.
            try { if (stage is not null && Directory.Exists(stage)) Directory.Delete(ExportOwned(stage), true); }
            finally { _gate.Release(); }
        }
    }
    private async Task<string> FlattenDatasetAsync(string sourceFolder, BatchExportManifest original, CancellationToken token)
    {
        var folder = ExportOwned(Path.Combine(Path.GetDirectoryName(sourceFolder)!, "flat", Path.GetFileName(sourceFolder)));
        var flat = new BatchExportManifest(original.BatchId, original.Files.Select(f => f with {
            RelativePath = Path.Combine(Safe(f.FinalClass), Path.GetFileName(f.RelativePath).StartsWith(f.SampleId + "_", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(f.RelativePath) : f.SampleId + "_" + Path.GetFileName(f.RelativePath))
        }).ToArray());
        if (Directory.Exists(folder))
        {
            if (JsonSerializer.Serialize(ReadExport(folder)) != JsonSerializer.Serialize(flat))
                throw new IOException("An existing flat dataset differs; it was left unchanged.");
            ValidateExport(folder, flat);
            return folder;
        }
        var stage = ExportOwned(Path.Combine(_datasetRoot, ".staging", original.BatchId + "_" + Guid.NewGuid().ToString("N")));
        try
        {
            Directory.CreateDirectory(stage);
            for (var i = 0; i < original.Files.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var target = ExportChild(stage, flat.Files[i].RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(ExportChild(sourceFolder, original.Files[i].RelativePath), target, false);
            }
            ValidateExport(stage, flat);
            await File.WriteAllTextAsync(Path.Combine(stage, ".batch-export.json"), JsonSerializer.Serialize(flat), token);
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
            Directory.Move(stage, folder);
            return folder;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(ExportOwned(stage), true); }
    }
    private string ExportOwned(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(_datasetRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
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
