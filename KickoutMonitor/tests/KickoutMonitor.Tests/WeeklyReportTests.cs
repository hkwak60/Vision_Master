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
        writer.Update(original, [s], Day.AddDays(-2), Day, []);
        Assert.Equal(before, File.ReadAllBytes(original + ".bak"));
        var once = ReadParts(original);
        Assert.Equal(old["xl/worksheets/sheet1.xml"], once["xl/worksheets/sheet1.xml"]);
        writer.Update(original, [s], Day.AddDays(-2), Day, []);
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
            Assert.Throws<IOException>(() => new WeeklyReportWorkbook().Update(path, [s], Day, Day, []));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }
    [Fact]
    public void CasesKeepOfflineImagesOrderingAndPeriodBoundary()
    {
        Directory.CreateDirectory(_root);
        var image = Path.Combine(_root, "exact.jpg"); File.WriteAllBytes(image, [1, 2, 3]);
        var store = new WeeklyReportCaseStore(Storage);
        WeeklyReportCase Case() => new() { Start = Day, End = Day, Day = Day, Line = "1-1(-)", Field = "SEPA", SourceFolder = _root, SourceImage = image, Order = 100 };
        var first = Case(); var second = Case(); var third = Case();
        store.Save(first); store.Save(second); store.Save(third);
        Assert.Throws<InvalidOperationException>(() => store.Save(Case()));
        var next = Case(); next.Start = Day.AddDays(1); next.End = next.Start; store.Save(next);
        File.Delete(image); first.Reason = "offline"; store.Save(first);
        Assert.True(File.Exists(first.LocalImage)); Assert.Equal("", first.Error);
        store.Remove(second.Id); Assert.Equal(3, store.Load().Count);
        store.Move(third.Id, -1);
        Assert.Equal("offline", store.Load().Single(x => x.Id == first.Id).Reason);
    }
    private static Dictionary<string, byte[]> ReadParts(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(e => e.FullName, e => { using var s = e.Open(); using var b = new MemoryStream(); s.CopyTo(b); return b.ToArray(); });
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
