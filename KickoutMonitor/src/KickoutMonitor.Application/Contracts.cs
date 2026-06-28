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
