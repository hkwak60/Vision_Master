using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using KickoutMonitor.App.Services;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;
using Microsoft.Win32;

namespace KickoutMonitor.App.ViewModels;

public sealed class IrsCandidateItem : INotifyPropertyChanged
{
    private string _reviewStatus = "Pending";

    public IrsCandidateItem(IrsReviewCandidate candidate)
    {
        Candidate = candidate;
    }

    public IrsReviewCandidate Candidate { get; }
    public IrsDatasetItem? DatasetItem { get; }

    public IrsCandidateItem(IrsDatasetItem datasetItem)
    {
        DatasetItem = datasetItem;
        Candidate = new(
            datasetItem.Key,
            string.Empty,
            datasetItem.SourceFolder,
            datasetItem.LinePolarity,
            datasetItem.ProducedAt,
            string.Empty,
            datasetItem.CellId,
            datasetItem.CameraLocation,
            string.Empty,
            string.Empty,
            datasetItem.SecondReason,
            0,
            datasetItem.ImagePaths);
    }

    public string Time => Candidate.ProducedAt.ToString("HH:mm:ss");
    public string LinePolarity => Candidate.LinePolarity;
    public string CellId => Candidate.CellId;
    public string VisionType => Candidate.VisionType;
    public string Camera => Candidate.CameraLocation;
    public string SecondResult => Candidate.SecondResult;
    public string SecondReason => Candidate.SecondReason;
    public string ImageState => Candidate.RawImagePath is null ? "Missing" : "Ready";
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

public sealed class IrsPreviewItem : INotifyPropertyChanged
{
    private BitmapSource? _image;
    private string _state = "Loading";

    public IrsPreviewItem(string label, string? path)
    {
        Label = label;
        Path = path;
    }

    public string Label { get; }
    public string? Path { get; }

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

public sealed class IrsSelectionOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public IrsSelectionOption(IrsReviewSelection selection)
    {
        Selection = selection;
    }

    public IrsReviewSelection Selection { get; }
    public string Id => Selection.Id;
    public string DisplayName => Selection.DisplayName;
    public string ToolTip => Selection.Kind == IrsSelectionKind.Crop
        ? $"Fetch {Selection.DisplayName} crop images"
        : $"{Selection.DisplayName} classification";

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

public sealed class IrsReviewViewModel : INotifyPropertyChanged
{
    private readonly IrsReviewQueueService _queue;
    private readonly IPreviewImageLoader<BitmapSource> _images;
    private readonly IMachineRegistry _machines;
    private readonly IIrsReviewCommitService _commits;
    private readonly IIrsDatasetService _dataset;
    private readonly VisionMasterSettings _settings;
    private readonly Dictionary<string, IReadOnlyList<string>> _committedSelections = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _pendingFirstStageCommits = [];
    private readonly Dictionary<string, IrsDatasetDecision> _datasetDecisions = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _previewCancellation;
    private IrsCandidateItem? _selectedCandidate;
    private string _workbookPath = string.Empty;
    private string _status = "Ready";
    private bool _isBusy;
    private bool _restoringSelections;
    private bool _datasetMode;
    private IReadOnlyList<IrsReviewCandidate> _loadedCandidates = [];
    private IReadOnlyList<IrsReviewRecord> _loadedReviewRecords = [];
    private IReadOnlyList<IrsDatasetItem> _datasetItems = [];
    private int _currentImageIndex = -1;

