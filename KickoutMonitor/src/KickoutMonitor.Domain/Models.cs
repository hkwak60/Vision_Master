namespace KickoutMonitor.Domain;

public enum Polarity
{
    Anode,
    Cathode
}

public enum NgSide
{
    None,
    Upper,
    Lower,
    Both
}

public enum ReviewDecision
{
    Pending,
    RealNg,
    Overkill,
    MultiDefectNg,
    Ignore
}

public enum CopyState
{
    NotRequested,
    Copied,
    PendingRetry,
    MissingSource
}

public sealed record WeldingMachine(
    string Id,
    string Line,
    Polarity Polarity,
    string IpAddress,
    IReadOnlyList<char> ImageDrives,
    string Model = "E81C",
    char DataDrive = 'D',
    bool Enabled = true)
{
    public string OutputFolderName => $"{Line}({(Polarity == Polarity.Anode ? "-" : "+")})";
    public string DisplayName => $"{Line} Welding ({(Polarity == Polarity.Anode ? "-" : "+")})";
    public string ShareRoot(char drive, bool administrative = false) =>
        $@"\\{IpAddress}\{char.ToUpperInvariant(drive)}{(administrative ? "$" : string.Empty)}";

    public string DataRoot(bool administrative = false) =>
        Path.Combine(ShareRoot(DataDrive, administrative), "Files", "Data", "Result", "Day");
}

public sealed record CandidateImage(
    string Side,
    int Index,
    bool IsOverlay,
    string ProductionPath,
    string NetworkPath,
    string? CachedPath = null)
{
    public string Label => $"{Side} {Index} {(IsOverlay ? "Overlay" : "Raw")}";
    public string PreviewPath => CachedPath ?? NetworkPath;
}

public sealed record KickoutCandidate(
    string Key,
    string MachineId,
    DateTime InspectedAt,
    string Model,
    string LotId,
    string CellId,
    string Defect,
    NgSide NgSide,
    IReadOnlyList<CandidateImage> PreviewImages,
    string SourceFolder,
    string SourceCsv,
    int SourceRow);

public sealed record ReviewEntry(
    string CandidateKey,
    ReviewDecision Decision,
    CopyState CopyState,
    string Comment,
    string? LocalFolder,
    DateTimeOffset UpdatedAt);

public sealed record SnapshotResult(
    string SourcePath,
    string SnapshotPath,
    bool IsProvisional,
    string? Warning);

public sealed record CopyResult(
    CopyState State,
    string? Destination,
    string Message);

public enum ConnectionState
{
    Accessible,
    Missing,
    AccessDenied,
    TimedOut,
    PcUnreachable,
    Error
}

public sealed record ShareConnectionResult(
    char Drive,
    ConnectionState State,
    string Path,
    TimeSpan Elapsed,
    string Message);

public sealed record InspectionSummaryRecord(
    string MachineId,
    DateTime InspectedAt,
    string LotId,
    string CellId,
    string Judge,
    string Defect,
    NgSide NgSide,
    string CandidateKey,
    IReadOnlyList<string> Headers,
    IReadOnlyList<string> Values,
    string SourceCsv);

public sealed record SummaryReportRow(
    string LinePolarity,
    string Defect,
    int TotalInspected,
    int InitialNg,
    int RealNg,
    int Overkill,
    double InitialNgRate,
    double ConfirmedNgRate,
    double OverkillRate);

public sealed record SummaryReportResult(
    DateOnly ReportDate,
    DateTime WindowStart,
    DateTime WindowEndExclusive,
    IReadOnlyList<SummaryReportRow> Rows,
    string OutputPath);

public sealed record SummaryDetailRow(
    string LinePolarity,
    string Defect,
    ReviewDecision Decision,
    string? LocalFolder,
    IReadOnlyList<string> Headers,
    IReadOnlyList<string> Values);

