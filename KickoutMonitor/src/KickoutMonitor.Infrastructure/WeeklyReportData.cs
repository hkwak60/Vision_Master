using System.Text.Json;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed record WeeklyCount(DateOnly Start, DateOnly End, int? Count, int CoveredDays, bool Partial);
public sealed record WeeklyLineSummary(string Line, int? Inspected, int? Ng, int? Real, int? Overkill, int? Pending, int CoveredDays);
public sealed class WeeklyReportCase
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public DateOnly Day { get; set; }
    public string Line { get; set; } = "";
    public string Field { get; set; } = "";
    public string Inspection { get; set; } = "";
    public string SourceFolder { get; set; } = "";
    public string SourceImage { get; set; } = "";
    public string LocalImage { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Action { get; set; } = "";
    public string Error { get; set; } = "";
    public int Order { get; set; }
    public override string ToString() => $"{Line} · {Field} · {Day:yyyy-MM-dd} · {Reason}";
}
public sealed class WeeklyReportCaseStore
{
    private readonly string _root;
    private string Manifest => Path.Combine(_root, "cases.json");
    public WeeklyReportCaseStore(AppStorage storage) => _root = Path.Combine(storage.Summary, ".weekly");
    public IReadOnlyList<WeeklyReportCase> Load() => File.Exists(Manifest)
        ? JsonSerializer.Deserialize<List<WeeklyReportCase>>(File.ReadAllText(Manifest)) ?? [] : [];
    public static IReadOnlyList<string> Images(string folder) => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(p =>
            new[] { ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(p).ToLowerInvariant())).Order().ToArray() : [];
    public void Save(WeeklyReportCase item)
    {
        var records = Load().ToList();
        if (records.Count(x => x.Id != item.Id && x.Start == item.Start && x.End == item.End && x.Line == item.Line) >= 3)
            throw new InvalidOperationException("보고 기간·기계별 대표 사례는 최대 3개입니다.");
        if (string.IsNullOrWhiteSpace(item.SourceImage) || !Images(item.SourceFolder).Contains(item.SourceImage, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(item.LocalImage)) item.Error = "선택한 검사 폴더에서 이미지를 찾을 수 없습니다. 이미지 선택 후 다시 저장하세요.";
        }
        else
        {
            try
            {
                if (new FileInfo(item.SourceImage).Length == 0) throw new IOException("선택 이미지가 비어 있습니다.");
                var folder = Path.Combine(_root, "images", item.Id);
                Directory.CreateDirectory(folder);
                var target = Path.Combine(folder, Path.GetFileName(item.SourceImage));
                File.Copy(item.SourceImage, target + ".tmp", true);
                File.Move(target + ".tmp", target, true);
                item.LocalImage = target;
                item.Error = "";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { item.Error = e.Message; }
        }
        records.RemoveAll(x => x.Id == item.Id);
        records.Add(item);
        Write(records);
    }
    public void Remove(string id) => Write(Load().Where(x => x.Id != id).ToArray());
    public void Move(string id, int direction)
    {
        var records = Load().ToList(); var item = records.First(x => x.Id == id);
        var group = records.Where(x => x.Start == item.Start && x.End == item.End && x.Line == item.Line)
            .OrderBy(x => x.Order).ThenBy(x => x.Id).ToArray();
        var index = Array.IndexOf(group, item); var other = index + direction;
        if (other < 0 || other >= group.Length) return;
        (group[index], group[other]) = (group[other], group[index]);
        for (var i = 0; i < group.Length; i++) group[i].Order = i;
        Write(records);
    }
    private void Write(IReadOnlyList<WeeklyReportCase> records)
    {
        Directory.CreateDirectory(_root);
        ReviewCompatibility.Backup(Manifest);
        File.WriteAllText(Manifest + ".tmp", JsonSerializer.Serialize(records));
        File.Move(Manifest + ".tmp", Manifest, true);
    }
}
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
        var selected = history.Where(h => h.Day >= start && h.Day <= end).ToArray();
        var rows = selected.SelectMany(h => h.Rows).Where(r => r.LinePolarity == line && r.Defect == "ALL").ToArray();
        return new(line, rows.Length == 0 ? null : rows.Sum(r => r.TotalInspected),
            rows.Length == 0 ? null : rows.Sum(r => r.InitialNg),
            rows.Length == 0 ? null : rows.Sum(r => r.RealNg),
            rows.Length == 0 ? null : rows.Sum(r => r.Overkill),
            rows.Length == 0 ? null : rows.Sum(r => Math.Max(0, r.InitialNg - r.RealNg - r.Overkill)),
            selected.Where(h => h.Rows.Any(r => r.LinePolarity == line && r.Defect == "ALL")).Select(h => h.Day).Distinct().Count());
    }
}
