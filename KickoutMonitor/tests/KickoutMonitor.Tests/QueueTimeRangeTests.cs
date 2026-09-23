using KickoutMonitor.Domain;

namespace KickoutMonitor.Tests;

public class QueueTimeRangeTests
{
    [Theory]
    [InlineData(6, 29, -1)]
    [InlineData(6, 30, 0)]
    [InlineData(23, 59, 0)]
    [InlineData(0, 0, -1)]
    public void KickoutDefaultsFollowSixThirtyCutover(int hour, int minute, int dayOffset)
    {
        var now = new DateTime(2026, 9, 23, hour, minute, 0);
        var range = QueueTimeRange.KickoutDefault(now);
        Assert.Equal(now.Date.AddDays(dayOffset).AddHours(6), range.Start);
        Assert.Equal(range.Start.AddDays(1), range.End);
    }

    [Fact]
    public void DlngDefaultsAreYesterdaySixToCurrentTime()
    {
        var now = new DateTime(2027, 1, 1, 2, 15, 0);
        var range = QueueTimeRange.DlngDefault(now);
        Assert.Equal(new DateTime(2026, 12, 31, 6, 0, 0), range.Start);
        Assert.Equal(now, range.End);
        Assert.True(range.IsValid);
    }

    [Theory]
    [InlineData("", "06:00")]
    [InlineData("garbage", "06:00")]
    [InlineData("24:00", "06:00")]
    [InlineData("-1:00", "06:00")]
    [InlineData("06:60", "06:00")]
    [InlineData("06:00", "25:00")]
    public void InvalidTimeReturnsMessage(string start, string end)
    {
        Assert.False(QueueTimeRange.TryCreate(new(2026,9,20), start, new(2026,9,21), end, out var range, out var error));
        Assert.Null(range);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ShiftIncludesStartExcludesEndAndSearchesBothDates()
    {
        Assert.True(QueueTimeRange.TryCreate(new(2026,9,20), "06:00", new(2026,9,21), "06:00", out var range, out _));
        Assert.True(range!.Contains(new(2026,9,20,6,0,0)));
        Assert.False(range.Contains(new(2026,9,20,5,59,59)));
        Assert.True(range.Contains(new(2026,9,21,5,59,59,999)));
        Assert.False(range.Contains(new(2026,9,21,6,0,0)));
        Assert.Equal(new[] { new DateOnly(2026,9,20), new DateOnly(2026,9,21) }, range.Dates());
    }

    [Fact]
    public void InvalidOrMissingDatesDoNotRollOverAutomatically()
    {
        Assert.False(QueueTimeRange.TryCreate(new(2026,9,20), "06:00", new(2026,9,20), "06:00", out _, out _));
        Assert.False(QueueTimeRange.TryCreate(new(2026,9,21), "06:00", new(2026,9,20), "06:00", out _, out _));
        Assert.False(QueueTimeRange.TryCreate(null, "06:00", new(2026,9,20), "06:00", out _, out _));
        Assert.True(QueueTimeRange.TryCreate(new(2026,9,20), "6:00 AM", new(2026,9,20), "18:00:01", out _, out _));
    }

    [Fact]
    public void MidnightEndDoesNotSearchFollowingDateAndMaximumDateDoesNotOverflow()
    {
        var range = new QueueTimeRange(new(2026,9,20,6,0,0), new(2026,9,21));
        Assert.Single(range.Dates());
        Assert.Single(new QueueTimeRange(DateTime.MaxValue.Date, DateTime.MaxValue).Dates());
    }
}
