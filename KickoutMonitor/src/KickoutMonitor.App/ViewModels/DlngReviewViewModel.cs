using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;
using KickoutMonitor.Infrastructure;

namespace KickoutMonitor.App.ViewModels;

public sealed class DlngCandidateItem : INotifyPropertyChanged, IReworkRow
{
    private string _reviewStatus;

    public DlngCandidateItem(DlngReviewItem item, DlngReviewRecord? review)
    {
        Item = item;
        _reviewStatus = review is null ? "Pending" : "Saved";
        SavedClass = review?.FinalClass ?? "";
    }

    private string _savedClass = "";
    public string SavedClass { get => _savedClass; set { _savedClass = value; PropertyChanged?.Invoke(this, new(nameof(SavedClass))); PropertyChanged?.Invoke(this, new(nameof(JudgmentTone))); } }
    public string JudgmentTone => ReviewSemantics.Tone(SavedClass);
    public DlngReviewItem Item { get; }
    public string Date => Item.InspectedAt.ToString("yyyy-MM-dd");
    public string Time => Item.InspectedAt.ToString("HH:mm:ss");
    public string LinePolarity => Item.LinePolarity;
    public string CellId => Item.CellId;
    public DateTime InspectionTime => Item.InspectedAt;
    public string InspectionKey => Item.Inspection?.Identity ?? $"{Item.MachineId}|{InspectionTime:O}|{Item.LotId}|{Item.CellId}";
    public string ReworkGroup => InspectionIdentity.Group(Item.MachineId, Item.LotId, Item.CellId, InspectionKey);
    public string ReworkLabel { get; set; } = "";

