using KickoutMonitor.Domain;
using KickoutMonitor.Infrastructure;
using Xunit;

namespace KickoutMonitor.Tests;

public sealed class OverkillTrendTests
{
    private static DlngReviewRecord Review(DateTime time, string cell = "A", string line = "1-1(-)", string crop = "SEPA", string final = "Overkill") =>
        new(cell + time.Ticks, line, line, time, cell, "NG", crop, "UPPER", crop, "NG", final, false,
            [$"{line}/{time:yyyyMMdd_HHmmss}/{cell}_source.jpg", $"{line}/{time:yyyyMMdd_HHmmss}/{cell}_mask.png"], DateTimeOffset.Now,
            ModelKind: DlngModelKind.Segmentation);
    private static SummaryReportRow Row(string field, int count, string line = "1-1(-)") => new(line, field, 100, 10, 5, count, 0, 0, 0, "E81C");
    private static KickoutHistorySnapshot Snapshot(DateOnly day, params SummaryReportRow[] rows) =>
        new(day, day.ToDateTime(new(6, 0)), day.AddDays(1).ToDateTime(new(6, 0)), rows, [], "fixture");
    [Theory]
    [InlineData(2026, 1, 1, 2025, 12, 26)]
    [InlineData(2026, 3, 1, 2026, 2, 23)]
    [InlineData(2024, 3, 1, 2024, 2, 24)]
    public void SevenDaysIncludesTodayAcrossBoundaries(int y, int m, int d, int sy, int sm, int sd)
    {
        var range = OverkillTrendService.RecentSeven(new(y, m, d, 17, 30, 0));
        Assert.Equal(new DateTime(sy, sm, sd), range.Start);
        Assert.Equal(new DateTime(y, m, d), range.End);
        Assert.Equal(6, (range.End - range.Start).Days);
    }
    [Theory]
    [InlineData(5, 59, 59, 31)]
    [InlineData(6, 0, 0, 1)]
    public void DlngUsesInspectionProductionDay(int h, int m, int s, int expectedDay)
    {
        var review = Review(new(2027, 1, 1, h, m, s)) with { UpdatedAt = new(2027, 5, 1, 0, 0, 0, TimeSpan.Zero) };
        var data = OverkillTrendService.Dlng([review], new(2026, 12, 31), new(2027, 1, 1));
        var p = Assert.Single(data.Points.Where(p => p.Field == "SEPA" && p.HasData));
        Assert.Equal(expectedDay, p.Day.Day); Assert.Equal(1, p.Count);
    }
    [Fact]
    public void KickoutAllDoesNotSumDefectsAndMissingIsDifferentFromZero()
    {
        var day = new DateOnly(2026, 9, 20);
        var data = OverkillTrendService.Kickout([
            Snapshot(day, Row("ALL", 3), Row("SEPA", 3), Row("BEAD", 2)),
            Snapshot(day.AddDays(2), Row("ALL", 0))], day, day.AddDays(2));
        MachineDayCount Point(int offset, string field) => data.Points.Single(p => p.Day == day.AddDays(offset) && p.Line == "1-1(-)" && p.Field == field);
        Assert.Equal(3, Point(0, OverkillTrendService.All).Count);
        Assert.Null(Point(1, "SEPA").Count);
        Assert.Equal(0, Point(2, "SEPA").Count);
        Assert.Equal(100, Point(2, "SEPA").Inspected);
        Assert.All(data.Points.Where(p => p.Line == "2-2(+)"), p => Assert.False(p.HasData));
        var period = OverkillTrendService.Period(data, ["1-1(-)"]);
        Assert.Equal(2, period.Single(p => p.Field == "SEPA").CoveredDays);
        Assert.Equal(3, period.Single(p => p.Field == "SEPA").Count);
    }
    [Fact]
    public void DlngValidZeroUnknownMissingAndSelectionAreIndependent()
    {
        var at = new DateTime(2026, 9, 20, 12, 0, 0); var day = DateOnly.FromDateTime(at);
        var a = Review(at) with { IncludeInTraining = false };
        var real = Review(at.AddDays(1), "B", final: "Real");
        var unknown = Review(at.AddDays(2), "C", final: "No Need to Retrain");
        var other = Review(at.AddDays(2), "D", crop: "BEAD", final: "Real");
        var data = OverkillTrendService.Dlng([a, real, unknown, other], day, day.AddDays(3));
        MachineDayCount Point(int offset, string field) => data.Points.Single(p => p.Day == day.AddDays(offset) && p.Line == "1-1(-)" && p.Field == field);
        Assert.Equal(1, Point(0, "SEPA").Count);
        Assert.Equal(0, Point(1, "SEPA").Count);
        Assert.Null(Point(2, "SEPA").Count);
        Assert.Equal(0, Point(2, OverkillTrendService.All).Count);
        Assert.Null(Point(3, OverkillTrendService.All).Count);
        var selected = OverkillTrendService.Dlng([a with { IncludeInTraining = true }, real, unknown, other], day, day.AddDays(3));
        Assert.Equal(data.Points, selected.Points);
    }
    [Fact]
    public void DuplicateRepresentationCollapsesButReworkAndMachinesStayDistinct()
    {
        var at = new DateTime(2026, 9, 20, 12, 0, 0); var day = DateOnly.FromDateTime(at);
        var a = Review(at);
        var older = a with { ItemKey = "legacy-alias", UpdatedAt = a.UpdatedAt.AddDays(-1), FinalClass = "Real" };
        var rework = Review(at.AddMinutes(20));
        var other = Review(at, line: "2-2(+)");
        var data = OverkillTrendService.Dlng([a, older, rework, other], day, day);
        Assert.Equal(2, data.Points.Single(p => p.Line == "1-1(-)" && p.Field == OverkillTrendService.All).Count);
        Assert.Equal(1, data.Points.Single(p => p.Line == "2-2(+)" && p.Field == OverkillTrendService.All).Count);
    }
    [Fact]
    public void ClassificationCorrectionsMissedAndUnknownAreNotOverkill()
    {
        var at = new DateTime(2026, 9, 20, 12, 0, 0); var day = DateOnly.FromDateTime(at);
        var records = new[] {
            Review(at, "A", final: "01_OK") with { ModelKind = DlngModelKind.Classification, SourceClass = "03_NG_TORN" },
            Review(at, "B", final: "04_NG_PARTICLE") with { ModelKind = DlngModelKind.Classification, SourceClass = "03_NG_TORN" },
            Review(at, "C", final: "03_NG_TORN") with { ModelKind = DlngModelKind.Classification, SourceClass = "01_OK" },
            Review(at, "D", final: "Saved") with { ModelKind = DlngModelKind.Classification }
        };
        var p = OverkillTrendService.Dlng(records, day, day).Points.Single(p => p.Line == "1-1(-)" && p.Field == OverkillTrendService.All);
        Assert.Equal(1, p.Count); Assert.Equal(3, p.Reviewed);
    }
    [Fact]
    public void ColorsUseOneLinearCountScaleAndKeepMissingSeparate()
    {
        Assert.Equal(OverkillTrendService.HeatColor(10, 20), OverkillTrendService.HeatColor(10, 20));
        Assert.NotEqual(OverkillTrendService.HeatColor(null, 20), OverkillTrendService.HeatColor(0, 20));
        Assert.Equal("#EFF6FF", OverkillTrendService.HeatColor(0, 0));
        int Red(int count) => Convert.ToInt32(OverkillTrendService.HeatColor(count, 20).Substring(1, 2), 16);
        Assert.True(Red(0) > Red(10)); Assert.True(Red(10) > Red(20));
        Assert.InRange(Math.Abs((Red(0) + Red(20)) / 2 - Red(10)), 0, 1);
    }
    [Fact]
    public void InvalidAndEmptyRangesAreSafe()
    {
        var day = new DateOnly(2026, 9, 20);
        Assert.Empty(OverkillTrendService.Dlng([], day, day.AddDays(-1)).Points);
        Assert.Empty(OverkillTrendService.Kickout([], day, day.AddDays(3661)).Points);
        var empty = OverkillTrendService.Dlng([], day, day.AddDays(6));
        Assert.Equal(56, empty.Points.Count); Assert.All(empty.Points, p => Assert.Null(p.Count));
    }
}
