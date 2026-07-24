using KickoutMonitor.Domain;

namespace KickoutMonitor.Application;

public interface IMachineRegistry
{
    IReadOnlyList<WeldingMachine> All { get; }
    WeldingMachine Get(string id);
}

public interface IDailyCsvLocator
{
    Task<IReadOnlyList<string>> FindAsync(
        WeldingMachine machine,
        DateOnly date,
        CancellationToken cancellationToken);
}

public interface IReadOnlySnapshotService
{
    Task<SnapshotResult> CreateAsync(
        string sourceCsv,
        bool currentDate,
        CancellationToken cancellationToken);
}

public interface IKickoutCsvReader
{
    IAsyncEnumerable<KickoutCandidate> ReadAsync(
        WeldingMachine machine,
        SnapshotResult snapshot,
        CancellationToken cancellationToken);
}

public interface IReviewStore
{
    Task<IReadOnlyDictionary<string, ReviewEntry>> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(ReviewEntry entry, CancellationToken cancellationToken);
}

public interface IClassifiedFolderService
{
    Task<CopyResult> ClassifyAsync(
        WeldingMachine machine,
        KickoutCandidate candidate,
        ReviewDecision decision,
        CancellationToken cancellationToken);
}

public interface IPreviewImageLoader<TImage>
{
    Task<TImage?> LoadAsync(string networkPath, int decodeWidth, CancellationToken cancellationToken);
}

public interface IPreviewCache
{
    Task<KickoutCandidate> EnsureCachedAsync(
        WeldingMachine machine,
        KickoutCandidate candidate,
        CancellationToken cancellationToken);

    Task RemoveAsync(
        WeldingMachine machine,
        KickoutCandidate candidate,
        CancellationToken cancellationToken);
}

public interface IConnectionProbe
{
    Task<IReadOnlyList<ShareConnectionResult>> ProbeAsync(
        WeldingMachine machine,
        TimeSpan timeoutPerShare,
        CancellationToken cancellationToken);
}

public interface ISharePathResolver
{
    string GetRoot(WeldingMachine machine, char drive);
    void RecordAccessibleRoot(WeldingMachine machine, char drive, string root);
}

public interface IInspectionSummaryCsvReader
{
    IAsyncEnumerable<InspectionSummaryRecord> ReadAsync(
        WeldingMachine machine,
        SnapshotResult snapshot,
        CancellationToken cancellationToken);
}

public interface ISummaryReportWriter
{
    Task<string> WriteAsync(
        DateOnly reportDate,
        DateTime windowStart,
        DateTime windowEndExclusive,
        IReadOnlyList<SummaryReportRow> rows,
        IReadOnlyList<SummaryDetailRow> details,
        CancellationToken cancellationToken);
}

public interface IIrsWorkbookReader
{
    Task<IReadOnlyList<IrsReviewCandidate>> ReadRequestedAsync(
        string workbookPath,
        CancellationToken cancellationToken);
}

public interface IIrsRawImageLocator
{
    Task<IrsImageLookupResult> FindAsync(
        WeldingMachine machine,
        IrsReviewCandidate candidate,
        CancellationToken cancellationToken);
}

public interface IIrsReviewCommitService
{
    Task<IReadOnlyList<IrsReviewRecord>> LoadRecordsAsync(CancellationToken cancellationToken);

    Task<IrsReviewCommitResult> CommitAsync(
        IrsReviewCommitRequest request,
        CancellationToken cancellationToken);
}

public interface IIrsDatasetService
{
    Task<IReadOnlyList<IrsDatasetItem>> BuildQueueAsync(
        IReadOnlyList<IrsReviewCandidate> candidates,
        IReadOnlyList<IrsReviewRecord> reviewRecords,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, IrsDatasetDecision>> LoadDecisionsAsync(CancellationToken cancellationToken);

    Task SaveDecisionAsync(
        IrsDatasetItem item,
        IReadOnlyList<string> finalClasses,
        bool noNeedToRetrain,
        CancellationToken cancellationToken);

    Task<IrsSummaryResult> WriteSummaryAsync(
        IReadOnlyList<IrsReviewCandidate> candidates,
        IReadOnlyList<IrsReviewRecord> reviewRecords,
        IReadOnlyList<IrsDatasetItem> datasetItems,
        CancellationToken cancellationToken);
}

public interface IFlaggedItemStore
{
    Task<IReadOnlyDictionary<string, FlaggedItem>> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(FlaggedItem item, CancellationToken cancellationToken);

    Task MarkSummarizedAsync(
        IReadOnlyList<string> keys,
        DateTimeOffset summarizedAt,
        CancellationToken cancellationToken);
}

public interface IFlaggedReviewService
{
    Task<IReadOnlyList<FlaggedItem>> LoadAsync(bool summarized, CancellationToken cancellationToken);

    Task<IReadOnlyList<IrsReviewCandidate>> BuildCandidatesAsync(
        IReadOnlyList<FlaggedItem> flags,
        CancellationToken cancellationToken);

    Task<FlaggedSummaryResult> WriteSummaryAsync(
        IReadOnlyList<FlaggedItem> flags,
        IReadOnlyList<IrsReviewCandidate> candidates,
        IReadOnlyList<IrsReviewRecord> reviewRecords,
        IReadOnlyList<IrsDatasetItem> datasetItems,
        CancellationToken cancellationToken);
}

public interface IDlngCsvReader
{
    IAsyncEnumerable<DlngReviewItem> ReadAsync(
        WeldingMachine machine,
        SnapshotResult snapshot,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}

public interface IDlngCropLocator
{
    Task<IReadOnlyList<DlngReviewItem>> ExpandAsync(
        WeldingMachine machine,
        DlngReviewItem candidate,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}

public interface IDlngReviewStore
{
    Task<IReadOnlyDictionary<string, DlngReviewRecord>> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(DlngReviewRecord record, CancellationToken cancellationToken);
}

public interface IDlngReportService
{
    Task<DlngReportResult> GenerateAsync(
        IReadOnlyList<WeldingMachine> machines,
        DateOnly reportDate,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}

public interface INgBypassCsvReader
{
    IAsyncEnumerable<NgBypassCandidate> ReadAsync(
        WeldingMachine machine,
        SnapshotResult snapshot,
        NgBypassQuery query,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}

public interface INgBypassReviewStore
{
    Task<IReadOnlyDictionary<string, NgBypassReviewRecord>> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(NgBypassReviewRecord record, CancellationToken cancellationToken);
}

public interface INgBypassClassifiedFolderService
{
    Task<CopyResult> ClassifyAsync(
        WeldingMachine machine,
        NgBypassCandidate candidate,
        ReviewDecision decision,
        CancellationToken cancellationToken);
}

public interface INgBypassReportService
{
    Task<NgBypassReportResult> GenerateAsync(
        IReadOnlyList<WeldingMachine> machines,
        NgBypassQuery query,
        DateOnly reportDate,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}
