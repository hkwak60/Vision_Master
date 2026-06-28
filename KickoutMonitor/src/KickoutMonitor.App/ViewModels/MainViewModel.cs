using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;
using KickoutMonitor.Infrastructure;

namespace KickoutMonitor.App.ViewModels;

public sealed class PreviewItem : INotifyPropertyChanged
{
    private BitmapSource? _image;
    private string _state = "Loading";

    public PreviewItem(CandidateImage source) => Source = source;
    public CandidateImage Source { get; }
    public string Label => Source.Label;

    public BitmapSource? Image
    {
        get => _image;
        set => Set(ref _image, value);
    }

    public string State
    {
        get => _state;
        set => Set(ref _state, value);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class MachineOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public MachineOption(WeldingMachine machine, bool isSelected = false)
    {
        Machine = machine;
        _isSelected = isSelected;
    }

    public WeldingMachine Machine { get; }
    public string Label => Machine.OutputFolderName;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CandidateItem : INotifyPropertyChanged
{
    private ReviewDecision _decision;
    private CopyState _copyState;
    private string _reviewComment;

    public CandidateItem(
        WeldingMachine machine,
        KickoutCandidate candidate,
        ReviewEntry? review)
    {
        Machine = machine;
        Candidate = candidate;
        _decision = review?.Decision ?? ReviewDecision.Pending;
        _copyState = review?.CopyState ?? CopyState.NotRequested;
        _reviewComment = review?.Comment ?? string.Empty;
    }

    public WeldingMachine Machine { get; }
    public KickoutCandidate Candidate { get; private set; }
    public string Time => Candidate.InspectedAt.ToString("HH:mm:ss");
    public string LinePolarity => Machine.OutputFolderName;
    public string CellId => Candidate.CellId;
    public string Defect => Candidate.Defect;
    public string Side => Candidate.NgSide.ToString();
    public string ReviewLabel => Decision switch
    {
        ReviewDecision.RealNg => "Real NG",
        ReviewDecision.Overkill => "Overkill",
        ReviewDecision.MultiDefectNg => "Multi-Defect NG",
        _ => "Pending"
    };
    public string ReviewComment
    {
        get => _reviewComment;
        set => Set(ref _reviewComment, value);
    }

    public void ReplaceCandidate(KickoutCandidate candidate)
    {
        Candidate = candidate;
        PropertyChanged?.Invoke(this, new(nameof(Candidate)));
    }

    public ReviewDecision Decision
    {
        get => _decision;
        set
        {
            if (_decision == value) return;
            _decision = value;
            PropertyChanged?.Invoke(this, new(nameof(Decision)));
            PropertyChanged?.Invoke(this, new(nameof(ReviewLabel)));
        }
    }

    public CopyState CopyState
    {
        get => _copyState;
        set => Set(ref _copyState, value);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IMachineRegistry _machines;
    private readonly KickoutQueueService _queue;
    private readonly IReviewStore _reviews;
    private readonly IClassifiedFolderService _folders;
    private readonly IPreviewCache _cache;
    private readonly IConnectionProbe _connections;
    private readonly IPreviewImageLoader<BitmapSource> _images;
    private readonly SummaryReportService _reports;
    private CancellationTokenSource? _previewCancellation;
    private CandidateItem? _selectedCandidate;
    private DateTime? _startDate = DateTime.Today;
    private DateTime? _endDate = DateTime.Today;
    private DateTime? _reportDate = DateTime.Today;
    private string _comment = string.Empty;
    private string _status = "Ready";
    private bool _isBusy;
    private int _currentImageIndex = -1;
    private bool _reviewCompletionLogged;

    public MainViewModel(
        IMachineRegistry machines,
        KickoutQueueService queue,
        IReviewStore reviews,
        IClassifiedFolderService folders,
        IPreviewCache cache,
        IConnectionProbe connections,
        IPreviewImageLoader<BitmapSource> images,
        SummaryReportService reports,
        AppStorage storage)
    {
        _machines = machines;
        _queue = queue;
        _reviews = reviews;
        _folders = folders;
        _cache = cache;
        _connections = connections;
        _images = images;
        _reports = reports;
        MachineOptions = new(machines.All.Select(
            (machine, index) => new MachineOption(machine, index == 0)));
        StorageRoot = storage.Root;
        LoadCommand = new(
            LoadQueueAsync,
            () => !IsBusy
                && MachineOptions.Any(option => option.IsSelected)
                && StartDate is not null
                && EndDate is not null);
        GenerateReportCommand = new(
            GenerateReportAsync,
            () => !IsBusy
                && MachineOptions.Any(option => option.IsSelected)
                && ReportDate is not null);
        RealNgCommand = new(() => ReviewAsync(ReviewDecision.RealNg), CanReview);
        OverkillCommand = new(() => ReviewAsync(ReviewDecision.Overkill), CanReview);
        MultiDefectCommand = new(() => ReviewAsync(ReviewDecision.MultiDefectNg), CanReview);
        PreviousCommand = new(Previous, () => SelectedIndex > 0);
        NextCommand = new(Next, () => SelectedIndex >= 0 && SelectedIndex < Candidates.Count - 1);
        PreviousImageCommand = new(
            PreviousImageAsync,
            () => CurrentImageIndex > 0);
        NextImageCommand = new(
            NextImageAsync,
            () => CurrentImageIndex >= 0 && CurrentImageIndex < PreviewImages.Count - 1);
    }

    public ObservableCollection<MachineOption> MachineOptions { get; }
    public ObservableCollection<CandidateItem> Candidates { get; } = [];
    public ObservableCollection<PreviewItem> PreviewImages { get; } = [];
    public ObservableCollection<string> ConnectionLog { get; } = [];
    public ObservableCollection<SummaryReportRow> SummaryRows { get; } = [];
    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand GenerateReportCommand { get; }
    public AsyncRelayCommand RealNgCommand { get; }
    public AsyncRelayCommand OverkillCommand { get; }
    public AsyncRelayCommand MultiDefectCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand NextCommand { get; }
    public AsyncRelayCommand PreviousImageCommand { get; }
    public AsyncRelayCommand NextImageCommand { get; }
    public string StorageRoot { get; }

    public DateTime? StartDate
    {
        get => _startDate;
        set => Set(ref _startDate, value);
    }

    public DateTime? EndDate
    {
        get => _endDate;
        set => Set(ref _endDate, value);
    }

    public DateTime? ReportDate
    {
        get => _reportDate;
        set => Set(ref _reportDate, value);
    }

    public CandidateItem? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (!Set(ref _selectedCandidate, value)) return;
            Comment = value?.ReviewComment ?? string.Empty;
            _ = LoadPreviewsAsync(value);
            CommandManager.InvalidateRequerySuggested();
            OnPropertyChanged(nameof(SelectedIndex));
            OnPropertyChanged(nameof(PositionText));
        }
    }

    public int SelectedIndex => SelectedCandidate is null ? -1 : Candidates.IndexOf(SelectedCandidate);
    public string PositionText => SelectedIndex < 0 ? "0 / 0" : $"{SelectedIndex + 1} / {Candidates.Count}";
    public int CurrentImageIndex
    {
        get => _currentImageIndex;
        private set
        {
            if (_currentImageIndex != value && _currentImageIndex >= 0)
            {
                PreviewImageChanging?.Invoke(this, EventArgs.Empty);
            }
            if (!Set(ref _currentImageIndex, value)) return;
            OnPropertyChanged(nameof(CurrentPreview));
            OnPropertyChanged(nameof(ImagePositionText));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public PreviewItem? CurrentPreview =>
        CurrentImageIndex >= 0 && CurrentImageIndex < PreviewImages.Count
            ? PreviewImages[CurrentImageIndex]
            : null;
    public string ImagePositionText =>
        CurrentImageIndex < 0 ? "0 / 0" : $"{CurrentImageIndex + 1} / {PreviewImages.Count}";

    public string Comment
    {
        get => _comment;
        set => Set(ref _comment, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public void HandleHotkey(Key key)
    {
        if (IsBusy) return;
        switch (key)
        {
            case Key.R when CanReview():
                RealNgCommand.Execute(null);
                break;
            case Key.O when CanReview():
                OverkillCommand.Execute(null);
                break;
            case Key.M when CanReview():
                MultiDefectCommand.Execute(null);
                break;
            case Key.Left when PreviousImageCommand.CanExecute(null):
                PreviousImageCommand.Execute(null);
                break;
            case Key.Right when NextImageCommand.CanExecute(null):
                NextImageCommand.Execute(null);
                break;
            case Key.Up when PreviousCommand.CanExecute(null):
                PreviousCommand.Execute(null);
                break;
            case Key.Down when NextCommand.CanExecute(null):
                NextCommand.Execute(null);
                break;
        }
    }

    private async Task LoadQueueAsync()
    {
        var selectedMachines = MachineOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Machine)
            .ToArray();
        if (selectedMachines.Length == 0 || StartDate is null || EndDate is null) return;
        var start = DateOnly.FromDateTime(StartDate.Value);
        var end = DateOnly.FromDateTime(EndDate.Value);
        if (end < start)
        {
            Status = "End date must be on or after the start date.";
            return;
        }

        IsBusy = true;
        Candidates.Clear();
        ClearPreviews();
        ConnectionLog.Clear();
        _reviewCompletionLogged = false;
        try
        {
            AddConnectionLog(
                $"Fetching review queue for {selectedMachines.Length} machine(s), " +
                $"{start:yyyy-MM-dd} through {end:yyyy-MM-dd}...");
            var saved = await _reviews.LoadAsync(CancellationToken.None);
            var progress = new Progress<string>(message => Status = message);
            var cachedCount = 0;
            var loaded = new List<CandidateItem>();

            foreach (var machine in selectedMachines)
            {
                AddConnectionLog($"Testing {machine.DisplayName} at {machine.IpAddress}...");
                var connectionResults = await _connections.ProbeAsync(
                    machine,
                    TimeSpan.FromSeconds(4),
                    CancellationToken.None);
                foreach (var result in connectionResults.OrderBy(result => result.Drive))
                {
                    AddConnectionLog(
                        $"{machine.OutputFolderName} {result.Drive}: {result.State}  {result.Path}  " +
                        $"({result.Elapsed.TotalSeconds:0.0}s)  {result.Message}");
                }

                if (connectionResults.First(result => result.Drive == 'D').State
                    != ConnectionState.Accessible)
                {
                    AddConnectionLog(
                        $"{machine.OutputFolderName} skipped: D share is not accessible.");
                    continue;
                }

                for (var date = start; date <= end; date = date.AddDays(1))
                {
                    try
                    {
                        var records = await _queue.LoadAsync(
                            machine,
                            date,
                            progress,
                            CancellationToken.None);
                        foreach (var record in records)
                        {
                            saved.TryGetValue(record.Key, out var review);
                            var workingRecord = record;
                            if (review?.Decision is not (
                                ReviewDecision.RealNg
                                or ReviewDecision.Overkill
                                or ReviewDecision.MultiDefectNg))
                            {
                                Status = $"Caching review images locally: {++cachedCount:N0}";
                                workingRecord = await _cache.EnsureCachedAsync(
                                    machine,
                                    record,
                                    CancellationToken.None);
                            }
                            else
                            {
                                await _cache.RemoveAsync(machine, record, CancellationToken.None);
                            }
                            loaded.Add(new(machine, workingRecord, review));
                        }
                        AddConnectionLog(
                            $"{machine.OutputFolderName} {date:yyyy-MM-dd}: " +
                            $"{records.Count:N0} candidate(s) fetched.");
                    }
                    catch (FileNotFoundException)
                    {
                        AddConnectionLog(
                            $"{machine.OutputFolderName} {date:yyyy-MM-dd}: no daily CSV found.");
                    }
                    catch (Exception exception)
                    {
                        AddConnectionLog(
                            $"{machine.OutputFolderName} {date:yyyy-MM-dd}: {exception.Message}");
                    }
                }
            }

            foreach (var item in loaded
                         .OrderBy(item => item.Candidate.InspectedAt)
                         .ThenBy(item => item.LinePolarity, StringComparer.OrdinalIgnoreCase))
            {
                Candidates.Add(item);
            }
            SelectedCandidate = Candidates.FirstOrDefault();
            Status = Candidates.Count == 0
                ? "No eligible JUDGE=NG records were found."
                : $"Loaded {Candidates.Count:N0} KICKOUT candidates.";
            AddConnectionLog(
                $"Review queue ready: {Candidates.Count:N0} candidate(s) from " +
                $"{selectedMachines.Length} machine(s), {start:yyyy-MM-dd} through {end:yyyy-MM-dd}.");
            LogReviewCompletionIfDone();
            OnPropertyChanged(nameof(PositionText));
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            AddConnectionLog($"Review queue fetch failed: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AddConnectionLog(string message) =>
        ConnectionLog.Add($"[{DateTime.Now:HH:mm:ss}] {message}");

    private async Task GenerateReportAsync()
    {
        var selectedMachines = MachineOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Machine)
            .ToArray();
        if (selectedMachines.Length == 0 || ReportDate is null) return;

        IsBusy = true;
        SummaryRows.Clear();
        var reportDate = DateOnly.FromDateTime(ReportDate.Value);
        try
        {
            AddConnectionLog(
                $"Generating NG summary for {selectedMachines.Length} machine(s), " +
                $"{reportDate:yyyy-MM-dd} 06:00 to {reportDate.AddDays(1):yyyy-MM-dd} 05:59:59...");
            var progress = new Progress<string>(AddConnectionLog);
            var report = await _reports.GenerateAsync(
                selectedMachines,
                reportDate,
                progress,
                CancellationToken.None);
            foreach (var row in report.Rows)
            {
                SummaryRows.Add(row);
            }

            Status = $"Summary report saved: {report.OutputPath}";
            AddConnectionLog($"Summary report saved: {report.OutputPath}");
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            AddConnectionLog($"REPORT BLOCKED: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadPreviewsAsync(CandidateItem? item)
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new();
        var token = _previewCancellation.Token;
        ClearPreviews();
        if (item is null) return;

        foreach (var source in item.Candidate.PreviewImages)
        {
            PreviewImages.Add(new(source));
        }
        CurrentImageIndex = PreviewImages.Count > 0 ? 0 : -1;
        await LoadCurrentPreviewAsync(token);
    }

    private bool CanReview() => !IsBusy && SelectedCandidate is not null;

    private async Task ReviewAsync(ReviewDecision decision)
    {
        if (SelectedCandidate is null) return;
        IsBusy = true;
        var item = SelectedCandidate;
        try
        {
            Status = "Verifying and copying the complete source folder...";
            var copy = await _folders.ClassifyAsync(
                item.Machine,
                item.Candidate,
                decision,
                CancellationToken.None);
            var entry = new ReviewEntry(
                item.Candidate.Key,
                decision,
                copy.State,
                Comment.Trim(),
                copy.Destination,
                DateTimeOffset.Now);
            await _reviews.SaveAsync(entry, CancellationToken.None);
            item.Decision = decision;
            item.CopyState = copy.State;
            item.ReviewComment = Comment.Trim();
            if (copy.State == CopyState.Copied
                && decision is (
                    ReviewDecision.RealNg
                    or ReviewDecision.Overkill
                    or ReviewDecision.MultiDefectNg))
            {
                ClearPreviews();
                await _cache.RemoveAsync(
                    item.Machine,
                    item.Candidate,
                    CancellationToken.None);
                item.ReplaceCandidate(item.Candidate with
                {
                    PreviewImages = item.Candidate.PreviewImages
                        .Select(image => image with { CachedPath = null })
                        .ToArray()
                });
            }
            Status = $"{decision}: {copy.Message}";
            LogReviewCompletionIfDone();
            Next();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Previous()
    {
        var index = SelectedIndex;
        if (index > 0) SelectedCandidate = Candidates[index - 1];
    }

    private void Next()
    {
        var index = SelectedIndex;
        if (index >= 0 && index < Candidates.Count - 1)
        {
            SelectedCandidate = Candidates[index + 1];
        }
    }

    private async Task PreviousImageAsync()
    {
        if (CurrentImageIndex <= 0) return;
        CurrentImageIndex--;
        await LoadCurrentPreviewAsync(_previewCancellation?.Token ?? CancellationToken.None);
    }

    private async Task NextImageAsync()
    {
        if (CurrentImageIndex < 0 || CurrentImageIndex >= PreviewImages.Count - 1) return;
        CurrentImageIndex++;
        await LoadCurrentPreviewAsync(_previewCancellation?.Token ?? CancellationToken.None);
    }

    private async Task LoadCurrentPreviewAsync(CancellationToken cancellationToken)
    {
        var current = CurrentPreview;
        if (current is null) return;

        foreach (var preview in PreviewImages)
        {
            if (!ReferenceEquals(preview, current)) preview.Image = null;
        }

        current.State = "Loading";
        var image = await _images.LoadAsync(current.Source.PreviewPath, 1600, cancellationToken);
        if (cancellationToken.IsCancellationRequested) return;
        current.Image = image;
        current.State = image is null ? "Unavailable or still writing" : string.Empty;
        OnPropertyChanged(nameof(CurrentPreview));
        PreviewImageLoaded?.Invoke(this, EventArgs.Empty);
    }

    private void ClearPreviews()
    {
        PreviewImages.Clear();
        CurrentImageIndex = -1;
        OnPropertyChanged(nameof(CurrentPreview));
        OnPropertyChanged(nameof(ImagePositionText));
    }

    private void LogReviewCompletionIfDone()
    {
        if (_reviewCompletionLogged || Candidates.Count == 0
            || Candidates.Any(candidate =>
                candidate.Decision is not (
                    ReviewDecision.RealNg
                    or ReviewDecision.Overkill
                    or ReviewDecision.MultiDefectNg)))
        {
            return;
        }

        _reviewCompletionLogged = true;
        var start = StartDate?.ToString("yyyy-MM-dd") ?? "unknown date";
        var end = EndDate?.ToString("yyyy-MM-dd") ?? "unknown date";
        AddConnectionLog(
            $"REVIEW COMPLETE: {start} through {end}. " +
            $"All {Candidates.Count:N0} NG candidates classified.");
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new(name));

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? PreviewImageChanging;
    public event EventHandler? PreviewImageLoaded;
}
