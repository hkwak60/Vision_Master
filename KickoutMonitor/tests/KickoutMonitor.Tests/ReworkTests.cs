using System.Text.Json;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;
using KickoutMonitor.Infrastructure;

namespace KickoutMonitor.Tests;

public sealed class ReworkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VisionMasterReworkTests", Guid.NewGuid().ToString("N"));
    private readonly WeldingMachine _machine = new("1-1-an", "1-1", Polarity.Anode, "unused", ['F']);
    private static readonly DateOnly Day = new(2026, 9, 16);
    private const string Cell = "D69GX05JJ1";
    private string Csv => Path.Combine(_root, "sample.csv");
    private AppStorage Storage => new(Path.Combine(_root, "output"));
    public ReworkTests()
    {
        Directory.CreateDirectory(_root);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rework.csv"), Csv);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private DlngQueueService Queue() => new(new Locator(Csv), new Snapshot(),
        new DlngCsvReader(new Shares(_root)), new DlngCropLocator(new Shares(_root)));
    private async Task<IReadOnlyList<DlngReviewItem>> Load() => await Queue().LoadAsync(_machine, Day, null, CancellationToken.None);
    private string Crop(string time, string side = "UPPER", string folder = "SEPA", string? suffix = null, string? cell = null, string day = "16")
    {
        var root = Path.Combine(_root, "Files", "Image", "E81C", "2026", "09", day, "Mavin", folder);
        Directory.CreateDirectory(root);
        var name = $"{cell ?? Cell}_01-1_AN_{time}_{side}_0_{suffix ?? "SEPA DL_0_0_0_SourceImg.jpg"}";
        var path = Path.Combine(root, name);
        File.WriteAllText(path, time);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        return path;
    }
    private void SepaPair(string time, string side = "UPPER", string day = "16")
    {
        Crop(time, side, day: day);
        Crop(time, side, suffix: "SEPA DL_0_0_0_SourceImg_mask.png", day: day);
    }

    private void PrepareBFixture()
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "b-crops.csv"), Csv, true);
        var names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "b-files.json")))!;
        foreach (var name in names)
        {
            var path = Path.Combine(_root, "Files", "Image", "E81C", "2026", "09", "20", "Mavin", "Crop_B", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "crop fixture");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        }
    }


    [Theory]
    [InlineData("SS_Left")]
    [InlineData("SS_Right")]
    [InlineData("Tab_Burr_LL")]
    [InlineData("Tab_Burr_LB")]
    [InlineData("Tab_Burr_RR")]
    [InlineData("Tab_Burr_RB")]
    public async Task AdditionalDefectsCollectOnlySelectedSepaModelsAndOwnAttempt(string defect)
    {
        File.WriteAllText(Csv, File.ReadAllText(Csv).Replace("SEPA", defect));
        foreach (var time in new[] { "111303", "113627" })
        {
            SepaPair(time);
            Crop(time, folder: "SEPA_SHOULDER", suffix: "SEPA SHOULDER_0_0_0_SourceImg.jpg");
            Crop(time, folder: "SEPA_SHOULDER", suffix: "SEPA SHOULDER_0_0_0_SourceImg_mask.png");
        }
        var rows = await Queue().LoadAsync(_machine, Day, null, default,
            new HashSet<string>(["SEPA", "SEPA_SHOULDER"], StringComparer.OrdinalIgnoreCase));
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => {
            Assert.Equal(DlngModelKind.Segmentation, row.ModelKind);
            Assert.Equal(2, row.Images.Count);
            var expected = row.InspectedAt.Hour == 11 && row.InspectedAt.Minute == 13 ? "111303" : "113627";
            Assert.All(row.Images, image => Assert.Contains(expected, image.Path));
        });
        var singleModel = await Queue().LoadAsync(_machine, Day, null, default, new HashSet<string>(["SEPA"]));
        Assert.Equal(2, singleModel.Count);
        Assert.All(singleModel, row => Assert.Equal("SEPA", row.CropFolder));
        if (defect.StartsWith("Tab_Burr"))
            Assert.Contains(DlngRules.FindMappings(defect, VisionMasterSettings.CreateDefault().DlngRules),
                mapping => mapping.ModelKind == DlngModelKind.Classification && mapping.CropFolders.Contains("Crop_micro_tabside"));
        var settings = VisionMasterSettings.CreateDefault();
        Assert.Empty(DlngRules.FindMappings("Tab_Burr_LM", settings.DlngRules).Where(m => m.ModelKind == DlngModelKind.Segmentation));
        File.WriteAllText(Csv, File.ReadAllText(Csv).Replace(",NG,", ",OK,"));
        Assert.Empty(await Queue().LoadAsync(_machine, Day, null, default, new HashSet<string>(["SEPA", "SEPA_SHOULDER"])));
    }
    [Fact]
    public async Task QueueTimeFilterUsesJudgmentAndLeavesUnfilteredReportCallsUnchanged()
    {
        SepaPair("113627");
        var range = new QueueTimeRange(new(2026,9,16,11,36,26), new(2026,9,16,11,36,27));
        var dlng = Assert.Single(await Queue().LoadAsync(_machine, Day, null, CancellationToken.None, timeRange: range));
        Assert.Equal(new DateTime(2026,9,16,11,36,27), dlng.Inspection!.ImageAt);
        Assert.Equal(DlngModelKind.Segmentation, dlng.ModelKind);
        var kickout = new KickoutQueueService(new Locator(Csv), new Snapshot(), new WeldingKickoutCsvReader(new Shares(_root)));
        Assert.Single(await kickout.LoadAsync(_machine, Day, null, CancellationToken.None, range));
        Assert.Equal(2, (await kickout.LoadAsync(_machine, Day, null, CancellationToken.None)).Count);
        Assert.Equal(2, (await Load()).Count);
        var empty = new QueueTimeRange(new(2026,9,16,6,0,0), new(2026,9,16,7,0,0));
        Assert.Empty(await Queue().LoadAsync(_machine, Day, null, CancellationToken.None, timeRange: empty));
        Assert.Empty(await kickout.LoadAsync(_machine, Day, null, CancellationToken.None, empty));
        var invalid = new QueueTimeRange(empty.End, empty.Start);
        await Assert.ThrowsAsync<ArgumentException>(() => Queue().LoadAsync(_machine, Day, null, CancellationToken.None, timeRange: invalid));
    }

    [Fact]
    public async Task TimeFilterDoesNotHideCompetingInspectionFromCropOwnership()
    {
        var lines = File.ReadAllLines(Csv);
        File.AppendAllLines(Csv, [lines[2].Replace("11:36:26", "11:36:25")]);
        SepaPair("113626");
        var range = new QueueTimeRange(new(2026,9,16,11,36,26), new(2026,9,16,11,36,27));
        var row = Assert.Single(await Queue().LoadAsync(_machine, Day, null, CancellationToken.None, timeRange: range));
        Assert.Contains("Ambiguous", row.ResolutionMessage);
    }

    [Fact]
    public async Task NeighboringDayOwnershipBlocksCropWithoutAddingQueueRows()
    {
        var lines = File.ReadAllLines(Csv);
        File.WriteAllLines(Csv, [lines[0], lines[2].Replace("11:36:26", "23:59:59")
            .Replace("20260916_113627", "20260917_000000")]);
        var neighbor = Path.Combine(_root, "next-day.csv");
        File.WriteAllLines(neighbor, [lines[0], lines[2].Replace("20260916", "20260917")
            .Replace("11:36:26", "00:00:00").Replace("113627", "000001")]);
        SepaPair("000000", day: "17");
        var locator = new DatedLocator(new Dictionary<DateOnly, string> { [Day] = Csv, [Day.AddDays(1)] = neighbor });
        var queue = new DlngQueueService(locator, new Snapshot(), new DlngCsvReader(new Shares(_root)), new DlngCropLocator(new Shares(_root)));
        var row = Assert.Single(await queue.LoadAsync(_machine, Day, null, CancellationToken.None));
        Assert.Equal(16, row.InspectedAt.Day);
        Assert.Equal(DlngModelKind.FallbackRaw, row.ModelKind);
        Assert.Contains("Ambiguous", row.ResolutionMessage);
    }

    [Fact]
    public async Task SuppliedBFixtures_BothEligibleLowerRightPairsResolve()
    {
        PrepareBFixture();
        var machine = _machine with { Id = "1-1-ca", Polarity = Polarity.Cathode };
        var rows = await Queue().LoadAsync(machine, new(2026, 9, 20), null, CancellationToken.None);
        Assert.Equal(2, rows.Count);
        foreach (var (row, time) in rows.Zip(new[] { "000257", "000556" }))
        {
            Assert.Equal("LOWER", row.Side);
            Assert.Equal("B_R", row.JudgeDefect);
            Assert.Equal(DlngModelKind.Classification, row.ModelKind);
            Assert.Equal(2, row.Images.Count);
            Assert.All(row.Images, image => Assert.Contains($"_{time}_LOWER_2_B_R_", image.Path));
            Assert.Equal(InspectionIdentity.PairKey(row.Images[0].Path), InspectionIdentity.PairKey(row.Images[1].Path));
        }
        Assert.Equal("08_NG_CRITICAL", rows[0].SourceClass);
        Assert.Equal("04_NG_TORN", rows[1].SourceClass);
    }

    [Fact]
    public async Task IrsBCommit_UsesIntervalAndSavedValidationAcceptsTheSamePair()
    {
        PrepareBFixture();
        var machine = _machine with { Id = "1-1-ca", Polarity = Polarity.Cathode };
        var candidate = Irs() with { Key = "b-review", CellId = "B69JX0A2W6", LotId = "3A4FI191I1",
            ProducedAt = new(2026, 9, 20, 0, 2, 57), LinePolarity = "1-1(+)", CameraLocation = "LOWER" };
        var service = new IrsReviewCommitService(Storage, new Locator(Csv), new Shares(_root));
        await service.CommitAsync(new(machine, candidate,
            [new("B_R", "B R", "Crop_B", IrsSelectionKind.Crop, "Crop_B", "B_R")]), CancellationToken.None);
        var record = Assert.Single(await service.LoadRecordsAsync(CancellationToken.None));
        Assert.NotNull(record.Inspection!.CropConflicts);
        Assert.True(ReviewCompatibility.SavedImagesMatch(candidate with { Inspection = record.Inspection }, record));
        var copied = Directory.GetFiles(Storage.Root, "*Map.jpg", SearchOption.AllDirectories);
        Assert.Equal(2, copied.Length);
        Assert.All(copied, path => Assert.Contains("_000257_LOWER_2_B_R_", path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExcludedOkAttempt_BlocksOverlappingCropEvenAcrossLots(bool differentLot)
    {
        var lines = File.ReadAllLines(Csv);
        var headers = lines[0].Split(',');
        var other = lines[2].Replace("11:36:26", "11:36:25").Split(',');
        other[Array.IndexOf(headers, "JUDGE")] = "OK";
        if (differentLot)
            for (var i = 0; i < other.Length; i++) other[i] = other[i].Replace("3A4FI161I1", "OTHER-LOT");
        File.AppendAllLines(Csv, [string.Join(",", other)]);
        SepaPair("113626");
        var rows = await Load();
        Assert.Equal(2, rows.Count); // OK was indexed for ownership, not admitted.
        Assert.Equal(DlngModelKind.FallbackRaw, rows[1].ModelKind);
        Assert.Contains("Ambiguous", rows[1].ResolutionMessage);
    }

    [Fact]
    public void CropWindow_MidnightMillisecondsAndPerSideTimesRemainBounded()
    {
        var judged = new DateTime(2026, 9, 16, 23, 59, 59, 600);
        var context = InspectionIdentity.WithCropConflicts([
            new InspectionContext(_machine.Id, "E81C", "LOT", Cell, judged, judged.Date.AddDays(1), [])
        ])[0];
        Assert.True(InspectionIdentity.MatchesCrop($"{Cell}_01-1_AN_235959_UPPER_2_B_R_SourceMap.jpg", _machine, context, judged.Date));
        Assert.True(InspectionIdentity.MatchesCrop($"{Cell}_01-1_AN_000000_LOWER_2_B_R_SourceMap.jpg", _machine, context, judged.Date.AddDays(1)));
        Assert.False(InspectionIdentity.MatchesCrop($"{Cell}_01-1_AN_000001_LOWER_2_B_R_SourceMap.jpg", _machine, context, judged.Date.AddDays(1)));
        Assert.False(InspectionIdentity.MatchesCrop($"{Cell}_01-1_AN_235959_UPPER_2_B_R_SourceMap.jpg", _machine, context, judged.Date.AddDays(1)));
        Assert.Equal(2, InspectionIdentity.CropDates(context).Count());
    }

    [Fact]
    public async Task IntervalIncludesJudgmentTime_AndDuplicateRepresentationsDoNotBlockIt()
    {
        var lines = File.ReadAllLines(Csv);
        File.AppendAllLines(Csv, [lines[2]]);
        SepaPair("113626");
        var row = (await Load())[1];
        Assert.Equal(DlngModelKind.Segmentation, row.ModelKind);
        Assert.Empty(row.Inspection!.CropConflicts!);
        Assert.All(row.Images, image => Assert.Contains("_113626_", image.Path));
    }

    [Fact]
    public async Task CropFallback_ReportsTimestampMismatchWithoutSelectingNearbyAttempt()
    {
        SepaPair("113628");
        var row = (await Load())[1];
        Assert.Equal(DlngModelKind.FallbackRaw, row.ModelKind);
        Assert.Contains("expected 1-1(-) 11:36:26–11:36:27", row.ResolutionMessage);
        Assert.Contains("113628", row.ResolutionMessage);
        Assert.All(row.Images, image => Assert.Contains("113627", image.Path));
    }

    [Fact]
    public async Task CropFallback_DistinguishesMissingFolderCellAndSide()
    {
        var missing = (await Load())[1];
        Assert.Contains("folder missing or unavailable", missing.ResolutionMessage);
        Crop("113627", cell: "UNRELATED");
        var noCell = (await Load())[1];
        Assert.Contains("No crop files for this cell", noCell.ResolutionMessage);
        SepaPair("113627", "LOWER");
        var wrongSide = (await Load())[1];
        Assert.Contains("none for UPPER", wrongSide.ResolutionMessage);
    }

    [Fact]
    public async Task Irs_LowerCameraAlias_ResolvesLowerRawPaths()
    {
        var result = await new IrsRawImageLocator(new Shares(_root), new Locator(Csv))
            .FindAsync(_machine, Irs() with { CameraLocation = "LOWER" }, CancellationToken.None);
        Assert.Equal(3, result.NetworkPaths.Count);
        Assert.All(result.NetworkPaths, path => Assert.Contains("_EXT_1_", path));
    }

    [Fact]
    public async Task StandardFilenamesWithoutExtMarker_ResolveBothAttemptsAndRawPreviews()
    {
        File.WriteAllText(Csv, File.ReadAllText(Csv).Replace("_EXT_", "_"));
        SepaPair("111303"); SepaPair("113627");
        var rows = await Load();
        Assert.Equal(2, rows.Count);
        foreach (var row in rows)
        {
            Assert.Null(row.Inspection!.Issue);
            Assert.Equal(2, row.Images.Count);
        }
        var result = await new IrsRawImageLocator(new Shares(_root), new Locator(Csv))
            .FindAsync(_machine, Irs(), CancellationToken.None);
        Assert.Equal(3, result.NetworkPaths.Count);
        Assert.All(result.NetworkPaths, path =>
        {
            Assert.Contains("_113627_", path);
            Assert.DoesNotContain("_EXT_", path);
            Assert.DoesNotContain("overlay", path);
        });
        var kickout = new KickoutQueueService(new Locator(Csv), new Snapshot(), new WeldingKickoutCsvReader(new Shares(_root)));
        Assert.All(await kickout.LoadAsync(_machine, Day, null, CancellationToken.None),
            row => Assert.NotEmpty(row.PreviewImages));
    }

    [Fact]
    public async Task Sample_UsesImageTimestampNotJudgmentTimestamp_AndOnlyEligibleSide()
    {
        foreach (var time in new[] { "111303", "113627" })
            foreach (var side in new[] { "UPPER", "LOWER" }) SepaPair(time, side);
        SepaPair("113625"); // Outside the selected inspection window.
        Crop("113627", cell: Cell + "X");
        var rows = await Load();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new DateTime(2026, 9, 16, 11, 36, 26), rows[1].InspectedAt);
        Assert.Equal(new DateTime(2026, 9, 16, 11, 36, 27), rows[1].Inspection!.ImageAt);
        Assert.Equal(12, rows[1].Inspection!.ImagePaths.Count);
        foreach (var (item, time) in rows.Zip(new[] { "111303", "113627" }))
        {
            Assert.Equal("UPPER", item.Side);
            Assert.Equal(2, item.Images.Count);
            Assert.All(item.Images, image => Assert.Contains($"_{time}_UPPER_", image.Path));
            Assert.Equal(3, item.RawImages!.Count);
        }
    }

    [Fact]
    public async Task ClassificationPairs_StableWhenAnotherPairArrives_AndDoNotMixAttempts()
    {
        var candidate = (await Load())[1] with { JudgeDefect = "A_L" };
        foreach (var time in new[] { "111303", "113627" })
            foreach (var kind in new[] { "SourceMap", "ActiveMap" })
                Crop(time, folder: "Crop_A", suffix: $"A_L_CL03_NG_{kind}.jpg");
        var locator = new DlngCropLocator(new Shares(_root));
        var first = Assert.Single(await locator.ExpandAsync(_machine, candidate, null, CancellationToken.None));
        Assert.All(first.Images, x => Assert.Contains("_113627_", x.Path));
        foreach (var kind in new[] { "SourceMap", "ActiveMap" })
            Crop("113627", folder: "Crop_A", suffix: $"A_L_AAA_{kind}.jpg");
        locator.Reset();
        var second = await locator.ExpandAsync(_machine, candidate, null, CancellationToken.None);
        Assert.Equal(2, second.Count);
        Assert.Contains(second, x => x.Key == first.Key);
        Assert.Equal(2, second.Select(x => x.Key).Distinct().Count());
    }

    [Fact]
    public async Task Reload_SeesLateCrops_InsteadOfKeepingMissingIndex()
    {
        var queue = Queue();
        var first = await queue.LoadAsync(_machine, Day, null, CancellationToken.None);
        Assert.All(first, x => Assert.Equal(DlngModelKind.FallbackRaw, x.ModelKind));
        SepaPair("113627");
        var second = await queue.LoadAsync(_machine, Day, null, CancellationToken.None);
        Assert.Equal(DlngModelKind.Segmentation, second[1].ModelKind);
        Assert.Equal(DlngModelKind.FallbackRaw, second[0].ModelKind);
    }

    [Fact]
    public async Task DuplicateContinuationRowsCollapse_ButThirdAttemptIsRetained()
    {
        var lines = File.ReadAllLines(Csv);
        File.AppendAllLines(Csv, [lines[1], lines[2].Replace("11:36:26", "12:00:00").Replace("113627", "120001")]);
        Assert.Equal(3, (await Load()).Count);
    }

    [Fact]
    public async Task Eligibility_IsAppliedBeforeGrouping()
    {
        var lines = File.ReadAllLines(Csv);
        var third = lines[2].Replace("11:36:26", "12:00:00").Replace("113627", "120001").Split(',');
        third[Array.IndexOf(lines[0].Split(','), "JUDGE")] = "OK";
        File.AppendAllLines(Csv, [string.Join(",", third)]);
        Assert.Equal(2, (await Load()).Count);
        var kickout = new KickoutQueueService(new Locator(Csv), new Snapshot(), new WeldingKickoutCsvReader(new Shares(_root)));
        Assert.Equal(2, (await kickout.LoadAsync(_machine, Day, null, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task SameJudgmentTimeWithDifferentImageEvidence_DoesNotCollapse()
    {
        var lines = File.ReadAllLines(Csv);
        File.AppendAllLines(Csv, [lines[2].Replace("113627", "113628")]);
        var queue = new KickoutQueueService(new Locator(Csv), new Snapshot(), new WeldingKickoutCsvReader(new Shares(_root)));
        var items = await queue.LoadAsync(_machine, Day, null, CancellationToken.None);
        Assert.Equal(3, items.Count);
        Assert.Equal(3, items.Select(x => x.Key).Distinct().Count());
        Assert.Equal(2, items.Count(x => x.Key.Contains("|IMAGE:")));
    }

    [Fact]
    public async Task Midnight_UsesImageDateForCrops_WhileRetainingJudgmentDate()
    {
        var lines = File.ReadAllLines(Csv);
        File.WriteAllLines(Csv, [lines[0], lines[2].Replace("11:36:26", "23:59:59")
            .Replace("20260916_113627", "20260917_000000")]);
        SepaPair("000000", day: "17");
        var row = Assert.Single(await Load());
        Assert.Equal(16, row.InspectedAt.Day);
        Assert.Equal(17, row.Inspection!.ImageAt!.Value.Day);
        Assert.Equal(2, row.Images.Count);
        Assert.All(row.Images, x => Assert.Contains(Path.Combine("09", "17", "Mavin"), x.Path));
    }

    private IrsReviewCandidate Irs(DateTime? at = null, string name = "") => new("irs", "PACKAGE #1-1", "Welding Minus",
        "1-1(-)", at ?? new DateTime(2026, 9, 16, 11, 36, 26), "3A4FI161I1", Cell, "TOP", name, "NG", "SEPA", 1);
    [Fact]
    public async Task Irs_ExactJudgmentResolvesSecondAttempt_AndAmbiguityStaysUnresolved()
    {
        var locator = new IrsRawImageLocator(new Shares(_root), new Locator(Csv));
        var found = await locator.FindAsync(_machine, Irs(), CancellationToken.None);
        Assert.Equal(3, found.NetworkPaths.Count);
        Assert.All(found.NetworkPaths, p => Assert.Contains("113627", p));
        var lines = File.ReadAllLines(Csv);
        File.AppendAllLines(Csv, [lines[2].Replace("113627", "113628")]);
        locator.Reset();
        var ambiguous = await locator.FindAsync(_machine, Irs(), CancellationToken.None);
        Assert.Empty(ambiguous.NetworkPaths);
        Assert.Contains("Ambiguous", ambiguous.Message);
        var exact = await locator.FindAsync(_machine, Irs(name: Path.GetFileName(found.NetworkPaths[0])), CancellationToken.None);
        Assert.Equal(found.NetworkPaths, exact.NetworkPaths);
    }

    [Fact]
    public async Task Irs_ConflictingRawTimestamps_AreNotCombined()
    {
        var lines = File.ReadAllLines(Csv);
        var columns = lines[2].Split(',');
        columns[Array.IndexOf(lines[0].Split(','), "UPPER_IMAGE-PATH-1")] =
            columns[Array.IndexOf(lines[0].Split(','), "UPPER_IMAGE-PATH-1")].Replace("113627", "113628");
        File.WriteAllLines(Csv, [lines[0], string.Join(",", columns)]);
        var result = await new IrsRawImageLocator(new Shares(_root), new Locator(Csv)).FindAsync(_machine, Irs(), CancellationToken.None);
        Assert.Empty(result.NetworkPaths);
        Assert.Contains("Conflicting", result.Message);
    }

    [Fact]
    public async Task CancelledLookup_DoesNotPoisonNextLoad()
    {
        var resolver = new IrsRawImageLocator(new Shares(_root), new Locator(Csv));
        using var token = new CancellationTokenSource();
        token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.FindAsync(_machine, Irs(), token.Token));
        var found = await resolver.FindAsync(_machine, Irs(), CancellationToken.None);
        Assert.Equal(3, found.NetworkPaths.Count);
    }

    [Fact]
    public async Task UnavailableCsv_IsReportedWithoutGuessing()
    {
        File.Delete(Csv);
        var result = await new IrsRawImageLocator(new Shares(_root), new Locator(Csv)).FindAsync(_machine, Irs(), CancellationToken.None);
        Assert.Empty(result.NetworkPaths);
        Assert.Contains("Missing", result.Message);
    }

    [Fact]
    public async Task LegacyDlngPair_MigratesOnlyToTheSamePair_AndBacksUpStore()
    {
        SepaPair("111303"); SepaPair("113627");
        var item = (await Load())[1];
        var legacy = Record(item) with { ItemKey = item.Key[..item.Key.LastIndexOf('|')] + "|0" };
        Directory.CreateDirectory(Storage.Root);
        await File.WriteAllTextAsync(Storage.DlngReviewFile, JsonSerializer.Serialize(new[] { legacy }));
        var store = new JsonDlngReviewStore(Storage);
        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.True(loaded.ContainsKey(item.Key));
        await store.SaveAsync(Record(item), CancellationToken.None);
        Assert.True(File.Exists(Storage.DlngReviewFile + ".rework-v1.bak"));
        Assert.Contains("|0", File.ReadAllText(Storage.DlngReviewFile + ".rework-v1.bak"));
    }

    [Fact]
    public async Task LegacyMixedAttemptImages_AreRejectedForIrsDataset()
    {
        SepaPair("111303"); SepaPair("113627");
        var resolved = await new IrsRawImageLocator(new Shares(_root), new Locator(Csv)).FindAsync(_machine, Irs(), CancellationToken.None);
        var candidate = Irs() with { Inspection = resolved.Inspection, RawImagePaths = resolved.NetworkPaths };
        var saved = new[] { Crop("111303"), Crop("113627") };
        var review = new IrsReviewRecord(candidate.Key, _machine.Id, "1-1(-)", candidate.ProducedAt, Cell, "TOP",
            "NG", "SEPA", ["SEPA"], 0, 2, 0, Storage.Root, DateTimeOffset.Now, saved);
        Assert.False(ReviewCompatibility.SavedImagesMatch(candidate, review));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new IrsDatasetService(Storage)
            .BuildQueueAsync([candidate], [review], CancellationToken.None));
    }

    [Fact]
    public async Task Export_KeepsBothAttemptPairsAndSavedDecisionsSeparate()
    {
        SepaPair("111303"); SepaPair("113627");
        var items = await Load();
        var store = new JsonDlngReviewStore(Storage);
        foreach (var item in items)
        {
            var record = Record(item) with { IncludeInTraining = true };
            await store.SaveAsync(record, CancellationToken.None);
            Assert.Equal("Collected", await new TrainingCollectionService(Storage).ApplyAsync(record));
        }
        var generator = new DlngReportGenerator(Queue(), store, Storage);
        var export = await generator.GenerateDatasetFromItemsAsync(items, Day, Day, null, CancellationToken.None);
        var files = Directory.GetFiles(export.OutputFolder, "*.*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".json")).ToArray();
        Assert.Equal(4, files.Length);
        Assert.Single(files.Select(Path.GetDirectoryName).Distinct());
        Assert.Equal(2, (await new JsonDlngReviewStore(Storage).LoadAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task BothSepaExportPathsAreFlatOfflineAndRemoveOnlyDeselectedOwnedPair()
    {
        SepaPair("111303"); SepaPair("113627");
        var items = await Load();
        var store = new JsonDlngReviewStore(Storage);
        var collection = new TrainingCollectionService(Storage);
        foreach(var item in items)
        {
            var review=Record(item) with { IncludeInTraining=true };
            await store.SaveAsync(review,default);
            Assert.Equal("Collected",await collection.ApplyAsync(review));
            foreach(var image in item.Images)File.Delete(image.Path); // Test-owned fixture simulates an unavailable share.
        }
        var generator=new DlngReportGenerator(Queue(),store,Storage);
        var report=await generator.GenerateFromItemsAsync(items,Day,null,default);
        var export=await generator.GenerateDatasetFromItemsAsync(items,Day,Day,null,default);
        string[] Images(string root)=>Directory.GetFiles(root,"*",SearchOption.AllDirectories).Where(p=>p.EndsWith(".jpg")||p.EndsWith(".png")).ToArray();
        var reportRoot=Path.Combine(report.OutputFolder,"Dataset");
        Assert.Equal(4,Images(reportRoot).Length);
        Assert.All(Images(reportRoot),p=>Assert.Equal("Real",new DirectoryInfo(Path.GetDirectoryName(p)!).Name));
        Assert.All(Images(export.OutputFolder),p=>Assert.Equal("Real",new DirectoryInfo(Path.GetDirectoryName(p)!).Name));
        var excluded=Record(items[0]) with { IncludeInTraining=false };
        await store.SaveAsync(excluded,default);await collection.ApplyAsync(excluded);
        var unowned=Path.Combine(Path.GetDirectoryName(Images(export.OutputFolder).First())!,"user-image.jpg");
        File.WriteAllBytes(unowned,[8]);
        await generator.GenerateDatasetFromItemsAsync(items,Day,Day,null,default);
        var regenerated=await generator.GenerateFromItemsAsync(items,Day,null,default);
        Assert.Equal(2,Images(reportRoot).Length);
        Assert.Equal(3,Images(export.OutputFolder).Length);Assert.True(File.Exists(unowned));
        Assert.Equal(2,regenerated.Rows.Sum(r=>r.Count)); // Reports retain unselected judgments.
    }

    [Fact]
    public void Grouping_UsesLotAndMachine_NotCalendarDay_AndCountsInspections()
    {
        var date = new DateTime(2026, 9, 16, 23, 59, 59);
        var rows = new[] {
            (Machine:"M1", Lot:"L1", Cell:"C", Time:date, Pending:false),
            (Machine:"M1", Lot:"L2", Cell:"C", Time:date.AddSeconds(1), Pending:false),
            (Machine:"M2", Lot:"L1", Cell:"C", Time:date.AddSeconds(2), Pending:false),
            (Machine:"M1", Lot:"L1", Cell:"C", Time:date.AddSeconds(3), Pending:true),
            (Machine:"M1", Lot:"", Cell:"C", Time:date.AddSeconds(4), Pending:false),
            (Machine:"M1", Lot:"", Cell:"C", Time:date.AddSeconds(5), Pending:false)
        };
        string Group((string Machine,string Lot,string Cell,DateTime Time,bool Pending) row) =>
            InspectionIdentity.Group(row.Machine,row.Lot,row.Cell,row.Time.ToString("O"));
        var sorted = InspectionIdentity.GroupQueue(rows, Group, x => x.Time, x => x.Pending).ToArray();
        Assert.Equal(rows[0], sorted[^2]);
        Assert.Equal(rows[3], sorted[^1]);
        Assert.Equal(5, rows.Select(Group).Distinct().Count());
    }


    [Fact]
    public async Task Dlng_ConflictingRowStaysVisibleButDoesNotDisplayOrExportOtherAttempt()
    {
        var lines = File.ReadAllLines(Csv);
        var values = lines[2].Split(',');
        var index = Array.IndexOf(lines[0].Split(','), "LOWER_IMAGE-PATH-1");
        values[index] = values[index].Replace("113627", "111303");
        File.WriteAllLines(Csv, [lines[0], string.Join(",", values)]);
        SepaPair("113627"); SepaPair("111303");
        var row = Assert.Single(await Load());
        Assert.Empty(row.Images);
        Assert.Empty(row.SourceFolder);
        Assert.Contains("Conflicting", row.ResolutionMessage);
        var shares = new Shares(_root);
        var kickout = new KickoutQueueService(new Locator(Csv), new Snapshot(), new WeldingKickoutCsvReader(shares));
        var raw = Assert.Single(await kickout.LoadAsync(_machine, Day, null, CancellationToken.None));
        Assert.Empty(raw.PreviewImages);
        Assert.Empty(raw.SourceFolder);
        Assert.Contains("Conflicting", raw.Inspection!.Issue);
        var bypass = new NgBypassQueueService(new Locator(Csv), new Snapshot(), new NgBypassCsvReader(shares));
        var measure = Assert.Single((await bypass.LoadAsync(_machine, Day, new("SEPA", true, false, false), null, CancellationToken.None)).Items);
        Assert.Empty(measure.Images);
        Assert.Empty(measure.SourceFolder);
    }

    [Fact]
    public async Task RepeatedExport_IsIdempotent_AndDoesNotInventRenamedCopies()
    {
        SepaPair("113627");
        var items = (await Load()).Where(x => x.ModelKind == DlngModelKind.Segmentation).ToArray();
        var store = new JsonDlngReviewStore(Storage);
        foreach (var item in items)
        {
            var record = Record(item) with { IncludeInTraining = true };
            await store.SaveAsync(record, CancellationToken.None);
            Assert.Equal("Collected", await new TrainingCollectionService(Storage).ApplyAsync(record));
        }
        var generator = new DlngReportGenerator(Queue(), store, Storage);
        var result = await generator.GenerateDatasetFromItemsAsync(items, Day, Day, null, CancellationToken.None);
        var before = Directory.GetFiles(result.OutputFolder, "*", SearchOption.AllDirectories);
        await generator.GenerateDatasetFromItemsAsync(items, Day, Day, null, CancellationToken.None);
        Assert.Equal(before, Directory.GetFiles(result.OutputFolder, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task NgBypass_FirstOccurrenceCountsAreSeparateForDifferentLots()
    {
        var lines = File.ReadAllLines(Csv);
        File.AppendAllLines(Csv, [lines[2].Replace("3A4FI161I1", "NEXT-LOT").Replace("11:36:26", "12:00:00").Replace("113627", "120001")]);
        var shares = new Shares(_root);
        var queue = new NgBypassQueueService(new Locator(Csv), new Snapshot(), new NgBypassCsvReader(shares));
        var query = new NgBypassQuery("SEPA", true, false, false);
        var rows = (await queue.LoadAsync(_machine, Day, query, null, CancellationToken.None)).Items;
        Assert.Equal(3, rows.Count);
        var reviews = new JsonNgBypassReviewStore(Storage);
        foreach (var row in rows)
            await reviews.SaveAsync(new(row.Key, row.MachineId, row.LinePolarity, row.InspectedAt,
                row.CellId, row.Measure, row.Side, row.TargetValue, ReviewDecision.RealNg, CopyState.NotRequested, null,
                DateTimeOffset.Now), CancellationToken.None);
        var report = new NgBypassReportGenerator(queue, new Locator(Csv), new Snapshot(),
            new InspectionSummaryCsvReader(), reviews, Storage);
        var result = await report.GenerateAsync([_machine], query, Day, null, CancellationToken.None);
        Assert.Equal(2, Assert.Single(result.Rows).InitialMatched);
        Assert.Equal(2, Assert.Single(result.Rows).Real);
    }

    [Fact]
    public async Task SameFolderName_ReclassifyingOneInspectionCannotMoveAnother()
    {
        var rootA = Path.Combine(_root, "source-a", "same-folder");
        var rootB = Path.Combine(_root, "source-b", "same-folder");
        foreach (var path in new[] { rootA, rootB })
        {
            Directory.CreateDirectory(path);
            File.WriteAllBytes(Path.Combine(path, "raw.jpg"), [0xff, 0xd8, 0xff, 0xd9]);
            File.SetLastWriteTimeUtc(Path.Combine(path, "raw.jpg"), DateTime.UtcNow.AddMinutes(-5));
        }
        var first = new KickoutCandidate("inspection-a", _machine.Id, new DateTime(2026,9,16,11,0,0),
            "E81C", "LOT", Cell, "SEPA", NgSide.Upper, [], rootA, Csv, 1);
        var second = first with { Key = "inspection-b", SourceFolder = rootB, InspectedAt = first.InspectedAt.AddMinutes(1) };
        var service = new ClassifiedFolderService(Storage);
        var a = await service.ClassifyAsync(_machine, first, ReviewDecision.RealNg, CancellationToken.None);
        var b = await service.ClassifyAsync(_machine, second, ReviewDecision.RealNg, CancellationToken.None);
        Assert.NotEqual(a.Destination, b.Destination);
        var changed = await service.ClassifyAsync(_machine, first, ReviewDecision.Overkill, CancellationToken.None);
        Assert.True(Directory.Exists(b.Destination));
        Assert.False(Directory.Exists(a.Destination));
        Assert.True(Directory.Exists(changed.Destination));
    }

    private static DlngReviewRecord Record(DlngReviewItem item) => new(item.Key, item.MachineId, item.LinePolarity,
        item.InspectedAt, item.CellId, item.Judge, item.JudgeDefect, item.Side, item.CropFolder, item.SourceClass,
        "Real", false, item.Images.Select(x => x.Path).ToArray(), DateTimeOffset.Now, item.Inspection);

    private sealed class Shares(string root) : ISharePathResolver
    {
        public string GetRoot(WeldingMachine machine, char drive) => root;
        public void RecordAccessibleRoot(WeldingMachine machine, char drive, string path) { }
    }
    private sealed class DatedLocator(IReadOnlyDictionary<DateOnly, string> paths) : IDailyCsvLocator
    {
        public Task<IReadOnlyList<string>> FindAsync(WeldingMachine machine, DateOnly date, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<string>>(paths.TryGetValue(date, out var path) ? [path] : []);
    }

    private sealed class Locator(string path) : IDailyCsvLocator
    {
        public Task<IReadOnlyList<string>> FindAsync(WeldingMachine machine, DateOnly date, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<string>>([path]); }
    }
    private sealed class Snapshot : IReadOnlySnapshotService
    {
        public Task<SnapshotResult> CreateAsync(string path, bool provisional, CancellationToken token)
            => Task.FromResult(new SnapshotResult(path,path,false,null));
    }
}
