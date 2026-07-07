using KickoutMonitor.Application;
using KickoutMonitor.Domain;
using KickoutMonitor.Infrastructure;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace KickoutMonitor.Tests;

public sealed class CoreTests
{
    [Theory]
    [InlineData("NG", "B663X0B7NS", true)]
    [InlineData("ng", "B663X0B7NS", true)]
    [InlineData("NG", "OCR12345", false)]
    [InlineData("NG", "AGING20260609", false)]
    [InlineData("OK", "B663X0B7NS", false)]
    public void Eligibility_FollowsJudgeAndCellIdRules(string judge, string cellId, bool expected)
    {
        Assert.Equal(expected, KickoutRules.IsEligible(judge, cellId));
    }

    [Theory]
    [InlineData("NG", "OK", NgSide.Upper)]
    [InlineData("OK", "NG", NgSide.Lower)]
    [InlineData("NG", "NG", NgSide.Both)]
    [InlineData("OK", "OK", NgSide.None)]
    public void NgSide_IsDerivedFromUpperAndLowerJudges(
        string upper,
        string lower,
        NgSide expected)
    {
        Assert.Equal(expected, KickoutRules.GetNgSide(upper, lower));
    }

    [Fact]
    public void Defect_IsSafeForWindowsFolder()
    {
        Assert.Equal("GAP_NG", KickoutRules.NormalizeDefect("GAP/NG"));
        Assert.Equal("UNSPECIFIED", KickoutRules.NormalizeDefect(""));
    }