    public IrsReviewViewModel(
        IrsReviewQueueService queue,
        IPreviewImageLoader<BitmapSource> images,
        IMachineRegistry machines,
        IIrsReviewCommitService commits,
        IIrsDatasetService dataset,
        VisionMasterSettings? settings = null)
    {
        _queue = queue;
        _images = images;
        _machines = machines;
        _commits = commits;
        _dataset = dataset;
        _settings = settings ?? VisionMasterSettings.CreateDefault();
        BrowseCommand = new(Browse);
        LoadCommand = new(LoadAsync, () => !IsBusy && File.Exists(WorkbookPath));
        PreviousCommand = new(Previous, CanPrevious);
        NextCommand = new(Next, CanNext);
        PreviousImageCommand = new(PreviousImageAsync, () => CurrentImageIndex > 0);
        NextImageCommand = new(
            NextImageAsync,
            () => CurrentImageIndex >= 0 && CurrentImageIndex < PreviewImages.Count - 1);
        CommitCommand = new(CommitSelection, CanCommitSelection);
        GenerateDatasetCommand = new(GenerateDatasetAsync, () => !IsBusy && Candidates.Count > 0);
        SummaryReportCommand = new(SummaryReportAsync, () => !IsBusy && _datasetMode && Candidates.Count > 0);
        SelectionOptions = _settings.IrsRules.FirstStageSelections
            .Select(option => Option(
                option.Id,
                option.DisplayName,
                option.CategoryFolder,
                option.Kind,
                option.MavinFolder,
                option.Token))
            .ToArray();
        foreach (var option in SelectionOptions)
        {
            option.PropertyChanged += SelectionOption_PropertyChanged;
        }
    }

    public ObservableCollection<IrsCandidateItem> Candidates { get; } = [];
    public ObservableCollection<IrsPreviewItem> PreviewImages { get; } = [];
    public ObservableCollection<string> ActivityLog { get; } = [];
    public RelayCommand BrowseCommand { get; }
    public AsyncRelayCommand LoadCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand NextCommand { get; }
    public AsyncRelayCommand PreviousImageCommand { get; }
    public AsyncRelayCommand NextImageCommand { get; }
    public RelayCommand CommitCommand { get; }
    public AsyncRelayCommand GenerateDatasetCommand { get; }
    public AsyncRelayCommand SummaryReportCommand { get; }
    public ObservableCollection<IrsSelectionOption> FinalClassOptions { get; } = [];
    public IReadOnlyList<IrsSelectionOption> SelectionOptions { get; }
    public IReadOnlyList<IrsSelectionOption> TopClassifications => SelectionOptions.Take(2).ToArray();
    public IReadOnlyList<IrsSelectionOption> ASelections => SelectionOptions.Where(x => x.Id is "A_L" or "A_R").ToArray();
    public IReadOnlyList<IrsSelectionOption> BSelections => SelectionOptions.Where(x => x.Id is "B_L" or "B_R").ToArray();
    public IReadOnlyList<IrsSelectionOption> MicroSelections => SelectionOptions.Where(x => x.Id.StartsWith("MICRO_", StringComparison.Ordinal)).ToArray();
    public IReadOnlyList<IrsSelectionOption> TabsideSelections => SelectionOptions.Where(x => x.Id.StartsWith("TABSIDE_", StringComparison.Ordinal)).ToArray();
    public IReadOnlyList<IrsSelectionOption> GapSelections => SelectionOptions.Where(x => x.Id == "GAP").ToArray();
    public IReadOnlyList<IrsSelectionOption> SepaSelections => SelectionOptions.Where(x => x.Id == "SEPA").ToArray();
    public IReadOnlyList<IrsSelectionOption> SepaShoulderSelections => SelectionOptions.Where(x => x.Id.StartsWith("SEPA_SHOULDER_", StringComparison.Ordinal)).ToArray();
    public string SelectionPanelTitle => _datasetMode ? "Final Class" : "IRS Selection";
    public Visibility FirstStageSelectionVisibility => _datasetMode ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FinalClassVisibility => _datasetMode ? Visibility.Visible : Visibility.Collapsed;

