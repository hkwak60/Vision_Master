using System.IO.Compression;
using System.Xml.Linq;
using KickoutMonitor.Domain;
using KickoutMonitor.Infrastructure;
using Xunit;
namespace KickoutMonitor.Tests;

public sealed class WeeklyReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WeeklyReportTests", Guid.NewGuid().ToString("N"));
    private AppStorage Storage => new(_root);
    private static readonly DateOnly Day = new(2026, 9, 29);
    private static KickoutHistorySnapshot Snapshot(DateOnly day, int overkill = 3) => new(day,
        day.ToDateTime(new(6, 0)), day.AddDays(1).ToDateTime(new(6, 0)),
        [new("1-1(-)", "ALL", 100, 10, 5, overkill, 0, 0, 0, "E81C"),
         new("1-1(-)", "SEPA", 100, 10, 5, overkill, 0, 0, 0, "E81C"),
         new("1-1(-)", "ZERO", 100, 0, 0, 0, 0, 0, 0, "E81C")], [], "fixture");
    [Fact]
    public void WeeksKeepZeroGapsPartialAndYearBoundary()
    {
        var end = new DateOnly(2027, 1, 2);
        var weeks = WeeklyReportData.Weeks([Snapshot(end, 0)], "1-1(-)", end);
        Assert.Equal(8, weeks.Count);
        Assert.All(weeks, w => Assert.Equal(DayOfWeek.Sunday, w.Start.DayOfWeek));
        Assert.Null(weeks[0].Count); Assert.Equal(0, weeks[7].Count); Assert.False(weeks[7].Partial);
        Assert.Equal(1, weeks[7].CoveredDays);
        Assert.True(WeeklyReportData.Weeks([], "1-1(-)", end.AddDays(-1))[7].Partial);
    }
    [Fact]
    public void SummaryUsesAllAndDoesNotClassifyPendingAsReal()
    {
        var total = WeeklyReportData.Summary([Snapshot(Day)], "1-1(-)", Day, Day);
        Assert.Equal(3, total.Overkill); Assert.Equal(5, total.Real); Assert.Equal(2, total.Pending);
        Assert.Null(WeeklyReportData.Summary([], "1-1(-)", Day, Day).Overkill);
    }
    [Fact]
    public async Task WorkbookPreservesOriginalPartsAndRepeatedUpdateDoesNotGrowStylesOrCharts()
    {
        var s = Snapshot(Day);
        var original = await new SummaryReportWriter(Storage).WriteAsync(Day, s.Start, s.End, s.Rows, [], default);
        byte[] before = File.ReadAllBytes(original);
        var old = ReadParts(original);
        var writer = new WeeklyReportWorkbook();
        writer.Update(original, [s], Day.AddDays(-2), Day, OverkillTrendService.Lines);
        Assert.Equal(before, File.ReadAllBytes(original + ".bak"));
        var once = ReadParts(original);
        Assert.Equal(old["xl/worksheets/sheet1.xml"], once["xl/worksheets/sheet1.xml"]);
        writer.Update(original, [s], Day.AddDays(-2), Day, OverkillTrendService.Lines);
        var twice = ReadParts(original);
        Assert.Equal(once["xl/styles.xml"], twice["xl/styles.xml"]);
        Assert.Equal(16, twice.Keys.Count(x => x.StartsWith("xl/vmWeekly/chart")));
        Assert.Equal(once.Keys.Order(), twice.Keys.Order());
        XNamespace c = "http://schemas.openxmlformats.org/drawingml/2006/chart";
        var chart = XDocument.Load(new MemoryStream(twice["xl/vmWeekly/chart1.xml"]));
        Assert.Equal("gap", chart.Descendants(c + "dispBlanksAs").Single().Attribute("val")!.Value);
        Assert.Single(chart.Descendants(c + "numCache").Single().Elements(c + "pt"));
        var sheet = System.Text.Encoding.UTF8.GetString(twice["xl/vmWeekly/sheet.xml"]);
        Assert.DoesNotContain(">ZERO<", sheet); Assert.Contains("SEPA", sheet);
    }
    [Fact]
    public async Task LockedWorkbookIsUnchanged()
    {
        var s = Snapshot(Day); var path = await new SummaryReportWriter(Storage).WriteAsync(Day, s.Start, s.End, s.Rows, [], default);
        var before = File.ReadAllBytes(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => new WeeklyReportWorkbook().Update(path, [s], Day, Day, OverkillTrendService.Lines));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }
    [Fact]
    public void CommitRestoresMissingOriginalAndRetainsPreparedOnPartialFailure()
    {
        Directory.CreateDirectory(_root);
        var original = Path.Combine(_root, "report.xlsx"); var prepared = original + ".tmp";
        File.WriteAllText(original, "old report"); File.WriteAllText(prepared, "complete new report");
        var error = Assert.Throws<IOException>(() => SafeWorkbookCommit.Commit(prepared, original, SafeWorkbookCommit.Digest(original),
            (source, target) => { File.Delete(target); throw new IOException("simulated partial filesystem failure"); }));
        Assert.Equal("old report", File.ReadAllText(original));
        Assert.Equal("old report", File.ReadAllText(original + ".bak"));
        Assert.Equal("complete new report", File.ReadAllText(prepared));
        Assert.Contains("복구", error.Message);
        SafeWorkbookCommit.Commit(prepared, original, SafeWorkbookCommit.Digest(original));
        Assert.Equal("complete new report", File.ReadAllText(original));
    }
    [Fact]
    public void CommitDoesNotOverwriteWorkbookEditedAfterSnapshot()
    {
        Directory.CreateDirectory(_root);
        var original = Path.Combine(_root, "report.xlsx"); var prepared = original + ".tmp";
        File.WriteAllText(original, "old"); var digest = SafeWorkbookCommit.Digest(original);
        File.WriteAllText(original, "user edited"); File.WriteAllText(prepared, "new");
        Assert.Throws<IOException>(() => SafeWorkbookCommit.Commit(prepared, original, digest));
        Assert.Equal("user edited", File.ReadAllText(original));
        Assert.True(File.Exists(prepared));
    }
    [Fact]
    public void ReworkCountsOncePerLotLineAndFieldWhileKeepingAttempts()
    {
        SummaryDetailRow Detail(string lot, string cell, string defect, int second, string line = "1-1(-)") =>
            new(line, defect, ReviewDecision.Overkill, null, [], [])
            { LotId = lot, CellId = cell, InspectionKey = line + second, InspectedAt = Day.ToDateTime(new(12, 0, second)) };
        var snapshot = Snapshot(Day, 5) with
        {
            Rows = [new("1-1(-)", "ALL", 100, 5, 0, 5, 0, 0, 0),
                new("1-1(-)", "SEPA", 100, 4, 0, 4, 0, 0, 0), new("1-1(-)", "GAP", 100, 1, 0, 1, 0, 0, 0)],
            Details = [Detail("LOT1", "CELL", "SEPA", 1), Detail("LOT1", "CELL", "SEPA", 2),
                Detail("LOT1", "CELL", "GAP", 3), Detail("LOT2", "CELL", "SEPA", 4), Detail("LOT2", "OTHER", "SEPA", 5)]
        };
        var result = KickoutOverkillHistory.Normalize([snapshot]).Single();
        Assert.Equal(3, result.Rows.Single(x => x.Defect == "ALL").Overkill);
        Assert.Equal(3, result.Rows.Single(x => x.Defect == "SEPA").Overkill);
        Assert.Equal(1, result.Rows.Single(x => x.Defect == "GAP").Overkill);
        Assert.Equal(5, result.Details.Count);
        Assert.Equal(result.Rows, KickoutOverkillHistory.Normalize([result]).Single().Rows);
        var tomorrow = snapshot with { Day = Day.AddDays(1) };
        Assert.Equal(0, KickoutOverkillHistory.Normalize([snapshot, tomorrow])[1].Rows[0].Overkill);
        Assert.NotEqual(KickoutOverkillHistory.Identity(Detail("", "X", "SEPA", 1), "a").Key,
            KickoutOverkillHistory.Identity(Detail("", "X", "SEPA", 2), "b").Key);
        Assert.NotEqual(KickoutOverkillHistory.Identity(Detail("LOT1", "X", "SEPA", 1), "a").Key,
            KickoutOverkillHistory.Identity(Detail("LOT1", "X", "SEPA", 1, "1-1(+)"), "b").Key);
    }
    [Fact]
    public void ExportCopiesSelectedAttemptsOnlyAndRetriesEmptyFiles()
    {
        var folder = Path.Combine(_root, "20260929_120000_LOT_CELL"); Directory.CreateDirectory(folder);
        var jpg = Path.Combine(folder, "raw.jpg"); var png = Path.Combine(folder, "overlay.png");
        File.WriteAllBytes(jpg, [1, 2]); File.WriteAllBytes(png, []);
        var detail = new SummaryDetailRow("1-1(-)", "SEPA", ReviewDecision.Overkill, folder, [], []);
        var snapshot = Snapshot(Day, 1) with { Details = [detail, detail with { LinePolarity = "2-1(-)" }] };
        var path = Path.Combine(_root, "report.xlsx");
        var first = WeeklyOverkillImages.Export(path, [snapshot], Day, Day, ["1-1(-)"]);
        Assert.Single(first.Failures);
        Assert.False(Directory.Exists(Path.Combine(first.Folder, "2-1(-)")));
        File.WriteAllBytes(png, [3, 4]);
        var second = WeeklyOverkillImages.Export(path, [snapshot], Day, Day, ["1-1(-)"]);
        Assert.Empty(second.Failures); Assert.Equal(2, second.Files);
        Assert.Equal(2, Directory.GetFiles(second.Folder, "*", SearchOption.AllDirectories).Count(KickoutOverkillHistory.IsImage));
        WeeklyOverkillImages.Export(path, [snapshot], Day, Day, ["1-1(-)"]);
        Assert.Equal(2, Directory.GetFiles(second.Folder, "*", SearchOption.AllDirectories).Count(KickoutOverkillHistory.IsImage));
    }
    [Fact]
    public async Task SelectedLinesPopulateReportAndPreserveManualNotes()
    {
        var snapshot = Snapshot(Day);
        var path = await new SummaryReportWriter(Storage).WriteAsync(Day, snapshot.Start, snapshot.End, snapshot.Rows, [], default);
        var writer = new WeeklyReportWorkbook();
        writer.Update(path, [snapshot], Day, Day, ["1-1(-)"]);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("xl/worksheets/vmOverkillReport.xml")!; XDocument doc;
            using (var input = entry.Open()) doc = XDocument.Load(input);
            var row = doc.Descendants(ns + "row").Single(r => (int?)r.Attribute("r") == 3);
            row.Add(new XElement(ns + "c", new XAttribute("r", "K3"), new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", "KEEP MY ACTION"))));
            entry.Delete(); using var output = zip.CreateEntry("xl/worksheets/vmOverkillReport.xml").Open(); doc.Save(output);
        }
        writer.Update(path, [snapshot], Day, Day, ["1-1(-)"]);
        var parts = ReadParts(path);
        Assert.Equal(2, parts.Keys.Count(k => k.StartsWith("xl/vmWeekly/chart")));
        var report = XDocument.Load(new MemoryStream(parts["xl/worksheets/vmOverkillReport.xml"]));
        Assert.Contains("KEEP MY ACTION", report.ToString()); Assert.Contains("SEPA · 3건", report.ToString());
        Assert.Equal("1", report.Descendants(ns + "row").Single(r => (int?)r.Attribute("r") == 4).Attribute("hidden")?.Value);
        var before = File.ReadAllBytes(path);
        Assert.Throws<ArgumentException>(() => writer.Update(path, [snapshot], Day, Day, []));
        Assert.Equal(before, File.ReadAllBytes(path));
    }
    [Fact]
    public async Task ReplacingFormulaReportDropsStaleCalculationChain()
    {
        var snapshot = Snapshot(Day);
        var path = await new SummaryReportWriter(Storage).WriteAsync(Day, snapshot.Start, snapshot.End, snapshot.Rows, [], default);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            void Amend(string part, Action<XDocument> edit)
            {
                var entry = zip.GetEntry(part)!; XDocument doc;
                using (var stream = entry.Open()) doc = XDocument.Load(stream);
                edit(doc); entry.Delete(); using var output = zip.CreateEntry(part).Open(); doc.Save(output);
            }
            Amend("xl/_rels/workbook.xml.rels", doc => doc.Root!.Add(new XElement(doc.Root.Name.Namespace + "Relationship",
                new XAttribute("Id", "calc"), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/calcChain"),
                new XAttribute("Target", "calcChain.xml"))));
            Amend("[Content_Types].xml", doc => doc.Root!.Add(new XElement(doc.Root.Name.Namespace + "Override",
                new XAttribute("PartName", "/xl/calcChain.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.calcChain+xml"))));
            using var output = zip.CreateEntry("xl/calcChain.xml").Open();
            new XDocument(new XElement("calcChain")).Save(output);
        }
        new WeeklyReportWorkbook().Update(path, [snapshot], Day, Day, ["1-1(-)"]);
        var parts = ReadParts(path);
        Assert.DoesNotContain("xl/calcChain.xml", parts.Keys);
        Assert.DoesNotContain("calcChain", System.Text.Encoding.UTF8.GetString(parts["xl/_rels/workbook.xml.rels"]));
        Assert.DoesNotContain("calcChain", System.Text.Encoding.UTF8.GetString(parts["[Content_Types].xml"]));
    }
    private static Dictionary<string, byte[]> ReadParts(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(e => e.FullName, e => { using var s = e.Open(); using var b = new MemoryStream(); s.CopyTo(b); return b.ToArray(); });
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