    public string Judge => Item.Judge;
    public string Defect => Item.JudgeDefect;
    public string CropFolder => Item.CropFolder;
    public string Side => Item.Side;
    public string SourceClass => Item.SourceClass;
    public string ImageState => Item.ResolutionMessage ?? "Ready";
    public string DisplayOverlay => Item.ModelKind == DlngModelKind.Classification
        ? $"{Item.SourceClass} / {Item.SideTitle()}"
        : $"{(Item.ModelKind == DlngModelKind.FallbackRaw ? "NEED_TO_SIMULATE" : "Segmentation")} / {Item.SideTitle()}";
    public string ReviewStatus
    {
        get => _reviewStatus;
        set
        {
            if (_reviewStatus == value) return;
            _reviewStatus = value;
            PropertyChanged?.Invoke(this, new(nameof(ReviewStatus)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class DlngPreviewItem : INotifyPropertyChanged
{
    private BitmapSource? _image;
    private string _state = "Loading";

    public DlngPreviewItem(DlngImage source)
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

public sealed class DlngClassOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public DlngClassOption(string displayName)
    {
        DisplayName = displayName;
    }

    public string DisplayName { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class DlngModelOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public DlngModelOption(string name, bool isSelected = false)
    {
        Name = name;
        _isSelected = isSelected;
    }

    public string Name { get; }
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

public sealed class DlngReviewViewModel : INotifyPropertyChanged
{
    private readonly IMachineRegistry _machines;
    private readonly DlngQueueService _queue;
    private readonly IDlngReviewStore _reviews;
    private readonly IDlngReportService _reports;
    private readonly IPreviewImageLoader<BitmapSource> _images;
    private readonly VisionMasterSettings _settings;
    private readonly IFlaggedItemStore? _flags;
    private readonly TrainingCollectionService? _collection;
    private bool _draftEdited;
    private bool _includeInTraining;
    private bool _trainingTouched;
    private bool _applyingTrainingDefault;
    private bool _alwaysSave;
    public bool AlwaysSave
    {
        get => _alwaysSave;
        set
        {
            if (!Set(ref _alwaysSave, value)) return;
            if (CanCollect && !_trainingTouched)
            {
                _applyingTrainingDefault = true;
                try { IncludeInTraining = value || FinalClassOptions.Any(o => o.IsSelected && o.DisplayName == "Overkill"); }
                finally { _applyingTrainingDefault = false; }
            }
        }
    }
    public bool IncludeInTraining
    {
        get => _includeInTraining;
        set { if (Set(ref _includeInTraining, value) && !_restoringSelections) { if (!_applyingTrainingDefault) _trainingTouched = true; _draftEdited = true; CommandManager.InvalidateRequerySuggested(); } }
    }
    public bool CanCollect => (SelectedCandidate?.Item.ModelKind is DlngModelKind.Classification or DlngModelKind.Segmentation)
        && !FinalClassOptions.Any(x => x.IsSelected && ReviewSemantics.IsNotDlng(x.DisplayName));
    private CancellationTokenSource? _previewCancellation;
    private DlngCandidateItem? _selectedCandidate;
    private DateTime? _startDate = DateTime.Today;
    private DateTime? _endDate = DateTime.Today;
    private DateTime? _reportDate = DateTime.Today;
    private DateTime? _reportEndDate = DateTime.Today;
    private string _status = "Ready";
    private bool _isBusy;
    private bool _autoAdvanceAfterReview = true;
    private bool _allModelsSelected;
    private bool _updatingModelSelections;
    private bool _restoringSelections;
    private int _currentImageIndex = -1;
    private readonly Dictionary<string, DlngReviewRecord> _reviewRecords = new(StringComparer.OrdinalIgnoreCase);

    public DlngReviewViewModel(
        IMachineRegistry machines,
        DlngQueueService queue,
        IDlngReviewStore reviews,
        IDlngReportService reports,
        IPreviewImageLoader<BitmapSource> images,
        VisionMasterSettings? settings = null,
        IFlaggedItemStore? flags = null,
        TrainingCollectionService? collection = null)
    {
        var defaultRange = QueueTimeRange.DlngDefault(DateTime.Now);
        _startDate = defaultRange.Start.Date;
        _endDate = defaultRange.End.Date;
        _startTime = defaultRange.Start.ToString("HH:mm");
        _endTime = defaultRange.End.ToString("HH:mm");
        _machines = machines;
        _queue = queue;
        _reviews = reviews;
        _reports = reports;
        _images = images;
        _settings = settings ?? VisionMasterSettings.CreateDefault();
        _flags = flags;
        _collection = collection;
        MachineOptions = new(_machines.All.Select((machine, index) => new MachineOption(machine, machine.Line is "1-1" or "1-2")));
        ModelOptions = new(DlngModelNames(_settings).Select(name => new DlngModelOption(name)));
        foreach (var option in ModelOptions) option.PropertyChanged += ModelOption_PropertyChanged;
        LoadCommand = new(LoadQueueAsync, () => !IsBusy && MachineOptions.Any(x => x.IsSelected) && ModelOptions.Any(x => x.IsSelected) && StartDate is not null && EndDate is not null);
        GenerateReportCommand = new(GenerateReportAsync, () => !IsBusy && MachineOptions.Any(x => x.IsSelected) && ModelOptions.Any(x => x.IsSelected) && ReportDate is not null && ReportEndDate is not null);
        PreviousCommand = new(Previous, CanPrevious);
        NextCommand = new(Next, CanNext);
        PreviousImageCommand = new(PreviousImageAsync, () => CurrentImageIndex > 0);
        NextImageCommand = new(NextImageAsync, () => CurrentImageIndex >= 0 && CurrentImageIndex < PreviewImages.Count - 1);
        FlagCommand = new(FlagCurrentAsync, () => SelectedCandidate is not null && _flags is not null);
        CommitCommand = new(CommitSelection, () => !IsBusy && _draftEdited && SelectedCandidate is not null && FinalClassOptions.Any(x => x.IsSelected));
    }

    public ObservableCollection<MachineOption> MachineOptions { get; }
    public ObservableCollection<DlngModelOption> ModelOptions { get; }
    public ObservableCollection<DlngCandidateItem> Candidates { get; } = [];
    public ObservableCollection<DlngPreviewItem> PreviewImages { get; } = [];
    public ObservableCollection<DlngClassOption> FinalClassOptions { get; } = [];
    public ObservableCollection<string> ActivityLog { get; } = [];
    public ObservableCollection<DlngReportRow> SummaryRows { get; } = [];
    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand GenerateReportCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand NextCommand { get; }
    public AsyncRelayCommand PreviousImageCommand { get; }
    public AsyncRelayCommand NextImageCommand { get; }
    public AsyncRelayCommand FlagCommand { get; }
    public AsyncRelayCommand CommitCommand { get; }

    public bool AutoAdvanceAfterReview
    {
        get => _autoAdvanceAfterReview;
        set => Set(ref _autoAdvanceAfterReview, value);
    }

    public bool AllModelsSelected
    {
        get => _allModelsSelected;
        set
        {
            if (!Set(ref _allModelsSelected, value)) return;
            if (_updatingModelSelections) return;
            _updatingModelSelections = true;
            try
            {
                foreach (var option in ModelOptions)
                {
                    option.IsSelected = value;
                }
            }
            finally
            {
                _updatingModelSelections = false;
            }
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _startTime = "06:00";
    private string _endTime = "06:00";
    public string StartTime { get => _startTime; set => Set(ref _startTime, value); }
    public string EndTime { get => _endTime; set => Set(ref _endTime, value); }

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
        set
        {
            if (Set(ref _reportDate, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public DateTime? ReportEndDate
    {
        get => _reportEndDate;
        set
        {
            if (Set(ref _reportEndDate, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public DlngCandidateItem? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (!Set(ref _selectedCandidate, value)) return;
            _ = LoadPreviewsAsync(value);
            ConfigureFinalClasses(value);
            OnPropertyChanged(nameof(SelectedIndex));
            OnPropertyChanged(nameof(PositionText));
            OnPropertyChanged(nameof(DisplayOverlay));
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
    public string DisplayOverlay => SelectedCandidate?.DisplayOverlay ?? string.Empty;

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

    public DlngPreviewItem? CurrentPreview =>
        CurrentImageIndex >= 0 && CurrentImageIndex < PreviewImages.Count
            ? PreviewImages[CurrentImageIndex]
            : null;
    public string ImagePositionText =>
        CurrentImageIndex < 0 ? "0 / 0" : $"{CurrentImageIndex + 1} / {PreviewImages.Count}";

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
            case Key.R:
                SelectByDisplay("Real");
                return;
            case Key.O:
                SelectByDisplay("Overkill");
                return;
            case Key.N when SelectedCandidate?.Item.ModelKind == DlngModelKind.Segmentation:
                SelectByDisplay(ReviewSemantics.NotDlng);
                return;
            case Key.T when CanCollect:
                _trainingTouched = true;
                IncludeInTraining = true;
                return;
            case Key.Enter when CommitCommand.CanExecute(null):
                CommitCommand.Execute(null);
                return;
            case Key.Enter when SelectedCandidate?.ReviewStatus == "Saved" && !_draftEdited && CanNext():
                Next();
                return;
            case Key.D1 or Key.NumPad1:
                SelectByPrefix("01");
                return;
            case Key.D2 or Key.NumPad2:
                SelectByPrefix("02");
                return;
            case Key.D3 or Key.NumPad3:
                SelectByPrefix("03");
                return;
            case Key.D4 or Key.NumPad4:
                SelectByPrefix("04");
                return;
            case Key.D5 or Key.NumPad5:
                SelectByPrefix("05");
                return;
            case Key.D6 or Key.NumPad6:
                SelectByPrefix("06");
                return;
            case Key.D7 or Key.NumPad7:
                SelectByPrefix("07");
                return;
            case Key.D8 or Key.NumPad8:
                SelectByPrefix("08");
                return;
            case Key.D9 or Key.NumPad9:
                SelectByPrefix("09");
                return;
        }
    }

    private async Task LoadQueueAsync()
    {
        var selectedMachines = MachineOptions.Where(x => x.IsSelected).Select(x => x.Machine).ToArray();
        if (StartDate is null || EndDate is null || selectedMachines.Length == 0) return;
        if (!QueueTimeRange.TryCreate(StartDate, StartTime, EndDate, EndTime, out var range, out var error))
        {
            Status = error;
            return;
        }
        var start = DateOnly.FromDateTime(range!.Start);
        var end = DateOnly.FromDateTime(range.End.AddTicks(-1));

        IsBusy = true;
        Candidates.Clear();
        ClearPreviews();
        ActivityLog.Clear();
        try
        {
            AddLog($"Loading DLNG queue for {selectedMachines.Length} machine(s), {range.Start:yyyy-MM-dd HH:mm:ss} to {range.End:yyyy-MM-dd HH:mm:ss} (end excluded).");
            var saved = await _reviews.LoadAsync(CancellationToken.None);
            _reviewRecords.Clear();
            foreach (var pair in saved) _reviewRecords[pair.Key] = pair.Value;
            var progress = new Progress<string>(AddLog);
            var loaded = new List<DlngCandidateItem>();
            var selectedModels = SelectedModelNames();
            foreach (var machine in selectedMachines)
            {
                foreach (var date in range.Dates())
                {
                    try
                    {
                        var items = await _queue.LoadAsync(machine, date, progress, CancellationToken.None, selectedModels, range);
                        foreach (var item in items)
                        {
                            saved.TryGetValue(item.Key, out var review);
                            loaded.Add(new(item, review));
                        }
                        AddLog($"{machine.OutputFolderName} {date:yyyy-MM-dd}: {items.Count:N0} DLNG crop item(s).");
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
                         .ReworkOrder(IsUnclassified))
            {
                Candidates.Add(item);
            }
            SelectedCandidate = Candidates.FirstOrDefault();
            Status = Candidates.Count == 0
                ? "No eligible DLNG crop items were found in the selected timeframe."
                : $"Loaded {Candidates.Count:N0} DLNG crop item(s).";
            RequestKeyboardFocus?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            AddLog($"DLNG queue load failed: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task FlagCurrentAsync()
    {
        if (_flags is null) return;
        var item = SelectedCandidate?.Item;
        if (item is null) return;
        var rawPaths = (item.RawImages ?? []).Select(x => x.Path).ToArray();
        var now = DateTimeOffset.Now;
        await _flags.SaveAsync(new(
            FlagKey("DLNG", item.MachineId, item.InspectedAt, item.CellId, item.Side) + "|" + InspectionIdentity.Hash(item.Inspection?.Identity ?? item.Key),
            "DLNG",
            item.MachineId,
            item.LinePolarity,
            item.Polarity,
            item.InspectedAt,
            item.Model,
            item.LotId,
            item.CellId,
            item.Side,
            $"{item.JudgeDefect} / {item.CropFolder}",
            rawPaths,
            now,
            now, Inspection: item.Inspection), CancellationToken.None);
        Status = $"Flagged {item.LinePolarity} {item.CellId} {item.Side}.";
        AddLog(Status);
    }

    private async Task GenerateReportAsync()
    {
        var selectedMachines = MachineOptions.Where(x => x.IsSelected).Select(x => x.Machine).ToArray();
        if (ReportDate is null || ReportEndDate is null || selectedMachines.Length == 0) return;
        var reportStart = DateOnly.FromDateTime(ReportDate.Value);
        var reportEnd = DateOnly.FromDateTime(ReportEndDate.Value);
        if (reportEnd < reportStart)
        {
            Status = "Report end date must be on or after report start date.";
            return;
        }

        IsBusy = true;
        SummaryRows.Clear();
        try
        {
            var progress = new Progress<string>(AddLog);
            var queuedItems = Candidates.Select(x => x.Item).ToArray();
            var results = new List<DlngReportResult>();
            for (var date = reportStart; date <= reportEnd; date = date.AddDays(1))
            {
                try
                {
                    var result = await _reports.GenerateFromItemsAsync(queuedItems, date, progress, CancellationToken.None);
                    results.Add(result);
                    foreach (var row in result.Rows) SummaryRows.Add(row);
                    AddLog($"DLNG report saved: {result.SummaryWorkbook}");
                }
                catch (InvalidOperationException exception)
                {
                    AddLog($"{date:yyyy-MM-dd}: {exception.Message}");
                }
            }

            if (results.Count == 0)
            {
                throw new InvalidOperationException("Cannot generate DLNG report: no reviewed DLNG crop item(s) were found for the selected date range.");
            }

            CommandManager.InvalidateRequerySuggested();
            Status = results.Count == 1
                ? $"DLNG report saved: {results[0].SummaryWorkbook}"
                : $"DLNG reports saved: {results.Count:N0} day(s).";
            AddLog(Status);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            AddLog($"DLNG REPORT BLOCKED: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ConfigureFinalClasses(DlngCandidateItem? item)
    {
        _restoringSelections = true;
        try
        {
            FinalClassOptions.Clear();
            _draftEdited = false;
            _trainingTouched = false;
            IncludeInTraining = item is not null && (_reviewRecords.TryGetValue(item.Item.Key, out var old) ? old.IncludeInTraining : AlwaysSave);
            OnPropertyChanged(nameof(CanCollect));
            if (item is null) return;
            var classes = item.Item.ModelKind is DlngModelKind.Segmentation or DlngModelKind.FallbackRaw
                ? _settings.DlngRules.SegmentationClasses
                : DlngRules.ClassesFor(item.Item.CropFolder, item.Item.Polarity, _settings, item.Item.Side);
            var selected = _reviewRecords.TryGetValue(item.Item.Key, out var saved)
                ? saved.FinalClass
                : string.Empty;
            var choices = classes.Where(klass => !ReviewSemantics.IsLegacyNoNeed(klass) && !ReviewSemantics.IsNotDlng(klass));
            if (item.Item.ModelKind == DlngModelKind.Segmentation) choices = choices.Append(ReviewSemantics.NotDlng);
            foreach (var klass in choices.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var option = new DlngClassOption(klass)
                {
                    IsSelected = klass.Equals(selected, StringComparison.OrdinalIgnoreCase)
                };
                option.PropertyChanged += FinalClassOption_PropertyChanged;
                FinalClassOptions.Add(option);
            }
        }
        finally
        {
            if (!CanCollect) IncludeInTraining = false;
            OnPropertyChanged(nameof(CanCollect));
            _restoringSelections = false;
        }
    }

    private void FinalClassOption_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_restoringSelections || e.PropertyName != nameof(DlngClassOption.IsSelected)) return;
        _draftEdited = true;
        if (sender is DlngClassOption option && option.IsSelected)
        {
            foreach (var other in FinalClassOptions.Where(x => !ReferenceEquals(x, option)))
            {
                other.IsSelected = false;
            }

            _applyingTrainingDefault = true;
            try
            {
                if (ReviewSemantics.IsNotDlng(option.DisplayName)) IncludeInTraining = false;
                else if (!_trainingTouched)
                    IncludeInTraining = AlwaysSave || (SelectedCandidate?.Item.ModelKind == DlngModelKind.Segmentation && option.DisplayName.Equals("Overkill", StringComparison.OrdinalIgnoreCase));
            }
            finally { _applyingTrainingDefault = false; }
            OnPropertyChanged(nameof(CanCollect));
            if (AutoAdvanceAfterReview && CommitCommand.CanExecute(null))
            {
                CommitCommand.Execute(null);
            }
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task CommitSelection()
    {
        var item = SelectedCandidate;
        var selected = FinalClassOptions.FirstOrDefault(x => x.IsSelected)?.DisplayName;
        if (IsBusy || !_draftEdited || item is null || string.IsNullOrWhiteSpace(selected)) return;
        var displayed = DisplayedCandidates();
        var index = DisplayedIndexOf(item, displayed);
        var next = index >= 0 && index + 1 < displayed.Count ? displayed[index + 1] : null;
        IsBusy = true;
        var record = new DlngReviewRecord(
            item.Item.Key,
            item.Item.MachineId,
            item.Item.LinePolarity,
            item.Item.InspectedAt,
            item.Item.CellId,
            item.Item.Judge,
            item.Item.JudgeDefect,
            item.Item.Side,
            item.Item.CropFolder,
            item.Item.SourceClass,
            selected,
            item.Item.ModelKind == DlngModelKind.FallbackRaw,
            item.Item.Images.Select(x => x.Path).ToArray(),
            DateTimeOffset.Now, item.Item.Inspection,
            IncludeInTraining && CanCollect,
            IncludeInTraining ? (_reviewRecords.GetValueOrDefault(item.Item.Key)?.TrainingSelectedAt ?? DateTimeOffset.Now) : null,
            item.Item.Model, item.Item.ModelKind, item.Item.Polarity.ToString());
        try
        {
            await _reviews.SaveAsync(record, CancellationToken.None);
            _reviewRecords[item.Item.Key] = record;
            item.ReviewStatus = "Saved";
            item.SavedClass = selected;
            _draftEdited = false;
            Status = "Judgment saved.";
            if (_collection is not null)
            {
                try
                {
                    Status = "Judgment saved. " + await _collection.ApplyAsync(record);
                    var collected = (await _collection.LoadAsync()).SelectMany(b => b.Samples)
                        .FirstOrDefault(s => s.Id == ReviewSemantics.SampleId(record) && s.State == "Ready");
                    if (collected is not null)
                    {
                        record = record with { CollectedAt = collected.CollectedAt };
                        await _reviews.SaveAsync(record, CancellationToken.None);
                        _reviewRecords[item.Item.Key] = record;
                    }
                }
                catch (Exception copyError) { Status = "Judgment saved; collection pending: " + copyError.Message; }
            }
            AddLog($"{item.LinePolarity} {item.CellId} {item.Defect}: classified as {selected}.");
            if (ReferenceEquals(SelectedCandidate, item) && next is not null) SelectedCandidate = next;
            RequestKeyboardFocus?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            item.ReviewStatus = "Save failed";
            Status = exception.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task LoadPreviewsAsync(DlngCandidateItem? item)
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new();
        var token = _previewCancellation.Token;
        ClearPreviews();
        if (item is null) return;
        foreach (var image in item.Item.Images)
        {
            PreviewImages.Add(new(image));
        }
        CurrentImageIndex = PreviewImages.Count > 0 ? 0 : -1;
        await LoadCurrentPreviewAsync(token);
    }

    private static bool IsUnclassified(DlngCandidateItem item) =>
        !item.ReviewStatus.StartsWith("Saved", StringComparison.OrdinalIgnoreCase);

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

    private IReadOnlyList<DlngCandidateItem> DisplayedCandidates()
    {
        var view = CollectionViewSource.GetDefaultView(Candidates);
        return view is null ? Candidates.ToArray() : view.Cast<DlngCandidateItem>().ToArray();
    }

    private int DisplayedIndexOf(DlngCandidateItem? item)
    {
        if (item is null) return -1;
        return DisplayedIndexOf(item, DisplayedCandidates());
    }

    private static int DisplayedIndexOf(DlngCandidateItem? item, IReadOnlyList<DlngCandidateItem> displayed)
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

    private void ClearPreviews()
    {
        PreviewImages.Clear();
        CurrentImageIndex = -1;
        OnPropertyChanged(nameof(CurrentPreview));
        OnPropertyChanged(nameof(ImagePositionText));
    }

    private void SelectByDisplay(string display)
    {
        var option = FinalClassOptions.FirstOrDefault(x => x.DisplayName.Equals(display, StringComparison.OrdinalIgnoreCase));
        if (option is not null)
        {
            if (option.IsSelected) { _draftEdited = true; if (AutoAdvanceAfterReview && CommitCommand.CanExecute(null)) CommitCommand.Execute(null); }
            else option.IsSelected = true;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void SelectByPrefix(string prefix)
    {
        var option = FinalClassOptions.FirstOrDefault(x => x.DisplayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (option is not null)
        {
            if (option.IsSelected) { _draftEdited = true; if (AutoAdvanceAfterReview && CommitCommand.CanExecute(null)) CommitCommand.Execute(null); }
            else option.IsSelected = true;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void AddLog(string message) => ActivityLog.Add($"[{DateTime.Now:HH:mm:ss}] {message}");

    private void ModelOption_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DlngModelOption.IsSelected) || _updatingModelSelections) return;
        _updatingModelSelections = true;
        try
        {
            _allModelsSelected = ModelOptions.Count > 0 && ModelOptions.All(x => x.IsSelected);
            OnPropertyChanged(nameof(AllModelsSelected));
        }
        finally
        {
            _updatingModelSelections = false;
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private HashSet<string> SelectedModelNames() =>
        new(ModelOptions.Where(x => x.IsSelected).Select(x => x.Name), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> DlngModelNames(VisionMasterSettings settings) =>
        settings.DlngRules.DefectMappings
            .SelectMany(x => x.CropFolders)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string FlagKey(string source, string machineId, DateTime timestamp, string cellId, string side) =>
        string.Join(
            "|",
            source,
            machineId,
            timestamp.ToString("O"),
            cellId.Trim(),
            side.Trim().ToUpperInvariant());

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
    public event EventHandler? RequestKeyboardFocus;
}

internal static class DlngReviewItemExtensions
{
    public static string SideTitle(this DlngReviewItem item) =>
        item.Side.Equals("LOWER", StringComparison.OrdinalIgnoreCase) ? "Lower" : "Upper";
}




