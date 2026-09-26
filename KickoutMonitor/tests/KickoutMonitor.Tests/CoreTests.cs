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
                Path.Combine("OVERKILL", "B_DIM", InspectionIdentity.Hash(candidate.Key), Path.GetFileName(source)),
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
                Path.Combine("NG", "MULTI-NG", InspectionIdentity.Hash(candidate.Key), Path.GetFileName(source)),
                result.Destination!,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Ignore_DoesNotCopyImages()
    {
        var root = Path.Combine(Path.GetTempPath(), "KickoutMonitorTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source", "20260609_224553_LOT_CELL");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "raw.jpg"), [0xFF, 0xD8, 0x00, 0xFF, 0xD9]);

        var storage = new AppStorage(Path.Combine(root, "output"));
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
                ReviewDecision.Ignore,
                CancellationToken.None);

            Assert.Equal(CopyState.NotRequested, result.State);
            Assert.Null(result.Destination);
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(storage.MachineRoot(machine), "NG")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(storage.MachineRoot(machine), "OVERKILL")));
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
        Assert.Contains(settings.IrsRules.FinalClassGroups, group => group.Folder == "SEPA" && group.Classes.SequenceEqual(["Real", "Overkill", "No Need to Retrain"]));
    }

    [Fact]
    public void DlngCropAClasses_FollowReviewSide()
    {
        var settings = VisionMasterSettings.CreateDefault();

        var upperAnode = DlngRules.ClassesFor("Crop_A", Polarity.Anode, settings, "UPPER");
        var lowerAnode = DlngRules.ClassesFor("Crop_A", Polarity.Anode, settings, "LOWER");
        var lowerCathode = DlngRules.ClassesFor("Crop_A", Polarity.Cathode, settings, "LOWER");

        Assert.Contains("01_OK_TOP_ANODE", upperAnode);
        Assert.DoesNotContain("02_OK_BACK_ANODE", upperAnode);
        Assert.Contains("02_OK_BACK_ANODE", lowerAnode);
        Assert.DoesNotContain("01_OK_TOP_ANODE", lowerAnode);
        Assert.Contains("03_NG_TORN", lowerAnode);
        Assert.Contains("02_OK_BACK_CATHODE", lowerCathode);
        Assert.DoesNotContain("01_OK_TOP_CATHODE", lowerCathode);
    }

    [Fact]
    public void DlngCropMicroClasses_FollowReviewSide()
    {
        var settings = VisionMasterSettings.CreateDefault();

        var upper = DlngRules.ClassesFor("Crop_micro", Polarity.Anode, settings, "UPPER");
        var lower = DlngRules.ClassesFor("Crop_micro", Polarity.Anode, settings, "LOWER");

        Assert.Contains("01_OK_TAB", upper);
        Assert.DoesNotContain("02_OK_BTM", upper);
        Assert.Contains("02_OK_BTM", lower);
        Assert.DoesNotContain("01_OK_TAB", lower);
        Assert.Contains("03_OK_QNG_DENT", upper);
        Assert.Contains("04_NG_TORN_DENT", lower);
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
    public async Task DlngCsvReader_UsesGapSpecificSideForRulebaseNg()
    {
        var csv = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
        var headers = new[]
        {
            "DATE", "TIME", "MODEL-ID", "LOT-ID", "CELL-ID", "JUDGE", "JUDGE-DEFECT",
            "UPPER_JUDGE", "LOWER_JUDGE", "UPPER_GAP_DL-JUDGE", "LOWER_GAP_DL-JUDGE",
            "UPPER_IMAGE-PATH-1", "UPPER_IMAGE-PATH-2", "UPPER_IMAGE-PATH-3",
            "LOWER_IMAGE-PATH-1", "LOWER_IMAGE-PATH-2", "LOWER_IMAGE-PATH-3"
        };
        var values = new[]
        {
            "20260623", "07:00:00", "E81C", "LOT", "CELL-GAP", "NG", "GAP",
            "NG", "NG", "OK", "NG",
            @"E:\Files\Image\Raw\CELL-GAP\upper1.jpg",
            @"E:\Files\Image\Raw\CELL-GAP\upper2.jpg",
            @"E:\Files\Image\Raw\CELL-GAP\upper3.jpg",
            @"E:\Files\Image\Raw\CELL-GAP\lower1.jpg",
            @"E:\Files\Image\Raw\CELL-GAP\lower2.jpg",
            @"E:\Files\Image\Raw\CELL-GAP\lower3.jpg"
        };
        await File.WriteAllLinesAsync(csv, [string.Join(",", headers), string.Join(",", values)]);
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
            Assert.Equal("LOWER", result.Side);
            Assert.All(result.Images, image => Assert.Contains("lower", image.Path, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(csv);
        }
    }

    [Fact]
    public async Task DlngCsvReader_UsesDefectSpecificSideForSepaRulebaseNg()
    {
        var csv = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
        var headers = new[]
        {
            "DATE", "TIME", "MODEL-ID", "LOT-ID", "CELL-ID", "JUDGE", "JUDGE-DEFECT",
            "UPPER_JUDGE", "LOWER_JUDGE", "UPPER_SEPA-JUDGE", "LOWER_SEPA-JUDGE",
            "UPPER_IMAGE-PATH-1", "UPPER_IMAGE-PATH-2", "UPPER_IMAGE-PATH-3",
            "LOWER_IMAGE-PATH-1", "LOWER_IMAGE-PATH-2", "LOWER_IMAGE-PATH-3"
        };
        var values = new[]
        {
            "20260623", "07:00:00", "E81C", "LOT", "CELL-SEPA", "NG", "SEPA",
            "NG", "NG", "NG", "OK",
            @"E:\Files\Image\Raw\CELL-SEPA\upper1.jpg",
            @"E:\Files\Image\Raw\CELL-SEPA\upper2.jpg",
            @"E:\Files\Image\Raw\CELL-SEPA\upper3.jpg",
            @"E:\Files\Image\Raw\CELL-SEPA\lower1.jpg",
            @"E:\Files\Image\Raw\CELL-SEPA\lower2.jpg",
            @"E:\Files\Image\Raw\CELL-SEPA\lower3.jpg"
        };
        await File.WriteAllLinesAsync(csv, [string.Join(",", headers), string.Join(",", values)]);
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
            Assert.Equal("UPPER", result.Side);
            Assert.All(result.Images, image => Assert.Contains("upper", image.Path, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(csv);
        }
    }

    [Fact]
    public async Task DlngCsvReader_AcceptsSepaShoulderDlJudgeDefectAlias()
    {
        var csv = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
        var headers = new[]
        {
            "DATE", "TIME", "MODEL-ID", "LOT-ID", "CELL-ID", "JUDGE", "JUDGE-DEFECT",
            "UPPER_JUDGE", "LOWER_JUDGE", "UPPER_SEPA_SHOULDER_DL-JUDGE", "LOWER_SEPA_SHOULDER_DL-JUDGE",
            "UPPER_IMAGE-PATH-1", "UPPER_IMAGE-PATH-2", "UPPER_IMAGE-PATH-3",
            "LOWER_IMAGE-PATH-1", "LOWER_IMAGE-PATH-2", "LOWER_IMAGE-PATH-3"
        };
        var values = new[]
        {
            "20260623", "07:00:00", "E81C", "LOT", "CELL-SEPA-SHOULDER", "DLNG", "SEPA_SHOULDER_DL",
            "OK", "OK", "BYPASS_NG", "OK",
            @"E:\Files\Image\Raw\CELL-SEPA-SHOULDER\upper1.jpg",
            @"E:\Files\Image\Raw\CELL-SEPA-SHOULDER\upper2.jpg",
            @"E:\Files\Image\Raw\CELL-SEPA-SHOULDER\upper3.jpg",
            @"E:\Files\Image\Raw\CELL-SEPA-SHOULDER\lower1.jpg",
            @"E:\Files\Image\Raw\CELL-SEPA-SHOULDER\lower2.jpg",
            @"E:\Files\Image\Raw\CELL-SEPA-SHOULDER\lower3.jpg"
        };
        await File.WriteAllLinesAsync(csv, [string.Join(",", headers), string.Join(",", values)]);
        try
        {
            var settings = VisionMasterSettings.CreateDefault();
            settings.DlngRules.DefectMappings.RemoveAll(x => x.Defect.Equals("SEPA_SHOULDER_DL", StringComparison.OrdinalIgnoreCase));
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "127.0.0.1", ['E']);
            var snapshot = new SnapshotResult(csv, csv, false, null);
            var items = new List<DlngReviewItem>();
            await foreach (var item in new DlngCsvReader(new SharePathResolver(), settings)
                               .ReadAsync(machine, snapshot, null, CancellationToken.None))
            {
                items.Add(item);
            }

            var result = Assert.Single(items);
            Assert.Equal("SEPA_SHOULDER", result.JudgeDefect);
            Assert.Equal("UPPER", result.Side);
            Assert.All(result.Images, image => Assert.Contains("upper", image.Path, StringComparison.OrdinalIgnoreCase));
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
    public async Task DlngCropLocator_UsesRawImageDriveForCropLookup()
    {
        var root = Path.Combine(Path.GetTempPath(), "DlngDriveCropTests", Guid.NewGuid().ToString("N"));
        var eCropRoot = Path.Combine(root, "E", "Files", "Image", "E81C", "2026", "06", "23", "Mavin", "Crop_A", "03_NG_TORN");
        var fCropRoot = Path.Combine(root, "F", "Files", "Image", "E81C", "2026", "06", "23", "Mavin", "Crop_A", "04_NG_PTCL");
        Directory.CreateDirectory(eCropRoot);
        Directory.CreateDirectory(fCropRoot);
        await File.WriteAllBytesAsync(Path.Combine(eCropRoot, "CELL-DRIVE_01-1_AN_010203_UPPER_1_A_L_CL03_NG_P1.000_SourceMap.jpg"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(eCropRoot, "CELL-DRIVE_01-1_AN_010203_UPPER_1_A_L_CL03_NG_P1.000_ActiveMap.jpg"), [2]);
        var fSource = Path.Combine(fCropRoot, "CELL-DRIVE_01-1_AN_010203_UPPER_1_A_L_CL04_NG_P1.000_SourceMap.jpg");
        var fActive = Path.Combine(fCropRoot, "CELL-DRIVE_01-1_AN_010203_UPPER_1_A_L_CL04_NG_P1.000_ActiveMap.jpg");
        await File.WriteAllBytesAsync(fSource, [3]);
        await File.WriteAllBytesAsync(fActive, [4]);
        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "127.0.0.1", ['E', 'F']);
            var item = DlngItem(machine, "A_L", "UPPER", "CELL-DRIVE") with
            {
                Images = [new("Raw 1", @"\\127.0.0.1\F\Files\Image\E81C\2026\06\23\01\OK\CELL-DRIVE\raw.jpg", false)]
            };
            var expanded = await new DlngCropLocator(new DriveShareResolver(root))
                .ExpandAsync(machine, item, null, CancellationToken.None);

            var result = Assert.Single(expanded);
            Assert.Equal("04_NG_PTCL", result.SourceClass);
            Assert.Equal(
                [fActive, fSource],
                result.Images.Select(image => image.Path).OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
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
    public async Task DlngCropLocator_OnlyExpandsSelectedCropFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "DlngSelectedFolderTests", Guid.NewGuid().ToString("N"));
        var horn = Path.Combine(root, "Files", "Image", "E81C", "2026", "06", "23", "Mavin", "HORNMARK");
        var lead = Path.Combine(root, "Files", "Image", "E81C", "2026", "06", "23", "Mavin", "LEADEDGE");
        Directory.CreateDirectory(horn);
        Directory.CreateDirectory(lead);
        await File.WriteAllBytesAsync(Path.Combine(horn, "CELL-SEL_01-1_AN_010203_UPPER_1_HORN MARK L_f0_SourceImg.jpg"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(horn, "CELL-SEL_01-1_AN_010203_UPPER_1_HORN MARK L_f0_SourceImg_mask.png"), [2]);
        await File.WriteAllBytesAsync(Path.Combine(lead, "CELL-SEL_01-1_AN_010203_UPPER_1_LEAD EDGE L_SourceImg.jpg"), [3]);
        await File.WriteAllBytesAsync(Path.Combine(lead, "CELL-SEL_01-1_AN_010203_UPPER_1_LEAD EDGE L_SourceImg.png"), [4]);
        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "unused", ['E']);
            var item = DlngItem(machine, "B_DIM_L", "UPPER", "CELL-SEL");
            var expanded = await new DlngCropLocator(new FakeShareResolver(root))
                .ExpandAsync(machine, item, null, CancellationToken.None, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HORNMARK" });

            var result = Assert.Single(expanded);
            Assert.Equal("HORNMARK", result.CropFolder);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DlngCropLocator_HornmarkMatchesCathodeHornLeftNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "DlngHornLeftTests", Guid.NewGuid().ToString("N"));
        var horn = Path.Combine(root, "Files", "Image", "E81C", "2026", "06", "23", "Mavin", "HORNMARK");
        Directory.CreateDirectory(horn);
        await File.WriteAllBytesAsync(Path.Combine(horn, "CELL-HORN_01-1_CA_000724_UPPER_1_HORN LEFT_f0_SourceImg.jpg"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(horn, "CELL-HORN_01-1_CA_000724_UPPER_1_HORN LEFT_f0_SourceImg_mask.png"), [2]);
        try
        {
            var machine = new WeldingMachine("1-1-ca", "1-1", Polarity.Cathode, "unused", ['E']);
            var item = DlngItem(machine, "B_DIM_L", "UPPER", "CELL-HORN");
            item = item with { Inspection = item.Inspection! with { ImageAt = new DateTime(2026, 6, 23, 0, 7, 24) } };
            var expanded = await new DlngCropLocator(new FakeShareResolver(root))
                .ExpandAsync(machine, item, null, CancellationToken.None);

            var hornmark = Assert.Single(expanded.Where(x => x.CropFolder == "HORNMARK"));
            Assert.Equal(2, hornmark.Images.Count);
            Assert.DoesNotContain(expanded, x => x.CropFolder == "HORNMARK" && x.ModelKind == DlngModelKind.FallbackRaw);
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
            "OK", "OK", "BYPASS_NG", "OK", @"E:\Files\Image\20260623_070000_LOT_CELL-RPT_EXT_0_0.jpg", "", ""
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

            await CollectFixtureReviewsAsync(storage, reviews);

            var result = await new DlngReportGenerator(queue, reviews, storage)
                .GenerateAsync([machine], new DateOnly(2026, 6, 23), null, CancellationToken.None);

            Assert.True(File.Exists(result.SummaryWorkbook));
            Assert.Equal(Path.Combine(storage.DlngReport, "REPORT", "DLNG_REPORT_20260623"), result.OutputFolder);
            var destination = Path.Combine(result.OutputFolder, "Dataset", "Classification", "미검_오검", "Crop_A", "1-1(-)", "04_NG_PTCL");
            Assert.True(Directory.Exists(destination));
            Assert.True(ExportExists(Path.Combine(destination, ModelSuffixedFileName(source, "Crop_A"))));
            Assert.False(ExportExists(Path.Combine(destination, ModelSuffixedFileName(active, "Crop_A"))));
            Assert.False(File.Exists(Path.Combine(destination, Path.GetFileName(source))));
            Assert.False(File.Exists(Path.Combine(destination, Path.GetFileName(active))));
            Assert.False(File.Exists(Path.Combine(destination, $"1-1(-)_CELL-RPT_A_L_{Path.GetFileName(source)}")));
            Assert.False(File.Exists(Path.Combine(destination, $"1-1(-)_CELL-RPT_A_L_{Path.GetFileName(active)}")));
            Assert.False(Directory.Exists(Path.Combine(result.OutputFolder, "Dataset", "Crop_A", "04_NG_PTCL")));
            var summary = Assert.Single(result.Rows);
            Assert.Equal("Classification/미검_오검", summary.DatasetSection);
            Assert.Equal(1, summary.SwitchedCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DlngReport_GeneratesFromReviewedSubsetWhenQueueIsIncomplete()
    {
        var root = Path.Combine(Path.GetTempPath(), "DlngPartialReportTests", Guid.NewGuid().ToString("N"));
        var csv = Path.Combine(root, "result.csv");
        var share = Path.Combine(root, "share");
        var cropA = Path.Combine(share, "Files", "Image", "E81C", "2026", "06", "23", "Mavin", "Crop_A", "03_NG_TORN");
        var cropB = Path.Combine(share, "Files", "Image", "E81C", "2026", "06", "23", "Mavin", "Crop_B", "02_NG_TORN");
        Directory.CreateDirectory(cropA);
        Directory.CreateDirectory(cropB);
        Directory.CreateDirectory(Path.GetDirectoryName(csv)!);
        var reviewedSource = Path.Combine(cropA, "CELL-REVIEWED_01-1_AN_070000_UPPER_1_A_L_SourceMap.jpg");
        var reviewedActive = Path.Combine(cropA, "CELL-REVIEWED_01-1_AN_070000_UPPER_1_A_L_ActiveMap.jpg");
        var pendingSource = Path.Combine(cropB, "CELL-PENDING_01-1_AN_071000_UPPER_1_B_L_SourceMap.jpg");
        var pendingActive = Path.Combine(cropB, "CELL-PENDING_01-1_AN_071000_UPPER_1_B_L_ActiveMap.jpg");
        await File.WriteAllBytesAsync(reviewedSource, [1]);
        await File.WriteAllBytesAsync(reviewedActive, [2]);
        await File.WriteAllBytesAsync(pendingSource, [3]);
        await File.WriteAllBytesAsync(pendingActive, [4]);
        var headers = new[]
        {
            "DATE", "TIME", "MODEL-ID", "LOT-ID", "CELL-ID", "JUDGE", "JUDGE-DEFECT",
            "UPPER_JUDGE", "LOWER_JUDGE", "UPPER_A_L-JUDGE", "LOWER_A_L-JUDGE",
            "UPPER_B_L-JUDGE", "LOWER_B_L-JUDGE", "UPPER_IMAGE-PATH-1", "UPPER_IMAGE-PATH-2", "UPPER_IMAGE-PATH-3"
        };
        var reviewedValues = new[]
        {
            "20260623", "07:00:00", "E81C", "LOT", "CELL-REVIEWED", "DLNG", "A_L",
            "OK", "OK", "BYPASS_NG", "OK", "OK", "OK", @"E:\Files\Image\20260623_070000_LOT_CELL-REVIEWED_EXT_0_0.jpg", "", ""
        };
        var pendingValues = new[]
        {
            "20260623", "07:10:00", "E81C", "LOT", "CELL-PENDING", "DLNG", "B_L",
            "OK", "OK", "OK", "OK", "BYPASS_NG", "OK", @"E:\Files\Image\20260623_071000_LOT_CELL-PENDING_EXT_0_0.jpg", "", ""
        };
        await File.WriteAllLinesAsync(csv, [
            string.Join(",", headers),
            string.Join(",", reviewedValues),
            string.Join(",", pendingValues)
        ]);

        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "unused", ['E']);
            var storage = new AppStorage(Path.Combine(root, "out"));
            storage.EnsureCreated([machine]);
            var reviews = new JsonDlngReviewStore(storage);
            var queue = new DlngQueueService(
                new SingleFileLocator(csv, new DateOnly(2026, 6, 23)),
                new FakeSnapshotService(),
                new DlngCsvReader(new FakeShareResolver(share)),
                new DlngCropLocator(new FakeShareResolver(share)));
            var items = await queue.LoadAsync(machine, new DateOnly(2026, 6, 23), null, CancellationToken.None);
            Assert.Equal(2, items.Count);
            var reviewed = Assert.Single(items.Where(x => x.CellId == "CELL-REVIEWED"));
            await reviews.SaveAsync(new(
                reviewed.Key,
                reviewed.MachineId,
                reviewed.LinePolarity,
                reviewed.InspectedAt,
                reviewed.CellId,
                reviewed.Judge,
                reviewed.JudgeDefect,
                reviewed.Side,
                reviewed.CropFolder,
                reviewed.SourceClass,
                "04_NG_PTCL",
                false,
                reviewed.Images.Select(x => x.Path).ToArray(),
                DateTimeOffset.Now), CancellationToken.None);

            await CollectFixtureReviewsAsync(storage, reviews);

            var result = await new DlngReportGenerator(queue, reviews, storage)
                .GenerateAsync([machine], new DateOnly(2026, 6, 23), null, CancellationToken.None);

            Assert.True(File.Exists(result.SummaryWorkbook));
            var summary = Assert.Single(result.Rows);
            Assert.Equal("Crop_A", summary.CropFolder);
            var category = summary.DatasetSection.Split('/')[1];
            Assert.True(Directory.Exists(Path.Combine(result.OutputFolder, "Dataset", "Classification", category, "Crop_A", "1-1(-)", "04_NG_PTCL")));
            Assert.False(Directory.Exists(Path.Combine(result.OutputFolder, "Dataset", "Classification", category, "Crop_B")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DlngReport_FromItemsOnlySummarizesProvidedQueueItems()
    {
        var root = Path.Combine(Path.GetTempPath(), "DlngQueuedOnlyReportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var includedImage = Path.Combine(root, "included_SourceMap.jpg");
        var excludedImage = Path.Combine(root, "excluded_SourceMap.jpg");
        await File.WriteAllBytesAsync(includedImage, [1]);
        await File.WriteAllBytesAsync(excludedImage, [2]);

        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "unused", ['E']);
            var storage = new AppStorage(Path.Combine(root, "out"));
            storage.EnsureCreated([machine]);
            var reviews = new JsonDlngReviewStore(storage);
            var included = DlngItem(machine, "A_L", "UPPER", "CELL-INCLUDED") with
            {
                Key = "included",
                InspectedAt = new DateTime(2026, 6, 23, 7, 0, 0),
                CropFolder = "Crop_A",
                SourceClass = "03_NG_TORN",
                ModelKind = DlngModelKind.Classification,
                Images = [new("SourceMap", includedImage, false)]
            };
            var excluded = included with
            {
                Key = "excluded",
                CellId = "CELL-EXCLUDED",
                CropFolder = "Crop_B",
                Images = [new("SourceMap", excludedImage, false)]
            };
            foreach (var item in new[] { included, excluded })
            {
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
            }

            await CollectFixtureReviewsAsync(storage, reviews);

            var report = await new DlngReportGenerator(
                    new DlngQueueService(new FakeLocator(), new FakeSnapshotService(), new DlngCsvReader(new FakeShareResolver(root)), new DlngCropLocator(new FakeShareResolver(root))),
                    reviews,
                    storage)
                .GenerateFromItemsAsync([included], new DateOnly(2026, 6, 23), null, CancellationToken.None);

            var row = Assert.Single(report.Rows);
            Assert.Equal("Crop_A", row.CropFolder);
            Assert.True(Directory.Exists(Path.Combine(report.OutputFolder, "Dataset", "Classification", row.DatasetSection.Split('/')[1], "Crop_A", "1-1(-)", "04_NG_PTCL")));
            Assert.False(Directory.Exists(Path.Combine(report.OutputFolder, "Dataset", "Classification", row.DatasetSection.Split('/')[1], "Crop_B")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DlngDatasetExport_FlattensReviewedCropsByModelDateRangeAndClass()
    {
        var root = Path.Combine(Path.GetTempPath(), "DlngFlatDatasetTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourceImage = Path.Combine(root, "CELL-FLAT_01-1_AN_070000_UPPER_1_A_L_SourceMap.jpg");
        var segmentationImage = Path.Combine(root, "CELL-FLAT-SEG_01-1_AN_070000_UPPER_1_BEAD_SourceImg.jpg");
        var noNeedImage = Path.Combine(root, "CELL-FLAT-SKIP_01-1_AN_070500_UPPER_1_BEAD_SourceImg.jpg");
        await File.WriteAllBytesAsync(sourceImage, [1]);
        await File.WriteAllBytesAsync(segmentationImage, [2]);
        await File.WriteAllBytesAsync(noNeedImage, [3]);

        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "unused", ['E']);
            var storage = new AppStorage(Path.Combine(root, "out"));
            storage.EnsureCreated([machine]);
            var reviews = new JsonDlngReviewStore(storage);
            var item = DlngItem(machine, "A_L", "UPPER", "CELL-FLAT") with
            {
                Key = "flat",
                InspectedAt = new DateTime(2026, 6, 24, 1, 30, 0),
                CropFolder = "Crop_A",
                SourceClass = "03_NG_TORN",
                ModelKind = DlngModelKind.Classification,
                Images = [new("SourceMap", sourceImage, false)]
            };
            var segmentation = DlngItem(machine, "BEAD_CNT", "UPPER", "CELL-FLAT-SEG") with
            {
                Key = "flat-seg",
                InspectedAt = new DateTime(2026, 6, 24, 2, 30, 0),
                CropFolder = "SEGMENTATION",
                SourceClass = "Segmentation",
                ModelKind = DlngModelKind.Segmentation,
                Images = [new("SourceImg", segmentationImage, false)]
            };
            var noNeed = segmentation with
            {
                Key = "flat-skip",
                CellId = "CELL-FLAT-SKIP",
                InspectedAt = new DateTime(2026, 6, 24, 2, 35, 0),
                Images = [new("SourceImg", noNeedImage, false)]
            };
            await SaveDlngReviewAsync(reviews, item, "04_NG_PTCL", false);
            await SaveDlngReviewAsync(reviews, segmentation, "Overkill", false);
            await SaveDlngReviewAsync(reviews, noNeed, "No Need to Train", false);
            await CollectFixtureReviewsAsync(storage, reviews);
            var generator = new DlngReportGenerator(
                new DlngQueueService(new FakeLocator(), new FakeSnapshotService(), new DlngCsvReader(new FakeShareResolver(root)), new DlngCropLocator(new FakeShareResolver(root))),
                reviews,
                storage);

            var result = await generator.GenerateDatasetFromItemsAsync(
                [item, segmentation, noNeed],
                new DateOnly(2026, 6, 23),
                new DateOnly(2026, 6, 24),
                null,
                CancellationToken.None);

            var destination = Path.Combine(storage.DlngReport, "DATASET", "Crop_A", "20260623-20260624", "04_NG_PTCL");
            Assert.Equal(Path.Combine(storage.DlngReport, "DATASET"), result.OutputFolder);
            Assert.Equal(3, result.CopiedCount);
            Assert.True(ExportExists(Path.Combine(destination, Path.GetFileName(sourceImage))));
            Assert.False(File.Exists(Path.Combine(destination, ModelSuffixedFileName(sourceImage, "Crop_A"))));
            Assert.True(ExportExists(Path.Combine(storage.DlngReport, "DATASET", "SEGMENTATION", "20260623-20260624", "Overkill", Path.GetFileName(segmentationImage))));
            Assert.False(File.Exists(Path.Combine(storage.DlngReport, "DATASET", "SEGMENTATION", "20260623-20260624", "No Need to Train", Path.GetFileName(noNeedImage))));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task DlngReport_PreservesExistingModelDatasetWhenGeneratingAnotherModelForSameDate()
    {
        var root = Path.Combine(Path.GetTempPath(), "DlngPreserveModelReportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cropAImage = Path.Combine(root, "crop-a_SourceMap.jpg");
        var gapImage = Path.Combine(root, "gap_SourceImg.jpg");
        await File.WriteAllBytesAsync(cropAImage, [1]);
        await File.WriteAllBytesAsync(gapImage, [2]);

        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "unused", ['E']);
            var storage = new AppStorage(Path.Combine(root, "out"));
            storage.EnsureCreated([machine]);
            var reviews = new JsonDlngReviewStore(storage);
            var cropA = DlngItem(machine, "A_L", "UPPER", "CELL-CROPA") with
            {
                Key = "crop-a",
                InspectedAt = new DateTime(2026, 6, 23, 7, 0, 0),
                CropFolder = "Crop_A",
                SourceClass = "03_NG_TORN",
                ModelKind = DlngModelKind.Classification,
                Images = [new("SourceMap", cropAImage, false)]
            };
            var gap = DlngItem(machine, "GAP_DL", "UPPER", "CELL-GAP") with
            {
                Key = "gap",
                InspectedAt = new DateTime(2026, 6, 23, 7, 5, 0),
                CropFolder = "Gap_DL",
                SourceClass = "Segmentation",
                ModelKind = DlngModelKind.Segmentation,
                Images = [new("SourceImg", gapImage, false)]
            };
            await SaveDlngReviewAsync(reviews, cropA, "04_NG_PTCL", false);
            await SaveDlngReviewAsync(reviews, gap, "Real", false);
            await CollectFixtureReviewsAsync(storage, reviews);
            var reportGenerator = new DlngReportGenerator(
                new DlngQueueService(new FakeLocator(), new FakeSnapshotService(), new DlngCsvReader(new FakeShareResolver(root)), new DlngCropLocator(new FakeShareResolver(root))),
                reviews,
                storage);

            var cropAReport = await reportGenerator.GenerateFromItemsAsync([cropA], new DateOnly(2026, 6, 23), null, CancellationToken.None);
            var cropAWorkbook = cropAReport.SummaryWorkbook;
            var cropADataset = Path.Combine(cropAReport.OutputFolder, "Dataset", "Classification", cropAReport.Rows.Single().DatasetSection.Split('/')[1], "Crop_A", "1-1(-)", "04_NG_PTCL");
            Assert.True(ExportExists(Path.Combine(cropADataset, ModelSuffixedFileName(cropAImage, "Crop_A"))));

            var gapReport = await reportGenerator.GenerateFromItemsAsync([gap], new DateOnly(2026, 6, 23), null, CancellationToken.None);

            Assert.True(File.Exists(cropAWorkbook));
            Assert.True(ExportExists(Path.Combine(cropADataset, ModelSuffixedFileName(cropAImage, "Crop_A"))));
            Assert.True(File.Exists(gapReport.SummaryWorkbook));
            Assert.True(ExportExists(Path.Combine(gapReport.OutputFolder, "Dataset", "Segmentation", "Gap_DL", "Real", ModelSuffixedFileName(gapImage, "Gap_DL"))));
            Assert.NotEqual(cropAReport.SummaryWorkbook, gapReport.SummaryWorkbook);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DlngReport_CopiesSegmentationOverkillAndSkipsNoNeed()
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
            "OK", "OK", "BYPASS_NG", "OK", @"E:\Files\Image\20260623_070000_LOT_CELL-SEG_EXT_0_0.jpg", "", ""
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
                "Overkill",
                false,
                item.Images.Select(x => x.Path).ToArray(),
                DateTimeOffset.Now), CancellationToken.None);

            await CollectFixtureReviewsAsync(storage, reviews);

            var result = await new DlngReportGenerator(queue, reviews, storage)
                .GenerateAsync([machine], new DateOnly(2026, 6, 23), null, CancellationToken.None);

            Assert.True(Directory.Exists(Path.Combine(result.OutputFolder, "Dataset", "Segmentation", "Gap_DL", "Overkill")));
            Assert.False(Directory.Exists(Path.Combine(result.OutputFolder, "Dataset", "Segmentation", "Gap_DL", "No Need to Train")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DlngReport_ReportsFallbackRawWithoutCollectingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "DlngFallbackReportTests", Guid.NewGuid().ToString("N"));
        var csv = Path.Combine(root, "result.csv");
        var rawFolder = Path.Combine(root, "share", "Files", "Image", "Raw", "CELL-RAW");
        Directory.CreateDirectory(rawFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(csv)!);
        var rawFiles = Enumerable.Range(1, 12)
            .Select(index => Path.Combine(rawFolder, $"raw-{index:00}.jpg"))
            .ToArray();
        foreach (var raw in rawFiles) await File.WriteAllBytesAsync(raw, [1]);

        var headers = new[]
        {
            "DATE", "TIME", "MODEL-ID", "LOT-ID", "CELL-ID", "JUDGE", "JUDGE-DEFECT",
            "UPPER_JUDGE", "LOWER_JUDGE", "UPPER_GAP_DL-JUDGE", "LOWER_GAP_DL-JUDGE",
            "UPPER_IMAGE-PATH-1", "UPPER_IMAGE-PATH-2", "UPPER_IMAGE-PATH-3"
        };
        var values = new[]
        {
            "20260623", "07:00:00", "E81C", "LOT", "CELL-RAW", "DLNG", "GAP_DL",
            "OK", "OK", "BYPASS_NG", "OK",
            @"E:\Files\Image\Raw\CELL-RAW\raw-01.jpg",
            @"E:\Files\Image\Raw\CELL-RAW\raw-02.jpg",
            @"E:\Files\Image\Raw\CELL-RAW\raw-03.jpg"
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
            Assert.True(item.ModelKind == DlngModelKind.FallbackRaw);
            Assert.Equal(3, item.Images.Count);
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
                "Real",
                true,
                item.Images.Select(x => x.Path).ToArray(),
                DateTimeOffset.Now), CancellationToken.None);

            var result = await new DlngReportGenerator(queue, reviews, storage)
                .GenerateAsync([machine], new DateOnly(2026, 6, 23), null, CancellationToken.None);

            var destination = Path.Combine(result.OutputFolder, "Dataset", "Segmentation", "NEED_TO_SIMULATE", "Gap_DL", "CELL-RAW");
            Assert.False(Directory.Exists(destination));
            Assert.Single(result.Rows);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task NgBypassQueue_UsesMeasureColumnsAndQueuesSelectedSides()
    {
        var root = Path.Combine(Path.GetTempPath(), "NgBypassQueueTests", Guid.NewGuid().ToString("N"));
        var csv = Path.Combine(root, "result.csv");
        var raw = Path.Combine(root, "share", "Files", "Image", "Raw");
        Directory.CreateDirectory(raw);
        Directory.CreateDirectory(Path.GetDirectoryName(csv)!);
        for (var side = 0; side < 2; side++)
        {
            for (var index = 1; index <= 3; index++)
            {
                await File.WriteAllBytesAsync(Path.Combine(raw, $"cell-{side}-{index}.jpg"), [1]);
            }
        }

        var headers = new[]
        {
            "DATE", "TIME", "MODEL-ID", "LOT-ID", "CELL-ID", "UPPER_MEASURE_A-OK/NG", "LOWER_MEASURE_A-OK/NG",
            "UPPER_IMAGE-PATH-1", "UPPER_IMAGE-PATH-2", "UPPER_IMAGE-PATH-3",
            "LOWER_IMAGE-PATH-1", "LOWER_IMAGE-PATH-2", "LOWER_IMAGE-PATH-3"
        };
        var values = new[]
        {
            "20260623", "07:00:00", "E81C", "LOT", "CELL-NGBYP", "NG", "NG",
            @"E:\Files\Image\Raw\cell-0-1.jpg", @"E:\Files\Image\Raw\cell-0-2.jpg", @"E:\Files\Image\Raw\cell-0-3.jpg",
            @"E:\Files\Image\Raw\cell-1-1.jpg", @"E:\Files\Image\Raw\cell-1-2.jpg", @"E:\Files\Image\Raw\cell-1-3.jpg"
        };
        await File.WriteAllLinesAsync(csv, [string.Join(",", headers), string.Join(",", values)]);

        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "unused", ['E']);
            var queue = new NgBypassQueueService(
                new SingleFileLocator(csv, new DateOnly(2026, 6, 23)),
                new FakeSnapshotService(),
                new NgBypassCsvReader(new FakeShareResolver(Path.Combine(root, "share"))));
            var result = await queue.LoadAsync(
                machine,
                new DateOnly(2026, 6, 23),
                new("MEASURE_A", true, true, false),
                null,
                CancellationToken.None);

            Assert.Empty(result.HeaderWarnings);
            Assert.Equal(2, result.Items.Count);
            Assert.Contains(result.Items, x => x.Side == "UPPER" && x.Images.Count == 3);
            Assert.Contains(result.Items, x => x.Side == "LOWER" && x.Images.Count == 3);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task NgBypassQueue_WarnsWhenMeasureHeaderIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "NgBypassHeaderTests", Guid.NewGuid().ToString("N"));
        var csv = Path.Combine(root, "result.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(csv)!);
        await File.WriteAllLinesAsync(
            csv,
            [
                "DATE,TIME,MODEL-ID,LOT-ID,CELL-ID,UPPER_OTHER-OK/NG",
                "20260623,07:00:00,E81C,LOT,CELL-MISS,NG"
            ]);

        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "unused", ['E']);
            var queue = new NgBypassQueueService(
                new SingleFileLocator(csv, new DateOnly(2026, 6, 23)),
                new FakeSnapshotService(),
                new NgBypassCsvReader(new FakeShareResolver(Path.Combine(root, "share"))));
            var result = await queue.LoadAsync(
                machine,
                new DateOnly(2026, 6, 23),
                new("MEASURE_A", true, false, false),
                null,
                CancellationToken.None);

            Assert.Empty(result.Items);
            var warning = Assert.Single(result.HeaderWarnings);
            Assert.Equal("UPPER_MEASURE_A-OK/NG", warning.ColumnName);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task NgBypassQueue_UsesBypassValueAndIgnoresGlobalCellPrefixes()
    {
        var root = Path.Combine(Path.GetTempPath(), "NgBypassValueTests", Guid.NewGuid().ToString("N"));
        var csv = Path.Combine(root, "result.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(csv)!);
        var headers = new[]
        {
            "DATE", "TIME", "MODEL-ID", "LOT-ID", "CELL-ID", "UPPER_MEASURE_A-OK/NG",
            "UPPER_IMAGE-PATH-1", "UPPER_IMAGE-PATH-2", "UPPER_IMAGE-PATH-3"
        };
        var rows = new[]
        {
            string.Join(",", headers),
            @"20260623,07:00:00,E81C,LOT,CELL-BYPASS,BYPASS_NG,E:\a.jpg,E:\b.jpg,E:\c.jpg",
            @"20260623,07:01:00,E81C,LOT,OCR-BYPASS,BYPASS_NG,E:\a.jpg,E:\b.jpg,E:\c.jpg",
            @"20260623,07:02:00,E81C,LOT,CELL-NG,NG,E:\a.jpg,E:\b.jpg,E:\c.jpg"
        };
        await File.WriteAllLinesAsync(csv, rows);

        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "unused", ['E']);
            var queue = new NgBypassQueueService(
                new SingleFileLocator(csv, new DateOnly(2026, 6, 23)),
                new FakeSnapshotService(),
                new NgBypassCsvReader(new FakeShareResolver(Path.Combine(root, "share"))));
            var result = await queue.LoadAsync(
                machine,
                new DateOnly(2026, 6, 23),
                new("MEASURE_A", true, false, true),
                null,
                CancellationToken.None);

            var item = Assert.Single(result.Items);
            Assert.Equal("CELL-BYPASS", item.CellId);
            Assert.Equal("BYPASS_NG", item.TargetValue);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task NgBypassQueue_SkipNgFiltersBypassedRowsWithOverallNgJudge()
    {
        var root = Path.Combine(Path.GetTempPath(), "NgBypassSkipNgTests", Guid.NewGuid().ToString("N"));
        var csv = Path.Combine(root, "result.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(csv)!);
        var headers = new[]
        {
            "DATE", "TIME", "MODEL-ID", "LOT-ID", "CELL-ID", "JUDGE", "UPPER_MEASURE_A-OK/NG",
            "UPPER_IMAGE-PATH-1", "UPPER_IMAGE-PATH-2", "UPPER_IMAGE-PATH-3"
        };
        var rows = new[]
        {
            string.Join(",", headers),
            @"20260623,07:00:00,E81C,LOT,CELL-JUDGE-NG,NG,BYPASS_NG,E:\a.jpg,E:\b.jpg,E:\c.jpg",
            @"20260623,07:01:00,E81C,LOT,CELL-JUDGE-OK,OK,BYPASS_NG,E:\a.jpg,E:\b.jpg,E:\c.jpg"
        };
        await File.WriteAllLinesAsync(csv, rows);

        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "unused", ['E']);
            var queue = new NgBypassQueueService(
                new SingleFileLocator(csv, new DateOnly(2026, 6, 23)),
                new FakeSnapshotService(),
                new NgBypassCsvReader(new FakeShareResolver(Path.Combine(root, "share"))));
            var result = await queue.LoadAsync(
                machine,
                new DateOnly(2026, 6, 23),
                new("MEASURE_A", true, false, true, true),
                null,
                CancellationToken.None);

            var item = Assert.Single(result.Items);
            Assert.Equal("CELL-JUDGE-OK", item.CellId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task NgBypassReport_BlocksUntilReviewedAndExportsSeparateSummary()
    {
        var root = Path.Combine(Path.GetTempPath(), "NgBypassReportTests", Guid.NewGuid().ToString("N"));
        var csv = Path.Combine(root, "result.csv");
        var raw = Path.Combine(root, "share", "Files", "Image", "Raw", "CELL-RPT");
        Directory.CreateDirectory(raw);
        Directory.CreateDirectory(Path.GetDirectoryName(csv)!);
        await File.WriteAllBytesAsync(Path.Combine(raw, "raw1.jpg"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(raw, "raw2.jpg"), [2]);
        await File.WriteAllBytesAsync(Path.Combine(raw, "raw3.jpg"), [3]);
        var headers = new[]
        {
            "DATE", "TIME", "MODEL-ID", "LOT-ID", "CELL-ID", "UPPER_MEASURE_A-OK/NG",
            "UPPER_IMAGE-PATH-1", "UPPER_IMAGE-PATH-2", "UPPER_IMAGE-PATH-3"
        };
        var values = new[]
        {
            "20260623", "07:00:00", "E81C", "LOT", "CELL-RPT", "NG",
            @"E:\Files\Image\Raw\CELL-RPT\raw1.jpg",
            @"E:\Files\Image\Raw\CELL-RPT\raw2.jpg",
            @"E:\Files\Image\Raw\CELL-RPT\raw3.jpg"
        };
        await File.WriteAllLinesAsync(csv, [string.Join(",", headers), string.Join(",", values)]);

        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "unused", ['E']);
            var storage = new AppStorage(Path.Combine(root, "out"));
            storage.EnsureCreated([machine]);
            var reviews = new JsonNgBypassReviewStore(storage);
            var locator = new SingleFileLocator(csv, new DateOnly(2026, 6, 23));
            var snapshots = new FakeSnapshotService();
            var queue = new NgBypassQueueService(
                locator,
                snapshots,
                new NgBypassCsvReader(new FakeShareResolver(Path.Combine(root, "share"))));
            var report = new NgBypassReportGenerator(
                queue,
                locator,
                snapshots,
                new InspectionSummaryCsvReader(),
                reviews,
                storage);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                report.GenerateAsync([machine], new("MEASURE_A", true, false, false), new DateOnly(2026, 6, 23), null, CancellationToken.None));

            var item = Assert.Single((await queue.LoadAsync(machine, new DateOnly(2026, 6, 23), new("MEASURE_A", true, false, false), null, CancellationToken.None)).Items);
            var copy = await new NgBypassClassifiedFolderService(storage)
                .ClassifyAsync(machine, item, ReviewDecision.RealNg, CancellationToken.None);
            await reviews.SaveAsync(new(
                item.Key,
                item.MachineId,
                item.LinePolarity,
                item.InspectedAt,
                item.CellId,
                item.Measure,
                item.Side,
                item.TargetValue,
                ReviewDecision.RealNg,
                copy.State,
                copy.Destination,
                DateTimeOffset.Now), CancellationToken.None);

            var result = await report.GenerateAsync(
                [machine],
                new("MEASURE_A", true, false, false),
                new DateOnly(2026, 6, 23),
                null,
                CancellationToken.None);

            Assert.True(File.Exists(result.SummaryWorkbook));
            var summary = Assert.Single(result.Rows);
            Assert.Equal(1, summary.TotalInspected);
            Assert.Equal(1, summary.Real);
            Assert.Equal(
                Path.Combine(result.DateFolder, "MEASURE_A"),
                result.OutputFolder);
            Assert.True(Directory.Exists(Path.Combine(result.OutputFolder, "REAL", "1-1(-)", "UPPER")));
            Assert.False(Directory.Exists(Path.Combine(result.OutputFolder, "REAL", "1-1(-)", "MEASURE_A", "UPPER")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task NgBypassReport_ExcludesSameCellReworkDuplicatesFromCountsButExportsImages()
    {
        var root = Path.Combine(Path.GetTempPath(), "NgBypassReworkReportTests", Guid.NewGuid().ToString("N"));
        var share = Path.Combine(root, "share");
        var csv = Path.Combine(root, "result.csv");
        var raw1 = Path.Combine(share, "Files", "Image", "Raw", "CELL-REWORK-RUN1");
        var raw2 = Path.Combine(share, "Files", "Image", "Raw", "CELL-REWORK-RUN2");
        Directory.CreateDirectory(raw1);
        Directory.CreateDirectory(raw2);
        Directory.CreateDirectory(Path.GetDirectoryName(csv)!);
        foreach (var folder in new[] { raw1, raw2 })
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "raw1.jpg"), [1]);
            await File.WriteAllBytesAsync(Path.Combine(folder, "raw2.jpg"), [2]);
            await File.WriteAllBytesAsync(Path.Combine(folder, "raw3.jpg"), [3]);
        }

        var headers = new[]
        {
            "DATE", "TIME", "MODEL-ID", "LOT-ID", "CELL-ID", "UPPER_MEASURE_A-OK/NG",
            "UPPER_IMAGE-PATH-1", "UPPER_IMAGE-PATH-2", "UPPER_IMAGE-PATH-3"
        };
        var first = new[]
        {
            "20260623", "07:00:00", "E81C", "LOT", "CELL-REWORK", "NG",
            @"E:\Files\Image\Raw\CELL-REWORK-RUN1\raw1.jpg",
            @"E:\Files\Image\Raw\CELL-REWORK-RUN1\raw2.jpg",
            @"E:\Files\Image\Raw\CELL-REWORK-RUN1\raw3.jpg"
        };
        var second = new[]
        {
            "20260623", "07:30:00", "E81C", "LOT", "CELL-REWORK", "NG",
            @"E:\Files\Image\Raw\CELL-REWORK-RUN2\raw1.jpg",
            @"E:\Files\Image\Raw\CELL-REWORK-RUN2\raw2.jpg",
            @"E:\Files\Image\Raw\CELL-REWORK-RUN2\raw3.jpg"
        };
        await File.WriteAllLinesAsync(csv, [string.Join(",", headers), string.Join(",", first), string.Join(",", second)]);

        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "unused", ['E']);
            var storage = new AppStorage(Path.Combine(root, "out"));
            storage.EnsureCreated([machine]);
            var reviews = new JsonNgBypassReviewStore(storage);
            var locator = new SingleFileLocator(csv, new DateOnly(2026, 6, 23));
            var snapshots = new FakeSnapshotService();
            var queue = new NgBypassQueueService(
                locator,
                snapshots,
                new NgBypassCsvReader(new FakeShareResolver(share)));
            var report = new NgBypassReportGenerator(
                queue,
                locator,
                snapshots,
                new InspectionSummaryCsvReader(),
                reviews,
                storage);
            var items = (await queue.LoadAsync(machine, new DateOnly(2026, 6, 23), new("MEASURE_A", true, false, false), null, CancellationToken.None)).Items
                .OrderBy(x => x.InspectedAt)
                .ToArray();
            Assert.Equal(2, items.Length);

            var firstCopy = await new NgBypassClassifiedFolderService(storage)
                .ClassifyAsync(machine, items[0], ReviewDecision.RealNg, CancellationToken.None);
            var secondCopy = await new NgBypassClassifiedFolderService(storage)
                .ClassifyAsync(machine, items[1], ReviewDecision.Overkill, CancellationToken.None);
            await reviews.SaveAsync(new(items[0].Key, items[0].MachineId, items[0].LinePolarity, items[0].InspectedAt, items[0].CellId, items[0].Measure, items[0].Side, items[0].TargetValue, ReviewDecision.RealNg, firstCopy.State, firstCopy.Destination, DateTimeOffset.Now), CancellationToken.None);
            await reviews.SaveAsync(new(items[1].Key, items[1].MachineId, items[1].LinePolarity, items[1].InspectedAt, items[1].CellId, items[1].Measure, items[1].Side, items[1].TargetValue, ReviewDecision.Overkill, secondCopy.State, secondCopy.Destination, DateTimeOffset.Now), CancellationToken.None);

            var result = await report.GenerateAsync(
                [machine],
                new("MEASURE_A", true, false, false),
                new DateOnly(2026, 6, 23),
                null,
                CancellationToken.None);

            var summary = Assert.Single(result.Rows);
            Assert.Equal(2, summary.TotalInspected);
            Assert.Equal(1, summary.InitialMatched);
            Assert.Equal(1, summary.Real);
            Assert.Equal(0, summary.Overkill);
            Assert.Single(Directory.EnumerateDirectories(Path.Combine(result.OutputFolder, "REAL", "1-1(-)", "UPPER"), "CELL-REWORK-RUN1", SearchOption.AllDirectories));
            Assert.Single(Directory.EnumerateDirectories(Path.Combine(result.OutputFolder, "OVERKILL", "1-1(-)", "UPPER"), "CELL-REWORK-RUN2", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task NgBypassReport_GeneratesOneMeasureFolderPerReportDateInRange()
    {
        var root = Path.Combine(Path.GetTempPath(), "NgBypassRangeReportTests", Guid.NewGuid().ToString("N"));
        var share = Path.Combine(root, "share");
        var csv1 = Path.Combine(root, "result-20260726.csv");
        var csv2 = Path.Combine(root, "result-20260727.csv");
        Directory.CreateDirectory(root);
        await WriteNgBypassCsvAsync(csv1, share, "20260726", "CELL-RANGE-1");
        await WriteNgBypassCsvAsync(csv2, share, "20260727", "CELL-RANGE-2");

        try
        {
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "unused", ['E']);
            var storage = new AppStorage(Path.Combine(root, "out"));
            storage.EnsureCreated([machine]);
            var reviews = new JsonNgBypassReviewStore(storage);
            var locator = new MultiFileLocator(new Dictionary<DateOnly, IReadOnlyList<string>>
            {
                [new(2026, 7, 26)] = [csv1],
                [new(2026, 7, 27)] = [csv2]
            });
            var snapshots = new FakeSnapshotService();
            var queue = new NgBypassQueueService(
                locator,
                snapshots,
                new NgBypassCsvReader(new FakeShareResolver(share)));
            var report = new NgBypassReportGenerator(
                queue,
                locator,
                snapshots,
                new InspectionSummaryCsvReader(),
                reviews,
                storage);
            var query = new NgBypassQuery("MEASURE_A", true, false, false);

            foreach (var date in new[] { new DateOnly(2026, 7, 26), new DateOnly(2026, 7, 27) })
            {
                var item = Assert.Single((await queue.LoadAsync(machine, date, query, null, CancellationToken.None)).Items);
                var copy = await new NgBypassClassifiedFolderService(storage)
                    .ClassifyAsync(machine, item, ReviewDecision.RealNg, CancellationToken.None);
                await reviews.SaveAsync(new(
                    item.Key,
                    item.MachineId,
                    item.LinePolarity,
                    item.InspectedAt,
                    item.CellId,
                    item.Measure,
                    item.Side,
                    item.TargetValue,
                    ReviewDecision.RealNg,
                    copy.State,
                    copy.Destination,
                    DateTimeOffset.Now), CancellationToken.None);
            }

            var results = await report.GenerateRangeAsync(
                [machine],
                query,
                new DateOnly(2026, 7, 26),
                new DateOnly(2026, 7, 27),
                null,
                CancellationToken.None);

            Assert.Equal(2, results.Count);
            foreach (var result in results)
            {
                Assert.True(Directory.Exists(Path.Combine(
                    storage.NgBypassSummary,
                    $"NG_Bypass_Summary_{result.ReportDate:yyyyMMdd}")));
                Assert.Equal(
                    Path.Combine(result.DateFolder, "MEASURE_A"),
                    result.OutputFolder);
                Assert.True(File.Exists(result.SummaryWorkbook));
                Assert.True(Directory.Exists(Path.Combine(result.OutputFolder, "REAL", "1-1(-)", "UPPER")));
            }
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
    public async Task SummaryReport_IgnoreRowsAreExcludedFromCountsAndDetails()
    {
        var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "127.0.0.1", ['E']);
        var ok = SummaryRecord(
            machine,
            new DateTime(2026, 6, 25, 6, 30, 0),
            "CELL-OK",
            "OK",
            "");
        var real = SummaryRecord(
            machine,
            new DateTime(2026, 6, 25, 8, 0, 0),
            "CELL-REAL",
            "NG",
            "B_DIM");
        var ignored = SummaryRecord(
            machine,
            new DateTime(2026, 6, 25, 9, 0, 0),
            "CELL-IGNORE",
            "NG",
            "C_DIM");
        var writer = new FakeSummaryWriter();
        var service = new SummaryReportService(
            new FakeLocator(),
            new FakeSnapshotService(),
            new FakeSummaryReader([ok, real, ignored]),
            new FakeReviewStore([
                new ReviewEntry(real.CandidateKey, ReviewDecision.RealNg, CopyState.Copied, "", null, DateTimeOffset.Now),
                new ReviewEntry(ignored.CandidateKey, ReviewDecision.Ignore, CopyState.NotRequested, "", null, DateTimeOffset.Now)
            ]),
            writer);

        var result = await service.GenerateAsync(
            [machine],
            new DateOnly(2026, 6, 25),
            null,
            CancellationToken.None);

        var all = Assert.Single(result.Rows, row => row.Defect == "ALL");
        Assert.Equal(2, all.TotalInspected);
        Assert.Equal(1, all.InitialNg);
        Assert.Equal(1, all.RealNg);
        Assert.Equal(0, all.Overkill);
        Assert.DoesNotContain(result.Rows, row => row.Defect == "C_DIM");
        Assert.DoesNotContain(writer.Details, detail => detail.Values.Contains("CELL-IGNORE"));
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
                InspectionIdentity.Hash(overkillSource),
                "overkill-cell",
                "raw.jpg")));
            Assert.True(File.Exists(Path.Combine(
                reportFolder,
                "NG",
                "1-1(-)",
                "MULTI-NG",
                InspectionIdentity.Hash(multiSource),
                "multi-cell",
                "raw.jpg")));
            Assert.True(File.Exists(Path.Combine(
                reportFolder,
                "NG",
                "1-1(-)",
                "C_DIM",
                InspectionIdentity.Hash(realSource),
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
    public async Task IrsRawImageLocator_DoesNotGuessFromSameCellInHourFolder()
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

            Assert.Empty(result.NetworkPaths);
            Assert.Contains("Missing", result.Message);
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
    public async Task IrsRawImageLocator_DoesNotGuessFromModelFolderWithoutInspectionEvidence()
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

            Assert.Empty(result.NetworkPaths);
            Assert.Contains("Missing", result.Message);
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
                "DATE,TIME,LOT-ID,CELL-ID,UPPER_IMAGE-PATH-1,UPPER_IMAGE-PATH-2,UPPER_IMAGE-PATH-3,LOWER_IMAGE-PATH-1,LOWER_IMAGE-PATH-2,LOWER_IMAGE-PATH-3",
                @"20260601,08:09:25,LOT,CELL-CSV,E:\Files\Image\E81C\2026\06\01\08\OK\20260601_080925_LOT_CELL-CSV_EXT_0_0.jpg,E:\Files\Image\E81C\2026\06\01\08\OK\20260601_080925_LOT_CELL-CSV_EXT_0_1.jpg,E:\Files\Image\E81C\2026\06\01\08\OK\20260601_080925_LOT_CELL-CSV_EXT_0_2.jpg,E:\Files\Image\E81C\2026\06\01\08\OK\20260601_080925_LOT_CELL-CSV_EXT_1_0.jpg,E:\Files\Image\E81C\2026\06\01\08\OK\20260601_080925_LOT_CELL-CSV_EXT_1_1.jpg,E:\Files\Image\E81C\2026\06\01\08\OK\20260601_080925_LOT_CELL-CSV_EXT_1_2.jpg"));
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
            "",
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
                    Path.Combine(root, @"Files\Image\E81C\2026\06\01\08\OK\20260601_080925_LOT_CELL-CSV_EXT_0_0.jpg"),
                    Path.Combine(root, @"Files\Image\E81C\2026\06\01\08\OK\20260601_080925_LOT_CELL-CSV_EXT_0_1.jpg"),
                    Path.Combine(root, @"Files\Image\E81C\2026\06\01\08\OK\20260601_080925_LOT_CELL-CSV_EXT_0_2.jpg")
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
                    var file = Path.Combine(originalFolder, $"20260601_080925_LOT_CELL-IRS_EXT_{(side == "UPPER" ? 0 : 1)}_{index - 1}{(kind == "Overlay" ? "_overlay" : "")}.jpg");
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
                "DATE,TIME,LOT-ID,CELL-ID,UPPER_IMAGE-PATH-1,UPPER_OVERLAY-IMAGE-PATH-1,UPPER_IMAGE-PATH-2,UPPER_OVERLAY-IMAGE-PATH-2,UPPER_IMAGE-PATH-3,UPPER_OVERLAY-IMAGE-PATH-3,LOWER_IMAGE-PATH-1,LOWER_OVERLAY-IMAGE-PATH-1,LOWER_IMAGE-PATH-2,LOWER_OVERLAY-IMAGE-PATH-2,LOWER_IMAGE-PATH-3,LOWER_OVERLAY-IMAGE-PATH-3",
                string.Join(
                    ",",
                    [
                        "20260601",
                        "08:09:25",
                        "LOT",
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
            "",
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
                "IRS_LEAK", InspectionIdentity.Hash(candidate.Key),
                "ORIGINAL",
                "CELL-IRS-FOLDER")));
            Assert.True(File.Exists(Path.Combine(
                storageRoot,
                "1-1(+)",
                "IRS_LEAK", InspectionIdentity.Hash(candidate.Key),
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
                "IRS_LEAK", InspectionIdentity.Hash(candidate.Key),
                "Crop_micro",
                Path.GetFileName(crop))));
            Assert.True(Directory.Exists(Path.Combine(
                storageRoot,
                "1-1(+)",
                "IRS_LEAK", InspectionIdentity.Hash(candidate.Key),
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
                "IRS_LEAK", InspectionIdentity.Hash(candidate.Key),
                "ORIGINAL",
                "CELL-IRS-FOLDER")));
            Assert.True(Directory.Exists(Path.Combine(
                storageRoot,
                "1-1(+)",
                "IRS_LEAK", InspectionIdentity.Hash(candidate.Key),
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
        var original = Path.Combine(originalFolder, "20260601_080925_LOT_CELL-TAB_EXT_0_0.jpg");
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
        await File.WriteAllTextAsync(csv, $"DATE,TIME,LOT-ID,CELL-ID,UPPER_IMAGE-PATH-1{Environment.NewLine}20260601,08:09:25,LOT,CELL-TAB,{original.Replace(shareRoot, "E:")}");
        var machine = new WeldingMachine("1-1-ca", "1-1", Polarity.Cathode, "127.0.0.1", ['E']);
        var candidate = new IrsReviewCandidate("tab-key", "PACKAGE #1-1", "Welding Plus", "1-1(+)", new DateTime(2026, 6, 1, 8, 9, 25), "LOT", "CELL-TAB", "TOP", "", "NG", "reason", 4);
        var service = new IrsReviewCommitService(new AppStorage(storageRoot), new StaticDailyCsvLocator([csv]), new TestSharePathResolver(shareRoot));

        try
        {
            await service.CommitAsync(new(machine, candidate, [new("TABSIDE_R", "Tabside R", "Crop_micro_tabside", IrsSelectionKind.Crop, "Crop_micro_tabside", "_R")]), CancellationToken.None);
            var destination = Path.Combine(storageRoot, "1-1(+)", "IRS_LEAK", InspectionIdentity.Hash(candidate.Key), "Crop_micro_tabside");
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
    public async Task IrsReviewCommitService_HornmarkSelectionOnlyCopiesCandidateSide()
    {
        var shareRoot = Path.Combine(Path.GetTempPath(), "IrsHornmarkSideShare", Guid.NewGuid().ToString("N"));
        var storageRoot = Path.Combine(Path.GetTempPath(), "IrsHornmarkSideStorage", Guid.NewGuid().ToString("N"));
        var originalFolder = Path.Combine(shareRoot, "Files", "Image", "E81C", "2026", "06", "01", "08", "OK", "CELL-HORN-FOLDER");
        Directory.CreateDirectory(originalFolder);
        var original = Path.Combine(originalFolder, "20260601_080925_LOT_CELL-HORN_EXT_0_0.jpg");
        await File.WriteAllTextAsync(original, "image");
        File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddMinutes(-10));

        var cropFolder = Path.Combine(shareRoot, "Files", "Image", "E81C", "2026", "06", "01", "Mavin", "HORNMARK");
        Directory.CreateDirectory(cropFolder);
        var upper = Path.Combine(cropFolder, "CELL-HORN_01-1_CA_080925_UPPER_1_HORN LEFT_f0_SourceImg.jpg");
        var lower = Path.Combine(cropFolder, "CELL-HORN_01-1_CA_080925_LOWER_1_HORN LEFT_f0_SourceImg.jpg");
        foreach (var file in new[] { upper, lower })
        {
            await File.WriteAllTextAsync(file, "crop");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-10));
        }

        var csv = Path.Combine(shareRoot, "#1-1 WELDING VISION(+)_JF2_20260601.csv");
        await File.WriteAllTextAsync(csv, $"DATE,TIME,LOT-ID,CELL-ID,UPPER_IMAGE-PATH-1{Environment.NewLine}20260601,08:09:25,LOT,CELL-HORN,{original.Replace(shareRoot, "E:")}");
        var machine = new WeldingMachine("1-1-ca", "1-1", Polarity.Cathode, "127.0.0.1", ['E']);
        var candidate = new IrsReviewCandidate("horn-key", "Flagged", "Flagged", "1-1(+)", new DateTime(2026, 6, 1, 8, 9, 25), "LOT", "CELL-HORN", "TOP", "", "FLAGGED", "Hornmark L", 0, [original.Replace(shareRoot, "E:")]);
        var service = new IrsReviewCommitService(new AppStorage(storageRoot), new StaticDailyCsvLocator([csv]), new TestSharePathResolver(shareRoot));

        try
        {
            await service.CommitAsync(new(machine, candidate, [new("HORNMARK_L", "Hornmark L", "HORNMARK", IrsSelectionKind.Crop, "HORNMARK", "L")]), CancellationToken.None);
            var destination = Path.Combine(storageRoot, "1-1(+)", "IRS_LEAK", InspectionIdentity.Hash(candidate.Key), "HORNMARK");
            Assert.True(File.Exists(Path.Combine(destination, Path.GetFileName(upper))));
            Assert.False(File.Exists(Path.Combine(destination, Path.GetFileName(lower))));
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
        candidate = ResolvedFixture(candidate, record);

        try
        {
            var items = await new IrsDatasetService(new AppStorage(storageRoot)).BuildQueueAsync([candidate], [record], CancellationToken.None);
            var item = Assert.Single(items);
            Assert.True(item.IsNeedToSimulate);
            Assert.Equal("Crop_A", item.SourceFolder);
            Assert.Equal("NEED_TO_SIMULATE", item.OriginalClass);
            Assert.Equal([upper1, upper2, upper3], item.ImagePaths);
            Assert.Contains("01_OK_TOP_CATHODE", item.AllowedClasses);
            Assert.DoesNotContain("02_OK_BACK_CATHODE", item.AllowedClasses);
        }
        finally
        {
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, true);
        }
    }

    [Fact]
    public async Task IrsDatasetService_CropABottomCameraHidesTopOkClass()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "IrsCropABtmClasses", Guid.NewGuid().ToString("N"));
        var rawFolder = Path.Combine(storageRoot, "1-1(+)", "IRS_LEAK", "NEED_TO_SIMULATE", "Crop_A", "CELL-BTM-FOLDER");
        Directory.CreateDirectory(rawFolder);
        var lower1 = Path.Combine(rawFolder, "CELL-BTM_1_0.jpg");
        var lower2 = Path.Combine(rawFolder, "CELL-BTM_1_1.jpg");
        var lower3 = Path.Combine(rawFolder, "CELL-BTM_1_2.jpg");
        foreach (var file in new[] { lower1, lower2, lower3 }) await File.WriteAllTextAsync(file, "image");

        var candidate = new IrsReviewCandidate("btm-key", "PACKAGE #1-1", "Welding Plus", "1-1(+)", new DateTime(2026, 6, 1, 8, 9, 25), "LOT", "CELL-BTM", "BTM", "raw.jpg", "NG", "reason", 4);
        var record = new IrsReviewRecord("btm-key", "1-1-ca", "1-1(+)", candidate.ProducedAt, "CELL-BTM", "BTM", "NG", "reason", ["A_L"], 3, 0, 0, storageRoot, DateTimeOffset.Now, [rawFolder]);
        candidate = ResolvedFixture(candidate, record);

        try
        {
            var items = await new IrsDatasetService(new AppStorage(storageRoot)).BuildQueueAsync([candidate], [record], CancellationToken.None);
            var item = Assert.Single(items);

            Assert.Equal([lower1, lower2, lower3], item.ImagePaths);
            Assert.Contains("02_OK_BACK_CATHODE", item.AllowedClasses);
            Assert.DoesNotContain("01_OK_TOP_CATHODE", item.AllowedClasses);
            Assert.Contains("03_NG_TORN", item.AllowedClasses);
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
        candidate = ResolvedFixture(candidate, record);
        var service = new IrsDatasetService(new AppStorage(storageRoot));

        try
        {
            var items = await service.BuildQueueAsync([candidate], [record], CancellationToken.None);
            var item = Assert.Single(items);
            Assert.Equal([files[0], files[1], files[2]], item.ImagePaths);
            await service.SaveDecisionAsync(item, ["Real"], false, CancellationToken.None);

            var result = await service.WriteSummaryAsync([candidate], [record], items, CancellationToken.None);
            var destination = Path.Combine(result.OutputFolder, "Dataset", "Segmentation", "NEED_TO_SIMULATE", "SEPA", InspectionIdentity.Hash(candidate.Key), "CELL-SEPA-FOLDER");
            Assert.True(Directory.Exists(destination));
            foreach (var file in files)
            {
                Assert.True(ExportExists(Path.Combine(destination, Path.GetFileName(file))));
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
        candidate = ResolvedFixture(candidate, record);
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
            Path.Combine(folder, "CELL-PAIR_01-1_CA_080925_UPPER_1_A_L_CL01_OK_SourceMap.jpg"),
            Path.Combine(folder, "CELL-PAIR_01-1_CA_080925_UPPER_1_A_R_CL01_OK_SourceMap.jpg"),
            Path.Combine(folder, "CELL-PAIR_01-1_CA_080925_UPPER_1_A_L_CL01_OK_ActiveMap.jpg"),
            Path.Combine(folder, "CELL-PAIR_01-1_CA_080925_UPPER_1_A_R_CL01_OK_ActiveMap.jpg")
        };
        foreach (var file in files) await File.WriteAllTextAsync(file, "image");
        var candidate = new IrsReviewCandidate("pair-key", "PACKAGE #1-1", "Welding Plus", "1-1(+)", new DateTime(2026, 6, 1, 8, 9, 25), "LOT", "CELL-PAIR", "TOP", "raw.jpg", "NG", "reason", 4);
        var record = new IrsReviewRecord("pair-key", "1-1-ca", "1-1(+)", candidate.ProducedAt, "CELL-PAIR", "TOP", "NG", "reason", ["A_L", "A_R"], 0, 4, 0, storageRoot, DateTimeOffset.Now, files);
        candidate = ResolvedFixture(candidate, record);

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
            Path.Combine(folder, "CELL-NAME_01-1_CA_080925_UPPER_1_A_L_CL01_OK_SourceMap.jpg"),
            Path.Combine(folder, "CELL-NAME_01-1_CA_080925_UPPER_1_A_L_CL01_OK_ActiveMap.jpg")
        };
        foreach (var file in files) await File.WriteAllTextAsync(file, "image");
        var candidate = new IrsReviewCandidate("name-key", "PACKAGE #1-1", "Welding Plus", "1-1(+)", new DateTime(2026, 6, 1, 8, 9, 25), "LOT", "CELL-NAME", "TOP", "raw.jpg", "NG", "reason", 4);
        var record = new IrsReviewRecord("name-key", "1-1-ca", "1-1(+)", candidate.ProducedAt, "CELL-NAME", "TOP", "NG", "reason", ["A_L"], 0, 2, 0, storageRoot, DateTimeOffset.Now, files);
        candidate = ResolvedFixture(candidate, record);
        var service = new IrsDatasetService(new AppStorage(storageRoot));

        try
        {
            var items = await service.BuildQueueAsync([candidate], [record], CancellationToken.None);
            var item = Assert.Single(items);
            await service.SaveDecisionAsync(item, ["01_OK_TOP_CATHODE"], false, CancellationToken.None);
            var result = await service.WriteSummaryAsync([candidate], [record], items, CancellationToken.None);
            var destination = Path.Combine(result.OutputFolder, "Dataset", "Classification", "정상검출", "Crop_A", "1-1(+)", "01_OK_TOP_CATHODE");

            foreach (var file in files)
            {
                Assert.True(ExportExists(Path.Combine(destination, Path.GetFileName(file))));
                Assert.False(File.Exists(Path.Combine(destination, $"CELL-NAME_{Path.GetFileName(file)}")));
            }
            Assert.False(Directory.Exists(Path.Combine(result.OutputFolder, "Dataset", "Crop_A", "01_OK_TOP_CATHODE")));
            Assert.False(Directory.Exists(Path.Combine(result.OutputFolder, "Dataset", "Crop_A", "1-1(+)", "01_OK_TOP_CATHODE")));
        }
        finally
        {
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, true);
        }
    }

    [Fact]
    public async Task IrsDatasetService_SummaryCopiesClassificationCategoriesAndRulebaseBySecondReason()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "IrsSummarySections", Guid.NewGuid().ToString("N"));
        var cropFolder = Path.Combine(storageRoot, "1-1(+)", "IRS_LEAK", "Crop_B");
        var rulebaseFolder = Path.Combine(storageRoot, "1-1(+)", "IRS_LEAK", "RULEBASE", "CELL-RULE-FOLDER");
        var unrelatedRulebaseFolder = Path.Combine(storageRoot, "1-1(-)", "IRS_LEAK", "RULEBASE", "CELL-OLD-FOLDER");
        Directory.CreateDirectory(cropFolder);
        Directory.CreateDirectory(rulebaseFolder);
        Directory.CreateDirectory(unrelatedRulebaseFolder);
        var cropFiles = new[]
        {
            Path.Combine(cropFolder, "CELL-CAT_01-1_CA_080000_UPPER_1_B_L_CL01_OK_SourceMap.jpg"),
            Path.Combine(cropFolder, "CELL-CAT_01-1_CA_080000_UPPER_1_B_L_CL01_OK_ActiveMap.jpg")
        };
        foreach (var file in cropFiles) await File.WriteAllTextAsync(file, "crop");
        var rulebaseImage = Path.Combine(rulebaseFolder, "CELL-RULE_UPPER_1.jpg");
        await File.WriteAllTextAsync(rulebaseImage, "raw");
        var unrelatedRulebaseImage = Path.Combine(unrelatedRulebaseFolder, "CELL-OLD_UPPER_1.jpg");
        await File.WriteAllTextAsync(unrelatedRulebaseImage, "old raw");

        var cropCandidate = new IrsReviewCandidate("cat-key", "PACKAGE #1-1", "Welding Plus", "1-1(+)", new DateTime(2026, 7, 9, 8, 0, 0), "LOT", "CELL-CAT", "TOP", "raw.jpg", "NG", "Burr", 4);
        var rulebaseCandidate = new IrsReviewCandidate("rule-key", "PACKAGE #1-1", "Welding Plus", "1-1(+)", new DateTime(2026, 7, 9, 8, 1, 0), "LOT", "CELL-RULE", "TOP", "raw.jpg", "NG", "Tab Folded", 5);
        var cropRecord = new IrsReviewRecord("cat-key", "1-1-ca", "1-1(+)", cropCandidate.ProducedAt, "CELL-CAT", "TOP", "NG", "Burr", ["B_L"], 0, 2, 0, storageRoot, DateTimeOffset.Now, cropFiles);
        var rulebaseRecord = new IrsReviewRecord("rule-key", "1-1-ca", "1-1(+)", rulebaseCandidate.ProducedAt, "CELL-RULE", "TOP", "NG", "Tab Folded", ["RULEBASE"], 1, 0, 0, storageRoot, DateTimeOffset.Now, [rulebaseFolder]);
        var unrelatedRulebaseRecord = new IrsReviewRecord("old-rule-key", "1-1-an", "1-1(-)", new DateTime(2026, 7, 1, 8, 1, 0), "CELL-OLD", "TOP", "NG", "Foreign body on leadfilm", ["RULEBASE"], 1, 0, 0, storageRoot, DateTimeOffset.Now, [unrelatedRulebaseFolder]);
        cropCandidate = ResolvedFixture(cropCandidate, cropRecord);
        rulebaseCandidate = ResolvedFixture(rulebaseCandidate, rulebaseRecord);
        var service = new IrsDatasetService(new AppStorage(storageRoot));

        try
        {
            var items = await service.BuildQueueAsync([cropCandidate, rulebaseCandidate], [cropRecord, rulebaseRecord], CancellationToken.None);
            var item = Assert.Single(items);
            await service.SaveDecisionAsync(item, ["02_NG_TORN"], false, CancellationToken.None);

            var result = await service.WriteSummaryAsync(
                [cropCandidate, rulebaseCandidate],
                [cropRecord, rulebaseRecord, unrelatedRulebaseRecord],
                items,
                CancellationToken.None);

            var classification = Path.Combine(result.OutputFolder, "Dataset", "Classification", "미검_오검", "Crop_B", "1-1(+)", "02_NG_TORN");
            Assert.True(Directory.Exists(classification));
            foreach (var file in cropFiles)
            {
                Assert.True(ExportExists(Path.Combine(classification, Path.GetFileName(file))));
            }

            var rulebase = Path.Combine(result.OutputFolder, "Rulebase", "1-1(+)", "Tab Folded", InspectionIdentity.Hash(rulebaseCandidate.Key), "CELL-RULE-FOLDER");
            Assert.True(ExportExists(Path.Combine(rulebase, Path.GetFileName(rulebaseImage))));
            Assert.False(Directory.Exists(Path.Combine(result.OutputFolder, "Rulebase", "1-1(-)", "Foreign body on leadfilm")));
            Assert.False(Directory.Exists(Path.Combine(result.OutputFolder, "Dataset", "RULEBASE")));
        }
        finally
        {
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, true);
        }
    }

    [Fact]
    public async Task FlaggedReviewService_LoadsNewAndPreviousFlagsAndResolvesRawSideImages()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "FlaggedReviewTests", Guid.NewGuid().ToString("N"));
        var shareRoot = Path.Combine(storageRoot, "share");
        var csv = Path.Combine(storageRoot, "#1-1 WELDING VISION(+)_E81C_20260709.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(csv)!);
        var headers = new[]
        {
            "DATE", "TIME", "LOT-ID", "CELL-ID",
            "UPPER_IMAGE-PATH-1", "UPPER_IMAGE-PATH-2", "UPPER_IMAGE-PATH-3",
            "LOWER_IMAGE-PATH-1", "LOWER_IMAGE-PATH-2", "LOWER_IMAGE-PATH-3"
        };
        var row = new[]
        {
            "20260709", "08:00:00", "LOT", "CELL-FLAG",
            @"E:\Files\Image\Raw\20260709_080000_LOT_CELL-FLAG_EXT_0_0.jpg", @"E:\Files\Image\Raw\20260709_080000_LOT_CELL-FLAG_EXT_0_1.jpg", @"E:\Files\Image\Raw\20260709_080000_LOT_CELL-FLAG_EXT_0_2.jpg",
            @"E:\Files\Image\Raw\20260709_080000_LOT_CELL-FLAG_EXT_1_0.jpg", @"E:\Files\Image\Raw\20260709_080000_LOT_CELL-FLAG_EXT_1_1.jpg", @"E:\Files\Image\Raw\20260709_080000_LOT_CELL-FLAG_EXT_1_2.jpg"
        };
        await File.WriteAllLinesAsync(csv, [string.Join(",", headers), string.Join(",", row)]);
        var machine = new WeldingMachine("1-1-ca", "1-1", Polarity.Cathode, "unused", ['E']);
        var store = new JsonFlaggedItemStore(new AppStorage(storageRoot));
        var current = new FlaggedItem(
            "flag-current",
            "DLNG",
            machine.Id,
            machine.OutputFolderName,
            machine.Polarity,
            new DateTime(2026, 7, 9, 8, 0, 0),
            "E81C",
            "LOT",
            "CELL-FLAG",
            "LOWER",
            "Foreign body on leadfilm",
            [],
            DateTimeOffset.Now,
            DateTimeOffset.Now);
        var previous = current with
        {
            Key = "flag-previous",
            CellId = "CELL-OLD",
            SummarizedAt = DateTimeOffset.Now
        };

        try
        {
            await store.SaveAsync(current, CancellationToken.None);
            await store.SaveAsync(previous, CancellationToken.None);
            var service = new FlaggedReviewService(
                store,
                new FakeIrsDatasetService(),
                new FakeMachineRegistry([machine]),
                new StaticDailyCsvLocator([csv]),
                new TestSharePathResolver(shareRoot));

            var newFlags = await service.LoadAsync(false, CancellationToken.None);
            var oldFlags = await service.LoadAsync(true, CancellationToken.None);
            var candidate = Assert.Single(await service.BuildCandidatesAsync(newFlags, CancellationToken.None));

            Assert.Equal("flag-current", Assert.Single(newFlags).Key);
            Assert.Equal("flag-previous", Assert.Single(oldFlags).Key);
            Assert.Equal("BTM", candidate.CameraLocation);
            Assert.Equal(3, candidate.RawImagePaths?.Count);
            Assert.All(candidate.RawImagePaths!, path => Assert.Contains("_EXT_1_", path));
        }
        finally
        {
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, true);
        }
    }

    [Fact]
    public async Task FlaggedReviewService_MarksFlagsSummarizedAfterSummaryGeneration()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "FlaggedSummaryMarkTests", Guid.NewGuid().ToString("N"));
        var machine = new WeldingMachine("1-1-ca", "1-1", Polarity.Cathode, "unused", ['E']);
        var store = new JsonFlaggedItemStore(new AppStorage(storageRoot));
        var flag = new FlaggedItem(
            "flag-summary",
            "Kickout",
            machine.Id,
            machine.OutputFolderName,
            machine.Polarity,
            new DateTime(2026, 7, 9, 8, 0, 0),
            "E81C",
            "LOT",
            "CELL-FLAG",
            "UPPER",
            "B_DIM",
            [@"E:\raw1.jpg", @"E:\raw2.jpg", @"E:\raw3.jpg"],
            DateTimeOffset.Now,
            DateTimeOffset.Now);

        try
        {
            await store.SaveAsync(flag, CancellationToken.None);
            var service = new FlaggedReviewService(
                store,
                new FakeIrsDatasetService(),
                new FakeMachineRegistry([machine]),
                new EmptyDailyCsvLocator(),
                new TestSharePathResolver(storageRoot));
            var candidates = await service.BuildCandidatesAsync([flag], CancellationToken.None);

            await service.WriteSummaryAsync([flag], candidates, [], [], CancellationToken.None);

            Assert.Empty(await service.LoadAsync(false, CancellationToken.None));
            Assert.Equal("flag-summary", Assert.Single(await service.LoadAsync(true, CancellationToken.None)).Key);

            await service.ReflagAsync("flag-summary", CancellationToken.None);

            Assert.Equal("flag-summary", Assert.Single(await service.LoadAsync(false, CancellationToken.None)).Key);
            Assert.Empty(await service.LoadAsync(true, CancellationToken.None));
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

    private static string ModelSuffixedFileName(string path, string cropFolder) =>
        $"{Path.GetFileNameWithoutExtension(path)}_{cropFolder}{Path.GetExtension(path)}";

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


    private static bool ExportExists(string path) => File.Exists(path) ||
        (Directory.Exists(Path.GetDirectoryName(path)) && Directory.EnumerateFiles(
            Path.GetDirectoryName(path)!, Path.GetFileName(path), SearchOption.AllDirectories).Any());

    private static IrsReviewCandidate ResolvedFixture(IrsReviewCandidate candidate, IrsReviewRecord record)
    {
        var originals = (record.SavedPaths ?? []).Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)).ToArray();
        if (originals.Length == 0)
            originals = [$"{candidate.ProducedAt:yyyyMMdd_HHmmss}_{candidate.LotId}_{candidate.CellId}_EXT_0_0.jpg"];
        return candidate with { Inspection = new(record.MachineId, "E81C", candidate.LotId, candidate.CellId,
            candidate.ProducedAt, candidate.ProducedAt, originals) };
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
            string.Empty,
            new InspectionContext(machine.Id, machine.Model, "LOT", cellId,
                new DateTime(2026, 6, 23, 1, 2, 3), new DateTime(2026, 6, 23, 1, 2, 3),
                [$"20260623_010203_LOT_{cellId}_EXT_0_0.jpg"]));

    private static async Task CollectFixtureReviewsAsync(AppStorage storage, IDlngReviewStore reviews)
    {
        var collection = new TrainingCollectionService(storage);
        foreach (var original in (await reviews.LoadAsync(default)).Values.ToArray())
        {
            if (original.IsFallbackRaw || !ReviewSemantics.Known(original.FinalClass)) continue;
            var paths = original.ImagePaths.ToList();
            if (paths.Count == 1)
            {
                var source = paths[0];
                var partner = source.Contains("_SourceMap", StringComparison.OrdinalIgnoreCase)
                    ? source.Replace("_SourceMap", "_ActiveMap") : Path.ChangeExtension(source, null) + "_mask.png";
                await File.WriteAllBytesAsync(partner, [2]);
                paths.Add(partner);
            }
            var selected = original with { IncludeInTraining = true, TrainingSelectedAt = DateTimeOffset.Now, ImagePaths = paths };
            await reviews.SaveAsync(selected, default);
            Assert.Equal("Collected", await collection.ApplyAsync(selected));
        }
    }

    private static Task SaveDlngReviewAsync(
        IDlngReviewStore reviews,
        DlngReviewItem item,
        string finalClass,
        bool isFallbackRaw) =>
        reviews.SaveAsync(new(
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
            finalClass,
            isFallbackRaw,
            item.Images.Select(x => x.Path).ToArray(),
            DateTimeOffset.Now), CancellationToken.None);

    private static async Task WriteNgBypassCsvAsync(
        string csv,
        string shareRoot,
        string date,
        string cellId)
    {
        var raw = Path.Combine(shareRoot, "Files", "Image", "Raw", cellId);
        Directory.CreateDirectory(raw);
        Directory.CreateDirectory(Path.GetDirectoryName(csv)!);
        await File.WriteAllBytesAsync(Path.Combine(raw, "raw1.jpg"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(raw, "raw2.jpg"), [2]);
        await File.WriteAllBytesAsync(Path.Combine(raw, "raw3.jpg"), [3]);
        var headers = new[]
        {
            "DATE", "TIME", "MODEL-ID", "LOT-ID", "CELL-ID", "UPPER_MEASURE_A-OK/NG",
            "UPPER_IMAGE-PATH-1", "UPPER_IMAGE-PATH-2", "UPPER_IMAGE-PATH-3"
        };
        var values = new[]
        {
            date, "07:00:00", "E81C", "LOT", cellId, "NG",
            $@"E:\Files\Image\Raw\{cellId}\raw1.jpg",
            $@"E:\Files\Image\Raw\{cellId}\raw2.jpg",
            $@"E:\Files\Image\Raw\{cellId}\raw3.jpg"
        };
        await File.WriteAllLinesAsync(csv, [string.Join(",", headers), string.Join(",", values)]);
    }

    private sealed class FakeShareResolver(string root) : ISharePathResolver
    {
        public string GetRoot(WeldingMachine machine, char drive) => root;

        public void RecordAccessibleRoot(WeldingMachine machine, char drive, string root)
        {
        }
    }

    private sealed class DriveShareResolver(string root) : ISharePathResolver
    {
        public string GetRoot(WeldingMachine machine, char drive) =>
            Path.Combine(root, char.ToUpperInvariant(drive).ToString());

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

    private sealed class MultiFileLocator(IReadOnlyDictionary<DateOnly, IReadOnlyList<string>> paths) : IDailyCsvLocator
    {
        public Task<IReadOnlyList<string>> FindAsync(
            WeldingMachine machine,
            DateOnly requestedDate,
            CancellationToken cancellationToken) =>
            Task.FromResult(paths.TryGetValue(requestedDate, out var found) ? found : []);
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
        public IReadOnlyList<SummaryDetailRow> Details { get; private set; } = [];

        public Task<string> WriteAsync(
            DateOnly reportDate,
            DateTime windowStart,
            DateTime windowEndExclusive,
            IReadOnlyList<SummaryReportRow> rows,
            IReadOnlyList<SummaryDetailRow> details,
            CancellationToken cancellationToken)
        {
            Rows = rows;
            Details = details;
            return Task.FromResult("summary.csv");
        }
    }

    private sealed class FakeMachineRegistry(IReadOnlyList<WeldingMachine> machines) : IMachineRegistry
    {
        public IReadOnlyList<WeldingMachine> All { get; } = machines;

        public WeldingMachine Get(string id) =>
            All.First(machine => machine.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeIrsDatasetService : IIrsDatasetService
    {
        public Task<IReadOnlyList<IrsDatasetItem>> BuildQueueAsync(
            IReadOnlyList<IrsReviewCandidate> candidates,
            IReadOnlyList<IrsReviewRecord> reviewRecords,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IrsDatasetItem>>([]);

        public Task<IReadOnlyDictionary<string, IrsDatasetDecision>> LoadDecisionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, IrsDatasetDecision>>(
                new Dictionary<string, IrsDatasetDecision>(StringComparer.OrdinalIgnoreCase));

        public Task SaveDecisionAsync(
            IrsDatasetItem item,
            IReadOnlyList<string> finalClasses,
            bool noNeedToRetrain,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IrsSummaryResult> WriteSummaryAsync(
            IReadOnlyList<IrsReviewCandidate> candidates,
            IReadOnlyList<IrsReviewRecord> reviewRecords,
            IReadOnlyList<IrsDatasetItem> datasetItems,
            CancellationToken cancellationToken,
            IProgress<string>? progress = null) =>
            Task.FromResult(new IrsSummaryResult("", "", []));
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








