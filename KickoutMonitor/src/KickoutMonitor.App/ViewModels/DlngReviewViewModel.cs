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

public sealed class DlngCandidateItem : INotifyPropertyChanged
{
    private string _reviewStatus;

    public DlngCandidateItem(DlngReviewItem item, DlngReviewRecord? review)
    {
        Item = item;
        _reviewStatus = review is null ? "Pending" : "Saved";
    }

    public DlngReviewItem Item { get; }
    public string Time => Item.InspectedAt.ToString("HH:mm:ss");
    public string LinePolarity => Item.LinePolarity;
    public string CellId => Item.CellId;
    public string Judge => Item.Judge;
    public string Defect => Item.JudgeDefect;
    public string CropFolder => Item.CropFolder;
    public string Side => Item.Side;
    public string SourceClass => Item.SourceClass;
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

public sealed class DlngReviewViewModel : INotifyPropertyChanged
{
    private readonly IMachineRegistry _machines;
    private readonly DlngQueueService _queue;
    private readonly IDlngReviewStore _reviews;
    private readonly IDlngReportService _reports;
    private readonly IPreviewImageLoader<BitmapSource> _images;
    private readonly VisionMasterSettings _settings;
    private CancellationTokenSource? _previewCancellation;
    private DlngCandidateItem? _selectedCandidate;
    private DateTime? _startDate = DateTime.Today;
    private DateTime? _endDate = DateTime.Today;
    private DateTime? _reportDate = DateTime.Today;
    private string _status = "Ready";
    private bool _isBusy;
    private bool _restoringSelections;
    private int _currentImageIndex = -1;
    private readonly Dictionary<string, DlngReviewRecord> _reviewRecords = new(StringComparer.OrdinalIgnoreCase);

    public DlngReviewViewModel(
        IMachineRegistry machines,
        DlngQueueService queue,
        IDlngReviewStore reviews,
        IDlngReportService reports,
        IPreviewImageLoader<BitmapSource> images,
        VisionMasterSettings? settings = null)
    {
        _machines = machines;
        _queue = queue;
        _reviews = reviews;
        _reports = reports;
        _images = images;
        _settings = settings ?? VisionMasterSettings.CreateDefault();
        MachineOptions = new(_machines.All.Select((machine, index) => new MachineOption(machine, index == 0)));
        LoadCommand = new(LoadQueueAsync, () => !IsBusy && MachineOptions.Any(x => x.IsSelected) && StartDate is not null && EndDate is not null);
        GenerateReportCommand = new(GenerateReportAsync, () => !IsBusy && MachineOptions.Any(x => x.IsSelected) && ReportDate is not null);
        PreviousCommand = new(Previous, CanPrevious);
        NextCommand = new(Next, CanNext);
        PreviousImageCommand = new(PreviousImageAsync, () => CurrentImageIndex > 0);
        NextImageCommand = new(NextImageAsync, () => CurrentImageIndex >= 0 && CurrentImageIndex < PreviewImages.Count - 1);
        CommitCommand = new(CommitSelection, () => !IsBusy && SelectedCandidate is not null && FinalClassOptions.Any(x => x.IsSelected));
    }

    public ObservableCollection<MachineOption> MachineOptions { get; }
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
    public RelayCommand CommitCommand { get; }

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
            case Key.N:
                SelectByDisplay("No Need to Train");
                return;
            case Key.Enter when CommitCommand.CanExecute(null):
                CommitCommand.Execute(null);
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
        }
    }

    private async Task LoadQueueAsync()
    {
        var selectedMachines = MachineOptions.Where(x => x.IsSelected).Select(x => x.Machine).ToArray();
        if (StartDate is null || EndDate is null || selectedMachines.Length == 0) return;
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
        ActivityLog.Clear();
        try
        {
            AddLog($"Loading DLNG queue for {selectedMachines.Length} machine(s), {start:yyyy-MM-dd} through {end:yyyy-MM-dd}.");
            var saved = await _reviews.LoadAsync(CancellationToken.None);
            _reviewRecords.Clear();
            foreach (var pair in saved) _reviewRecords[pair.Key] = pair.Value;
            var progress = new Progress<string>(AddLog);
            var loaded = new List<DlngCandidateItem>();
            foreach (var machine in selectedMachines)
            {
                for (var date = start; date <= end; date = date.AddDays(1))
                {
                    try
                    {
                        var items = await _queue.LoadAsync(machine, date, progress, CancellationToken.None);
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
                         .OrderBy(IsUnclassified)
                         .ThenBy(x => x.Item.InspectedAt)
                         .ThenBy(x => x.LinePolarity, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.CellId, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.CropFolder, StringComparer.OrdinalIgnoreCase))
            {
                Candidates.Add(item);
            }
            SelectedCandidate = Candidates.FirstOrDefault();
            Status = Candidates.Count == 0
                ? "No eligible DLNG crop items were found."
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

    private async Task GenerateReportAsync()
    {
        var selectedMachines = MachineOptions.Where(x => x.IsSelected).Select(x => x.Machine).ToArray();
        if (ReportDate is null || selectedMachines.Length == 0) return;
        IsBusy = true;
        SummaryRows.Clear();
        try
        {
            var reportDate = DateOnly.FromDateTime(ReportDate.Value);
            var progress = new Progress<string>(AddLog);
            var result = await _reports.GenerateAsync(selectedMachines, reportDate, progress, CancellationToken.None);
            foreach (var row in result.Rows) SummaryRows.Add(row);
            Status = $"DLNG report saved: {result.SummaryWorkbook}";
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
            if (item is null) return;
            var classes = item.Item.ModelKind is DlngModelKind.Segmentation or DlngModelKind.FallbackRaw
                ? _settings.DlngRules.SegmentationClasses
                : DlngRules.ClassesFor(item.Item.CropFolder, item.Item.Polarity, _settings, item.Item.Side);
            var selected = _reviewRecords.TryGetValue(item.Item.Key, out var saved)
                ? saved.FinalClass
                : string.Empty;
            foreach (var klass in classes)
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
            _restoringSelections = false;
        }
    }

    private void FinalClassOption_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_restoringSelections || e.PropertyName != nameof(DlngClassOption.IsSelected)) return;
        if (sender is DlngClassOption option && option.IsSelected)
        {
            foreach (var other in FinalClassOptions.Where(x => !ReferenceEquals(x, option)))
            {
                other.IsSelected = false;
            }
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private async void CommitSelection()
    {
        var item = SelectedCandidate;
        var selected = FinalClassOptions.FirstOrDefault(x => x.IsSelected)?.DisplayName;
        if (item is null || string.IsNullOrWhiteSpace(selected)) return;
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
            DateTimeOffset.Now);
        try
        {
            await _reviews.SaveAsync(record, CancellationToken.None);
            _reviewRecords[item.Item.Key] = record;
            item.ReviewStatus = "Saved";
            AddLog($"{item.LinePolarity} {item.CellId} {item.Defect}: classified as {selected}.");
            Next();
            RequestKeyboardFocus?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            item.ReviewStatus = "Save failed";
            Status = exception.Message;
        }
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
        if (option is not null) option.IsSelected = true;
    }

    private void SelectByPrefix(string prefix)
    {
        var option = FinalClassOptions.FirstOrDefault(x => x.DisplayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (option is not null) option.IsSelected = true;
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
