using KickoutMonitor.Domain;
using KickoutMonitor.Infrastructure;
using Xunit;
namespace KickoutMonitor.Tests;
public sealed class SharedTrainingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SharedTrainingTests", Guid.NewGuid().ToString("N"));
    private TrainingCollectionService Collection => new(new AppStorage(_root));
    private TrainingInput Irs(string cell = "CELL", string line = "1-1(-)", string crop = "SEPA", string label = "Overkill", DateTime? day = null)
    {
        Directory.CreateDirectory(_root);
        var seg = crop == "SEPA"; var stem = cell + (line.Contains('+') ? "_01-1_CA_" : "_01-1_AN_") + "120000_UPPER_" + crop;
        var paths = new[] { Path.Combine(_root, stem + (seg ? "_SourceImg.jpg" : "_SourceMap.jpg")), Path.Combine(_root, stem + (seg ? "_SourceImg_mask.png" : "_ActiveMap.jpg")) };
        foreach (var p in paths) File.WriteAllBytes(p, [1, 2, 3]);
        var at = day ?? new DateTime(2026, 9, 23, 12, 0, 0);
        var item = new IrsDatasetItem("IRS-" + cell, "ROW-" + cell, line, at, cell, "TOP", crop, crop, "NG", paths, [label], false);
        var decision = new IrsDatasetDecision(item.Key, item.SourceReviewKey, line, at, cell, crop, "NG", [label], false, DateTimeOffset.Now);
        return TrainingInput.FromIrs(item, decision, VisionMasterSettings.CreateDefault())!;
    }
    [Fact]
    public async Task IrsAndDlngShareOneOwnerAndConflictDoesNotOverwrite()
    {
        var irs = Irs(); var dlng = irs.Review with { ItemKey = "DLNG", Side = "UPPER" };
        await Collection.AddToCurrentBatchAsync([dlng]);
        await Collection.AddSamplesAsync([irs]);
        var batch = Assert.Single(await Collection.LoadAsync());
        var sample = Assert.Single(batch.Samples);
        Assert.Contains("IRS", sample.Origins); Assert.Contains("DLNG", sample.Origins);
        Assert.Equal(1, batch.TotalSamples);
        await Collection.QueueSelectedAsync([dlng]);
        Assert.Single((await Collection.LoadAsync()).Single().Samples);
        var message = await Collection.AddSamplesAsync([irs with { Review = irs.Review with { FinalClass = "Real" } }]);
        Assert.Contains("1 class conflicts", message);
        Assert.Equal("Overkill", (await Collection.LoadAsync()).Single().Samples.Single().Review.FinalClass);
    }
    [Fact]
    public async Task OlderAndNewerDatesPoolAndTrainedStartsIndependentBatch()
    {
        await Collection.AddSamplesAsync([Irs()]);
        await Collection.AddSamplesAsync([Irs("OLD", day: new(2026, 8, 28)), Irs("NEW", day: new(2026, 9, 29))]);
        var batch = Assert.Single(await Collection.LoadAsync()); Assert.Equal(3, batch.TotalSamples);
        var folder = await Collection.GenerateDatasetAsync(batch.Id);
        Assert.Equal(6, Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Count(p => p.EndsWith(".jpg") || p.EndsWith(".png")));
        await Collection.AddSamplesAsync([Irs()]);
        var batches = await Collection.LoadAsync();
        Assert.Equal(2, batches.Count); Assert.Equal(3, batches.Single(b => b.TrainedAt is not null).TotalSamples);
        Assert.Equal(folder, await Collection.GenerateDatasetAsync(batch.Id));
    }
    [Fact]
    public async Task MissingPairRetriesAndClassificationExcludesActivemap()
    {
        var input = Irs(); var missing = input.Review.ImagePaths[1]; File.Delete(missing);
        await Collection.AddSamplesAsync([input]);
        Assert.Equal(0, (await Collection.LoadAsync()).Single().TotalSamples);
        File.WriteAllBytes(missing, [4, 5, 6]);
        await Collection.AddSamplesAsync([input]); Assert.Equal(1, (await Collection.LoadAsync()).Single().TotalSamples);
        await Collection.AddSamplesAsync([Irs("A", "1-1(-)", "Crop_B", "01_OK"), Irs("B", "1-1(+)", "Crop_B", "01_OK")]);
        var batches = await Collection.LoadAsync(); Assert.Equal(3, batches.Count);
        foreach (var b in batches.Where(b => b.Crop == "Crop_B"))
        {
            Assert.Single(b.Samples.Single().Files); Assert.DoesNotContain("ActiveMap", b.Samples.Single().Files.Single());
            var folder = await Collection.GenerateDatasetAsync(b.Id);
            Assert.Empty(Directory.GetDirectories(Path.Combine(folder, "01_OK")));
        }
    }
    [Fact]
    public async Task IrsActiveReclassificationMovesOnlyItsOwnFiles()
    {
        var a = Irs("A"); var b = Irs("B");
        await Collection.AddSamplesAsync([a, b]);
        var initial = (await Collection.LoadAsync()).Single();
        var untouched = initial.Samples.Single(x => x.Review.CellId == "B").Files.ToArray();
        await Collection.AddSamplesAsync([a with { Review = a.Review with { FinalClass = "Real" } }]);
        var batch = (await Collection.LoadAsync()).Single();
        Assert.Equal("Real", batch.Samples.Single(x => x.Review.CellId == "A").Review.FinalClass);
        Assert.Equal(untouched, batch.Samples.Single(x => x.Review.CellId == "B").Files);
        Assert.All(untouched, f => Assert.True(File.Exists(f)));
    }
    [Fact]
    public async Task ConcurrentIrsDecisionsAllPersistAndNoNeedDoesNotCollect()
    {
        var service = new IrsDatasetService(new AppStorage(_root));
        var inputs = Enumerable.Range(0, 12).Select(i => Irs("C" + i)).ToArray();
        var items = inputs.Select(i => new IrsDatasetItem(i.Review.ItemKey, i.Review.ItemKey, i.Review.LinePolarity,
            i.Review.InspectedAt, i.Review.CellId, "TOP", "SEPA", "SEPA", "Segmentation", i.Review.ImagePaths, ["Real", "Overkill"], false)).ToArray();
        await Task.WhenAll(items.Select(x => service.SaveDecisionAsync(x, ["Overkill"], false, default)));
        var decisions = await service.LoadDecisionsAsync(default);
        Assert.Equal(12, decisions.Count);
        var noNeed = decisions[items[0].Key] with { NoNeedToRetrain = true };
        Assert.Null(TrainingInput.FromIrs(items[0], noNeed, VisionMasterSettings.CreateDefault()));
        Assert.Null(TrainingInput.FromIrs(items[0] with { IsNeedToSimulate = true }, decisions[items[0].Key], VisionMasterSettings.CreateDefault()));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
