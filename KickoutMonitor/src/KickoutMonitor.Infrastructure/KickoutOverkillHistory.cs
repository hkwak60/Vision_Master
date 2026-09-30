using System.Globalization;
using System.Text.RegularExpressions;
using KickoutMonitor.Domain;
namespace KickoutMonitor.Infrastructure;

public static class KickoutOverkillHistory
{
    public static (string Key, DateTime? Time) Identity(SummaryDetailRow d, string fallback)
    {
        string Value(string name) => d.Headers.Select((h, i) => (h, i)).Where(x => x.h.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.i < d.Values.Count ? d.Values[x.i] : "").FirstOrDefault() ?? "";
        var lot = string.IsNullOrWhiteSpace(d.LotId) ? Value("LOT-ID") : d.LotId;
        var cell = string.IsNullOrWhiteSpace(d.CellId) ? Value("CELL-ID") : d.CellId;
        var time = d.InspectedAt;
        if (time is null && DateTime.TryParseExact(Value("DATE") + " " + Value("TIME"),
            ["yyyyMMdd HH:mm:ss", "yyyyMMdd HH:mm:ss.fff"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) time = parsed;
        var folder = InspectionIdentity.FileName((d.LocalFolder ?? "").TrimEnd('\\', '/'));
        var match = Regex.Match(folder, @"^(\d{8})_(\d{6})_([^_]+)_([^_]+)$");
        if (match.Success)
        {
            if (lot.Length == 0) lot = match.Groups[3].Value;
            if (cell.Length == 0) cell = match.Groups[4].Value;
            if (time is null && DateTime.TryParseExact(match.Groups[1].Value + match.Groups[2].Value,
                "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) time = parsed;
        }
        var inspection = string.IsNullOrWhiteSpace(d.InspectionKey)
            ? (time.HasValue && cell.Length > 0 ? $"{time:O}|{cell}|{d.LocalFolder}" : fallback) : d.InspectionKey;
        return (InspectionIdentity.Group(d.LinePolarity, cell.Length == 0 ? "" : lot, cell, d.LinePolarity + "|" + inspection), time);
    }

    // Counts only are deduplicated. Keep every attempt's detail/images for investigation and export.
    public static IReadOnlyList<KickoutHistorySnapshot> Normalize(IEnumerable<KickoutHistorySnapshot> history)
    {
        var snapshots = history.ToArray();
        var all = snapshots.SelectMany((s, index) => s.Details.Select((d, ordinal) =>
            (Snapshot: index, Detail: d, Identity: Identity(d, $"{s.Source}|{s.Day}|{ordinal}"), s.Day)))
            .Where(x => x.Detail.Decision == ReviewDecision.Overkill)
            .OrderBy(x => x.Identity.Time ?? x.Day.ToDateTime(TimeOnly.MinValue)).ThenBy(x => x.Day).ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenField = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<(int Snapshot, string Line, string Field), int>();
        foreach (var item in all)
        {
            if (seen.Add(item.Identity.Key)) Add(item.Snapshot, item.Detail.LinePolarity, "ALL");
            if (seenField.Add(item.Identity.Key + "|" + item.Detail.Defect)) Add(item.Snapshot, item.Detail.LinePolarity, item.Detail.Defect);
        }
        void Add(int index, string line, string field) { var key = (index, line, field); counts[key] = counts.GetValueOrDefault(key) + 1; }
        return snapshots.Select((snapshot, index) => snapshot with
        {
            Rows = snapshot.Rows.Select(row =>
            {
                var details = all.Where(x => x.Snapshot == index && x.Detail.LinePolarity == row.LinePolarity
                    && (row.Defect == "ALL" || x.Detail.Defect.Equals(row.Defect, StringComparison.OrdinalIgnoreCase))).ToArray();
                var unique = details.Select(x => x.Identity.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                // Old aggregates without adequate identity evidence must not be guessed.
                if (row.Overkill != details.Length && row.Overkill != unique) return row;
                var count = counts.GetValueOrDefault((index, row.LinePolarity, row.Defect));
                return row with { Overkill = count, OverkillRate = row.TotalInspected == 0 ? 0 : (double)count / row.TotalInspected };
            }).ToArray()
        }).ToArray();
    }
    public static IReadOnlyList<SummaryDetailRow> ImportImageDetails(string reportFolder)
    {
        var root = Path.Combine(reportFolder, "OVERKILL");
        if (!Directory.Exists(root)) return [];
        var result = new List<SummaryDetailRow>();
        foreach (var line in Directory.EnumerateDirectories(root))
        foreach (var defect in Directory.EnumerateDirectories(line))
        foreach (var folder in Directory.EnumerateDirectories(defect, "*", SearchOption.AllDirectories))
            if (Directory.EnumerateFiles(folder).Any(IsImage))
                result.Add(new(Path.GetFileName(line), Path.GetFileName(defect), ReviewDecision.Overkill, folder, [], []));
        return result;
    }
    public static bool IsImage(string path) => new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(Path.GetExtension(path).ToLowerInvariant());
}