    public string WorkbookPath
    {
        get => _workbookPath;
        set
        {
            if (!Set(ref _workbookPath, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public IrsCandidateItem? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (!Set(ref _selectedCandidate, value)) return;
            _ = LoadPreviewsAsync(value);
            RestoreSelections(value);
            ConfigureFinalClassOptions(value);
            NotifyModeVisualsChanged();
            OnPropertyChanged(nameof(SelectedIndex));
            OnPropertyChanged(nameof(PositionText));
            OnPropertyChanged(nameof(ReasonOverlay));
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
    public string ReasonOverlay => SelectedCandidate?.SecondReason ?? string.Empty;

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

    public IrsPreviewItem? CurrentPreview =>
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
        }

        if (_datasetMode)
        {
            switch (key)
            {
                case Key.R:
                    SelectFinalByDisplay("Real");
                    break;
                case Key.N:
                    SelectFinalByDisplay("No Need to Retrain");
                    break;
                case Key.Enter when CommitCommand.CanExecute(null):
                    CommitCommand.Execute(null);
                    break;
                case Key.D1 or Key.NumPad1:
                    SelectFinalByPrefix("01");
                    break;
                case Key.D2 or Key.NumPad2:
                    SelectFinalByPrefix("02");
                    break;
                case Key.D3 or Key.NumPad3:
                    SelectFinalByPrefix("03");
                    break;
                case Key.D4 or Key.NumPad4:
                    SelectFinalByPrefix("04");
                    break;
                case Key.D5 or Key.NumPad5:
                    SelectFinalByPrefix("05");
                    break;
                case Key.D6 or Key.NumPad6:
                    SelectFinalByPrefix("06");
                    break;
            }

            return;
        }

        switch (key)
        {
            case Key.R:
                SelectExclusive("RULEBASE");
                break;
            case Key.U:
                SelectExclusive("UNDETECTABLE");
                break;
            case Key.Enter when CommitCommand.CanExecute(null):
                CommitCommand.Execute(null);
                break;
        }
    }
    private void Browse()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select IRS workbook",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            WorkbookPath = dialog.FileName;
        }
    }

