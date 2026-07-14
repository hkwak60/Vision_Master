using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.App.ViewModels;

public sealed class NgBypassCandidateItem : INotifyPropertyChanged
{
    private ReviewDecision _decision;
    private CopyState _copyState;

    public NgBypassCandidateItem(NgBypassCandidate candidate, NgBypassReviewRecord? review)
    {
        Candidate = candidate;
        _decision = review?.Decision ?? ReviewDecision.Pending;
        _copyState = review?.CopyState ?? CopyState.NotRequested;
    }

    public NgBypassCandidate Candidate { get; }
    public string Time => Candidate.InspectedAt.ToString("HH:mm:ss");
    public string LinePolarity => Candidate.LinePolarity;
    public string CellId => Candidate.CellId;
    public string Measure => Candidate.Measure;
    public string Side => Candidate.Side;
    public string TargetValue => Candidate.TargetValue;
    public string ReviewLabel => Decision switch
    {
        ReviewDecision.RealNg => "Real",
        ReviewDecision.Overkill => "Overkill",
        _ => "Pending"
    };

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
        set
        {
            if (_copyState == value) return;
            _copyState = value;
            PropertyChanged?.Invoke(this, new(nameof(CopyState)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class NgBypassPreviewItem : INotifyPropertyChanged
{
    private BitmapSource? _image;
    private string _state = "Loading";

    public NgBypassPreviewItem(DlngImage source)
    {
        Source = source;
    }

    public DlngImage Source { get; }
    public string Label => Source.Label;
    public string Path => Source.Path;

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

public sealed class NgBypassMonitorViewModel : INotifyPropertyChanged
{
    private readonly IMachineRegistry _machines;
    private readonly NgBypassQueueService _queue;
    private readonly INgBypassReviewStore _reviews;
    private readonly INgBypassClassifiedFolderService _folders;
    private readonly INgBypassReportService _reports;
    private readonly IPreviewImageLoader<BitmapSource> _images;
    private CancellationTokenSource? _previewCancellation;
    private NgBypassCandidateItem? _selectedCandidate;
    private DateTime? _startDate = DateTime.Today;
    private DateTime? _endDate = DateTime.Today;
    private DateTime? _reportDate = DateTime.Today;
    private string _measure = string.Empty;
    private bool _includeUpper = true;
    private bool _includeLower;
    private bool _bypassed;
    private bool _skipNg;
    private string _status = "Ready";
    private bool _isBusy;
    private int _currentImageIndex = -1;
    private bool _lastLoadHadHeaderWarnings;

    public NgBypassMonitorViewModel(
        IMachineRegistry machines,
        NgBypassQueueService queue,
        INgBypassReviewStore reviews,
        INgBypassClassifiedFolderService folders,
        INgBypassReportService reports,
        IPreviewImageLoader<BitmapSource> images)
    {
        _machines = machines;
        _queue = queue;
        _reviews = reviews;
        _folders = folders;
        _reports = reports;
        _images = images;
        MachineOptions = new(_machines.All.Select((machine, index) => new MachineOption(machine, index == 0)));
        LoadCommand = new(LoadQueueAsync, CanLoad);
        GenerateReportCommand = new(GenerateReportAsync, CanGenerateReport);
        RealCommand = new(() => ReviewAsync(ReviewDecision.RealNg), CanReview);
        OverkillCommand = new(() => ReviewAsync(ReviewDecision.Overkill), CanReview);
        PreviousCommand = new(Previous, CanPrevious);
        NextCommand = new(Next, CanNext);
        PreviousImageCommand = new(PreviousImageAsync, () => CurrentImageIndex > 0);
        NextImageCommand = new(NextImageAsync, () => CurrentImageIndex >= 0 && CurrentImageIndex < PreviewImages.Count - 1);
    }

    public ObservableCollection<MachineOption> MachineOptions { get; }
    public ObservableCollection<NgBypassCandidateItem> Candidates { get; } = [];
    public ObservableCollection<NgBypassPreviewItem> PreviewImages { get; } = [];
    public ObservableCollection<string> ActivityLog { get; } = [];
    public ObservableCollection<NgBypassReportRow> SummaryRows { get; } = [];
    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand GenerateReportCommand { get; }
    public AsyncRelayCommand RealCommand { get; }
    public AsyncRelayCommand OverkillCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand NextCommand { get; }
    public AsyncRelayCommand PreviousImageCommand { get; }
    public AsyncRelayCommand NextImageCommand { get; }

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

    public string Measure
    {
        get => _measure;
        set
        {
            if (!Set(ref _measure, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IncludeUpper
    {
        get => _includeUpper;
        set
        {
            if (!Set(ref _includeUpper, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IncludeLower
    {
        get => _includeLower;
        set
        {
            if (!Set(ref _includeLower, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool Bypassed
    {
        get => _bypassed;
        set
        {
            if (!Set(ref _bypassed, value)) return;
            if (!value) SkipNg = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool SkipNg
    {
        get => _skipNg;
        set => Set(ref _skipNg, Bypassed && value);
    }

    public NgBypassCandidateItem? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (!Set(ref _selectedCandidate, value)) return;
            _ = LoadPreviewsAsync(value);
            OnPropertyChanged(nameof(SelectedIndex));
            OnPropertyChanged(nameof(PositionText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public int SelectedIndex => DisplayedIndexOf(SelectedCandidate);
    public string PositionText
    {
        get
        {
            var displayed = DisplayedCandidates();
            var index = DisplayedIndexOf(SelectedCandidate, displayed);
            return index < 0 ? "0 / 0" : $"{index + 1} / {displayed.Count}";
        }
    }

    public int CurrentImageIndex
    {
        get => _currentImageIndex;
        private set
        {
            if (_currentImageIndex != value && _currentImageIndex >= 0) PreviewImageChanging?.Invoke(this, EventArgs.Empty);
            if (!Set(ref _currentImageIndex, value)) return;
            OnPropertyChanged(nameof(CurrentPreview));
            OnPropertyChanged(nameof(ImagePositionText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public NgBypassPreviewItem? CurrentPreview =>
        CurrentImageIndex >= 0 && CurrentImageIndex < PreviewImages.Count
            ? PreviewImages[CurrentImageIndex]
            : null;
    public string ImagePositionText => CurrentImageIndex < 0 ? "0 / 0" : $"{CurrentImageIndex + 1} / {PreviewImages.Count}";

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
            case Key.Left when PreviousImageCommand.CanExecute(null):
                PreviousImageCommand.Execute(null);
                return;
            case Key.Right when NextImageCommand.CanExecute(null):
                NextImageCommand.Execute(null);
                return;
            case Key.Up when PreviousCommand.CanExecute(null):
                PreviousCommand.Execute(null);
                return;
            case Key.Down when NextCommand.CanExecute(null):
                NextCommand.Execute(null);
                return;
            case Key.R when RealCommand.CanExecute(null):
                RealCommand.Execute(null);
                return;
            case Key.O when OverkillCommand.CanExecute(null):
                OverkillCommand.Execute(null);
                return;
        }
    }

    private bool CanLoad() =>
        !IsBusy
        && MachineOptions.Any(x => x.IsSelected)
        && StartDate is not null
        && EndDate is not null
        && !string.IsNullOrWhiteSpace(Measure)
        && (IncludeUpper || IncludeLower);

    private bool CanGenerateReport() => CanLoad() && ReportDate is not null;

    private NgBypassQuery Query() => new(Measure.Trim(), IncludeUpper, IncludeLower, Bypassed, Bypassed && SkipNg);

    private async Task LoadQueueAsync()
    {
        if (StartDate is null || EndDate is null) return;
        var start = DateOnly.FromDateTime(StartDate.Value);
        var end = DateOnly.FromDateTime(EndDate.Value);
        if (end < start)
        {
            Status = "End date must be on or after start date.";
            return;
        }

        var selectedMachines = MachineOptions.Where(x => x.IsSelected).Select(x => x.Machine).ToArray();
        var query = Query();
        IsBusy = true;
        Candidates.Clear();
        ClearPreviews();
        ActivityLog.Clear();
        _lastLoadHadHeaderWarnings = false;
        try
        {
            var saved = await _reviews.LoadAsync(CancellationToken.None);
            var loaded = new List<NgBypassCandidateItem>();
            var progress = new Progress<string>(AddLog);
            foreach (var machine in selectedMachines)
            {
                for (var date = start; date <= end; date = date.AddDays(1))
                {
                    try
                    {
                        var result = await _queue.LoadAsync(machine, date, query, progress, CancellationToken.None);
                        foreach (var warning in result.HeaderWarnings)
                        {
                            _lastLoadHadHeaderWarnings = true;
                            AddLog($"Missing measure column: {warning.Machine} {warning.Date:yyyy-MM-dd} {warning.FileName} column={warning.ColumnName}");
                        }
                        foreach (var item in result.Items)
                        {
                            saved.TryGetValue(item.Key, out var review);
                            loaded.Add(new(item, review));
                        }
                        AddLog($"{machine.OutputFolderName} {date:yyyy-MM-dd}: {result.Items.Count:N0} NG/Bypass item(s).");
                    }
                    catch (FileNotFoundException)
                    {
                        AddLog($"{machine.OutputFolderName} {date:yyyy-MM-dd}: no daily CSV found.");
                    }
                    catch (Exception exception)
                    {
                        AddLog($"{machine.OutputFolderName} {date:yyyy-MM-dd}: {exception.Message}");
                    }
                }
            }

            foreach (var item in loaded
                         .OrderBy(IsUnclassified)
                         .ThenBy(x => x.Candidate.InspectedAt)
                         .ThenBy(x => x.LinePolarity, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.CellId, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.Side, StringComparer.OrdinalIgnoreCase))
            {
                Candidates.Add(item);
            }
            SelectedCandidate = Candidates.FirstOrDefault();
            Status = Candidates.Count == 0 && _lastLoadHadHeaderWarnings
                ? "No NG/Bypass items were found. One or more selected CSVs were missing the measure column."
                : Candidates.Count == 0
                    ? "No matching NG/Bypass items were found."
                    : $"Loaded {Candidates.Count:N0} NG/Bypass item(s).";
            RequestKeyboardFocus?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(PositionText));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task GenerateReportAsync()
    {
        if (ReportDate is null) return;
        var selectedMachines = MachineOptions.Where(x => x.IsSelected).Select(x => x.Machine).ToArray();
        IsBusy = true;
        SummaryRows.Clear();
        try
        {
            var reportDate = DateOnly.FromDateTime(ReportDate.Value);
            var progress = new Progress<string>(AddLog);
            var result = await _reports.GenerateAsync(selectedMachines, Query(), reportDate, progress, CancellationToken.None);
            foreach (var row in result.Rows) SummaryRows.Add(row);
            Status = $"NG/Bypass summary saved: {result.SummaryWorkbook}";
            AddLog(Status);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            AddLog($"REPORT BLOCKED: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanReview() => !IsBusy && SelectedCandidate is not null;

    private async Task ReviewAsync(ReviewDecision decision)
    {
        var item = SelectedCandidate;
        if (item is null) return;
        IsBusy = true;
        try
        {
            var machine = _machines.Get(item.Candidate.MachineId);
            var copy = await _folders.ClassifyAsync(machine, item.Candidate, decision, CancellationToken.None);
            var record = new NgBypassReviewRecord(
                item.Candidate.Key,
                item.Candidate.MachineId,
                item.Candidate.LinePolarity,
                item.Candidate.InspectedAt,
                item.Candidate.CellId,
                item.Candidate.Measure,
                item.Candidate.Side,
                item.Candidate.TargetValue,
                decision,
                copy.State,
                copy.Destination,
                DateTimeOffset.Now);
            await _reviews.SaveAsync(record, CancellationToken.None);
            item.Decision = decision;
            item.CopyState = copy.State;
            Status = $"{item.CellId}: {item.ReviewLabel} - {copy.Message}";
            Next();
            RequestKeyboardFocus?.Invoke(this, EventArgs.Empty);
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

    private async Task LoadPreviewsAsync(NgBypassCandidateItem? item)
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new();
        var token = _previewCancellation.Token;
        ClearPreviews();
        if (item is null) return;
        foreach (var image in item.Candidate.Images) PreviewImages.Add(new(image));
        CurrentImageIndex = PreviewImages.Count > 0 ? 0 : -1;
        await LoadCurrentPreviewAsync(token);
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
        var image = File.Exists(current.Path)
            ? await _images.LoadAsync(current.Path, 1600, cancellationToken)
            : null;
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

    private static bool IsUnclassified(NgBypassCandidateItem item) =>
        item.Decision == ReviewDecision.Pending;

    private IReadOnlyList<NgBypassCandidateItem> DisplayedCandidates()
    {
        var view = CollectionViewSource.GetDefaultView(Candidates);
        return view is null ? Candidates.ToArray() : view.Cast<NgBypassCandidateItem>().ToArray();
    }

    private int DisplayedIndexOf(NgBypassCandidateItem? item)
    {
        if (item is null) return -1;
        return DisplayedIndexOf(item, DisplayedCandidates());
    }

    private static int DisplayedIndexOf(NgBypassCandidateItem? item, IReadOnlyList<NgBypassCandidateItem> displayed)
    {
        if (item is null) return -1;
        for (var index = 0; index < displayed.Count; index++)
        {
            if (ReferenceEquals(displayed[index], item)) return index;
        }
        return -1;
    }

    private bool CanPrevious() => DisplayedIndexOf(SelectedCandidate) > 0;

    private bool CanNext()
    {
        var displayed = DisplayedCandidates();
        var index = DisplayedIndexOf(SelectedCandidate, displayed);
        return index >= 0 && index < displayed.Count - 1;
    }

    private void Previous()
    {
        var displayed = DisplayedCandidates();
        var index = DisplayedIndexOf(SelectedCandidate, displayed);
        if (index > 0) SelectedCandidate = displayed[index - 1];
    }

    private void Next()
    {
        var displayed = DisplayedCandidates();
        var index = DisplayedIndexOf(SelectedCandidate, displayed);
        if (index >= 0 && index < displayed.Count - 1) SelectedCandidate = displayed[index + 1];
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

    private void AddLog(string message) => ActivityLog.Add($"[{DateTime.Now:HH:mm:ss}] {message}");

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new(name));

    public event EventHandler? PreviewImageChanging;
    public event EventHandler? PreviewImageLoaded;
    public event EventHandler? RequestKeyboardFocus;
    public event PropertyChangedEventHandler? PropertyChanged;
}