    [Fact]
    public async Task CsvReader_LoadsAllTwelveImagesWhenBothSidesAreNg()
    {
        var temporary = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
        var headers = new List<string>
        {
            "NO", "DATE", "TIME", "MODEL-ID", "LOT-ID", "CELL-ID", "JUDGE",
            "JUDGE-DEFECT", "UPPER_JUDGE", "LOWER_JUDGE"
        };
        for (var index = 1; index <= 3; index++)
        {
            headers.Add($"UPPER_IMAGE-PATH-{index}");
            headers.Add($"UPPER_OVERLAY-IMAGE-PATH-{index}");
            headers.Add($"LOWER_IMAGE-PATH-{index}");
            headers.Add($"LOWER_OVERLAY-IMAGE-PATH-{index}");
        }

        var values = new List<string>
        {
            "1", "20260609", "22:45:53", "E81C", "3A4FF031I2", "B663X0B7NS",
            "NG", "GAP", "NG", "NG"
        };
        for (var index = 0; index < 3; index++)
        {
            values.Add($@"E:\Images\cell\cell_EXT_0_{index}.jpg");
            values.Add($@"E:\Images\cell\cell_EXT_0_{index}_overlay.jpg");
            values.Add($@"E:\Images\cell\cell_EXT_1_{index}.jpg");
            values.Add($@"E:\Images\cell\cell_EXT_1_{index}_overlay.jpg");
        }

        await File.WriteAllLinesAsync(temporary, [string.Join(",", headers), string.Join(",", values)]);
        try
        {
            var machine = new WeldingMachine(
                "1-2-ca", "1-2", Polarity.Cathode, "10.112.99.67", ['E', 'F', 'G']);
            var snapshot = new SnapshotResult(temporary, temporary, false, null);
            var candidates = new List<KickoutCandidate>();
            await foreach (var candidate in new WeldingKickoutCsvReader(new SharePathResolver())
                               .ReadAsync(machine, snapshot, CancellationToken.None))
            {
                candidates.Add(candidate);
            }

            var result = Assert.Single(candidates);
            Assert.Equal(NgSide.Both, result.NgSide);
            Assert.Equal(12, result.PreviewImages.Count);
            Assert.Equal(6, result.PreviewImages.Count(x => x.Side == "UPPER"));
            Assert.Equal(6, result.PreviewImages.Count(x => x.Side == "LOWER"));
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    [Fact]
    public async Task RealNg_CopyPreservesOriginalFolderAndContents()
    {
        var root = Path.Combine(Path.GetTempPath(), "KickoutMonitorTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source", "20260609_224553_LOT_CELL");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "raw.jpg"), [0xFF, 0xD8, 0x00, 0xFF, 0xD9]);
        await File.WriteAllBytesAsync(Path.Combine(source, "overlay.jpg"), [0xFF, 0xD8, 0x01, 0xFF, 0xD9]);
        File.SetLastWriteTimeUtc(Path.Combine(source, "raw.jpg"), DateTime.UtcNow.AddSeconds(-5));
        File.SetLastWriteTimeUtc(Path.Combine(source, "overlay.jpg"), DateTime.UtcNow.AddSeconds(-5));

        var output = Path.Combine(root, "output");
        var storage = new AppStorage(output);
        var machine = new WeldingMachine("1-2-ca", "1-2", Polarity.Cathode, "127.0.0.1", ['E']);
        storage.EnsureCreated([machine]);
        var candidate = new KickoutCandidate(
            "key", machine.Id, DateTime.Now, "E81C", "LOT", "CELL", "GAP",
            NgSide.Upper, [], source, "result.csv", 2);

        try
        {
            var result = await new ClassifiedFolderService(storage).ClassifyAsync(
                machine,
                candidate,
                ReviewDecision.RealNg,
                CancellationToken.None);

            Assert.Equal(CopyState.Copied, result.State);
            Assert.NotNull(result.Destination);
            Assert.True(File.Exists(Path.Combine(result.Destination!, "raw.jpg")));
            Assert.True(File.Exists(Path.Combine(result.Destination!, "overlay.jpg")));
            Assert.True(File.Exists(Path.Combine(source, "raw.jpg")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Overkill_CopyUsesDefectSubfolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "KickoutMonitorTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source", "20260609_224553_LOT_CELL");
        Directory.CreateDirectory(source);
        var image = Path.Combine(source, "raw.jpg");
        await File.WriteAllBytesAsync(image, [0xFF, 0xD8, 0x00, 0xFF, 0xD9]);
        File.SetLastWriteTimeUtc(image, DateTime.UtcNow.AddSeconds(-5));

        var storage = new AppStorage(Path.Combine(root, "output"));
        var machine = new WeldingMachine("1-2-ca", "1-2", Polarity.Cathode, "127.0.0.1", ['E']);
        storage.EnsureCreated([machine]);
        var candidate = new KickoutCandidate(
            "key", machine.Id, DateTime.Now, "E81C", "LOT", "CELL", "B/DIM",
            NgSide.Upper, [], source, "result.csv", 2);

        try
        {
            var result = await new ClassifiedFolderService(storage).ClassifyAsync(
                machine,
                candidate,
                ReviewDecision.Overkill,
                CancellationToken.None);

            Assert.Equal(CopyState.Copied, result.State);
            Assert.Contains(
                Path.Combine("OVERKILL", "B_DIM", Path.GetFileName(source)),
                result.Destination!,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MultiDefectNg_CopyUsesDedicatedRealNgFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "KickoutMonitorTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source", "20260609_224553_LOT_CELL");
        Directory.CreateDirectory(source);
        var image = Path.Combine(source, "raw.jpg");
        await File.WriteAllBytesAsync(image, [0xFF, 0xD8, 0x00, 0xFF, 0xD9]);
        File.SetLastWriteTimeUtc(image, DateTime.UtcNow.AddSeconds(-5));

        var storage = new AppStorage(Path.Combine(root, "output"));
        var machine = new WeldingMachine("1-2-ca", "1-2", Polarity.Cathode, "127.0.0.1", ['E']);
        storage.EnsureCreated([machine]);
        var candidate = new KickoutCandidate(
            "key", machine.Id, DateTime.Now, "E81C", "LOT", "CELL", "GAP",
            NgSide.Both, [], source, "result.csv", 2);

        try
        {
            var result = await new ClassifiedFolderService(storage).ClassifyAsync(
                machine,
                candidate,
                ReviewDecision.MultiDefectNg,
                CancellationToken.None);

            Assert.Equal(CopyState.Copied, result.State);
            Assert.Contains(
                Path.Combine("NG", "MULTI-NG", Path.GetFileName(source)),
                result.Destination!,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }


    [Fact]
    public void DefaultSettings_PreserveCurrentMachineAndRuleDefaults()
    {
        var settings = VisionMasterSettings.CreateDefault();
        var registry = new MachineRegistry(settings);

        Assert.Equal(@"E:\KWAK\VisionMaster", settings.StorageRoot);
        Assert.Equal(8, registry.All.Count);
        Assert.Contains(registry.All, machine => machine.OutputFolderName == "1-1(-)" && machine.IpAddress == "10.112.99.181" && machine.Model == "E81C");
        Assert.Contains(registry.All, machine => machine.OutputFolderName == "2-2(+)" && machine.IpAddress == "10.112.99.78" && machine.Model == "E69B");
        Assert.Equal(['E', 'F', 'G'], registry.Get("1-1-ca").ImageDrives);
        Assert.Equal('D', registry.Get("1-1-ca").DataDrive);
        Assert.Contains("OCR", settings.KickoutRules.IgnoredCellPrefixes);
        Assert.Contains("AGING", settings.KickoutRules.IgnoredCellPrefixes);
        Assert.Equal("06:00", settings.KickoutRules.ReportStartTime);
    }

    [Fact]
    public void DefaultSettings_ProvideIrsSelectionAndFinalClassRules()
    {
        var settings = VisionMasterSettings.CreateDefault();

        Assert.Contains(settings.IrsRules.FirstStageSelections, option => option.Id == "TABSIDE_R" && option.CategoryFolder == "Crop_micro_tabside" && option.Token == "_R");
        Assert.Contains(settings.IrsRules.FirstStageSelections, option => option.Id == "SEPA_SHOULDER_L" && option.CategoryFolder == "SEPA_SHOULDER");
        Assert.Contains(settings.IrsRules.FinalClassGroups, group => group.Folder == "Crop_A" && group.Polarity == Polarity.Cathode && group.Classes.Contains("01_OK_TOP_CATHODE"));
        Assert.Contains(settings.IrsRules.FinalClassGroups, group => group.Folder == "Crop_micro_tabside" && group.Classes.Contains("04_NG_SIDE_TORN"));
        Assert.Contains(settings.IrsRules.FinalClassGroups, group => group.Folder == "SEPA" && group.Classes.SequenceEqual(["Real", "No Need to Retrain"]));
    }

    [Fact]
    public async Task SettingsStore_LoadsSavesAndResetsDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisionMasterSettingsTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        var store = new JsonSettingsStore(path);

        try
        {
            var defaults = await store.LoadOrCreateAsync(CancellationToken.None);
            Assert.True(File.Exists(path));
            Assert.Equal(@"E:\KWAK\VisionMaster", defaults.StorageRoot);

            defaults.StorageRoot = @"C:\VisionMasterTest";
            await store.SaveAsync(defaults, CancellationToken.None);
            var loaded = await store.LoadOrCreateAsync(CancellationToken.None);
            Assert.Equal(@"C:\VisionMasterTest", loaded.StorageRoot);

            await store.ResetToDefaultsAsync(CancellationToken.None);
            var reset = await store.LoadOrCreateAsync(CancellationToken.None);
            Assert.Equal(@"E:\KWAK\VisionMaster", reset.StorageRoot);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public void Registry_ContainsEightWeldingMachines()
    {
        var registry = new MachineRegistry();
        Assert.Equal(8, registry.All.Count);
        Assert.Contains(registry.All, x => x.OutputFolderName == "2-2(+)");
    }

    [Fact]
    public async Task DlngCsvReader_FiltersEligibleRowsAndUsesBypassSide()
    {
        var csv = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
        var headers = new[]
        {
            "DATE", "TIME", "MODEL-ID", "LOT-ID", "CELL-ID", "JUDGE", "JUDGE-DEFECT",
            "UPPER_JUDGE", "LOWER_JUDGE", "UPPER_GAP_DL-JUDGE", "LOWER_GAP_DL-JUDGE",
            "UPPER_IMAGE-PATH-1", "UPPER_IMAGE-PATH-2", "UPPER_IMAGE-PATH-3",
            "LOWER_IMAGE-PATH-1", "LOWER_IMAGE-PATH-2", "LOWER_IMAGE-PATH-3"
        };
        var good = new[]
        {
            "20260623", "00:00:30", "E81C", "LOT", "CELL-DLNG", "DLNG", "GAP_DL",
            "OK", "OK", "BYPASS_NG", "OK",
            @"E:\Files\Image\E81C\2026\06\23\00\OK\DL_OK\CELL-DLNG\CELL-DLNG_0_0.jpg",
            @"E:\Files\Image\E81C\2026\06\23\00\OK\DL_OK\CELL-DLNG\CELL-DLNG_0_1.jpg",
            @"E:\Files\Image\E81C\2026\06\23\00\OK\DL_OK\CELL-DLNG\CELL-DLNG_0_2.jpg",
            "", "", ""
        };
        var ignored = new[]
        {
            "20260623", "00:01:30", "E81C", "LOT", "CELL-QNG", "Q-NG", "GAP_DL",
            "OK", "OK", "BYPASS_NG", "OK", "", "", "", "", "", ""
        };
        var ignoredOcr = new[]
        {
            "20260623", "00:02:30", "E81C", "LOT", "OCR-CELL", "DLNG", "GAP_DL",
            "OK", "OK", "BYPASS_NG", "OK", "", "", "", "", "", ""
        };
        var ignoredAging = new[]
        {
            "20260623", "00:03:30", "E81C", "LOT", "AGING-CELL", "DLNG", "GAP_DL",
            "OK", "OK", "BYPASS_NG", "OK", "", "", "", "", "", ""
        };
        await File.WriteAllLinesAsync(csv, [string.Join(",", headers), string.Join(",", good), string.Join(",", ignored), string.Join(",", ignoredOcr), string.Join(",", ignoredAging)]);
        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "127.0.0.1", ['E']);
            var snapshot = new SnapshotResult(csv, csv, false, null);
            var items = new List<DlngReviewItem>();
            await foreach (var item in new DlngCsvReader(new SharePathResolver())
                               .ReadAsync(machine, snapshot, null, CancellationToken.None))
            {
                items.Add(item);
            }

            var result = Assert.Single(items);
            Assert.Equal("DLNG", result.Judge);
            Assert.Equal("GAP_DL", result.JudgeDefect);
            Assert.Equal("UPPER", result.Side);
            Assert.Equal(3, result.Images.Count);
        }
        finally
        {
            File.Delete(csv);
        }
    }

    [Fact]
    public async Task DlngCropLocator_LoadsClassificationPairsAndSourceClass()
    {
        var root = Path.Combine(Path.GetTempPath(), "DlngCropTests", Guid.NewGuid().ToString("N"));
        var cropRoot = Path.Combine(root, "Files", "Image", "E81C", "2026", "06", "23", "Mavin", "Crop_A", "03_NG_TORN");
        Directory.CreateDirectory(cropRoot);
        var source = Path.Combine(cropRoot, "CELL-1_01-1_AN_010203_UPPER_1_A_L_CL03_NG_P1.000_SourceMap.jpg");
        var active = Path.Combine(cropRoot, "CELL-1_01-1_AN_010203_UPPER_1_A_L_CL03_NG_P1.000_ActiveMap.jpg");
        await File.WriteAllBytesAsync(source, [1]);
        await File.WriteAllBytesAsync(active, [2]);
        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "unused", ['E']);
            var item = DlngItem(machine, "A_L", "UPPER", "CELL-1");
            var expanded = await new DlngCropLocator(new FakeShareResolver(root))
                .ExpandAsync(machine, item, null, CancellationToken.None);

            var result = Assert.Single(expanded);
            Assert.Equal("Crop_A", result.CropFolder);
            Assert.Equal("03_NG_TORN", result.SourceClass);
            Assert.Equal(DlngModelKind.Classification, result.ModelKind);
            Assert.Equal(2, result.Images.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DlngCropLocator_BDimCreatesHornmarkAndLeadedgePairs()
    {
        var root = Path.Combine(Path.GetTempPath(), "DlngBDimTests", Guid.NewGuid().ToString("N"));
        var horn = Path.Combine(root, "Files", "Image", "E81C", "2026", "06", "23", "Mavin", "HORNMARK");
        var lead = Path.Combine(root, "Files", "Image", "E81C", "2026", "06", "23", "Mavin", "LEADEDGE");
        Directory.CreateDirectory(horn);
        Directory.CreateDirectory(lead);
        await File.WriteAllBytesAsync(Path.Combine(horn, "CELL-2_01-1_AN_010203_UPPER_1_HORN MARK L_f0_SourceImg.jpg"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(horn, "CELL-2_01-1_AN_010203_UPPER_1_HORN MARK L_f0_SourceImg_mask.png"), [2]);
        await File.WriteAllBytesAsync(Path.Combine(lead, "CELL-2_01-1_AN_010203_UPPER_1_LEAD EDGE L_SourceImg.jpg"), [3]);
        await File.WriteAllBytesAsync(Path.Combine(lead, "CELL-2_01-1_AN_010203_UPPER_1_LEAD EDGE L_SourceImg.png"), [4]);
        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "unused", ['E']);
            var item = DlngItem(machine, "B_DIM_L", "UPPER", "CELL-2");
            var expanded = await new DlngCropLocator(new FakeShareResolver(root))
                .ExpandAsync(machine, item, null, CancellationToken.None);

            Assert.Contains(expanded, x => x.CropFolder == "HORNMARK" && x.Images.Count == 2);
            Assert.Contains(expanded, x => x.CropFolder == "LEADEDGE" && x.Images.Count == 2);
            Assert.Equal(2, expanded.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DlngCropLocator_FallsBackToRawWhenCropMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "DlngFallbackTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "unused", ['E']);
            var item = DlngItem(machine, "GAP_DL", "UPPER", "CELL-3") with
            {
                Images =
                [
                    new("Raw 1", Path.Combine(root, "raw1.jpg"), false),
                    new("Raw 2", Path.Combine(root, "raw2.jpg"), false),
                    new("Raw 3", Path.Combine(root, "raw3.jpg"), false)
                ]
            };
            var expanded = await new DlngCropLocator(new FakeShareResolver(root))
                .ExpandAsync(machine, item, null, CancellationToken.None);

            var result = Assert.Single(expanded);
            Assert.Equal(DlngModelKind.FallbackRaw, result.ModelKind);
            Assert.Equal("NEED_TO_SIMULATE", result.SourceClass);
            Assert.Equal(3, result.Images.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DlngReport_GeneratesWorkbookAndCopiesClassifiedCrops()
    {
        var root = Path.Combine(Path.GetTempPath(), "DlngReportTests", Guid.NewGuid().ToString("N"));
        var csv = Path.Combine(root, "result.csv");
        var cropRoot = Path.Combine(root, "share", "Files", "Image", "E81C", "2026", "06", "23", "Mavin", "Crop_A", "03_NG_TORN");
        Directory.CreateDirectory(cropRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(csv)!);
        var source = Path.Combine(cropRoot, "CELL-RPT_01-1_AN_070000_UPPER_1_A_L_CL03_NG_P1.000_SourceMap.jpg");
        var active = Path.Combine(cropRoot, "CELL-RPT_01-1_AN_070000_UPPER_1_A_L_CL03_NG_P1.000_ActiveMap.jpg");
        await File.WriteAllBytesAsync(source, [1]);
        await File.WriteAllBytesAsync(active, [2]);
        var headers = new[]
        {
            "DATE", "TIME", "MODEL-ID", "LOT-ID", "CELL-ID", "JUDGE", "JUDGE-DEFECT",
            "UPPER_JUDGE", "LOWER_JUDGE", "UPPER_A_L-JUDGE", "LOWER_A_L-JUDGE",
            "UPPER_IMAGE-PATH-1", "UPPER_IMAGE-PATH-2", "UPPER_IMAGE-PATH-3"
        };
        var values = new[]
        {
            "20260623", "07:00:00", "E81C", "LOT", "CELL-RPT", "DLNG", "A_L",
            "OK", "OK", "BYPASS_NG", "OK", "", "", ""
        };
        await File.WriteAllLinesAsync(csv, [string.Join(",", headers), string.Join(",", values)]);

        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "unused", ['E']);
            var storage = new AppStorage(Path.Combine(root, "out"));
            storage.EnsureCreated([machine]);
            var reviews = new JsonDlngReviewStore(storage);
            var queue = new DlngQueueService(
                new SingleFileLocator(csv, new DateOnly(2026, 6, 23)),
                new FakeSnapshotService(),
                new DlngCsvReader(new FakeShareResolver(Path.Combine(root, "share"))),
                new DlngCropLocator(new FakeShareResolver(Path.Combine(root, "share"))));
            var items = await queue.LoadAsync(machine, new DateOnly(2026, 6, 23), null, CancellationToken.None);
            var item = Assert.Single(items);
            await reviews.SaveAsync(new(
                item.Key,
                item.MachineId,
                item.LinePolarity,
                item.InspectedAt,
                item.CellId,
                item.Judge,
                item.JudgeDefect,
                item.Side,
                item.CropFolder,
                item.SourceClass,
                "04_NG_PTCL",
                false,
                item.Images.Select(x => x.Path).ToArray(),
                DateTimeOffset.Now), CancellationToken.None);

            var result = await new DlngReportGenerator(queue, reviews, storage)
                .GenerateAsync([machine], new DateOnly(2026, 6, 23), null, CancellationToken.None);

            Assert.True(File.Exists(result.SummaryWorkbook));
            var destination = Path.Combine(result.OutputFolder, "Dataset", "Crop_A", "1-1(-)", "04_NG_PTCL");
            Assert.True(Directory.Exists(destination));
            Assert.True(File.Exists(Path.Combine(destination, Path.GetFileName(source))));
            Assert.True(File.Exists(Path.Combine(destination, Path.GetFileName(active))));
            Assert.False(File.Exists(Path.Combine(destination, $"1-1(-)_CELL-RPT_A_L_{Path.GetFileName(source)}")));
            Assert.False(File.Exists(Path.Combine(destination, $"1-1(-)_CELL-RPT_A_L_{Path.GetFileName(active)}")));
            Assert.False(Directory.Exists(Path.Combine(result.OutputFolder, "Dataset", "Crop_A", "04_NG_PTCL")));
            Assert.Equal(1, Assert.Single(result.Rows).SwitchedCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DlngReport_CopiesSegmentationNoNeedToOverkillFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "DlngSegReportTests", Guid.NewGuid().ToString("N"));
        var csv = Path.Combine(root, "result.csv");
        var cropRoot = Path.Combine(root, "share", "Files", "Image", "E81C", "2026", "06", "23", "Mavin", "Gap_DL");
        Directory.CreateDirectory(cropRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(csv)!);
        await File.WriteAllBytesAsync(Path.Combine(cropRoot, "CELL-SEG_01-1_AN_070000_UPPER_2_Gap_DL_W0_C0_OK_SourceImg.jpg"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(cropRoot, "CELL-SEG_01-1_AN_070000_UPPER_2_Gap_DL_W0_C0_OK_SourceImg_mask.png"), [2]);
        var headers = new[]
        {
            "DATE", "TIME", "MODEL-ID", "LOT-ID", "CELL-ID", "JUDGE", "JUDGE-DEFECT",
            "UPPER_JUDGE", "LOWER_JUDGE", "UPPER_GAP_DL-JUDGE", "LOWER_GAP_DL-JUDGE",
            "UPPER_IMAGE-PATH-1", "UPPER_IMAGE-PATH-2", "UPPER_IMAGE-PATH-3"
        };
        var values = new[]
        {
            "20260623", "07:00:00", "E81C", "LOT", "CELL-SEG", "DLNG", "GAP_DL",
            "OK", "OK", "BYPASS_NG", "OK", "", "", ""
        };
        await File.WriteAllLinesAsync(csv, [string.Join(",", headers), string.Join(",", values)]);

        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "unused", ['E']);
            var storage = new AppStorage(Path.Combine(root, "out"));
            storage.EnsureCreated([machine]);
            var reviews = new JsonDlngReviewStore(storage);
            var queue = new DlngQueueService(
                new SingleFileLocator(csv, new DateOnly(2026, 6, 23)),
                new FakeSnapshotService(),
                new DlngCsvReader(new FakeShareResolver(Path.Combine(root, "share"))),
                new DlngCropLocator(new FakeShareResolver(Path.Combine(root, "share"))));
            var item = Assert.Single(await queue.LoadAsync(machine, new DateOnly(2026, 6, 23), null, CancellationToken.None));
            await reviews.SaveAsync(new(
                item.Key,
                item.MachineId,
                item.LinePolarity,
                item.InspectedAt,
                item.CellId,
                item.Judge,
                item.JudgeDefect,
                item.Side,
                item.CropFolder,
                item.SourceClass,
                "No Need to Train",
                false,
                item.Images.Select(x => x.Path).ToArray(),
                DateTimeOffset.Now), CancellationToken.None);

            var result = await new DlngReportGenerator(queue, reviews, storage)
                .GenerateAsync([machine], new DateOnly(2026, 6, 23), null, CancellationToken.None);

            Assert.True(Directory.Exists(Path.Combine(result.OutputFolder, "Dataset", "Gap_DL", "OVERKILL")));
            Assert.False(Directory.Exists(Path.Combine(result.OutputFolder, "Dataset", "Gap_DL", "No Need to Train")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(@"E:\Files\Image\cell\a.jpg", @"\\10.112.99.181\E\Files\Image\cell\a.jpg")]
    [InlineData(@"F:\Files\Image\cell\b.jpg", @"\\10.112.99.181\F\Files\Image\cell\b.jpg")]
    [InlineData(@"G:\Files\Image\cell\c.jpg", @"\\10.112.99.181\G\Files\Image\cell\c.jpg")]
    public void CsvImagePath_DirectlySelectsTheCorrectDrive(string source, string expected)
    {
        var machine = new WeldingMachine(
            "1-1-an", "1-1", Polarity.Anode, "10.112.99.181", ['E', 'F', 'G']);
        Assert.Equal(expected, ProductionPathMapper.ToUnc(machine, source));
    }

    [Fact]
    public async Task PreviewCache_StoresUnclassifiedImagesUnderTempAndRemovesThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "KickoutCacheTests", Guid.NewGuid().ToString("N"));
        var sourceFolder = Path.Combine(root, "source", "20260609_224553_LOT_CELL");
        Directory.CreateDirectory(sourceFolder);
        var sourceImage = Path.Combine(sourceFolder, "cell_EXT_0_0.jpg");
        await File.WriteAllBytesAsync(sourceImage, [0xFF, 0xD8, 0x00, 0xFF, 0xD9]);
        File.SetLastWriteTimeUtc(sourceImage, DateTime.UtcNow.AddSeconds(-5));

        var storage = new AppStorage(Path.Combine(root, "output"));
        var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "127.0.0.1", ['E']);
        storage.EnsureCreated([machine]);
        var candidate = new KickoutCandidate(
            "cache-key",
            machine.Id,
            new DateTime(2026, 6, 9, 22, 45, 53),
            "E81C",
            "LOT",
            "CELL",
            "GAP",
            NgSide.Upper,
            [new CandidateImage("UPPER", 0, false, sourceImage, sourceImage)],
            sourceFolder,
            "result.csv",
            2);

        try
        {
            var cache = new DiskPreviewCache(storage);
            var cached = await cache.EnsureCachedAsync(machine, candidate, CancellationToken.None);
            var cachedPath = Assert.Single(cached.PreviewImages).CachedPath;
            Assert.NotNull(cachedPath);
            Assert.True(File.Exists(cachedPath));
            Assert.StartsWith(storage.Temp, cachedPath, StringComparison.OrdinalIgnoreCase);

            await cache.RemoveAsync(machine, cached, CancellationToken.None);
            Assert.False(Directory.Exists(storage.CandidateTempFolder(machine, cached)));
            Assert.True(File.Exists(sourceImage));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ShareResolver_ReusesTheConnectionProbesSuccessfulShareForm()
    {
        var machine = new WeldingMachine(
            "1-1-an", "1-1", Polarity.Anode, "10.112.99.181", ['E', 'F', 'G']);
        var resolver = new SharePathResolver();
        resolver.RecordAccessibleRoot(machine, 'E', @"\\10.112.99.181\E$");

        var mapped = ProductionPathMapper.ToUnc(
            machine,
            @"E:\Files\Image\cell\a.jpg",
            resolver);

        Assert.Equal(@"\\10.112.99.181\E$\Files\Image\cell\a.jpg", mapped);
    }

    [Fact]
    public async Task DailyCsvLocator_AcceptsBaseAndNumberedFilesOnly()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "KickoutCsvLocatorTests",
            Guid.NewGuid().ToString("N"));
        var dayFolder = Path.Combine(root, "Files", "Data", "Result", "Day");
        Directory.CreateDirectory(dayFolder);
        var expectedNames = new[]
        {
            "#3-2 WELDING VISION(+)_JF2_20251019.csv",
            "#3-2 WELDING VISION(+)_JF2_20251019_1.csv",
            "#3-2 WELDING VISION(+)_JF2_20251019_23.csv"
        };
        var rejectedNames = new[]
        {
            "#3-2 WELDING VISION(+)_JF2_20251019 - Copy.csv",
            "#3-2 WELDING VISION(+)_JF2_20251019_Copy.csv",
            "#3-2 WELDING VISION(+)_JF2_20251019_defect.csv",
            "#3-2 WELDING VISION(+)_JF2_20251019_1 - Copy.csv",
            "#3-2 WELDING VISION(+)_JF2_202510190.csv"
        };

        foreach (var name in expectedNames.Concat(rejectedNames))
        {
            await File.WriteAllTextAsync(Path.Combine(dayFolder, name), "header");
        }

        var machine = new WeldingMachine(
            "test", "1-1", Polarity.Anode, "127.0.0.1", ['E', 'F', 'G']);
        var resolver = new TestSharePathResolver(root);

        try
        {
            var found = await new DailyCsvLocator(resolver).FindAsync(
                machine,
                new DateOnly(2025, 10, 19),
                CancellationToken.None);

            Assert.Equal(
                expectedNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
                found.Select(Path.GetFileName));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SummaryReport_BlocksWhenNgRecordIsNotReviewed()
    {
        var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "127.0.0.1", ['E']);
        var record = SummaryRecord(
            machine,
            new DateTime(2026, 6, 25, 7, 0, 0),
            "CELL-1",
            "NG",
            "B_DIM");
        var service = new SummaryReportService(
            new FakeLocator(),
            new FakeSnapshotService(),
            new FakeSummaryReader([record]),
            new FakeReviewStore([]),
            new FakeSummaryWriter());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateAsync([machine], new DateOnly(2026, 6, 25), null, CancellationToken.None));

        Assert.Contains("Report blocked", exception.Message);
    }

    [Fact]
    public async Task SummaryReport_UsesSixAmProductionWindowAndReviewedNgRates()
    {
        var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "127.0.0.1", ['E']);
        var beforeWindow = SummaryRecord(
            machine,
            new DateTime(2026, 6, 25, 5, 59, 59),
            "CELL-BEFORE",
            "OK",
            "");
        var ok = SummaryRecord(
            machine,
            new DateTime(2026, 6, 25, 6, 0, 0),
            "CELL-OK",
            "OK",
            "");
        var real = SummaryRecord(
            machine,
            new DateTime(2026, 6, 25, 8, 0, 0),
            "CELL-REAL",
            "NG",
            "B_DIM");
        var overkill = SummaryRecord(
            machine,
            new DateTime(2026, 6, 26, 5, 59, 59),
            "CELL-OVER",
            "NG",
            "C_DIM");
        var afterWindow = SummaryRecord(
            machine,
            new DateTime(2026, 6, 26, 6, 0, 0),
            "CELL-AFTER",
            "NG",
            "B_DIM");
        var writer = new FakeSummaryWriter();
        var service = new SummaryReportService(
            new FakeLocator(),
            new FakeSnapshotService(),
            new FakeSummaryReader([beforeWindow, ok, real, overkill, afterWindow]),
            new FakeReviewStore([
                new ReviewEntry(real.CandidateKey, ReviewDecision.RealNg, CopyState.Copied, "", null, DateTimeOffset.Now),
                new ReviewEntry(overkill.CandidateKey, ReviewDecision.Overkill, CopyState.Copied, "", null, DateTimeOffset.Now),
                new ReviewEntry(afterWindow.CandidateKey, ReviewDecision.RealNg, CopyState.Copied, "", null, DateTimeOffset.Now)
            ]),
            writer);

        var result = await service.GenerateAsync(
            [machine],
            new DateOnly(2026, 6, 25),
            null,
            CancellationToken.None);

        var all = Assert.Single(result.Rows, row => row.Defect == "ALL");
        Assert.Equal(3, all.TotalInspected);
        Assert.Equal(2, all.InitialNg);
        Assert.Equal(1, all.RealNg);
        Assert.Equal(1, all.Overkill);
        Assert.Equal(2.0 / 3.0, all.InitialNgRate, 6);
        Assert.Equal(1.0 / 3.0, all.ConfirmedNgRate, 6);
        Assert.Equal(1.0 / 3.0, all.OverkillRate, 6);
        Assert.Equal(writer.Rows, result.Rows);
    }

    [Fact]
    public async Task SummaryReport_GroupsMultiDefectReviewsAsMultiNgRealNg()
    {
        var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "127.0.0.1", ['E']);
        var multi = SummaryRecord(
            machine,
            new DateTime(2026, 6, 25, 8, 0, 0),
            "CELL-MULTI",
            "NG",
            "H_DIM_L");
        var writer = new FakeSummaryWriter();
        var service = new SummaryReportService(
            new FakeLocator(),
            new FakeSnapshotService(),
            new FakeSummaryReader([multi]),
            new FakeReviewStore([
                new ReviewEntry(multi.CandidateKey, ReviewDecision.MultiDefectNg, CopyState.Copied, "", null, DateTimeOffset.Now)
            ]),
            writer);

        var result = await service.GenerateAsync(
            [machine],
            new DateOnly(2026, 6, 25),
            null,
            CancellationToken.None);

        var multiRow = Assert.Single(result.Rows, row => row.Defect == "MULTI-NG");
        Assert.Equal(1, multiRow.InitialNg);
        Assert.Equal(1, multiRow.RealNg);
        Assert.Equal(0, multiRow.Overkill);
        Assert.DoesNotContain(result.Rows, row => row.Defect == "H_DIM_L");
    }

    [Fact]
    public async Task SummaryWriter_CreatesDatedFolderDetailsAndReviewedImageCopies()
    {
        var root = Path.Combine(Path.GetTempPath(), "KickoutSummaryWriterTests", Guid.NewGuid().ToString("N"));
        var storage = new AppStorage(root);
        var overkillSource = Path.Combine(root, "classified", "overkill-cell");
        var multiSource = Path.Combine(root, "classified", "multi-cell");
        var realSource = Path.Combine(root, "classified", "real-cell");
        Directory.CreateDirectory(overkillSource);
        Directory.CreateDirectory(multiSource);
        Directory.CreateDirectory(realSource);
        await File.WriteAllTextAsync(Path.Combine(overkillSource, "raw.jpg"), "overkill");
        await File.WriteAllTextAsync(Path.Combine(multiSource, "raw.jpg"), "multi");
        await File.WriteAllTextAsync(Path.Combine(realSource, "raw.jpg"), "real");

        var writer = new SummaryReportWriter(storage);
        try
        {
            var output = await writer.WriteAsync(
                new DateOnly(2026, 6, 25),
                new DateTime(2026, 6, 25, 6, 0, 0),
                new DateTime(2026, 6, 26, 6, 0, 0),
                [
                    new SummaryReportRow("1-1(-)", "ALL", 10, 3, 2, 1, 0.3, 0.2, 0.1)
                ],
                [
                    new SummaryDetailRow(
                        "1-1(-)",
                        "B_DIM",
                        ReviewDecision.Overkill,
                        overkillSource,
                        ["CELL-ID", "JUDGE", "JUDGE-DEFECT"],
                        ["CELL-OVER", "NG", "B_DIM"]),
                    new SummaryDetailRow(
                        "1-1(-)",
                        "MULTI-NG",
                        ReviewDecision.MultiDefectNg,
                        multiSource,
                        ["CELL-ID", "JUDGE", "JUDGE-DEFECT"],
                        ["CELL-MULTI", "NG", "GAP"]),
                    new SummaryDetailRow(
                        "1-1(-)",
                        "C_DIM",
                        ReviewDecision.RealNg,
                        realSource,
                        ["CELL-ID", "JUDGE", "JUDGE-DEFECT"],
                        ["CELL-REAL", "NG", "C_DIM"])
                ],
                CancellationToken.None);

            var reportFolder = Path.Combine(storage.Summary, "NG_Summary_20260625");
            Assert.Equal(Path.Combine(reportFolder, "NG_Summary_20260625.xlsx"), output);
            Assert.False(File.Exists(Path.Combine(reportFolder, "NG_Summary_20260625.csv")));
            Assert.True(File.Exists(Path.Combine(reportFolder, "NG_Details_1-1(-).csv")));
            Assert.True(File.Exists(Path.Combine(reportFolder, "NG_Details_1-1(-).xlsx")));
            Assert.True(File.Exists(Path.Combine(
                reportFolder,
                "OVERKILL",
                "1-1(-)",
                "B_DIM",
                "overkill-cell",
                "raw.jpg")));
            Assert.True(File.Exists(Path.Combine(
                reportFolder,
                "NG",
                "1-1(-)",
                "MULTI-NG",
                "multi-cell",
                "raw.jpg")));
            Assert.True(File.Exists(Path.Combine(
                reportFolder,
                "NG",
                "1-1(-)",
                "C_DIM",
                "real-cell",
                "raw.jpg")));
            Assert.False(File.Exists(Path.Combine(reportFolder, "NG", "1-1(-)", "B_DIM", "overkill-cell", "raw.jpg")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task IrsWorkbookReader_UsesRequestRowsAndSecondJudgmentReason()
    {
        var root = Path.Combine(Path.GetTempPath(), "IrsWorkbookTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workbook = Path.Combine(root, "irs.xlsx");
        CreateIrsWorkbook(
            workbook,
            [
                ["", "PACKAGE #1-1", "Welding Plus", "Algorithm NG", "Manual", "2026-06-05 03:12:41", "1", "7", "LOT", "CELL-1", "BTM", "1", "", "", "", "", "", "IMG1.JPG", "NG", "FIRST_REASON", "", "", "OK", "SECOND_REASON", "", "", "PKG", "Request", "Packing Hold"],
                ["", "PACKAGE #1-1", "Welding Plus", "Algorithm NG", "Manual", "2026-06-05 03:13:41", "1", "8", "LOT", "CELL-2", "TOP", "1", "", "", "", "", "", "IMG2.JPG", "NG", "FIRST_ONLY", "", "", "OK", "SECOND_2", "", "", "PKG", "Not request", ""]
            ]);

        try
        {
            var rows = await new IrsWorkbookReader().ReadRequestedAsync(workbook, CancellationToken.None);

            var row = Assert.Single(rows);
            Assert.Equal("CELL-1", row.CellId);
            Assert.Equal("SECOND_REASON", row.SecondReason);
            Assert.Equal("OK", row.SecondResult);
            Assert.DoesNotContain("FIRST", row.SecondReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task IrsWorkbookReader_AcceptsNgOutRequestFormat()
    {
        var root = Path.Combine(Path.GetTempPath(), "IrsWorkbookNgOutTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workbook = Path.Combine(root, "irs-ng-out.xlsx");
        CreateIrsWorkbook(
            workbook,
            [
                ["", "PACKAGE #1-1", "Welding Minus", "Algorithm NG", "Manual", "2026-06-24 07:49:21", "2", "1009", "LOT", "CELL-NGOUT", "TOP", "1", "", "", "", "", "", "IMG-NGOUT.JPG", "NG", "FIRST_REASON", "", "", "NG", "SECOND_REASON", "", "", "2026-06-29 08:10:54", "Success", "U", "", "PKG", "Request", "2026-06-29 08:10:54", "Inspector", "Y"],
                ["", "PACKAGE #1-1", "Welding Minus", "Algorithm NG", "Manual", "2026-06-24 07:50:21", "2", "1010", "LOT", "CELL-SKIP", "TOP", "1", "", "", "", "", "", "IMG-SKIP.JPG", "NG", "FIRST_REASON", "", "", "NG", "SECOND_REASON", "", "", "2026-06-29 08:10:54", "Success", "U", "", "PKG", "Not request", "2026-06-29 08:10:54", "Inspector", "Y"]
            ],
            useNgOutHeaders: true);

        try
        {
            var rows = await new IrsWorkbookReader().ReadRequestedAsync(workbook, CancellationToken.None);

            var row = Assert.Single(rows);
            Assert.Equal("CELL-NGOUT", row.CellId);
            Assert.Equal("SECOND_REASON", row.SecondReason);
            Assert.Equal("NG", row.SecondResult);
            Assert.Equal("IMG-NGOUT.JPG", row.RawImageFileName);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task IrsRawImageLocator_FindsTopRawImagesInModelDateHourFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "IrsImageLocatorTests", Guid.NewGuid().ToString("N"));
        var hourRoot = Path.Combine(root, "Files", "Image", "E81C", "2026", "06", "01", "08", "OK");
        Directory.CreateDirectory(hourRoot);
        var top0 = Path.Combine(hourRoot, "20260601_CELL-1_EXT_0_0.jpg");
        var top1 = Path.Combine(hourRoot, "20260601_CELL-1_EXT_0_1.jpg");
        var top2 = Path.Combine(hourRoot, "20260601_CELL-1_EXT_0_2.jpg");
        var bottom = Path.Combine(hourRoot, "20260601_CELL-1_EXT_1_0.jpg");
        var overlay = Path.Combine(hourRoot, "20260601_CELL-1_EXT_0_0_overlay.jpg");
        foreach (var image in new[] { top2, bottom, top0, overlay, top1 })
        {
            await File.WriteAllTextAsync(image, "raw");
        }
        var machine = new WeldingMachine("1-1-ca", "1-1", Polarity.Cathode, "127.0.0.1", ['E']);
        var candidate = new IrsReviewCandidate(
            "key",
            "PACKAGE #1-1",
            "Welding Plus",
            "1-1(+)",
            new DateTime(2026, 6, 1, 8, 9, 25),
            "LOT",
            "CELL-1",
            "TOP",
            "irs-file.jpg",
            "OK",
            "SECOND_REASON",
            3);

        try
        {
            var result = await new IrsRawImageLocator(new TestSharePathResolver(root), new EmptyDailyCsvLocator())
                .FindAsync(machine, candidate, CancellationToken.None);

            Assert.Equal([top0, top1, top2], result.NetworkPaths);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task IrsReviewQueue_MapsPackageAndWeldingPlusToLinePolarity()
    {
        var machine = new WeldingMachine("1-1-ca", "1-1", Polarity.Cathode, "127.0.0.1", ['E']);
        var candidate = new IrsReviewCandidate(
            "key",
            "PACKAGE #1-1",
            "Welding Plus",
            string.Empty,
            new DateTime(2026, 6, 5, 3, 12, 41),
            "LOT",
            "CELL-1",
            "BTM",
            "image.jpg",
            "OK",
            "reason",
            3);
        var service = new IrsReviewQueueService(
            new FakeMachineRegistry([machine]),
            new FakeIrsWorkbookReader([candidate]),
            new FakeIrsImageLocator(@"\\127.0.0.1\E\image.jpg"));

        var rows = await service.LoadAsync("irs.xlsx", null, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("1-1(+)", row.LinePolarity);
        Assert.Equal(@"\\127.0.0.1\E\image.jpg", row.RawImagePath);
    }

    [Fact]
    public async Task IrsRawImageLocator_UsesE69BForLineTwoTwo()
    {
        var root = Path.Combine(Path.GetTempPath(), "IrsImageLocatorTests", Guid.NewGuid().ToString("N"));
        var hourRoot = Path.Combine(root, "Files", "Image", "E69B", "2026", "06", "01", "08");
        Directory.CreateDirectory(hourRoot);
        var image = Path.Combine(hourRoot, "20260601_CELL-22_EXT_1_0.jpg");
        await File.WriteAllTextAsync(image, "raw");
        var machine = new WeldingMachine("2-2-ca", "2-2", Polarity.Cathode, "127.0.0.1", ['E'], "E69B");
        var candidate = new IrsReviewCandidate(
            "key",
            "PACKAGE #2-2",
            "Welding Plus",
            "2-2(+)",
            new DateTime(2026, 6, 1, 8, 9, 25),
            "LOT",
            "CELL-22",
            "BTM",
            "irs-file.jpg",
            "OK",
            "SECOND_REASON",
            3);

        try
        {
            var result = await new IrsRawImageLocator(new TestSharePathResolver(root), new EmptyDailyCsvLocator())
                .FindAsync(machine, candidate, CancellationToken.None);

            Assert.Equal([image], result.NetworkPaths);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task IrsRawImageLocator_UsesProductionCsvImagePathsBeforeFolderSearch()
    {
        var root = Path.Combine(Path.GetTempPath(), "IrsImageLocatorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var csv = Path.Combine(root, "#1-1 WELDING VISION(+)_JF2_20260601.csv");
        await File.WriteAllTextAsync(
            csv,
            string.Join(
                Environment.NewLine,
                "DATE,TIME,CELL-ID,UPPER_IMAGE-PATH-1,UPPER_IMAGE-PATH-2,UPPER_IMAGE-PATH-3,LOWER_IMAGE-PATH-1,LOWER_IMAGE-PATH-2,LOWER_IMAGE-PATH-3",
                @"20260601,08:09:25,CELL-CSV,E:\Files\Image\E81C\2026\06\01\08\OK\CELL-CSV_0_0.jpg,E:\Files\Image\E81C\2026\06\01\08\OK\CELL-CSV_0_1.jpg,E:\Files\Image\E81C\2026\06\01\08\OK\CELL-CSV_0_2.jpg,E:\Files\Image\E81C\2026\06\01\08\OK\CELL-CSV_1_0.jpg,E:\Files\Image\E81C\2026\06\01\08\OK\CELL-CSV_1_1.jpg,E:\Files\Image\E81C\2026\06\01\08\OK\CELL-CSV_1_2.jpg"));
        var machine = new WeldingMachine("1-1-ca", "1-1", Polarity.Cathode, "127.0.0.1", ['E']);
        var candidate = new IrsReviewCandidate(
            "key",
            "PACKAGE #1-1",
            "Welding Plus",
            "1-1(+)",
            new DateTime(2026, 6, 1, 8, 9, 25),
            "LOT",
            "CELL-CSV",
            "TOP",
            "irs-file.jpg",
            "OK",
            "SECOND_REASON",
            3);

        try
        {
            var result = await new IrsRawImageLocator(
                    new TestSharePathResolver(root),
                    new StaticDailyCsvLocator([csv]))
                .FindAsync(machine, candidate, CancellationToken.None);

            Assert.Equal(
                [
                    Path.Combine(root, @"Files\Image\E81C\2026\06\01\08\OK\CELL-CSV_0_0.jpg"),
                    Path.Combine(root, @"Files\Image\E81C\2026\06\01\08\OK\CELL-CSV_0_1.jpg"),
                    Path.Combine(root, @"Files\Image\E81C\2026\06\01\08\OK\CELL-CSV_0_2.jpg")
                ],
                result.NetworkPaths);
            Assert.Contains("production CSV", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task IrsReviewCommitService_CopiesOriginalFolderAndSelectedCropFiles()
    {
        var shareRoot = Path.Combine(Path.GetTempPath(), "IrsCommitShare", Guid.NewGuid().ToString("N"));
        var storageRoot = Path.Combine(Path.GetTempPath(), "IrsCommitStorage", Guid.NewGuid().ToString("N"));
        var originalFolder = Path.Combine(
            shareRoot,
            "Files",
            "Image",
            "E81C",
            "2026",
            "06",
            "01",
            "08",
            "OK",
            "CELL-IRS-FOLDER");
        Directory.CreateDirectory(originalFolder);
        var originalPaths = new List<string>();
        foreach (var side in new[] { "UPPER", "LOWER" })
        {
            foreach (var index in Enumerable.Range(1, 3))
            {
                foreach (var kind in new[] { "Raw", "Overlay" })
                {
                    var file = Path.Combine(originalFolder, $"{side}_{index}_{kind}.jpg");
                    await File.WriteAllTextAsync(file, "image");
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-10));
                    originalPaths.Add(file);
                }
            }
        }

        var cropFolder = Path.Combine(
            shareRoot,
            "Files",
            "Image",
            "E81C",
            "2026",
            "06",
            "01",
            "Mavin",
            "Crop_micro",
            "01_OK_TOP");
        Directory.CreateDirectory(cropFolder);
        var crop = Path.Combine(
            cropFolder,
            "CELL-IRS_01-1_CA_080925_UPPER_1_Micro_LL_CL01_OK_P1.000_SourceMap.jpg");
        await File.WriteAllTextAsync(crop, "crop");
        File.SetLastWriteTimeUtc(crop, DateTime.UtcNow.AddMinutes(-10));

        var csv = Path.Combine(shareRoot, "#1-1 WELDING VISION(+)_JF2_20260601.csv");
        await File.WriteAllTextAsync(
            csv,
            string.Join(
                Environment.NewLine,
                "DATE,TIME,CELL-ID,UPPER_IMAGE-PATH-1,UPPER_OVERLAY-IMAGE-PATH-1,UPPER_IMAGE-PATH-2,UPPER_OVERLAY-IMAGE-PATH-2,UPPER_IMAGE-PATH-3,UPPER_OVERLAY-IMAGE-PATH-3,LOWER_IMAGE-PATH-1,LOWER_OVERLAY-IMAGE-PATH-1,LOWER_IMAGE-PATH-2,LOWER_OVERLAY-IMAGE-PATH-2,LOWER_IMAGE-PATH-3,LOWER_OVERLAY-IMAGE-PATH-3",
                string.Join(
                    ",",
                    [
                        "20260601",
                        "08:09:25",
                        "CELL-IRS",
                        .. originalPaths.Select(path => path.Replace(shareRoot, "E:"))
                    ])));
        var machine = new WeldingMachine("1-1-ca", "1-1", Polarity.Cathode, "127.0.0.1", ['E']);
        var candidate = new IrsReviewCandidate(
            "irs-key",
            "PACKAGE #1-1",
            "Welding Plus",
            "1-1(+)",
            new DateTime(2026, 6, 1, 8, 9, 25),
            "LOT",
            "CELL-IRS",
            "TOP",
            "raw.jpg",
            "NG",
            "Tab Folded",
            4);
        var service = new IrsReviewCommitService(
            new AppStorage(storageRoot),
            new StaticDailyCsvLocator([csv]),
            new TestSharePathResolver(shareRoot));

        try
        {
            var result = await service.CommitAsync(
                new(
                    machine,
                    candidate,
                    [
                        new(
                            "MICRO_LL",
                            "LL",
                            "Crop_micro",
                            IrsSelectionKind.Crop,
                            "Crop_micro",
                            "Micro_LL")
                    ]),
                CancellationToken.None);

            Assert.Equal(12, result.OriginalFilesCopied);
            Assert.Equal(1, result.CropFilesCopied);
            Assert.True(Directory.Exists(Path.Combine(
                storageRoot,
                "1-1(+)",
                "IRS_LEAK",
                "ORIGINAL",
                "CELL-IRS-FOLDER")));
            Assert.True(File.Exists(Path.Combine(
                storageRoot,
                "1-1(+)",
                "IRS_LEAK",
                "Crop_micro",
                Path.GetFileName(crop))));
            Assert.True(File.Exists(Path.Combine(storageRoot, "irs-reviews.json")));

            var rulebase = await service.CommitAsync(
                new(
                    machine,
                    candidate,
                    [
                        new(
                            "RULEBASE",
                            "Rulebase",
                            "RULEBASE",
                            IrsSelectionKind.Rulebase,
                            null,
                            null)
                    ]),
                CancellationToken.None);

            Assert.Equal(12, rulebase.OriginalFilesCopied);
            Assert.Equal(0, rulebase.CropFilesCopied);
            Assert.False(File.Exists(Path.Combine(
                storageRoot,
                "1-1(+)",
                "IRS_LEAK",
                "Crop_micro",
                Path.GetFileName(crop))));
            Assert.True(Directory.Exists(Path.Combine(
                storageRoot,
                "1-1(+)",
                "IRS_LEAK",
                "RULEBASE",
                "CELL-IRS-FOLDER")));

            var missingCrop = await service.CommitAsync(
                new(
                    machine,
                    candidate,
                    [
                        new(
                            "A_L",
                            "A L",
                            "Crop_A",
                            IrsSelectionKind.Crop,
                            "Crop_A",
                            "A_L")
                    ]),
                CancellationToken.None);

            Assert.Equal(24, missingCrop.OriginalFilesCopied);
            Assert.Equal(0, missingCrop.CropFilesCopied);
            Assert.True(Directory.Exists(Path.Combine(
                storageRoot,
                "1-1(+)",
                "IRS_LEAK",
                "ORIGINAL",
                "CELL-IRS-FOLDER")));
            Assert.True(Directory.Exists(Path.Combine(
                storageRoot,
                "1-1(+)",
                "IRS_LEAK",
                "NEED_TO_SIMULATE",
                "Crop_A",
                "CELL-IRS-FOLDER")));
        }
        finally
        {
            if (Directory.Exists(shareRoot)) Directory.Delete(shareRoot, true);
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, true);
        }
    }

    [Fact]
    public async Task IrsReviewCommitService_TabsideRightDoesNotCopyLeftFiles()
    {
        var shareRoot = Path.Combine(Path.GetTempPath(), "IrsTabsideShare", Guid.NewGuid().ToString("N"));
        var storageRoot = Path.Combine(Path.GetTempPath(), "IrsTabsideStorage", Guid.NewGuid().ToString("N"));
        var originalFolder = Path.Combine(shareRoot, "Files", "Image", "E81C", "2026", "06", "01", "08", "OK", "CELL-TAB-FOLDER");
        Directory.CreateDirectory(originalFolder);
        var original = Path.Combine(originalFolder, "CELL-TAB_UPPER_1_Raw.jpg");
        await File.WriteAllTextAsync(original, "image");
        File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddMinutes(-10));
        var cropFolder = Path.Combine(shareRoot, "Files", "Image", "E81C", "2026", "06", "01", "Mavin", "Crop_micro_tabside", "01_OK_TAB_SIDE");
        Directory.CreateDirectory(cropFolder);
        var left = Path.Combine(cropFolder, "CELL-TAB_01-1_CA_080925_UPPER_1_Tabside_L_SourceMap.jpg");
        var right = Path.Combine(cropFolder, "CELL-TAB_01-1_CA_080925_UPPER_1_Tabside_R_SourceMap.jpg");
        foreach (var file in new[] { left, right })
        {
            await File.WriteAllTextAsync(file, "crop");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-10));
        }
        var csv = Path.Combine(shareRoot, "#1-1 WELDING VISION(+)_JF2_20260601.csv");
        await File.WriteAllTextAsync(csv, $"DATE,TIME,CELL-ID,UPPER_IMAGE-PATH-1{Environment.NewLine}20260601,08:09:25,CELL-TAB,{original.Replace(shareRoot, "E:")}");
        var machine = new WeldingMachine("1-1-ca", "1-1", Polarity.Cathode, "127.0.0.1", ['E']);
        var candidate = new IrsReviewCandidate("tab-key", "PACKAGE #1-1", "Welding Plus", "1-1(+)", new DateTime(2026, 6, 1, 8, 9, 25), "LOT", "CELL-TAB", "TOP", "raw.jpg", "NG", "reason", 4);
        var service = new IrsReviewCommitService(new AppStorage(storageRoot), new StaticDailyCsvLocator([csv]), new TestSharePathResolver(shareRoot));

        try
        {
            await service.CommitAsync(new(machine, candidate, [new("TABSIDE_R", "Tabside R", "Crop_micro_tabside", IrsSelectionKind.Crop, "Crop_micro_tabside", "_R")]), CancellationToken.None);
            var destination = Path.Combine(storageRoot, "1-1(+)", "IRS_LEAK", "Crop_micro_tabside");
            Assert.False(File.Exists(Path.Combine(destination, Path.GetFileName(left))));
            Assert.True(File.Exists(Path.Combine(destination, Path.GetFileName(right))));
        }
        finally
        {
            if (Directory.Exists(shareRoot)) Directory.Delete(shareRoot, true);
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, true);
        }
    }

    [Fact]
    public async Task IrsDatasetService_LoadsNeedToSimulateCropFolderItems()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "IrsNeedToSimulateStorage", Guid.NewGuid().ToString("N"));
        var rawFolder = Path.Combine(storageRoot, "1-1(+)", "IRS_LEAK", "NEED_TO_SIMULATE", "Crop_A", "CELL-SIM-FOLDER");
        Directory.CreateDirectory(rawFolder);
        var upper1 = Path.Combine(rawFolder, "CELL-SIM_0_0.jpg");
        var upper2 = Path.Combine(rawFolder, "CELL-SIM_0_1.jpg");
        var upper3 = Path.Combine(rawFolder, "CELL-SIM_0_2.jpg");
        var upperOverlay = Path.Combine(rawFolder, "CELL-SIM_0_0_overlay.jpg");
        var lowerRaw = Path.Combine(rawFolder, "CELL-SIM_1_0.jpg");
        foreach (var file in new[] { upper1, upper2, upper3, upperOverlay, lowerRaw }) await File.WriteAllTextAsync(file, "image");

        var candidate = new IrsReviewCandidate("sim-key", "PACKAGE #1-1", "Welding Plus", "1-1(+)", new DateTime(2026, 6, 1, 8, 9, 25), "LOT", "CELL-SIM", "TOP", "raw.jpg", "NG", "reason", 4);
        var record = new IrsReviewRecord("sim-key", "1-1-ca", "1-1(+)", candidate.ProducedAt, "CELL-SIM", "TOP", "NG", "reason", ["A_L"], 3, 0, 0, storageRoot, DateTimeOffset.Now, [rawFolder]);

        try
        {
            var items = await new IrsDatasetService(new AppStorage(storageRoot)).BuildQueueAsync([candidate], [record], CancellationToken.None);
            var item = Assert.Single(items);
            Assert.True(item.IsNeedToSimulate);
            Assert.Equal("Crop_A", item.SourceFolder);
            Assert.Equal("NEED_TO_SIMULATE", item.OriginalClass);
            Assert.Equal([upper1, upper2, upper3], item.ImagePaths);
            Assert.Contains("01_OK_TOP_CATHODE", item.AllowedClasses);
        }
        finally
        {
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, true);
        }
    }

    [Fact]
    public async Task IrsDatasetService_SummaryCopiesNeedToSimulateWholeOriginalFolder()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "IrsNeedToSimulateSummary", Guid.NewGuid().ToString("N"));
        var rawFolder = Path.Combine(storageRoot, "1-1(+)", "IRS_LEAK", "NEED_TO_SIMULATE", "SEPA", "CELL-SEPA-FOLDER");
        Directory.CreateDirectory(rawFolder);
        var files = new[]
        {
            Path.Combine(rawFolder, "CELL-SEPA_0_0.jpg"),
            Path.Combine(rawFolder, "CELL-SEPA_0_1.jpg"),
            Path.Combine(rawFolder, "CELL-SEPA_0_2.jpg"),
            Path.Combine(rawFolder, "CELL-SEPA_0_0_overlay.jpg"),
            Path.Combine(rawFolder, "CELL-SEPA_1_0.jpg"),
            Path.Combine(rawFolder, "CELL-SEPA_1_0_overlay.jpg")
        };
        foreach (var file in files) await File.WriteAllTextAsync(file, "image");

        var candidate = new IrsReviewCandidate("sepa-key", "PACKAGE #1-1", "Welding Plus", "1-1(+)", new DateTime(2026, 6, 15, 9, 59, 23), "LOT", "CELL-SEPA", "TOP", "raw.jpg", "NG", "reason", 4);
        var record = new IrsReviewRecord("sepa-key", "1-1-ca", "1-1(+)", candidate.ProducedAt, "CELL-SEPA", "TOP", "NG", "reason", ["SEPA"], 6, 0, 0, storageRoot, DateTimeOffset.Now, [rawFolder]);
        var service = new IrsDatasetService(new AppStorage(storageRoot));

        try
        {
            var items = await service.BuildQueueAsync([candidate], [record], CancellationToken.None);
            var item = Assert.Single(items);
            Assert.Equal([files[0], files[1], files[2]], item.ImagePaths);
            await service.SaveDecisionAsync(item, ["Real"], false, CancellationToken.None);

            var result = await service.WriteSummaryAsync([candidate], [record], items, CancellationToken.None);
            var destination = Path.Combine(result.OutputFolder, "Dataset", "NEED_TO_SIMULATE", "SEPA", "CELL-SEPA-FOLDER");
            Assert.True(Directory.Exists(destination));
            foreach (var file in files)
            {
                Assert.True(File.Exists(Path.Combine(destination, Path.GetFileName(file))));
            }
            Assert.False(Directory.Exists(Path.Combine(result.OutputFolder, "Dataset", "SEPA", "Real")));
        }
        finally
        {
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, true);
        }
    }

    [Fact]
    public async Task IrsDatasetService_SummaryIgnoresUnclassifiedNeedToSimulateItems()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "IrsNeedToSimulateUnclassified", Guid.NewGuid().ToString("N"));
        var rawFolder = Path.Combine(storageRoot, "1-1(+)", "IRS_LEAK", "NEED_TO_SIMULATE", "SEPA", "CELL-SEPA-FOLDER");
        Directory.CreateDirectory(rawFolder);
        await File.WriteAllTextAsync(Path.Combine(rawFolder, "CELL-SEPA_0_0.jpg"), "image");
        await File.WriteAllTextAsync(Path.Combine(rawFolder, "CELL-SEPA_0_1.jpg"), "image");
        await File.WriteAllTextAsync(Path.Combine(rawFolder, "CELL-SEPA_0_2.jpg"), "image");

        var candidate = new IrsReviewCandidate("sepa-unclassified-key", "PACKAGE #1-1", "Welding Plus", "1-1(+)", new DateTime(2026, 6, 15, 9, 59, 23), "LOT", "CELL-SEPA", "TOP", "raw.jpg", "NG", "reason", 4);
        var record = new IrsReviewRecord("sepa-unclassified-key", "1-1-ca", "1-1(+)", candidate.ProducedAt, "CELL-SEPA", "TOP", "NG", "reason", ["SEPA"], 3, 0, 0, storageRoot, DateTimeOffset.Now, [rawFolder]);
        var service = new IrsDatasetService(new AppStorage(storageRoot));

        try
        {
            var items = await service.BuildQueueAsync([candidate], [record], CancellationToken.None);
            var item = Assert.Single(items);
            Assert.True(item.IsNeedToSimulate);

            var result = await service.WriteSummaryAsync([candidate], [record], items, CancellationToken.None);
            Assert.False(Directory.Exists(Path.Combine(result.OutputFolder, "Dataset", "NEED_TO_SIMULATE")));
        }
        finally
        {
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, true);
        }
    }

    [Fact]
    public async Task IrsDatasetService_PairsSourceMapAndActiveMapByCropSelection()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "IrsDatasetStorage", Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(storageRoot, "1-1(+)", "IRS_LEAK", "Crop_A");
        Directory.CreateDirectory(folder);
        var files = new[]
        {
            Path.Combine(folder, "CELL-PAIR_UPPER_1_A_L_CL01_OK_SourceMap.jpg"),
            Path.Combine(folder, "CELL-PAIR_UPPER_1_A_R_CL01_OK_SourceMap.jpg"),
            Path.Combine(folder, "CELL-PAIR_UPPER_1_A_L_CL01_OK_ActiveMap.jpg"),
            Path.Combine(folder, "CELL-PAIR_UPPER_1_A_R_CL01_OK_ActiveMap.jpg")
        };
        foreach (var file in files) await File.WriteAllTextAsync(file, "image");
        var candidate = new IrsReviewCandidate("pair-key", "PACKAGE #1-1", "Welding Plus", "1-1(+)", new DateTime(2026, 6, 1, 8, 9, 25), "LOT", "CELL-PAIR", "TOP", "raw.jpg", "NG", "reason", 4);
        var record = new IrsReviewRecord("pair-key", "1-1-ca", "1-1(+)", candidate.ProducedAt, "CELL-PAIR", "TOP", "NG", "reason", ["A_L", "A_R"], 0, 4, 0, storageRoot, DateTimeOffset.Now, files);

        try
        {
            var items = await new IrsDatasetService(new AppStorage(storageRoot)).BuildQueueAsync([candidate], [record], CancellationToken.None);
            Assert.Equal(2, items.Count);
            Assert.Contains(items, item => item.ImagePaths.Count == 2
                && item.ImagePaths.All(path => Path.GetFileName(path).Contains("A_L", StringComparison.OrdinalIgnoreCase))
                && item.ImagePaths.Any(path => Path.GetFileName(path).Contains("SourceMap", StringComparison.OrdinalIgnoreCase))
                && item.ImagePaths.Any(path => Path.GetFileName(path).Contains("ActiveMap", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(items, item => item.ImagePaths.Count == 2
                && item.ImagePaths.All(path => Path.GetFileName(path).Contains("A_R", StringComparison.OrdinalIgnoreCase))
                && item.ImagePaths.Any(path => Path.GetFileName(path).Contains("SourceMap", StringComparison.OrdinalIgnoreCase))
                && item.ImagePaths.Any(path => Path.GetFileName(path).Contains("ActiveMap", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, true);
        }
    }

    [Fact]
    public async Task IrsDatasetService_SummaryPreservesOriginalCropFileNames()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "IrsDatasetNames", Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(storageRoot, "1-1(+)", "IRS_LEAK", "Crop_A");
        Directory.CreateDirectory(folder);
        var files = new[]
        {
            Path.Combine(folder, "CELL-NAME_UPPER_1_A_L_CL01_OK_SourceMap.jpg"),
            Path.Combine(folder, "CELL-NAME_UPPER_1_A_L_CL01_OK_ActiveMap.jpg")
        };
        foreach (var file in files) await File.WriteAllTextAsync(file, "image");
        var candidate = new IrsReviewCandidate("name-key", "PACKAGE #1-1", "Welding Plus", "1-1(+)", new DateTime(2026, 6, 1, 8, 9, 25), "LOT", "CELL-NAME", "TOP", "raw.jpg", "NG", "reason", 4);
        var record = new IrsReviewRecord("name-key", "1-1-ca", "1-1(+)", candidate.ProducedAt, "CELL-NAME", "TOP", "NG", "reason", ["A_L"], 0, 2, 0, storageRoot, DateTimeOffset.Now, files);
        var service = new IrsDatasetService(new AppStorage(storageRoot));

        try
        {
            var items = await service.BuildQueueAsync([candidate], [record], CancellationToken.None);
            var item = Assert.Single(items);
            await service.SaveDecisionAsync(item, ["01_OK_TOP_CATHODE"], false, CancellationToken.None);
            var result = await service.WriteSummaryAsync([candidate], [record], items, CancellationToken.None);
            var destination = Path.Combine(result.OutputFolder, "Dataset", "Crop_A", "01_OK_TOP_CATHODE");

            foreach (var file in files)
            {
                Assert.True(File.Exists(Path.Combine(destination, Path.GetFileName(file))));
                Assert.False(File.Exists(Path.Combine(destination, $"CELL-NAME_{Path.GetFileName(file)}")));
            }
        }
        finally
        {
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, true);
        }
    }
    private sealed class TestSharePathResolver(string root) : ISharePathResolver
    {
        public string GetRoot(WeldingMachine machine, char drive) => root;

        public void RecordAccessibleRoot(
            WeldingMachine machine,
            char drive,
            string accessibleRoot)
        {
        }
    }

    private sealed class EmptyDailyCsvLocator : IDailyCsvLocator
    {
        public Task<IReadOnlyList<string>> FindAsync(
            WeldingMachine machine,
            DateOnly date,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StaticDailyCsvLocator(IReadOnlyList<string> files) : IDailyCsvLocator
    {
        public Task<IReadOnlyList<string>> FindAsync(
            WeldingMachine machine,
            DateOnly date,
            CancellationToken cancellationToken) =>
            Task.FromResult(files);
    }

    private static void CreateIrsWorkbook(
        string path,
        IReadOnlyList<IReadOnlyList<string>> dataRows,
        bool useNgOutHeaders = false)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteZipEntry(
            archive,
            "[Content_Types].xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        WriteZipEntry(
            archive,
            "_rels/.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        WriteZipEntry(
            archive,
            "xl/_rels/workbook.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);
        WriteZipEntry(
            archive,
            "xl/workbook.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        WriteZipEntry(archive, "xl/worksheets/sheet1.xml", IrsWorksheetXml(dataRows, useNgOutHeaders));
    }

    private static string IrsWorksheetXml(
        IReadOnlyList<IReadOnlyList<string>> dataRows,
        bool useNgOutHeaders)
    {
        var row1 = useNgOutHeaders
            ? new[]
            {
                "", "Eqpt", "Vision Type", "Re-inspection target", "Process Method",
                "Prod. date", "Batch Order", "Sort Order", "Lot ID", "Cell ID",
                "Camera Location", "Camera Number", "Group Rank", "Group Count",
                "Group Category", "Tail cell rank", "Tail cell defect ranking", "Image",
                "1st judgment", "", "", "", "2nd judgment", "", "", "", "NG Out",
                "", "", "", "", "", "Hold/Release", "", ""
            }
            : new[]
        {
            "", "Eqpt", "Vision Type", "Re-inspection target", "Process Method",
            "Prod. date", "Batch Order", "Sort Order", "Lot ID", "Cell ID",
            "Camera Location", "Camera Number", "Group Rank", "Group Count",
            "Group Category", "Tail cell rank", "Tail cell defect ranking", "Image",
            "1st judgment", "", "", "", "2nd judgment", "", "", "", "PKG ID",
            "Request for NG Cell OUT", "Hold/Release"
        };
        var row2 = useNgOutHeaders
            ? new[]
            {
                "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "",
                "", "Result", "reason", "Inspector", "completion date", "Result", "reason",
                "Inspector", "completion date", "DateTime", "Result", "Judgment grade",
                "Message", "PKG ID", "Request", "DateTime", "Inspector", "Result"
            }
            : new[]
        {
            "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "",
            "", "Result", "reason", "Inspector", "completion date", "Result", "reason",
            "Inspector", "completion date", "", "", ""
        };
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.AppendLine("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        AppendIrsRow(builder, 1, row1);
        AppendIrsRow(builder, 2, row2);
        for (var index = 0; index < dataRows.Count; index++)
        {
            AppendIrsRow(builder, index + 3, dataRows[index]);
        }
        builder.AppendLine("</sheetData></worksheet>");
        return builder.ToString();
    }

    private static void AppendIrsRow(
        StringBuilder builder,
        int rowNumber,
        IReadOnlyList<string> values)
    {
        builder.Append("<row r=\"").Append(rowNumber).Append("\">");
        for (var index = 0; index < values.Count; index++)
        {
            if (string.IsNullOrEmpty(values[index])) continue;
            builder
                .Append("<c r=\"")
                .Append(ColumnName(index))
                .Append(rowNumber)
                .Append("\" t=\"inlineStr\"><is><t>")
                .Append(SecurityElement.Escape(values[index]))
                .Append("</t></is></c>");
        }
        builder.AppendLine("</row>");
    }
    private static string ColumnName(int index)
    {
        var name = string.Empty;
        index++;
        while (index > 0)
        {
            var remainder = (index - 1) % 26;
            name = (char)('A' + remainder) + name;
            index = (index - 1) / 26;
        }
        return name;
    }

    private static void WriteZipEntry(ZipArchive archive, string name, string contents)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(contents);
    }

    private static InspectionSummaryRecord SummaryRecord(
        WeldingMachine machine,
        DateTime inspectedAt,
        string cellId,
        string judge,
        string defect)
    {
        var normalizedDefect = KickoutRules.NormalizeDefect(defect);
        var key = string.Join("|", machine.Id, inspectedAt.ToString("O"), "LOT", cellId);
        return new(
            machine.Id,
            inspectedAt,
            "LOT",
            cellId,
            judge,
            normalizedDefect,
            judge.Equals("NG", StringComparison.OrdinalIgnoreCase) ? NgSide.Upper : NgSide.None,
            key,
            ["DATE", "TIME", "LOT-ID", "CELL-ID", "JUDGE", "JUDGE-DEFECT"],
            [
                inspectedAt.ToString("yyyyMMdd"),
                inspectedAt.ToString("HH:mm:ss"),
                "LOT",
                cellId,
                judge,
                defect
            ],
            "source.csv");
    }

    private static DlngReviewItem DlngItem(
        WeldingMachine machine,
        string defect,
        string side,
        string cellId) =>
        new(
            $"key|{defect}|{side}|{cellId}",
            machine.Id,
            machine.OutputFolderName,
            machine.Polarity,
            new DateTime(2026, 6, 23, 1, 2, 3),
            "E81C",
            "LOT",
            cellId,
            "DLNG",
            defect,
            side,
            string.Empty,
            string.Empty,
            DlngModelKind.FallbackRaw,
            [],
            "source.csv",
            2,
            string.Empty);

    private sealed class FakeShareResolver(string root) : ISharePathResolver
    {
        public string GetRoot(WeldingMachine machine, char drive) => root;

        public void RecordAccessibleRoot(WeldingMachine machine, char drive, string root)
        {
        }
    }

    private sealed class FakeLocator : IDailyCsvLocator
    {
        public Task<IReadOnlyList<string>> FindAsync(
            WeldingMachine machine,
            DateOnly date,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["fake.csv"]);
    }

    private sealed class SingleFileLocator(string path, DateOnly date) : IDailyCsvLocator
    {
        public Task<IReadOnlyList<string>> FindAsync(
            WeldingMachine machine,
            DateOnly requestedDate,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(requestedDate == date ? [path] : []);
    }

    private sealed class FakeSnapshotService : IReadOnlySnapshotService
    {
        public Task<SnapshotResult> CreateAsync(
            string sourceCsv,
            bool currentDate,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotResult(sourceCsv, sourceCsv, false, null));
    }

    private sealed class FakeSummaryReader(
        IReadOnlyList<InspectionSummaryRecord> records) : IInspectionSummaryCsvReader
    {
        public async IAsyncEnumerable<InspectionSummaryRecord> ReadAsync(
            WeldingMachine machine,
            SnapshotResult snapshot,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var record in records.Where(record => record.MachineId == machine.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return record;
            }
        }
    }

    private sealed class FakeReviewStore(
        IReadOnlyList<ReviewEntry> entries) : IReviewStore
    {
        public Task<IReadOnlyDictionary<string, ReviewEntry>> LoadAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, ReviewEntry>>(
                entries.ToDictionary(entry => entry.CandidateKey, StringComparer.OrdinalIgnoreCase));

        public Task SaveAsync(ReviewEntry entry, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeSummaryWriter : ISummaryReportWriter
    {
        public IReadOnlyList<SummaryReportRow> Rows { get; private set; } = [];

        public Task<string> WriteAsync(
            DateOnly reportDate,
            DateTime windowStart,
            DateTime windowEndExclusive,
            IReadOnlyList<SummaryReportRow> rows,
            IReadOnlyList<SummaryDetailRow> details,
            CancellationToken cancellationToken)
        {
            Rows = rows;
            return Task.FromResult("summary.csv");
        }
    }

    private sealed class FakeMachineRegistry(IReadOnlyList<WeldingMachine> machines) : IMachineRegistry
    {
        public IReadOnlyList<WeldingMachine> All { get; } = machines;

        public WeldingMachine Get(string id) =>
            All.First(machine => machine.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeIrsWorkbookReader(
        IReadOnlyList<IrsReviewCandidate> rows) : IIrsWorkbookReader
    {
        public Task<IReadOnlyList<IrsReviewCandidate>> ReadRequestedAsync(
            string workbookPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(rows);
    }

    private sealed class FakeIrsImageLocator(string path) : IIrsRawImageLocator
    {
        public Task<IrsImageLookupResult> FindAsync(
            WeldingMachine machine,
            IrsReviewCandidate candidate,
            CancellationToken cancellationToken) =>
            Task.FromResult(new IrsImageLookupResult([path], "found"));
    }
}
