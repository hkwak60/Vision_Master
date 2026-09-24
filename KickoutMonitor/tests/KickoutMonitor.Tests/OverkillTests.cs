using System.Text.Json;
using KickoutMonitor.Domain;
using KickoutMonitor.Infrastructure;
using Xunit;

namespace KickoutMonitor.Tests;

public sealed class OverkillTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OverkillTests", Guid.NewGuid().ToString("N"));
    private AppStorage Storage => new(Path.Combine(_root,"local"));
    private DateTimeOffset _now = new(2026,12,31,23,0,0,TimeSpan.Zero);
    private TrainingCollectionService Collection => new(Storage,()=>_now);
    private DlngReviewRecord Review(string cell="C", string crop="SEPA", string model="E81C", string polarity="Anode", string line="1-1(-)", string label="Overkill", bool selected=true)
    {
        var folder=Path.Combine(_root,"sources",model,line,cell,crop);
        Directory.CreateDirectory(folder);
        var segmentation=crop=="SEPA";
        var stem=cell+"_01-1_AN_120000_UPPER_1_"+crop;
        var source=Path.Combine(folder,stem+(segmentation?"_SourceImg.jpg":"_SourceMap.jpg"));
        var other=Path.Combine(folder,stem+(segmentation?"_SourceImg_mask.png":"_ActiveMap.jpg"));
        File.WriteAllBytes(source,[1,2,3]);File.WriteAllBytes(other,[4,5,6]);
        return new(cell+"|"+model+"|"+line+"|"+crop,line,line,new(2026,9,20,12,0,0),cell,"DLNG",crop,"UPPER",crop,
            segmentation?"Segmentation":"03_NG_TORN",label,false,[source,other],_now,
            IncludeInTraining:selected,ProductModel:model,ModelKind:segmentation?DlngModelKind.Segmentation:DlngModelKind.Classification,
            TrainingPolarity:polarity);
    }
    [Theory]
    [InlineData("SEPA_SHOULDER")]
    [InlineData("BEAD")]
    public async Task AllSegmentationExportsAreFlatAndKeepCollidingPairNames(string crop)
    {
        var source = Review("SAME");
        var a = source with { CropFolder = crop };
        var b = a with { MachineId = "1-2-an", LinePolarity = "1-2(-)", ItemKey = "other-machine" };
        var real = a with { InspectedAt = a.InspectedAt.AddMinutes(20), ItemKey = "rework", FinalClass = "Real" };
        await Collection.ApplyAsync(a); await Collection.ApplyAsync(b); await Collection.ApplyAsync(real);
        var batch = Assert.Single(await Collection.LoadAsync());
        var folder = await Collection.GenerateDatasetAsync(batch.Id);
        Assert.Equal(4, Directory.GetFiles(Path.Combine(folder, "Overkill")).Length);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(folder, "Real")).Length);
        Assert.Empty(Directory.GetDirectories(Path.Combine(folder, "Overkill")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(folder, "Real")));
        foreach (var pair in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".json"))
            .GroupBy(f => Path.GetFileName(f).Split('_')[0]))
            TrainingCollectionService.ValidatePair(pair.ToArray());
        Assert.Equal(folder, await Collection.GenerateDatasetAsync(batch.Id));
    }
    [Fact]
    public async Task TrainedLegacySegmentationCanRegenerateFlatWithoutChangingFrozenExport()
    {
        var source = Review() with { CropFolder = "SEPA_SHOULDER" };
        await Collection.ApplyAsync(source);
        var batch = Assert.Single(await Collection.LoadAsync());
        var folder = await Collection.GenerateDatasetAsync(batch.Id);
        var manifestPath = Path.Combine(folder, ".batch-export.json");
        var manifest = JsonSerializer.Deserialize<BatchExportManifest>(await File.ReadAllTextAsync(manifestPath))!;
        var legacyFiles = manifest.Files.Select(f => f with { RelativePath = Path.Combine(f.FinalClass, f.SampleId, Path.GetFileName(f.RelativePath)) }).ToArray();
        for (var i = 0; i < legacyFiles.Length; i++)
        {
            var destination = Path.Combine(folder, legacyFiles[i].RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(Path.Combine(folder, manifest.Files[i].RelativePath), destination);
        }
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new BatchExportManifest(batch.Id, legacyFiles)));
        var trainedAt = Assert.Single(await Collection.LoadAsync()).TrainedAt;
        var flattened = await Collection.GenerateDatasetAsync(batch.Id);
        Assert.NotEqual(folder, flattened);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(flattened, "Overkill")).Length);
        Assert.Empty(Directory.GetDirectories(Path.Combine(flattened, "Overkill")));
        Assert.All(legacyFiles, f => Assert.True(File.Exists(Path.Combine(folder, f.RelativePath))));
        Assert.Equal(trainedAt, Assert.Single(await Collection.LoadAsync()).TrainedAt);
        Assert.Equal(flattened, await Collection.GenerateDatasetAsync(batch.Id));
    }
    [Fact]
    public async Task BatchDatasetUsesImageDatesAndFreezesOnlyAfterCompleteExport()
    {
        var a = Review("A") with { InspectedAt = new(2026,12,31,23,0,0) };
        var b = Review("B") with { InspectedAt = new(2027,1,2,6,0,0) };
        await Collection.ApplyAsync(a); await Collection.ApplyAsync(b);
        var batch = Assert.Single(await Collection.LoadAsync());
        var path = await Collection.GenerateDatasetAsync(batch.Id);
        Assert.Equal("1231_0102", Path.GetFileName(path));
        Assert.Contains(Path.Combine("E81C","SEPA","shared","2026",batch.Id), path);
        Assert.Equal(4, Directory.GetFiles(Path.Combine(path, "Overkill")).Length);
        Assert.Empty(Directory.GetDirectories(Path.Combine(path, "Overkill")));
        var frozen = Assert.Single(await Collection.LoadAsync());
        Assert.NotNull(frozen.TrainedAt); Assert.Equal(0, frozen.NewSamples); Assert.Equal(path, frozen.DatasetFolder);
        Assert.Equal(path, await Collection.GenerateDatasetAsync(batch.Id));
        await Collection.ApplyAsync(a with { FinalClass = "Real" });
        Assert.Equal(path, await Collection.GenerateDatasetAsync(batch.Id));
        Assert.Equal(4, Directory.GetFiles(Path.Combine(path, "Overkill")).Length);
        await Collection.ApplyAsync(Review("NEXT"));
        Assert.Equal(2, (await Collection.LoadAsync()).Count);
    }
    [Fact]
    public async Task FailedAndCancelledExportsDoNotTrainTheBatch()
    {
        var a = Review();
        await Collection.ApplyAsync(a);
        var batch = Assert.Single(await Collection.LoadAsync());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collection.GenerateDatasetAsync(batch.Id, cancel.Token));
        Assert.Null(Assert.Single(await Collection.LoadAsync()).TrainedAt);
        File.Delete(batch.Samples[0].Files[0]);
        await Assert.ThrowsAsync<IOException>(() => Collection.GenerateDatasetAsync(batch.Id));
        Assert.Null(Assert.Single(await Collection.LoadAsync()).TrainedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collection.GenerateDatasetAsync(batch.Id));
    }
    [Fact]
    public async Task ExportPublicationRecoversBeforeTrainedStateWrite()
    {
        await Collection.ApplyAsync(Review());
        var batch = Assert.Single(await Collection.LoadAsync());
        var manifest = Path.Combine(Storage.Root,"Training","batches.json");
        var before = await File.ReadAllTextAsync(manifest);
        var folder = await Collection.GenerateDatasetAsync(batch.Id);
        // Simulate interruption after the atomic dataset publication but before persisting TrainedAt.
        await File.WriteAllTextAsync(manifest, before);
        Assert.Equal(folder, await Collection.GenerateDatasetAsync(batch.Id));
        Assert.NotNull(Assert.Single(await Collection.LoadAsync()).TrainedAt);
        Assert.Equal(3, Directory.GetFiles(folder,"*",SearchOption.AllDirectories).Length); // two images and manifest
    }
    [Theory]
    [InlineData("01_OK","Overkill")]
    [InlineData("02_OK_NG","Overkill")]
    [InlineData("03_NG_TORN","Real")]
    [InlineData("QNG","Real")]
    [InlineData("Real","Real")]
    [InlineData("Overkill","Overkill")]
    [InlineData("No Need to Retrain","Unknown")]
    [InlineData("Saved","Unknown")]
    public void SharedClassColors(string label,string expected)=>Assert.Equal(expected,ReviewSemantics.Tone(label));

    [Fact]
    public async Task NotDlngIsSeparateFromRealOverkillAndCannotBeCollected()
    {
        var r = Review(label: ReviewSemantics.NotDlng);
        Assert.Equal("Unknown", ReviewSemantics.Tone(r.FinalClass));
        Assert.Equal(ReviewSemantics.NotDlng, ReviewSemantics.Outcome(r));
        Assert.False(ReviewSemantics.CanTrain(r.FinalClass));
        var metric = Assert.Single(OverkillHistoryService.DlngMetrics([r], []));
        Assert.Equal(0, metric.Reviewed); Assert.Equal(0, metric.Overkill); Assert.Equal(0, metric.Unknown);
        var day = OverkillTrendService.ProductionDay(r.InspectedAt);
        Assert.All(OverkillTrendService.Dlng([r], day, day).Points, p => Assert.Null(p.Count));
        await Collection.ApplyAsync(r with { FinalClass = "Real" });
        Assert.Equal(1, Assert.Single(await Collection.LoadAsync()).NewSamples);
        await Collection.ApplyAsync(r);
        Assert.Equal(0, Assert.Single(await Collection.LoadAsync()).NewSamples);
        var store = new JsonDlngReviewStore(Storage);
        await store.SaveAsync(r, default);
        Assert.Equal("Not DLNG", (await store.LoadAsync(default))[r.ItemKey].FinalClass);
    }
    [Fact]
    public void OutcomesAndUnknownDenominatorsAreIndependentOfSelection()
    {
        var r=Review(crop:"Crop_B",label:"01_OK",selected:false);
        var records=new[]{r,r with {ItemKey="2",FinalClass="04_NG_PARTICLE"},r with {ItemKey="3",SourceClass="01_OK",FinalClass="03_NG"},
            r with {ItemKey="4",FinalClass="No Need to Train"}};
        var metric=Assert.Single(OverkillHistoryService.DlngMetrics(records,[]));
        Assert.Equal(3,metric.Reviewed);Assert.Equal(1,metric.Overkill);Assert.Equal(1,metric.Corrections);Assert.Equal(1,metric.Missed);Assert.Equal(1,metric.Unknown);
        Assert.Equal(0,metric.Collected);
    }
    [Fact]
    public async Task SelectionIsIndependentAndMissingPairRetriesWithoutLosingJudgment()
    {
        var r=Review(selected:false);
        var store=new JsonDlngReviewStore(Storage);
        await store.SaveAsync(r,default);
        Assert.Equal("Not collected",await Collection.ApplyAsync(r));
        Assert.Empty(await Collection.LoadAsync());
        r=r with {IncludeInTraining=true};
        File.Delete(r.ImagePaths[1]);
        await store.SaveAsync(r,default);
        Assert.StartsWith("Collection failed:",await Collection.ApplyAsync(r));
        var batch=Assert.Single(await Collection.LoadAsync());
        Assert.Equal(0,batch.NewSamples);Assert.Equal(1,batch.Pending);
        Assert.Equal("Overkill",(await store.LoadAsync(default))[r.ItemKey].FinalClass);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Collection.MarkTrainedAsync(batch.Id));
        File.WriteAllBytes(r.ImagePaths[1],[4]);
        await Collection.RecoverAsync((await store.LoadAsync(default)).Values);
        Assert.Equal(1,Assert.Single(await Collection.LoadAsync()).NewSamples);
        Assert.True(File.Exists(Storage.DlngReviewFile+".overkill-v1.bak"));
    }
    [Fact]
    public async Task FlatSepaOwnershipReclassificationAndDeselectionDoNotTouchOtherAttempts()
    {
        var a=Review("A");var b=Review("B");
        await Collection.ApplyAsync(a);await Collection.ApplyAsync(b);
        var before=Assert.Single(await Collection.LoadAsync());
        var bfiles=before.Samples.Single(s=>s.Id==ReviewSemantics.SampleId(b)).Files.ToArray();
        Assert.All(before.Samples.SelectMany(s=>s.Files),p=>Assert.Equal("Overkill",new DirectoryInfo(Path.GetDirectoryName(p)!).Name));
        foreach (var sample in before.Samples) Assert.All(sample.Files,p=>Assert.StartsWith(sample.Id+"_",Path.GetFileName(p)));
        foreach(var path in a.ImagePaths)File.Delete(path);
        await Collection.ApplyAsync(a with {FinalClass="Real"});
        var after=Assert.Single(await Collection.LoadAsync());
        Assert.All(after.Samples.Single(s=>s.Id==ReviewSemantics.SampleId(a)).Files,p=>Assert.Equal("Real",new DirectoryInfo(Path.GetDirectoryName(p)!).Name));
        Assert.All(bfiles,p=>Assert.True(File.Exists(p)));
        await Collection.ApplyAsync(a with {IncludeInTraining=false});
        Assert.Equal(1,Assert.Single(await Collection.LoadAsync()).NewSamples);
        Assert.All(bfiles,p=>Assert.True(File.Exists(p)));
    }
    [Fact]
    public async Task PoolingDatesFrozenBatchesAndNewBatches()
    {
        var a=Review();await Collection.ApplyAsync(a);
        _now=_now.AddDays(1);
        var b=Review("B",polarity:"Cathode",line:"1-2(+)");
        await Collection.ApplyAsync(b);
        var batch=Assert.Single(await Collection.LoadAsync());
        Assert.Equal("1231_0101",batch.Range);Assert.Equal(2,batch.NewSamples);
        Assert.All(batch.Samples.SelectMany(s=>s.Files),p=>Assert.Contains("2026",p));
        var frozen=batch.Samples.SelectMany(s=>s.Files).ToDictionary(p=>p,File.ReadAllBytes);
        await Collection.MarkTrainedAsync(batch.Id);
        Assert.Equal(0,Assert.Single(await Collection.LoadAsync()).NewSamples);
        await Collection.ApplyAsync(a with {FinalClass="Real"});
        Assert.True(Assert.Single(await Collection.LoadAsync()).Samples.Single(s=>s.Id==ReviewSemantics.SampleId(a)).Superseded);
        foreach(var file in frozen)Assert.Equal(file.Value,File.ReadAllBytes(file.Key));
        await Collection.ApplyAsync(Review("NEXT"));
        Assert.Equal(2,(await Collection.LoadAsync()).Count);
        await Collection.ApplyAsync(Review("D",model:"E69B"));
        Assert.Equal(3,(await Collection.LoadAsync()).Count);
        await Collection.ApplyAsync(Review("E",crop:"Crop_B",label:"01_OK"));
        await Collection.ApplyAsync(Review("F",crop:"Crop_B",label:"01_OK",line:"1-2(-)"));
        await Collection.ApplyAsync(Review("G",crop:"Crop_B",label:"01_OK",polarity:"Cathode",line:"1-2(+)"));
        var cls=(await Collection.LoadAsync()).Where(b=>b.Crop=="Crop_B").ToArray();
        Assert.Equal(2,cls.Length);Assert.Equal(3,cls.Sum(b=>b.NewSamples));
    }
    [Fact]
    public async Task IdempotenceOfflineAndInterruptedRenameRecovery()
    {
        var r=Review();
        await Collection.ApplyAsync(r);await Collection.ApplyAsync(r);
        var batch=Assert.Single(await Collection.LoadAsync());
        Assert.Equal(1,batch.NewSamples);Assert.Equal(2,Assert.Single(batch.Samples).Files.Count);
        var sample=batch.Samples[0];var originals=sample.Files.ToArray();
        sample.ObsoleteFiles.AddRange(originals);
        sample.Files=originals.Select(p=>p.Replace("1231_1231","1231_0101")).ToList();
        var manifest=Path.Combine(Storage.Root,"Training","batches.json");
        File.WriteAllText(manifest,JsonSerializer.Serialize(new[]{batch}));
        foreach(var path in r.ImagePaths)File.Delete(path);
        var recovered=Assert.Single(await Collection.LoadAsync());
        Assert.Equal(1,recovered.NewSamples);
        Assert.All(recovered.Samples[0].Files,p=>Assert.True(File.Exists(p)));
        Assert.Equal("Collected",await Collection.ApplyAsync(r));
    }
    [Fact]
    public async Task RawAndUnknownCannotBecomeTrainingSamples()
    {
        Assert.Contains("Raw fallback",await Collection.ApplyAsync(Review() with {IsFallbackRaw=true}));
        Assert.Equal("Not collected",await Collection.ApplyAsync(Review() with {FinalClass="No Need to Train"}));
        Assert.Empty(await Collection.LoadAsync());
    }
    [Fact]
    public async Task HistoryReplacesLineSnapshotsAndPreservesGapsAndSummedDenominators()
    {
        var day=new DateOnly(2026,9,20);
        KickoutHistorySnapshot Snapshot(DateOnly d,int total,int overkill)=>new(d,d.ToDateTime(new(6,0)),d.AddDays(1).ToDateTime(new(6,0)),
            [new("1-1(-)","ALL",total,10,10-overkill,overkill,0,0,0,"E81C"),new("1-1(-)","SEPA",total,10,10-overkill,overkill,0,0,0,"E81C")],[],"fixture");
        await OverkillHistoryService.SaveSnapshotAsync(Storage,Snapshot(day,100,5));
        await OverkillHistoryService.SaveSnapshotAsync(Storage,Snapshot(day,100,2));
        await OverkillHistoryService.SaveSnapshotAsync(Storage,Snapshot(day.AddDays(2),900,8));
        var history=await new OverkillHistoryService(Storage,new JsonDlngReviewStore(Storage),Collection).LoadKickoutAsync();
        var metric=Assert.Single(OverkillHistoryService.KickoutMetrics(history));
        Assert.Equal(1000,metric.Inspected);Assert.Equal(10,metric.Overkill);Assert.Equal(20,metric.Reviewed);
        Assert.Contains("1.0",metric.PerInspected);
        Assert.Null(OverkillHistoryService.Daily(history,day,day.AddDays(2))[1].Overkill);
        // A reported day without this defect still contributes inspected cells.
        var zero=Snapshot(day.AddDays(3),1000,0) with {Rows=[new("1-1(-)","ALL",1000,0,0,0,0,0,0,"E81C")]};
        Assert.Equal(2000,Assert.Single(OverkillHistoryService.KickoutMetrics(history.Append(zero))).Inspected);
    }
    [Fact]
    public async Task WorkbookImportRecognizesExistingSummaryWithoutCollectingImages()
    {
        var day=new DateOnly(2026,9,20);
        var path=await new SummaryReportWriter(Storage).WriteAsync(day,day.ToDateTime(new(6,0)),day.AddDays(1).ToDateTime(new(6,0)),
            [new("1-1(-)","ALL",100,10,8,2,0,0,0),new("1-1(-)","SEPA",100,10,8,2,0,0,0)],[],default);
        var imported=OverkillHistoryService.ImportWorkbook(path)!;
        Assert.Equal(2,imported.Rows.Count);Assert.Equal(100,imported.Rows[1].TotalInspected);Assert.Equal(day,imported.Day);
        Assert.Empty(await Collection.LoadAsync());
    }
    [Fact]
    public async Task LockedSourcesFailAndStagedCopiesRecoverWithoutShares()
    {
        var review=Review("LOCK");
        using(var locked=new FileStream(review.ImagePaths[0],FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            Assert.StartsWith("Collection failed:",await Collection.ApplyAsync(review));
        Assert.Equal(0,Assert.Single(await Collection.LoadAsync()).NewSamples);
        var staging=Path.Combine(Storage.Root,"Training",".staging",ReviewSemantics.SampleId(review));
        Directory.CreateDirectory(staging);
        foreach(var path in review.ImagePaths){File.Copy(path,Path.Combine(staging,Path.GetFileName(path)),true);File.Delete(path);}
        Assert.Equal("Collected",await Collection.ApplyAsync(review));
        Assert.Equal(1,Assert.Single(await Collection.LoadAsync()).NewSamples);
    }
    [Fact]
    public async Task CancelledOperationCanRetryAndDoesNotDuplicateSamples()
    {
        var review=Review("CANCEL");
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Collection.ApplyAsync(review,cancel.Token));
        Assert.Empty(await Collection.LoadAsync());
        await Collection.ApplyAsync(review);
        await Collection.ApplyAsync(review);
        Assert.Single(Assert.Single(await Collection.LoadAsync()).Samples);
    }
    [Fact]
    public async Task ReReviewReplacesLegacyAliasesRatherThanDoubleCountingHistory()
    {
        var legacy=Review("LEGACY",selected:false) with { ItemKey="old|0",ProductModel="",ModelKind=null };
        var store=new JsonDlngReviewStore(Storage);
        await store.SaveAsync(legacy,default);
        var updated=legacy with { ItemKey="current|PAIR:key",ProductModel="E81C",ModelKind=DlngModelKind.Segmentation,FinalClass="Real" };
        await store.SaveAsync(updated,default);
        var records=await new JsonDlngReviewStore(Storage).LoadAsync(default);
        Assert.Single(records);
        Assert.Equal("Real",Assert.Single(records.Values).FinalClass);
        Assert.Contains("old|0",File.ReadAllText(Storage.DlngReviewFile+".overkill-v1.bak"));
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
