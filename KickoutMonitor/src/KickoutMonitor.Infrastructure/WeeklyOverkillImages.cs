using System.Text.Json;
using KickoutMonitor.Domain;
namespace KickoutMonitor.Infrastructure;
public sealed record WeeklyImageExport(string Folder, int Inspections, int Files, IReadOnlyList<string> Failures);
public static class WeeklyOverkillImages
{
    public static WeeklyImageExport Export(string workbook, IReadOnlyList<KickoutHistorySnapshot> history,
        DateOnly start, DateOnly end, IReadOnlyList<string> lines)
    {
        var range = start.Year == end.Year ? $"{start:MMdd}-{end:MMdd}" : $"{start:yyyyMMdd}-{end:yyyyMMdd}";
        var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(workbook))!, "Overkill", range);
        // Distinguish a subsequent year's identical mmdd range without changing the usual layout.
        var metadata = Path.Combine(root, ".period.json");
        if (File.Exists(metadata))
        {
            var previous = JsonSerializer.Deserialize<string[]>(File.ReadAllText(metadata));
            if (previous is { Length: > 0 } && previous[0] != start.Year.ToString()) root += "_" + start.Year;
        }
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".period.json"), JsonSerializer.Serialize(new[] { start.Year.ToString(), start.ToString(), end.ToString() }));
        var failures = new List<string>(); var files = 0; var inspections = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in history.Where(h => h.Day >= start && h.Day <= end))
        {
        foreach (var line in lines)
            if (snapshot.Rows.Any(r => r.LinePolarity == line && r.Defect == "ALL" && r.Overkill > 0) &&
                !snapshot.Details.Any(d => d.LinePolarity == line && d.Decision == ReviewDecision.Overkill))
                failures.Add($"{snapshot.Day} {line}: 보고서에 이미지 상세 경로가 없습니다. 해당 날짜 Kickout 요약을 다시 생성하세요.");
        foreach (var detail in snapshot.Details.Where(d => d.Decision == ReviewDecision.Overkill && lines.Contains(d.LinePolarity)))
        {
            var local = detail.LocalFolder;
            if (string.IsNullOrWhiteSpace(local))
            { failures.Add($"{detail.LinePolarity} {detail.Defect}: 이미지 경로 없음"); continue; }
            var identity = $"{detail.LinePolarity}|{detail.Defect}|{local}";
            if (!seen.Add(identity)) continue;
            var originalName = InspectionIdentity.FileName(local.TrimEnd('\\', '/'));
            var ownedName = InspectionIdentity.Hash(local);
            var sources = new[] { local, Path.Combine(Path.GetDirectoryName(snapshot.Source) ?? "",
                "OVERKILL", TrainingCollectionService.Safe(detail.LinePolarity),
                TrainingCollectionService.Safe(detail.Defect), ownedName, originalName) };
            var folder = sources.FirstOrDefault(Directory.Exists);
            if (folder is null) { failures.Add($"{detail.LinePolarity} {detail.Defect} {originalName}: 원본 이미지 폴더 없음"); continue; }
            var target = Path.Combine(root, TrainingCollectionService.Safe(detail.LinePolarity),
                TrainingCollectionService.Safe(detail.Defect), ownedName, originalName);
            var copied = 0;
            try
            {
                foreach (var source in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(KickoutOverkillHistory.IsImage))
                {
                    if (new FileInfo(source).Length == 0) throw new IOException("크기가 0인 이미지: " + source);
                    var destination = Path.Combine(target, Path.GetRelativePath(folder, source));
                    if (Path.GetFullPath(destination).Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)) { copied++; continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var temp = destination + ".copying";
                    try
                    {
                        File.Copy(source, temp, true);
                        if (SafeWorkbookCommit.Digest(source) != SafeWorkbookCommit.Digest(temp)) throw new IOException("이미지 복사 검증 실패: " + source);
                        File.Move(temp, destination, true); copied++;
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                if (copied == 0) failures.Add($"{detail.LinePolarity} {originalName}: 이미지 파일 없음");
                else inspections++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failures.Add($"{detail.LinePolarity} {originalName}: {e.Message}"); }
            files += copied;
        }
        }
        File.WriteAllText(Path.Combine(root, "export-status.txt"),
            $"{start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}\n{inspections} inspections, {files} files\n" + string.Join("\n", failures));
        return new(root, inspections, files, failures);
    }
}
