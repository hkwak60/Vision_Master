using System.Text.Json;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed record WeeklyCount(DateOnly Start, DateOnly End, int? Count, int CoveredDays, bool Partial);
public sealed record WeeklyLineSummary(string Line, int? Inspected, int? Ng, int? Real, int? Overkill, int? Pending, int CoveredDays);
public static class WeeklyReportData
{
    public static DateOnly FirstWeek(DateOnly end) => end.AddDays(-(int)end.DayOfWeek - 49);
    public static IReadOnlyList<WeeklyCount> Weeks(IReadOnlyList<KickoutHistorySnapshot> history, string line, DateOnly end)
    {
        var start = FirstWeek(end);
        var points = OverkillTrendService.Kickout(history, start, end).Points
            .Where(x => x.Line == line && x.Field == OverkillTrendService.All).ToArray();
        return Enumerable.Range(0, 8).Select(i =>
        {
            var first = start.AddDays(i * 7); var last = first.AddDays(6);
            var rows = points.Where(x => x.Day >= first && x.Day <= last && x.HasData).ToArray();
            return new WeeklyCount(first, last > end ? end : last, rows.Length == 0 ? null : rows.Sum(x => x.Count!.Value),
                rows.Length, last > end);
        }).ToArray();
    }
    public static WeeklyLineSummary Summary(IReadOnlyList<KickoutHistorySnapshot> history, string line, DateOnly start, DateOnly end)
    {
        var selected = KickoutOverkillHistory.Normalize(history).Where(h => h.Day >= start && h.Day <= end).ToArray();
        var rows = selected.SelectMany(h => h.Rows).Where(r => r.LinePolarity == line && r.Defect == "ALL").ToArray();
        return new(line, rows.Length == 0 ? null : rows.Sum(r => r.TotalInspected),
            rows.Length == 0 ? null : rows.Sum(r => r.InitialNg),
            rows.Length == 0 ? null : rows.Sum(r => r.RealNg),
            rows.Length == 0 ? null : rows.Sum(r => r.Overkill),
            rows.Length == 0 ? null : selected.Sum(h => h.Rows.Where(r => r.LinePolarity == line && r.Defect == "ALL")
                .Sum(r => Math.Max(0, r.InitialNg - r.RealNg - Math.Max(r.Overkill,
                    h.Details.Count(d => d.LinePolarity == line && d.Decision == ReviewDecision.Overkill))))),
            selected.Where(h => h.Rows.Any(r => r.LinePolarity == line && r.Defect == "ALL")).Select(h => h.Day).Distinct().Count());
    }
}