    private async Task LoadAsync()
    {
        if (!File.Exists(WorkbookPath)) return;
        IsBusy = true;
        Candidates.Clear();
        ClearPreviews();
        ActivityLog.Clear();
        try
        {
            AddLog($"Loading IRS workbook: {WorkbookPath}");
            var progress = new Progress<string>(AddLog);
            _datasetMode = false;
            NotifyModeVisualsChanged();
            var records = await _queue.LoadAsync(WorkbookPath, progress, CancellationToken.None);
            _loadedCandidates = records;
            var committed = await _commits.LoadRecordsAsync(CancellationToken.None);
            _loadedReviewRecords = committed;
            _committedSelections.Clear();
            foreach (var record in committed)
            {
                _committedSelections[record.Key] = record.Selections;
            }

            var queueItems = records.Select(record =>
            {
                var item = new IrsCandidateItem(record);
                if (_committedSelections.ContainsKey(record.Key))
                {
                    item.ReviewStatus = "Saved";
                }
                return item;
            });
            foreach (var item in queueItems
                         .OrderBy(IsUnclassified)
                         .ThenBy(item => item.Candidate.ProducedAt)
                         .ThenBy(item => item.LinePolarity, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.CellId, StringComparer.OrdinalIgnoreCase))
            {
                Candidates.Add(item);
            }
            SelectedCandidate = Candidates.FirstOrDefault();
            Status = Candidates.Count == 0
                ? "No requested IRS rows were found."
                : $"Loaded {Candidates.Count:N0} IRS review row(s).";
            AddLog($"IRS review queue ready: {Candidates.Count:N0} row(s).");
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            AddLog($"IRS load failed: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadPreviewsAsync(IrsCandidateItem? item)
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new();
        var token = _previewCancellation.Token;
        ClearPreviews();
        if (item is null) return;
        var paths = item.DatasetItem?.ImagePaths ?? item.Candidate.RawImagePaths ?? [];
        if (paths.Count == 0)
        {
            PreviewImages.Add(new("Raw", null));
        }
        else
        {
            for (var index = 0; index < paths.Count; index++)
            {
                PreviewImages.Add(new($"Raw {index + 1}", paths[index]));
            }
        }
        CurrentImageIndex = PreviewImages.Count > 0 ? 0 : -1;
        await LoadCurrentPreviewAsync(token);
    }

    private static bool IsUnclassified(IrsCandidateItem item) =>
        !item.ReviewStatus.StartsWith("Saved", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<IrsCandidateItem> DisplayedCandidates()
    {
        var view = CollectionViewSource.GetDefaultView(Candidates);
        return view is null ? Candidates.ToArray() : view.Cast<IrsCandidateItem>().ToArray();
    }

    private int DisplayedIndexOf(IrsCandidateItem? item)
    {
        if (item is null) return -1;
        return DisplayedIndexOf(item, DisplayedCandidates());
    }

    private static int DisplayedIndexOf(IrsCandidateItem? item, IReadOnlyList<IrsCandidateItem> displayed)
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
        if (index >= 0 && index < displayed.Count - 1)
        {
            SelectedCandidate = displayed[index + 1];
        }
    }

    private void CommitSelection()
    {
        var item = SelectedCandidate;
        if (item is null) return;
        if (!TryGetMachine(item.Candidate, out var machine))
        {
            AddLog($"Commit skipped: machine not found for {item.LinePolarity}.");
            return;
        }

        if (_datasetMode)
        {
            CommitDatasetSelection();
            return;
        }

        var selections = SelectionOptions
            .Where(x => x.IsSelected)
            .Select(x => x.Selection)
            .ToArray();
        if (selections.Length == 0) return;

        item.ReviewStatus = "Copying";
        _committedSelections[item.Candidate.Key] = selections.Select(x => x.Id).ToArray();
        AddLog(
            $"{item.LinePolarity} {item.CellId}: committed {string.Join(", ", selections.Select(x => x.DisplayName))}; copy queued.");
        var request = new IrsReviewCommitRequest(machine, item.Candidate, selections);
        var copyTask = Task.Run(async () =>
        {
            try
            {
                var result = await _commits.CommitAsync(request, CancellationToken.None);
                RunOnUi(() =>
                {
                    item.ReviewStatus = result.MissingFiles == 0 ? "Saved" : "Saved with missing";
                    AddLog($"{item.LinePolarity} {item.CellId}: {result.Message}");
                });
            }
            catch (Exception exception)
            {
                RunOnUi(() =>
                {
                    item.ReviewStatus = "Copy failed";
                    AddLog($"{item.LinePolarity} {item.CellId}: copy failed - {exception.Message}");
                });
            }
        });
        TrackFirstStageCommit(copyTask);

        ClearSelections();
        Next();
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

        if (string.IsNullOrWhiteSpace(current.Path))
        {
            current.State = "Raw image not found";
            PreviewImageLoaded?.Invoke(this, EventArgs.Empty);
            return;
        }

        current.State = "Loading";
        var image = await _images.LoadAsync(current.Path, 1600, cancellationToken);
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

    private bool CanCommitSelection() =>
        SelectedCandidate is not null && (_datasetMode ? FinalClassOptions.Any(x => x.IsSelected) : SelectionOptions.Any(x => x.IsSelected));

    private void SelectionOption_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_datasetMode)
        {
            if (_restoringSelections || e.PropertyName != nameof(IrsSelectionOption.IsSelected)) return;
            if (sender is IrsSelectionOption datasetOption && datasetOption.IsSelected)
            {
                if (SelectedCandidate?.DatasetItem?.IsNeedToSimulate != true)
                {
                    foreach (var other in FinalClassOptions.Where(x => !ReferenceEquals(x, datasetOption))) other.IsSelected = false;
                    CommitDatasetSelection();
                }
            }
            CommandManager.InvalidateRequerySuggested();
            return;
        }
        if (_restoringSelections) return;
        if (e.PropertyName != nameof(IrsSelectionOption.IsSelected)) return;
        if (sender is not IrsSelectionOption option || !option.IsSelected)
        {
            CommandManager.InvalidateRequerySuggested();
            return;
        }

        if (option.Selection.Kind is IrsSelectionKind.Rulebase or IrsSelectionKind.Undetectable)
        {
            foreach (var other in SelectionOptions.Where(x => !ReferenceEquals(x, option)))
            {
                other.IsSelected = false;
            }
        }
        else
        {
            foreach (var other in SelectionOptions.Where(x =>
                         x.Selection.Kind is IrsSelectionKind.Rulebase or IrsSelectionKind.Undetectable))
            {
                other.IsSelected = false;
            }
        }

        CommandManager.InvalidateRequerySuggested();
    }


    private void SelectFinalByDisplay(string display)
    {
        var option = FinalClassOptions.FirstOrDefault(x => x.DisplayName.Equals(display, StringComparison.OrdinalIgnoreCase));
        if (option is not null) option.IsSelected = true;
    }

    private void SelectFinalByPrefix(string prefix)
    {
        var option = FinalClassOptions.FirstOrDefault(x => x.DisplayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (option is not null) option.IsSelected = true;
    }
    private void SelectExclusive(string id)
    {
        var option = SelectionOptions.FirstOrDefault(x => x.Id == id);
        if (option is null) return;
        option.IsSelected = !option.IsSelected;
    }

    private void ClearSelections()
    {
        _restoringSelections = true;
        foreach (var option in SelectionOptions)
        {
            option.IsSelected = false;
        }
        _restoringSelections = false;
        CommandManager.InvalidateRequerySuggested();
    }

    private void RestoreSelections(IrsCandidateItem? item)
    {
        _restoringSelections = true;
        try
        {
            var selected = item is not null
                && _committedSelections.TryGetValue(item.Candidate.Key, out var ids)
                ? ids.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in SelectionOptions)
            {
                option.IsSelected = selected.Contains(option.Id);
            }
        }
        finally
        {
            _restoringSelections = false;
        }

        CommandManager.InvalidateRequerySuggested();
    }

    private bool TryGetMachine(IrsReviewCandidate candidate, out WeldingMachine machine)
    {
        machine = _machines.All.FirstOrDefault(x =>
            x.OutputFolderName.Equals(candidate.LinePolarity, StringComparison.OrdinalIgnoreCase))!;
        return machine is not null;
    }

    private static IrsSelectionOption Option(
        string id,
        string displayName,
        string categoryFolder,
        IrsSelectionKind kind,
        string? mavinFolder,
        string? token) =>
        new(new(id, displayName, categoryFolder, kind, mavinFolder, token));

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }
    private void NotifyModeVisualsChanged()
    {
        OnPropertyChanged(nameof(SelectionPanelTitle));
        OnPropertyChanged(nameof(FirstStageSelectionVisibility));
        OnPropertyChanged(nameof(FinalClassVisibility));
    }