public sealed record IrsReviewCandidate(
    string Key,
    string Eqpt,
    string VisionType,
    string LinePolarity,
    DateTime ProducedAt,
    string LotId,
    string CellId,
    string CameraLocation,
    string RawImageFileName,
    string SecondResult,
    string SecondReason,
    int SourceRow,
    IReadOnlyList<string>? RawImagePaths = null)
{
    public string? RawImagePath => RawImagePaths?.FirstOrDefault();
}

public sealed record IrsImageLookupResult(
    IReadOnlyList<string> NetworkPaths,
    string Message);

public enum IrsSelectionKind
{
    Rulebase,
    Undetectable,
    Crop
}

public enum DlngModelKind
{
    Classification,
    Segmentation,
    FallbackRaw
}

public sealed record IrsReviewSelection(
    string Id,
    string DisplayName,
    string CategoryFolder,
    IrsSelectionKind Kind,
    string? MavinFolder,
    string? Token);

public sealed record IrsReviewCommitRequest(
    WeldingMachine Machine,
    IrsReviewCandidate Candidate,
    IReadOnlyList<IrsReviewSelection> Selections);

public sealed record IrsReviewCommitResult(
    int OriginalFilesCopied,
    int CropFilesCopied,
    int MissingFiles,
    string DestinationRoot,
    string Message);

public sealed record IrsReviewRecord(
    string Key,
    string MachineId,
    string LinePolarity,
    DateTime ProducedAt,
    string CellId,
    string CameraLocation,
    string SecondResult,
    string SecondReason,
    IReadOnlyList<string> Selections,
    int OriginalFilesCopied,
    int CropFilesCopied,
    int MissingFiles,
    string DestinationRoot,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string>? SavedPaths = null);

public sealed record IrsDatasetItem(
    string Key,
    string SourceReviewKey,
    string LinePolarity,
    DateTime ProducedAt,
    string CellId,
    string CameraLocation,
    string SecondReason,
    string SourceFolder,
    string OriginalClass,
    IReadOnlyList<string> ImagePaths,
    IReadOnlyList<string> AllowedClasses,
    bool IsNeedToSimulate);

public sealed record IrsDatasetDecision(
    string ItemKey,
    string SourceReviewKey,
    string LinePolarity,
    DateTime ProducedAt,
    string CellId,
    string SourceFolder,
    string OriginalClass,
    IReadOnlyList<string> FinalClasses,
    bool NoNeedToRetrain,
    DateTimeOffset UpdatedAt);

public sealed record IrsSummaryResult(
    string OutputFolder,
    string SummaryWorkbook,
    IReadOnlyList<string> DetailWorkbooks);

public sealed record DlngImage(
    string Label,
    string Path,
    bool IsMask);

public sealed record DlngReviewItem(
    string Key,
    string MachineId,
    string LinePolarity,
    Polarity Polarity,
    DateTime InspectedAt,
    string Model,
    string LotId,
    string CellId,
    string Judge,
    string JudgeDefect,
    string Side,
    string CropFolder,
    string SourceClass,
    DlngModelKind ModelKind,
    IReadOnlyList<DlngImage> Images,
    string SourceCsv,
    int SourceRow,
    string SourceFolder);

public sealed record DlngReviewRecord(
    string ItemKey,
    string MachineId,
    string LinePolarity,
    DateTime InspectedAt,
    string CellId,
    string Judge,
    string JudgeDefect,
    string Side,
    string CropFolder,
    string SourceClass,
    string FinalClass,
    bool IsFallbackRaw,
    IReadOnlyList<string> ImagePaths,
    DateTimeOffset UpdatedAt);

public sealed record DlngReportRow(
    string LinePolarity,
    string Judge,
    string JudgeDefect,
    string CropFolder,
    string SourceClass,
    string FinalClass,
    int Count,
    int SwitchedCount);

public sealed record DlngReportResult(
    DateOnly ReportDate,
    string OutputFolder,
    string SummaryWorkbook,
    IReadOnlyList<DlngReportRow> Rows);
