using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KickoutMonitor.Domain;

public sealed record InspectionContext(
    string MachineId, string Model, string LotId, string CellId,
    DateTime JudgedAt, DateTime? ImageAt, IReadOnlyList<string> ImagePaths,
    string SourceCsv = "", int SourceRow = 0, string? Issue = null)
{
    // Null means the surrounding CSV inspections have not been checked: retain exact-raw matching.
    public IReadOnlyList<CropTimeWindow>? CropConflicts { get; init; }
    public CropTimeWindow CropWindow => CropTimeWindow.Between(JudgedAt, ImageAt ?? JudgedAt);
    public string Identity => $"{MachineId}|{JudgedAt:O}|{LotId}|{CellId}|{ImageAt:O}|{InspectionIdentity.Fingerprint(ImagePaths)}";
}

public sealed record CropTimeWindow(DateTime Start, DateTime End)
{
    public static CropTimeWindow Between(DateTime a, DateTime b)
    {
        a = a.AddTicks(-(a.Ticks % TimeSpan.TicksPerSecond));
        b = b.AddTicks(-(b.Ticks % TimeSpan.TicksPerSecond));
        return a <= b ? new(a, b) : new(b, a);
    }
    public bool Contains(DateTime time) => time >= Start && time <= End;
}

public static class InspectionIdentity
{

    public static string Fingerprint(IEnumerable<string> paths) => Hash(string.Join("|",
        paths.Select(FileName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));

    public static IEnumerable<T> SeparateCollisions<T>(this IEnumerable<T> items, Func<T, string> key,
        Func<T, string> evidence, Func<T, string, T> replace)
    {
        foreach (var group in items.GroupBy(key, StringComparer.OrdinalIgnoreCase))
        {
            var distinct = group.GroupBy(evidence, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
            foreach (var item in distinct)
                yield return distinct.Length == 1 ? item : replace(item, $"{key(item)}|IMAGE:{Hash(evidence(item))}");
        }
    }

    public static string FileName(string path) => path.Replace('\\', '/').Split('/').Last();

    public static DateTime? ImageTime(IEnumerable<string> paths)
    {
        var times = paths.Select(path => Regex.Match(FileName(path), @"^(\d{8})_(\d{6})_"))
            .Where(match => match.Success)
            .Select(match => DateTime.TryParseExact(match.Groups[1].Value + match.Groups[2].Value,
                "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                ? (DateTime?)time : null)
            .Where(time => time.HasValue).Distinct().ToArray();
        return times.Length == 1 ? times[0] : null;
    }

    public static string DisplayTime(DateTime time) => time.ToString(time.Millisecond == 0 ? "HH:mm:ss" : "HH:mm:ss.fff");

    public static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant())))[..20];

    public static string Group(string machine, string lot, string cell, string inspection) =>
        string.IsNullOrWhiteSpace(lot) ? $"SINGLE|{inspection}" : $"{machine}|{lot}|{cell}";

    public static bool MatchesCrop(string path, WeldingMachine machine, string cellId, DateTime imageTime)
    {
        var line = machine.Line.Split('-');
        var lineToken = line.Length == 2 && int.TryParse(line[0], out var number)
            ? $"{number:00}-{line[1]}" : machine.Line;
        var prefix = $"{cellId}_{lineToken}_{(machine.Polarity == Polarity.Anode ? "AN" : "CA")}_{imageTime:HHmmss}_";
        return FileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<InspectionContext> WithCropConflicts(IEnumerable<InspectionContext> inspections)
    {
        var result = new List<InspectionContext>();
        // Crops do not encode Lot ID: different lots must still compete for the same crop.
        foreach (var group in inspections.DistinctBy(x => x.Identity, StringComparer.OrdinalIgnoreCase)
                     .GroupBy(x => $"{x.MachineId}|{x.Model}|{x.CellId}", StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.ToArray();
            foreach (var row in rows)
            {
                var window = row.CropWindow;
                var conflicts = rows.Where(other => other.Identity != row.Identity)
                    .Select(other => other.CropWindow)
                    .Where(other => other.Start <= window.End && other.End >= window.Start)
                    .Select(other => new CropTimeWindow(other.Start > window.Start ? other.Start : window.Start,
                        other.End < window.End ? other.End : window.End)).Distinct().ToArray();
                result.Add(row with { CropConflicts = conflicts });
            }
        }
        return result;
    }

    public static DateTime? CropPathDate(string path)
    {
        var match = Regex.Match(path.Replace('\\', '/'), @"/(\d{4})/(\d{2})/(\d{2})/");
        return match.Success && DateTime.TryParseExact(
            match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value,
            "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    public static IEnumerable<DateTime> CropDates(InspectionContext context)
    {
        var window = context.CropConflicts is null
            ? CropTimeWindow.Between(context.ImageAt ?? context.JudgedAt, context.ImageAt ?? context.JudgedAt)
            : context.CropWindow;
        for (var day = window.Start.Date; day <= window.End.Date; day = day.AddDays(1)) yield return day;
    }

    public static bool MatchesCrop(string path, WeldingMachine machine, InspectionContext context,
        DateTime? folderDate = null)
    {
        if (context.ImageAt is null || context.Issue is not null) return false;
        var line = machine.Line.Split('-');
        var lineToken = line.Length == 2 && int.TryParse(line[0], out var number) ? $"{number:00}-{line[1]}" : machine.Line;
        var prefix = $"{context.CellId}_{lineToken}_{(machine.Polarity == Polarity.Anode ? "AN" : "CA")}_";
        var name = FileName(path);
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var tail = name[prefix.Length..];
        if (tail.Length < 7 || tail[6] != '_' ||
            !DateTime.TryParseExact(tail[..6], "HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var clock))
            return false;
        var window = context.CropConflicts is null
            ? CropTimeWindow.Between(context.ImageAt.Value, context.ImageAt.Value) : context.CropWindow;
        var dates = folderDate.HasValue ? new[] { folderDate.Value.Date } : CropDates(context);
        return dates.Select(day => day.Add(clock.TimeOfDay)).Any(time => window.Contains(time)
            && !(context.CropConflicts ?? []).Any(conflict => conflict.Contains(time)));
    }

    public static string PairKey(string path)
    {
        var name = FileName(path);
        foreach (var marker in new[] { "_SourceMap", "_ActiveMap", "_SourceImg" })
        {
            var index = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) return name[..index];
        }
        return Path.GetFileNameWithoutExtension(name);
    }

    public static IReadOnlyList<T> GroupQueue<T>(IEnumerable<T> items, Func<T, string> group,
        Func<T, DateTime> time, Func<T, bool>? pending = null) => items
        .GroupBy(group, StringComparer.OrdinalIgnoreCase)
        .OrderBy(g => pending is not null && g.Any(pending))
        .ThenBy(g => g.Min(time)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .SelectMany(g => g.OrderBy(time)).ToArray();
}
