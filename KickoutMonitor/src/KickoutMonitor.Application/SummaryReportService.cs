using KickoutMonitor.Domain;

namespace KickoutMonitor.Application;

public sealed class SummaryReportService
{
    public const string MultiNgDefectName = "MULTI-NG";
    private static readonly TimeSpan ReportStartTime = TimeSpan.FromHours(6);
    private readonly IDailyCsvLocator _locator;
    private readonly IReadOnlySnapshotService _snapshots;
    private readonly IInspectionSummaryCsvReader _reader;
    private readonly IReviewStore _reviews;
    private readonly ISummaryReportWriter _writer;

    public SummaryReportService(
        IDailyCsvLocator locator,
        IReadOnlySnapshotService snapshots,
        IInspectionSummaryCsvReader reader,
        IReviewStore reviews,
        ISummaryReportWriter writer)
    {
        _locator = locator;
        _snapshots = snapshots;
        _reader = reader;
        _reviews = reviews;
        _writer = writer;
    }

    public async Task<SummaryReportResult> GenerateAsync(
        IReadOnlyList<WeldingMachine> machines,
        DateOnly reportDate,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (machines.Count == 0)
        {
            throw new InvalidOperationException("Select at least one line/polarity before generating a report.");
        }

        var windowStart = reportDate.ToDateTime(TimeOnly.FromTimeSpan(ReportStartTime));
        var windowEndExclusive = windowStart.AddDays(1);
        var savedReviews = await _reviews.LoadAsync(cancellationToken);
        var rows = new List<(WeldingMachine Machine, InspectionSummaryRecord Record)>();

        foreach (var machine in machines)
        {
            for (var date = reportDate; date <= reportDate.AddDays(1); date = date.AddDays(1))
            {
                var sources = await _locator.FindAsync(machine, date, cancellationToken);
                if (sources.Count == 0)
                {
                    progress?.Report($"{machine.OutputFolderName} {date:yyyy-MM-dd}: no CSV found for report scan.");
                    continue;
                }

                foreach (var source in sources)
                {
                    progress?.Report($"{machine.OutputFolderName}: scanning {Path.GetFileName(source)}");
                    var snapshot = await _snapshots.CreateAsync(
                        source,
                        date == DateOnly.FromDateTime(DateTime.Now),
                        cancellationToken);
                    await foreach (var record in _reader.ReadAsync(machine, snapshot, cancellationToken))
                    {
                        if (record.InspectedAt >= windowStart
                            && record.InspectedAt < windowEndExclusive)
                        {
                            rows.Add((machine, record));
                        }
                    }
                }
            }
        }

        var records = rows
            .GroupBy(
                x => x.Record.CandidateKey,
                StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToArray();
        var ngRecords = records
            .Where(x => string.Equals(x.Record.Judge, "NG", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var missingReviewCount = ngRecords.Count(x =>
            !savedReviews.TryGetValue(x.Record.CandidateKey, out var review)
            || !IsFinal(review.Decision));
        if (missingReviewCount > 0)
        {
            throw new InvalidOperationException(
                $"Report blocked: {missingReviewCount:N0} NG cell(s) in " +
                $"{reportDate:yyyy-MM-dd} 06:00 to {reportDate.AddDays(1):yyyy-MM-dd} 05:59:59 " +
                "are not reviewed yet.");
        }

        var reportRows = BuildRows(records, ngRecords, savedReviews);
        var details = BuildDetails(ngRecords, savedReviews);
        var outputPath = await _writer.WriteAsync(
            reportDate,
            windowStart,
            windowEndExclusive,
            reportRows,
            details,
            cancellationToken);
        return new(reportDate, windowStart, windowEndExclusive, reportRows, outputPath);
    }

    private static IReadOnlyList<SummaryReportRow> BuildRows(
        IReadOnlyList<(WeldingMachine Machine, InspectionSummaryRecord Record)> records,
        IReadOnlyList<(WeldingMachine Machine, InspectionSummaryRecord Record)> ngRecords,
        IReadOnlyDictionary<string, ReviewEntry> reviews)
    {
        var results = new List<SummaryReportRow>();
        foreach (var machineGroup in records.GroupBy(x => x.Machine.Id))
        {
            var machine = machineGroup.First().Machine;
            var totalInspected = machineGroup.Count();
            var machineNg = ngRecords
                .Where(x => x.Machine.Id == machine.Id)
                .ToArray();
            results.Add(BuildRow(machine.OutputFolderName, "ALL", totalInspected, machineNg, reviews));

            foreach (var defectGroup in machineNg.GroupBy(
                         x => DefectForSummary(x.Record, reviews),
                         StringComparer.OrdinalIgnoreCase)
                         .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(BuildRow(
                    machine.OutputFolderName,
                    defectGroup.Key,
                    totalInspected,
                    defectGroup.ToArray(),
                    reviews));
            }
        }

        return results
            .OrderBy(x => x.LinePolarity, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Defect == "ALL" ? string.Empty : x.Defect, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SummaryReportRow BuildRow(
        string linePolarity,
        string defect,
        int totalInspected,
        IReadOnlyList<(WeldingMachine Machine, InspectionSummaryRecord Record)> ngRecords,
        IReadOnlyDictionary<string, ReviewEntry> reviews)
    {
        var initialNg = ngRecords.Count;
        var realNg = 0;
        var overkill = 0;
        foreach (var (_, record) in ngRecords)
        {
            var decision = reviews[record.CandidateKey].Decision;
            if (decision is ReviewDecision.RealNg or ReviewDecision.MultiDefectNg) realNg++;
            else if (decision == ReviewDecision.Overkill) overkill++;
        }

        return new(
            linePolarity,
            defect,
            totalInspected,
            initialNg,
            realNg,
            overkill,
            Rate(initialNg, totalInspected),
            Rate(realNg, totalInspected),
            Rate(overkill, totalInspected));
    }

    private static bool IsFinal(ReviewDecision decision) =>
        decision is ReviewDecision.RealNg
            or ReviewDecision.Overkill
            or ReviewDecision.MultiDefectNg;

    private static IReadOnlyList<SummaryDetailRow> BuildDetails(
        IReadOnlyList<(WeldingMachine Machine, InspectionSummaryRecord Record)> ngRecords,
        IReadOnlyDictionary<string, ReviewEntry> reviews)
    {
        return ngRecords
            .OrderBy(x => x.Machine.OutputFolderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Record.InspectedAt)
            .Select(x =>
            {
                var review = reviews[x.Record.CandidateKey];
                return new SummaryDetailRow(
                    x.Machine.OutputFolderName,
                    x.Record.Defect,
                    review.Decision,
                    review.LocalFolder,
                    x.Record.Headers,
                    x.Record.Values);
            })
            .ToArray();
    }

    private static double Rate(int numerator, int denominator) =>
        denominator == 0 ? 0 : (double)numerator / denominator;

    private static string DefectForSummary(
        InspectionSummaryRecord record,
        IReadOnlyDictionary<string, ReviewEntry> reviews) =>
        reviews[record.CandidateKey].Decision == ReviewDecision.MultiDefectNg
            ? MultiNgDefectName
            : record.Defect;
}
