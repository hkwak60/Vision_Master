using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed record MachineDayCount(DateOnly Day, string Line, string Field, int? Count, int Reviewed, int Inspected)
{
    public bool HasData => Count.HasValue;
}
public sealed record PeriodOverkill(string Line, string Field, int? Count, int CoveredDays);
public sealed record OverkillTrendData(IReadOnlyList<string> Fields, IReadOnlyList<MachineDayCount> Points);
/// <summary>Read-only projections. Report windows and persisted decisions are never rewritten.</summary>
public static class OverkillTrendService
{
    public const string All = "전체";
    public static readonly string[] Lines = ["1-1(-)", "1-1(+)", "1-2(-)", "1-2(+)", "2-1(-)", "2-1(+)", "2-2(-)", "2-2(+)"];
    public static readonly string[] Colors = ["#2563EB", "#0891B2", "#7C3AED", "#DB2777", "#059669", "#CA8A04", "#EA580C", "#475569"];
    public static (DateTime Start, DateTime End) RecentSeven(DateTime now) => (now.Date.AddDays(-6), now.Date);
    public static DateOnly ProductionDay(DateTime inspectedAt) => DateOnly.FromDateTime(inspectedAt.AddHours(-6));
    public static IReadOnlyList<DlngReviewRecord> UniqueReviews(IEnumerable<DlngReviewRecord> records) =>
        records.GroupBy(ReviewSemantics.SampleId).Select(g => g.OrderByDescending(r => r.UpdatedAt).First()).ToArray();

    public static OverkillTrendData Kickout(IEnumerable<KickoutHistorySnapshot> history, DateOnly start, DateOnly end)
    {
        var snapshots = history.ToArray();
        var fields = snapshots.SelectMany(h => h.Rows).Where(r => r.Defect != "ALL").Select(r => r.Defect).Distinct().Order().ToArray();
        var rows = snapshots.Where(h => h.Day >= start && h.Day <= end)
            .SelectMany(h => h.Rows.Select(r => (h.Day, Row: r))).ToLookup(x => (x.Day, x.Row.LinePolarity));
        return Build(fields, start, end, (day, line, field) =>
        {
            var group = rows[(day, line)].Select(x => x.Row).ToArray();
            var all = group.Where(r => r.Defect == "ALL").ToArray();
            var selected = group.Where(r => r.Defect == (field == All ? "ALL" : field)).ToArray();
            // A complete report's absent defect row is zero, but an absent report is a gap.
            var present = selected.Length > 0 || (field != All && all.Length > 0);
            return new(day, line, field, present ? selected.Sum(r => r.Overkill) : null,
                selected.Sum(r => r.RealNg + r.Overkill), all.Sum(r => r.TotalInspected));
        });
    }
    public static OverkillTrendData Dlng(IEnumerable<DlngReviewRecord> records, DateOnly start, DateOnly end)
    {
        var unique = UniqueReviews(records);
        var fields = unique.Select(r => r.CropFolder).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().Order().ToArray();
        var rows = unique.Select(r => (Day: ProductionDay(r.InspectedAt), Review: r, Outcome: ReviewSemantics.Outcome(r)))
            .Where(x => x.Day >= start && x.Day <= end && ReviewSemantics.IsApplicableOutcome(x.Outcome))
            .ToLookup(x => (x.Day, x.Review.LinePolarity));
        return Build(fields, start, end, (day, line, field) =>
        {
            var selected = rows[(day, line)].Where(x => field == All || x.Review.CropFolder == field).ToArray();
            return new(day, line, field, selected.Length > 0 ? selected.Count(x => x.Outcome == "Overkill") : null, selected.Length, 0);
        });
    }
    private static OverkillTrendData Build(string[] fields, DateOnly start, DateOnly end, Func<DateOnly, string, string, MachineDayCount> get)
    {
        var result = new List<MachineDayCount>();
        if (end < start || end.DayNumber - start.DayNumber > 3660) return new(fields, result);
        for (var number = start.DayNumber; number <= end.DayNumber; number++)
            foreach (var line in Lines)
                foreach (var field in fields.Prepend(All))
                    result.Add(get(DateOnly.FromDayNumber(number), line, field));
        return new(fields, result);
    }
    public static IReadOnlyList<PeriodOverkill> Period(OverkillTrendData data, IEnumerable<string> visibleLines)
    {
        var visible = visibleLines.ToHashSet();
        return data.Points.Where(p => p.Field != All && visible.Contains(p.Line))
            .GroupBy(p => (p.Line, p.Field)).Select(g => new PeriodOverkill(g.Key.Line, g.Key.Field,
                g.Any(p => p.HasData) ? g.Sum(p => p.Count ?? 0) : null, g.Count(p => p.HasData))).ToArray();
    }
    public static string HeatColor(int? count, int maximum)
    {
        if (count is null) return "#E5E7EB";
        var fraction = maximum <= 0 ? 0 : Math.Clamp((double)count.Value / maximum, 0, 1);
        int Blend(int low, int high) => (int)Math.Round(low + (high - low) * fraction);
        return $"#{Blend(239, 37):X2}{Blend(246, 99):X2}{Blend(255, 235):X2}";
    }
}
