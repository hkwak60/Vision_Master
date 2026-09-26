using System.Collections.Concurrent;
using System.Text.Json;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class TrainingBatch
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Group { get; set; } = "";
    public string Product { get; set; } = "";
    public string Crop { get; set; } = "";
    public string Polarity { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? FirstCollected { get; set; }
    public DateTimeOffset? LastCollected { get; set; }
    public DateTimeOffset? TrainedAt { get; set; }
    public string? DatasetFolder { get; set; }
    public BatchExportManifest? ExportSnapshot { get; set; }
    public int TotalSamples => ExportSnapshot?.Files.Select(f => f.SampleId).Distinct().Count()
        ?? Samples.Count(s => s.State == "Ready" && (TrainedAt is not null || !s.Superseded));
    public int TotalFiles => ExportSnapshot?.Files.Count
        ?? Samples.Where(s => s.State == "Ready" && (TrainedAt is not null || !s.Superseded)).Sum(s => s.Files.Count);
    public string Status => TrainedAt is null ? "Active" : "Trained";
    public List<TrainingSample> Samples { get; set; } = [];
    public string Range => FirstCollected is null ? "pending" : $"{FirstCollected:MMdd}_{LastCollected:MMdd}";
    public int NewSamples => TrainedAt is null ? Samples.Count(x => x.State == "Ready" && !x.Superseded) : 0;
    public int Pending => Samples.Count(x => x.State is "Pending" or "Failed");
}
public sealed class TrainingSample
{
    public string Id { get; set; } = "";
    public DlngReviewRecord Review { get; set; } = null!;
    public string State { get; set; } = "Pending";
    public string Error { get; set; } = "";
    public DateTimeOffset? CollectedAt { get; set; }
    public bool Superseded { get; set; }
    // Full paths are an ownership journal. Old paths survive interrupted reclassification/range updates.
    public List<string> Files { get; set; } = [];
    public List<string> ObsoleteFiles { get; set; } = [];
}
public sealed partial class TrainingCollectionService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _manifest;
    private readonly string _datasetRoot;
    private readonly string _legacyRoot;
    private readonly SemaphoreSlim _gate;
    public TrainingCollectionService(AppStorage storage, Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.Now);
        _legacyRoot = Path.Combine(storage.Root, "Training");
        _root = Path.Combine(storage.Root, "DLNG", ".collection");
        _datasetRoot = Path.Combine(storage.Root, "DLNG", "DATASET");
        _manifest = Path.Combine(_root, "batches.json");
        _gate = Gates.GetOrAdd(_manifest, _ => new(1, 1));
    }
    public static string Safe(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    public static string Product(DlngReviewRecord r) => !string.IsNullOrWhiteSpace(r.ProductModel) ? r.ProductModel
        : !string.IsNullOrWhiteSpace(r.Inspection?.Model) ? r.Inspection.Model : "Unknown";
    public static bool Segmentation(DlngReviewRecord r) => r.ModelKind == DlngModelKind.Segmentation ||
        r.FinalClass.Equals("Real", StringComparison.OrdinalIgnoreCase) || r.FinalClass.Equals("Overkill", StringComparison.OrdinalIgnoreCase);
    public static string Group(DlngReviewRecord r) => $"{Product(r)}|{r.CropFolder}|{(Segmentation(r) ? "shared" : Polarity(r))}";
    private static string Polarity(DlngReviewRecord r) => string.IsNullOrWhiteSpace(r.TrainingPolarity)
        ? (r.LinePolarity.Contains('+') ? "Cathode" : r.LinePolarity.Contains('-') && r.LinePolarity.EndsWith('-') ? "Anode" : "Unknown")
        : r.TrainingPolarity;
    private List<TrainingBatch> Read()
    {
        MigrateLegacyCollection();
        return File.Exists(_manifest)
            ? JsonSerializer.Deserialize<List<TrainingBatch>>(File.ReadAllText(_manifest)) ?? [] : [];
    }
    private void Write(List<TrainingBatch> batches)
    {
        Directory.CreateDirectory(_root);
        ReviewCompatibility.Backup(_manifest);
        File.WriteAllText(_manifest + ".tmp", JsonSerializer.Serialize(batches, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(_manifest + ".tmp", _manifest, true);
    }
    public string? LoadWarning { get; private set; }
    public async Task<IReadOnlyList<TrainingBatch>> LoadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            LoadWarning = null;
            List<TrainingBatch> batches;
            try { batches = Read(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                var legacyManifest = Path.Combine(_legacyRoot, "batches.json");
                if (File.Exists(_manifest) || !File.Exists(legacyManifest)) throw;
                batches = JsonSerializer.Deserialize<List<TrainingBatch>>(File.ReadAllText(legacyManifest)) ?? [];
                LoadWarning = "Collection migration incomplete; showing original batches read-only. Keep Training. " + e;
                return batches; // Never move, clean up, or rewrite legacy-owned files.
            }
            var changed = false;
            foreach (var batch in batches.Where(b => b.TrainedAt is null))
            foreach (var sample in batch.Samples.Where(s => s.State == "Ready"))
            {
                try
                {
                    var targets = sample.Files.Select(path => Path.Combine(Folder(batch, sample), Path.GetFileName(path))).ToList();
                    if (!targets.SequenceEqual(sample.Files, StringComparer.OrdinalIgnoreCase))
                    {
                        sample.ObsoleteFiles.AddRange(sample.Files);
                        sample.Files = targets;
                        Write(batches);
                        changed = true;
                    }
                    RecoverFiles(sample);
                    ValidateTrainingFiles(sample.Review, sample.Files);
                }
                catch (IOException e) { sample.State = "Failed"; sample.Error = e.Message; changed = true; }
            }
            if (changed) Write(batches);
            return batches;
        } finally { _gate.Release(); }
    }
    public async Task<string> ApplyAsync(DlngReviewRecord review, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var batches = Read();
            var id = ReviewSemantics.SampleId(review);
            foreach (var frozen in batches.Where(x => x.TrainedAt is not null))
            foreach (var old in frozen.Samples.Where(x => x.Id == id))
            {
                if (old.Review.FinalClass != review.FinalClass || !review.IncludeInTraining) old.Superseded = true;
                Write(batches);
                return old.Superseded ? "Trained sample superseded; frozen files retained" : "Already trained";
            }
            var batch = batches.FirstOrDefault(x => x.TrainedAt is null && x.Samples.Any(s => s.Id == id));
            var sample = batch?.Samples.First(x => x.Id == id);
            if (!review.IncludeInTraining || review.IsFallbackRaw || !ReviewSemantics.CanTrain(review.FinalClass))
            {
                if (sample is not null)
                {
                    sample.State = "Excluded";
                    sample.ObsoleteFiles.AddRange(sample.Files);
                    sample.Files.Clear();
                    sample.Review = review;
                    Write(batches);
                    Cleanup(sample);
                    Write(batches);
                }
                return review.IsFallbackRaw && review.IncludeInTraining ? "Raw fallback is not a training crop" : "Not collected";
            }
            if (sample is { State: "Ready" } && sample.ObsoleteFiles.Count > 0)
            { RecoverFiles(sample); Write(batches); }
            if (sample is not null && sample.State == "Ready" && sample.Review.FinalClass == review.FinalClass &&
                sample.Files.Count == (Segmentation(review) ? 2 : 1) && sample.Files.All(File.Exists)) { sample.Review = review; Write(batches); return "Collected"; }
            if (batch is null)
            {
                batch = batches.FirstOrDefault(x => x.TrainedAt is null && x.Group == Group(review));
                if (batch is null)
                {
                    batch = new() { CreatedAt = _now(), Group = Group(review), Product = Product(review), Crop = review.CropFolder,
                        Polarity = Segmentation(review) ? "shared" : Polarity(review) };
                    batches.Add(batch);
                }
                sample = new() { Id = id, Review = review };
                batch.Samples.Add(sample);
            }
            sample!.ObsoleteFiles.AddRange(sample.Files);
            sample.Files.Clear();
            sample.Review = review;
            sample.State = "Pending";
            Write(batches); // Intent is durable before copying or moving anything.
            try
            {
                var ownedSources = sample.ObsoleteFiles.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
                    .GroupBy(Path.GetFileName).Select(g => g.First()).ToArray();
                var sources = ownedSources.Length == (Segmentation(review) ? 2 : 1) ? ownedSources : TrainingFiles(review, review.ImagePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var staged = Path.Combine(_root, ".staging", id);
                if (sources.Any(p => !File.Exists(p)) && Directory.Exists(staged))
                {
                    var recovered = TrainingFiles(review, review.ImagePaths).Select(p => Path.Combine(staged, Path.GetFileName(p))).ToArray();
                    if (recovered.Length == (Segmentation(review) ? 2 : 1) && recovered.All(File.Exists)) sources = recovered;
                }
                ValidateTrainingFiles(review, sources);
                Directory.CreateDirectory(staged);
                var stagedFiles = new List<string>();
                foreach (var source in sources)
                {
                    token.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(source);
                    if (name.StartsWith(id + "_", StringComparison.OrdinalIgnoreCase)) name = name[(id.Length + 1)..];
                    var target = Path.Combine(staged, name);
                    if (Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    { stagedFiles.Add(target); continue; }
                    using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                    { await input.CopyToAsync(output, token); output.Flush(true); }
                    stagedFiles.Add(target);
                }
                var now = sample.CollectedAt ?? _now();
                batch.FirstCollected = batch.FirstCollected is null || now < batch.FirstCollected ? now : batch.FirstCollected;
                batch.LastCollected = batch.LastCollected is null || now > batch.LastCollected ? now : batch.LastCollected;
                // Re-home every active owned file when the date range grows. Journal each move first.
                foreach (var other in batch.Samples.Where(x => x.State == "Ready"))
                {
                    var targets = other.Files.Select(path => Path.Combine(Folder(batch, other), Path.GetFileName(path))).ToList();
                    if (!targets.SequenceEqual(other.Files, StringComparer.OrdinalIgnoreCase))
                    {
                        other.ObsoleteFiles.AddRange(other.Files);
                        other.Files = targets;
                        Write(batches);
                        RecoverFiles(other);
                    }
                }
                sample.Files = stagedFiles.Select(path => Path.Combine(Folder(batch, sample),
                    batch.Crop.Equals("SEPA", StringComparison.OrdinalIgnoreCase) ? $"{id}_{Path.GetFileName(path)}" : Path.GetFileName(path))).ToList();
                Write(batches);
                Directory.CreateDirectory(Folder(batch, sample));
                for (var i = 0; i < stagedFiles.Count; i++) File.Move(stagedFiles[i], sample.Files[i], true);
                Cleanup(sample);
                sample.CollectedAt = now;
                sample.State = "Ready";
                sample.Error = "";
                Write(batches);
                return "Collected";
            }
            catch (Exception exception)
            {
                sample.State = "Failed";
                var successful = batch.Samples.Where(s => s.State == "Ready" && s.CollectedAt is not null).Select(s => s.CollectedAt!.Value).ToArray();
                batch.FirstCollected = successful.Length == 0 ? null : successful.Min();
                batch.LastCollected = successful.Length == 0 ? null : successful.Max();
                sample.Error = exception.Message;
                Write(batches);
                return "Collection failed: " + exception.Message;
            }
        }
        finally { _gate.Release(); }
    }
    private string Folder(TrainingBatch batch, TrainingSample sample)
    {
        var folder = Path.Combine(_root, Safe(batch.Product), Safe(batch.Crop), Safe(batch.Polarity),
            batch.CreatedAt.Year.ToString(), batch.Id, batch.Range, Safe(sample.Review.FinalClass));
        return batch.Crop.Equals("SEPA", StringComparison.OrdinalIgnoreCase) ? folder : Path.Combine(folder, sample.Id);
    }
    private void RecoverFiles(TrainingSample sample)
    {
        foreach (var target in sample.Files.Where(path => !File.Exists(path)))
        {
            var old = sample.ObsoleteFiles.FirstOrDefault(path => Path.GetFileName(path) == Path.GetFileName(target) && File.Exists(path));
            if (old is null) throw new IOException("An owned collection file is missing; retry its review.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(Owned(old), Owned(target), true);
        }
        Cleanup(sample);
    }
    private string Owned(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Collection manifest path is outside the collection root.");
        return full;
    }
    private void Cleanup(TrainingSample sample)
    {
        foreach (var path in sample.ObsoleteFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            if (!sample.Files.Contains(path, StringComparer.OrdinalIgnoreCase) && File.Exists(Owned(path))) File.Delete(path);
        foreach (var path in sample.ObsoleteFiles)
        {
            var directory = Path.GetDirectoryName(Owned(path));
            while (directory is not null && !directory.Equals(_root, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, false);
                directory = Path.GetDirectoryName(directory);
            }
        }
        sample.ObsoleteFiles.Clear();
    }
    public static IEnumerable<string> TrainingFiles(DlngReviewRecord review, IEnumerable<string> paths) =>
        paths.Where(p => Segmentation(review) || !p.EndsWith("_ActiveMap.jpg", StringComparison.OrdinalIgnoreCase));
    public static void ValidateTrainingFiles(DlngReviewRecord review, IReadOnlyList<string> paths)
    {
        if (Segmentation(review)) { ValidatePair(paths); return; }
        if (paths.Count != 1 || !paths[0].Contains("_SourceMap", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(paths[0]) || new FileInfo(paths[0]).Length == 0)
            throw new IOException("A complete classification source image is required.");
    }
    public static void ValidatePair(IReadOnlyList<string> paths)
    {
        var unique = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (unique.Length != 2) throw new IOException("A complete source/overlay or source/mask pair is required.");
        var names = unique.Select(InspectionIdentity.FileName).ToArray();
        bool Has(string value) => names.Any(n => n.Contains(value, StringComparison.OrdinalIgnoreCase));
        var classification = Has("_SourceMap") && Has("_ActiveMap");
        var segmentation = names.Any(n => n.Contains("_SourceImg", StringComparison.OrdinalIgnoreCase) && !n.Contains("mask", StringComparison.OrdinalIgnoreCase))
            && names.Any(n => n.Contains("mask", StringComparison.OrdinalIgnoreCase) || (n.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && n.Contains("_SourceImg", StringComparison.OrdinalIgnoreCase)));
        if (!classification && !segmentation) throw new IOException("The crop pair is missing a source, overlay, or mask.");
        var stems = unique.Select(InspectionIdentity.PairKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (stems.Length != 1) throw new IOException("The files do not share a crop pair identity.");
        if (unique.Any(p => !File.Exists(p) || new FileInfo(p).Length == 0))
            throw new IOException("A crop pair is missing or incomplete.");
    }
    public async Task MarkTrainedAsync(string id, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var batches = Read();
            var batch = batches.Single(x => x.Id == id);
            if (batch.TrainedAt is not null) return;
            if (batch.Pending != 0) throw new InvalidOperationException("Retry or explicitly exclude pending/failed samples before marking trained.");
            foreach (var sample in batch.Samples.Where(x => x.State == "Ready"))
            {
                RecoverFiles(sample);
                ValidateTrainingFiles(sample.Review, sample.Files);
            }
            if (batch.NewSamples == 0) throw new InvalidOperationException("This batch has no collected samples.");
            batch.TrainedAt = _now();
            Write(batches);
        }
        finally { _gate.Release(); }
    }
    public async Task RecoverAsync(IEnumerable<DlngReviewRecord> decisions, CancellationToken token = default)
    {
        // Only explicit selections from the review store can initiate collection.
        foreach (var review in decisions.GroupBy(ReviewSemantics.SampleId).Select(g => g.OrderByDescending(r => r.UpdatedAt).First()))
            if (review.IncludeInTraining || (await LoadAsync(token)).Any(b => b.Samples.Any(s => s.Id == ReviewSemantics.SampleId(review))))
                await ApplyAsync(review, token);
    }
    public async Task ExcludeAsync(string batchId, string sampleId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var batches = Read();
            var batch = batches.Single(x => x.Id == batchId);
            if (batch.TrainedAt is not null) throw new InvalidOperationException("Trained batches are immutable.");
            var sample = batch.Samples.Single(x => x.Id == sampleId);
            sample.State = "Excluded";
            sample.ObsoleteFiles.AddRange(sample.Files);
            sample.Files.Clear();
            Write(batches);
            Cleanup(sample);
            Write(batches);
        }
        finally { _gate.Release(); }
    }
}