    private void TrackFirstStageCommit(Task task)
    {
        lock (_pendingFirstStageCommits)
        {
            _pendingFirstStageCommits.RemoveAll(x => x.IsCompleted);
            _pendingFirstStageCommits.Add(task);
        }
    }

    private async Task WaitForFirstStageCommitsAsync()
    {
        Task[] pending;
        lock (_pendingFirstStageCommits)
        {
            _pendingFirstStageCommits.RemoveAll(x => x.IsCompleted);
            pending = _pendingFirstStageCommits.ToArray();
        }

        if (pending.Length == 0) return;
        AddLog($"Waiting for {pending.Length:N0} queued IRS copy job(s) before dataset generation.");
        await Task.WhenAll(pending);
        lock (_pendingFirstStageCommits)
        {
            _pendingFirstStageCommits.RemoveAll(x => x.IsCompleted);
        }
    }
    private async Task GenerateDatasetAsync()
    {
        if (_loadedCandidates.Count == 0) return;
        IsBusy = true;
        try
        {
            await WaitForFirstStageCommitsAsync();
            var records = await _commits.LoadRecordsAsync(CancellationToken.None);
            _loadedReviewRecords = records;
            var reviewed = records.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = _loadedCandidates.Where(x => !reviewed.Contains(x.Key)).ToArray();
            if (missing.Length > 0)
            {
                Status = $"Cannot generate dataset: {missing.Length:N0} IRS row(s) are not reviewed.";
                AddLog(Status);
                LogMissingFirstStageRows(missing);
                return;
            }

            var items = await _dataset.BuildQueueAsync(_loadedCandidates, records, CancellationToken.None);
            var decisions = await _dataset.LoadDecisionsAsync(CancellationToken.None);
            _datasetDecisions.Clear();
            foreach (var decision in decisions.Values)
            {
                _datasetDecisions[decision.ItemKey] = decision;
            }
            _datasetItems = items;
            Candidates.Clear();
            ClearPreviews();
            _datasetMode = true;
            NotifyModeVisualsChanged();
            var queueItems = items.Select(datasetItem => new IrsCandidateItem(datasetItem)
            {
                ReviewStatus = decisions.ContainsKey(datasetItem.Key) ? "Saved" : "Pending"
            });
            foreach (var item in queueItems
                         .OrderBy(IsUnclassified)
                         .ThenBy(item => item.Candidate.ProducedAt)
                         .ThenBy(item => item.LinePolarity, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.CellId, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.Candidate.SecondReason, StringComparer.OrdinalIgnoreCase))
            {
                Candidates.Add(item);
            }
            SelectedCandidate = Candidates.FirstOrDefault();
            Status = $"Dataset review queue ready: {Candidates.Count:N0} item(s).";
            AddLog(Status);
            NotifyModeVisualsChanged();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            AddLog($"Generate Dataset failed: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LogMissingFirstStageRows(IReadOnlyList<IrsReviewCandidate> missing)
    {
        var missingKeys = missing.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Candidates.Where(x => missingKeys.Contains(x.Candidate.Key)))
        {
            item.ReviewStatus = "Missing";
        }

        SelectedCandidate = Candidates.FirstOrDefault(x => missingKeys.Contains(x.Candidate.Key))
            ?? SelectedCandidate;

        foreach (var candidate in missing.Take(25))
        {
            AddLog(
                $"Missing IRS first-stage review: {candidate.LinePolarity} " +
                $"{candidate.ProducedAt:yyyy-MM-dd HH:mm:ss} cell={candidate.CellId} " +
                $"camera={candidate.CameraLocation} result={candidate.SecondResult} reason={candidate.SecondReason}.");
        }

        if (missing.Count > 25)
        {
            AddLog($"Missing IRS first-stage review: {missing.Count - 25:N0} additional row(s) not shown.");
        }
    }

    private async Task SummaryReportAsync()
    {
        if (!_datasetMode || _datasetItems.Count == 0) return;
        IsBusy = true;
        try
        {
            var decisions = await _dataset.LoadDecisionsAsync(CancellationToken.None);
            var missing = _datasetItems.Count(x => !x.IsNeedToSimulate && !decisions.ContainsKey(x.Key));
            if (missing > 0)
            {
                Status = $"Cannot generate summary: {missing:N0} dataset item(s) are not classified.";
                AddLog(Status);
                return;
            }

            var result = await _dataset.WriteSummaryAsync(_loadedCandidates, _loadedReviewRecords, _datasetItems, CancellationToken.None);
            Status = $"IRS summary generated: {result.OutputFolder}";
            AddLog(Status);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            AddLog($"Summary Report failed: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ConfigureFinalClassOptions(IrsCandidateItem? item)
    {
        _restoringSelections = true;
        try
        {
            FinalClassOptions.Clear();
            if (!_datasetMode || item?.DatasetItem is null) return;
            var selectedClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_datasetDecisions.TryGetValue(item.DatasetItem.Key, out var decision))
            {
                if (decision.NoNeedToRetrain)
                {
                    selectedClasses.Add("No Need to Retrain");
                }
                else
                {
                    foreach (var finalClass in decision.FinalClasses)
                    {
                        selectedClasses.Add(finalClass);
                    }
                }
            }

            foreach (var klass in FilterFinalClasses(item.DatasetItem))
            {
                var id = klass.Equals("No Need to Retrain", StringComparison.OrdinalIgnoreCase)
                    ? "NO_NEED"
                    : klass;
                var option = new IrsSelectionOption(new(id, klass, klass, IrsSelectionKind.Crop, null, null))
                {
                    IsSelected = selectedClasses.Contains(klass)
                };
                option.PropertyChanged += SelectionOption_PropertyChanged;
                FinalClassOptions.Add(option);
            }
        }
        finally
        {
            _restoringSelections = false;
        }
    }

    internal static IReadOnlyList<string> FilterFinalClasses(IrsDatasetItem item)
    {
        if (!item.SourceFolder.Equals("Crop_A", StringComparison.OrdinalIgnoreCase))
        {
            return item.AllowedClasses;
        }

        return item.AllowedClasses
            .Where(klass => CropAClassMatchesCamera(klass, item.CameraLocation))
            .ToArray();
    }

    private static bool CropAClassMatchesCamera(string klass, string cameraLocation)
    {
        var isTop = cameraLocation.Trim().Equals("TOP", StringComparison.OrdinalIgnoreCase);
        var isBottom = cameraLocation.Trim().Equals("BTM", StringComparison.OrdinalIgnoreCase)
            || cameraLocation.Trim().Equals("BOTTOM", StringComparison.OrdinalIgnoreCase);
        if (isTop && klass.Contains("_OK_BACK_", StringComparison.OrdinalIgnoreCase)) return false;
        if (isBottom && klass.Contains("_OK_TOP_", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private void CommitDatasetSelection()
    {
        var item = SelectedCandidate;
        if (item?.DatasetItem is null) return;
        var selected = FinalClassOptions.Where(x => x.IsSelected).Select(x => x.DisplayName).ToArray();
        if (selected.Length == 0) return;
        var noNeed = selected.Any(x => x.Equals("No Need to Retrain", StringComparison.OrdinalIgnoreCase));
        var finalClasses = noNeed ? Array.Empty<string>() : selected;
        item.ReviewStatus = "Saved";
        _datasetDecisions[item.DatasetItem.Key] = new(
            item.DatasetItem.Key,
            item.DatasetItem.SourceReviewKey,
            item.DatasetItem.LinePolarity,
            item.DatasetItem.ProducedAt,
            item.DatasetItem.CellId,
            item.DatasetItem.SourceFolder,
            item.DatasetItem.OriginalClass,
            finalClasses,
            noNeed,
            DateTimeOffset.Now);
        AddLog($"{item.LinePolarity} {item.CellId}: dataset classified as {string.Join(", ", selected)}.");
        _ = Task.Run(async () =>
        {
            try
            {
                await _dataset.SaveDecisionAsync(item.DatasetItem, finalClasses, noNeed, CancellationToken.None);
            }
            catch (Exception exception)
            {
                RunOnUi(() =>
                {
                    item.ReviewStatus = "Copy failed";
                    AddLog($"{item.LinePolarity} {item.CellId}: dataset decision failed - {exception.Message}");
                });
            }
        });
        ClearFinalSelections();
        Next();
        RequestKeyboardFocus?.Invoke(this, EventArgs.Empty);
    }

    private void ClearFinalSelections()
    {
        _restoringSelections = true;
        foreach (var option in FinalClassOptions) option.IsSelected = false;
        _restoringSelections = false;
        CommandManager.InvalidateRequerySuggested();
    }
    private void AddLog(string message) =>
        ActivityLog.Add($"[{DateTime.Now:HH:mm:ss}] {message}");

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
