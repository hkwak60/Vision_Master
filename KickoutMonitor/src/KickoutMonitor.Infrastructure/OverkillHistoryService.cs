using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed record KickoutHistorySnapshot(DateOnly Day, DateTime Start, DateTime End,
    IReadOnlyList<SummaryReportRow> Rows, IReadOnlyList<SummaryDetailRow> Details, string Source);
public sealed record OverkillMetric(string Kind, string Product, string Line, string Field, int Inspected,
    int Reviewed, int Overkill, int Corrections = 0, int Missed = 0, int Unknown = 0, int Collected = 0)
{
    public string PerInspected => Inspected == 0 ? "—" : $"{Overkill:N0} / {Inspected:N0} ({(double)Overkill / Inspected:P1})";
    public string PerReviewed => Reviewed == 0 ? "—" : $"{Overkill:N0} / {Reviewed:N0} ({(double)Overkill / Reviewed:P1})";
    public double Heat => Reviewed == 0 ? 0 : (double)Overkill / Reviewed;
}
public sealed record HistoryContribution(DateOnly Day, string Kind, string Product, string Line, string Field,
    string Identity, string SourceClass, string FinalClass, string Source, int Inspected, int Reviewed, int Overkill);
public sealed record DailyOverkill(DateOnly Day, int? Overkill, int? Reviewed, int? Inspected)
{
    public string Rate => Reviewed is > 0 ? $"{(double)Overkill!.Value / Reviewed.Value:P1}" : "—";
    public string State => Overkill is null ? "No report (gap)" : "Reported";
    public double BarWidth => Reviewed is > 0 ? 180.0 * Overkill!.Value / Reviewed.Value : 0;
}
public sealed class OverkillHistoryService(AppStorage storage, IDlngReviewStore reviews, TrainingCollectionService collection)
{
    private static string HistoryRoot(AppStorage storage) => Path.Combine(storage.Root, "OverkillHistory");
    public static async Task SaveSnapshotAsync(AppStorage storage, KickoutHistorySnapshot snapshot, CancellationToken token = default)
    {
        var root = HistoryRoot(storage);
        Directory.CreateDirectory(root);
        // A regenerated line replaces its whole day snapshot, including removed defect rows.
        foreach (var line in snapshot.Rows.Select(x => x.LinePolarity).Distinct())
        {
            var path = Path.Combine(root, $"{snapshot.Day:yyyyMMdd}_{TrainingCollectionService.Safe(line)}.json");
            var local = snapshot with { Rows = snapshot.Rows.Where(x => x.LinePolarity == line).ToArray(),
                Details = snapshot.Details.Where(x => x.LinePolarity == line).ToArray() };
            ReviewCompatibility.Backup(path);
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(local), token);
            File.Move(path + ".tmp", path, true);
        }
    }
    public List<string> Warnings { get; } = [];
    public async Task<IReadOnlyList<KickoutHistorySnapshot>> LoadKickoutAsync(CancellationToken token = default)
    {
        Warnings.Clear();
        if (Directory.Exists(storage.Summary))
        foreach (var path in Directory.EnumerateFiles(storage.Summary, "NG_Summary_*.xlsx", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var snapshot = ImportWorkbook(path);
                if (snapshot is null) continue;
                // Newly generated JSON preserves richer provenance. Import a changed legacy workbook only.
                var root = HistoryRoot(storage);
                var needs = snapshot.Rows.Select(r => Path.Combine(root, $"{snapshot.Day:yyyyMMdd}_{TrainingCollectionService.Safe(r.LinePolarity)}.json"))
                    .Any(p => !File.Exists(p) || File.GetLastWriteTimeUtc(path) > File.GetLastWriteTimeUtc(p));
                if (needs) await SaveSnapshotAsync(storage, snapshot, token);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or FormatException or System.Xml.XmlException)
            { Warnings.Add($"{Path.GetFileName(path)}: {e.Message}"); }
        }
        if (!Directory.Exists(HistoryRoot(storage))) return [];
        var result = new List<KickoutHistorySnapshot>();
        foreach (var path in Directory.EnumerateFiles(HistoryRoot(storage), "*.json"))
        {
            try { var row = JsonSerializer.Deserialize<KickoutHistorySnapshot>(await File.ReadAllTextAsync(path, token)); if (row is not null) result.Add(row); }
            catch (JsonException e) { Warnings.Add($"{Path.GetFileName(path)}: {e.Message}"); }
        }
        return result;
    }
    public static KickoutHistorySnapshot? ImportWorkbook(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("xl/worksheets/sheet1.xml");
        if (entry is null) return null;
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var strings = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } shared)
        { using var ss = shared.Open(); strings.AddRange(XDocument.Load(ss).Descendants(ns + "si").Select(s => string.Concat(s.Descendants(ns + "t").Select(t => t.Value)))); }
        string Cell(XElement? row, string col)
        {
            var c = row?.Elements(ns + "c").FirstOrDefault(x => new string(((string?)x.Attribute("r") ?? "").TakeWhile(char.IsLetter).ToArray()) == col);
            if (c is null) return "";
            var value = c.Element(ns + "v")?.Value ?? string.Concat(c.Descendants(ns + "t").Select(x => x.Value));
            return (string?)c.Attribute("t") == "s" && int.TryParse(value, out var ix) && ix >= 0 && ix < strings.Count ? strings[ix] : value;
        }
        var rows = doc.Descendants(ns + "row").ToArray();
        XElement? Row(int n) => rows.FirstOrDefault(x => (int?)x.Attribute("r") == n);
        if (Cell(Row(1), "A") != "Report Date" || Cell(Row(5), "A") != "Line") return null;
        var day = DateOnly.FromDateTime(DateTime.FromOADate(double.Parse(Cell(Row(1), "B"), CultureInfo.InvariantCulture)));
        var start = DateTime.FromOADate(double.Parse(Cell(Row(2), "B"), CultureInfo.InvariantCulture));
        var end = DateTime.FromOADate(double.Parse(Cell(Row(3), "B"), CultureInfo.InvariantCulture)).AddMinutes(1);
        int Number(XElement row, string col) => int.TryParse(Cell(row, col), out var v) ? v : 0;
        var line = ""; var total = 0; var output = new List<SummaryReportRow>();
        foreach (var row in rows.Where(x => (int?)x.Attribute("r") >= 6))
        {
            if (!string.IsNullOrWhiteSpace(Cell(row, "A"))) { line = Cell(row, "A"); total = Number(row, "C"); }
            var field = Cell(row, "B");
            if (line.Length == 0 || field.Length == 0) continue;
            output.Add(new(line, field, total, Number(row,"D"), Number(row,"E"), Number(row,"F"), 0,0,0));
        }
        return new(day, start, end, output, [], path);
    }
    public async Task<IReadOnlyList<DlngReviewRecord>> LoadDlngAsync(CancellationToken token = default) =>
        (await reviews.LoadAsync(token)).Values.GroupBy(ReviewSemantics.SampleId)
            .Select(g => g.OrderByDescending(r => r.UpdatedAt).First()).ToArray();
    public Task<IReadOnlyList<TrainingBatch>> LoadBatchesAsync(CancellationToken token = default) => collection.LoadAsync(token);

    public static IReadOnlyList<OverkillMetric> KickoutMetrics(IEnumerable<KickoutHistorySnapshot> history, string field = "")
    {
        var snapshots = history.ToArray();
        var fields = string.IsNullOrWhiteSpace(field) ? snapshots.SelectMany(h => h.Rows).Where(r => r.Defect != "ALL").Select(r => r.Defect).Distinct().ToArray() : [field];
        var rows = new List<SummaryReportRow>();
        foreach (var snapshot in snapshots)
        foreach (var all in snapshot.Rows.Where(r => r.Defect == "ALL"))
        foreach (var defect in fields)
            rows.Add(snapshot.Rows.FirstOrDefault(r => r.LinePolarity == all.LinePolarity && r.ProductModel == all.ProductModel && r.Defect == defect)
                ?? all with { Defect = defect, InitialNg = 0, RealNg = 0, Overkill = 0 });
        return rows.GroupBy(r => (r.ProductModel, r.LinePolarity, r.Defect)).Select(g => new OverkillMetric("Kickout",
            g.Key.ProductModel, g.Key.LinePolarity, g.Key.Defect, g.Sum(r => r.TotalInspected),
            g.Sum(r => r.RealNg + r.Overkill), g.Sum(r => r.Overkill))).OrderByDescending(r => r.Overkill).ToArray();
    }
    public static IReadOnlyList<OverkillMetric> DlngMetrics(IEnumerable<DlngReviewRecord> records, IEnumerable<TrainingBatch> batches)
    {
        var collected = batches.Where(b => b.TrainedAt is null).SelectMany(b => b.Samples).Where(s => s.State == "Ready" && !s.Superseded)
            .Select(s => s.Id).ToHashSet();
        return records.GroupBy(r => (Product: TrainingCollectionService.Product(r), r.LinePolarity, r.CropFolder))
            .Select(g => new OverkillMetric("DLNG",g.Key.Product,g.Key.LinePolarity,g.Key.CropFolder,0,
                g.Count(r => ReviewSemantics.Outcome(r) != "Unknown"),g.Count(r => ReviewSemantics.Outcome(r) == "Overkill"),
                g.Count(r => ReviewSemantics.Outcome(r) == "Class correction"),g.Count(r => ReviewSemantics.Outcome(r) == "Missed defect"),
                g.Count(r => ReviewSemantics.Outcome(r) == "Unknown"),g.Count(r => collected.Contains(ReviewSemantics.SampleId(r)))))
            .OrderByDescending(r => r.Overkill).ToArray();
    }
    public static IReadOnlyList<DailyOverkill> Daily(IEnumerable<KickoutHistorySnapshot> history, DateOnly start, DateOnly end, string field = "")
    {
        var snapshots = history.ToArray(); var result = new List<DailyOverkill>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            var rows = snapshots.Where(h => h.Day == day).SelectMany(h => h.Rows)
                .Where(r => r.Defect.Equals(string.IsNullOrWhiteSpace(field) ? "ALL" : field, StringComparison.OrdinalIgnoreCase)).ToArray();
            var all = snapshots.Where(h => h.Day == day).SelectMany(h => h.Rows).Where(r => r.Defect == "ALL").ToArray();
            result.Add(all.Length == 0 ? new(day,null,null,null) : new(day,rows.Sum(r=>r.Overkill),rows.Sum(r=>r.RealNg+r.Overkill),all.Sum(r=>r.TotalInspected)));
        }
        return result;
    }
}
