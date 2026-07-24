using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.App.ViewModels;

public sealed class FlaggedReviewViewModel : INotifyPropertyChanged
{
    private readonly IFlaggedReviewService _flags;
    private readonly IMachineRegistry _machines;
    private readonly IIrsReviewCommitService _commits;
    private readonly IIrsDatasetService _dataset;
    private readonly IPreviewImageLoader<BitmapSource> _images;
    private readonly VisionMasterSettings _settings;
    private readonly List<Task> _pendingFirstStageCommits = [];
    private readonly Dictionary<string, IrsDatasetDecision> _datasetDecisions = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _previewCancellation;
    private IrsCandidateItem? _selectedCandidate;
    private IReadOnlyList<FlaggedItem> _loadedFlags = [];
    private IReadOnlyList<IrsReviewCandidate> _loadedCandidates = [];
    private IReadOnlyList<IrsReviewRecord> _loadedReviewRecords = [];
    private IReadOnlyList<IrsDatasetItem> _datasetItems = [];
    private string _status = "Ready";
    private bool _isBusy;
    private bool _datasetMode;
    private bool _previousMode;
    private bool _restoringSelections;
    private int _currentImageIndex = -1;

    public FlaggedReviewViewModel(
        IFlaggedReviewService flags,
        IMachineRegistry machines,
        IIrsReviewCommitService commits,
        IIrsDatasetService dataset,
        IPreviewImageLoader<BitmapSource> images,
        VisionMasterSettings? settings = null)
    {
        _flags = flags;
        _machines = machines;
        _commits = commits;
        _dataset = dataset;
        _images = images;
        _settings = settings ?? VisionMasterSettings.CreateDefault();
        LoadFlaggedCommand = new(() => LoadAsync(false), () => !IsBusy);
        LoadPreviousCommand = new(() => LoadAsync(true), () => !IsBusy);
        GenerateDatasetCommand = new(GenerateDatasetAsync, () => !IsBusy && !_previousMode && Candidates.Count > 0);
        GenerateSummaryCommand = new(GenerateSummaryAsync, () => !IsBusy && !_previousMode && _datasetMode && _loadedCandidates.Count > 0);
        CommitCommand = new(CommitSelection, CanCommitSelection);
        PreviousCommand = new(Previous, CanPrevious);
        NextCommand = new(Next, CanNext);
        PreviousImageCommand = new(PreviousImageAsync, () => CurrentImageIndex > 0);
        NextImageCommand = new(NextImageAsync, () => CurrentImageIndex >= 0 && CurrentImageIndex < PreviewImages.Count - 1);
        SelectionOptions = _settings.IrsRules.FirstStageSelections.Select(Option).ToArray();
        foreach (var option in SelectionOptions) option.PropertyChanged += SelectionOption_PropertyChanged;
    }

    public ObservableCollection<IrsCandidateItem> Candidates { get; } = [];
    public ObservableCollection<IrsPreviewItem> PreviewImages { get; } = [];
    public ObservableCollection<string> ActivityLog { get; } = [];
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
    public Visibility FirstStageSelectionVisibility => _datasetMode ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FinalClassVisibility => _datasetMode ? Visibility.Visible : Visibility.Collapsed;
    public string SelectionPanelTitle => _datasetMode ? "Final Class" : "Flag Decision";
    public AsyncRelayCommand LoadFlaggedCommand { get; }
    public AsyncRelayCommand LoadPreviousCommand { get; }
    public AsyncRelayCommand GenerateDatasetCommand { get; }
    public AsyncRelayCommand GenerateSummaryCommand { get; }
    public RelayCommand CommitCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand NextCommand { get; }
    public AsyncRelayCommand PreviousImageCommand { get; }
    public AsyncRelayCommand NextImageCommand { get; }

    public IrsCandidateItem? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (!Set(ref _selectedCandidate, value)) return;
            _ = LoadPreviewsAsync(value);
            ConfigureFinalClassOptions(value);
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

    public IrsPreviewItem? CurrentPreview =>
        CurrentImageIndex >= 0 && CurrentImageIndex < PreviewImages.Count ? PreviewImages[CurrentImageIndex] : null;
    public string ImagePositionText => CurrentImageIndex < 0 ? "0 / 0" : $"{CurrentImageIndex + 1} / {PreviewImages.Count}";
    public string ReasonOverlay => SelectedCandidate?.SecondReason ?? string.Empty;

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
            case Key.Enter when CommitCommand.CanExecute(null):
                CommitCommand.Execute(null);
                return;
        }

        if (_datasetMode)
        {
            switch (key)
            {
                case Key.N:
                    SelectFinalByDisplay("No Need to Retrain");
                    return;
                case Key.D1 or Key.NumPad1:
                    SelectFinalByPrefix("01");
                    return;
                case Key.D2 or Key.NumPad2:
                    SelectFinalByPrefix("02");
                    return;
                case Key.D3 or Key.NumPad3:
                    SelectFinalByPrefix("03");
                    return;
                case Key.D4 or Key.NumPad4:
                    SelectFinalByPrefix("04");
                    return;
                case Key.D5 or Key.NumPad5:
                    SelectFinalByPrefix("05");
                    return;
                case Key.D6 or Key.NumPad6:
                    SelectFinalByPrefix("06");
                    return;
            }
        }
        else
        {
            switch (key)
            {
                case Key.R:
                    SelectExclusive("RULEBASE");
                    return;
                case Key.U:
                    SelectExclusive("UNDETECTABLE");
                    return;
            }
        }
    }

    private async Task LoadAsync(bool summarized)
    {
        IsBusy = true;
        try
        {
            _previousMode = summarized;
            _datasetMode = false;
            _datasetItems = [];
            _loadedReviewRecords = [];
            _datasetDecisions.Clear();
            Candidates.Clear();
            ClearPreviews();
            NotifyModeVisualsChanged();
            _loadedFlags = await _flags.LoadAsync(summarized, CancellationToken.None);
            _loadedCandidates = await _flags.BuildCandidatesAsync(_loadedFlags, CancellationToken.None);
            var records = await _commits.LoadRecordsAsync(CancellationToken.None);
            foreach (var candidate in _loadedCandidates)
            {
                var item = new IrsCandidateItem(candidate)
                {
                    ReviewStatus = records.Any(x => x.Key.Equals(candidate.Key, StringComparison.OrdinalIgnoreCase))
                        ? "Saved"
                        : summarized ? "Summarized" : "Pending"
                };
                Candidates.Add(item);
            }

            SelectedCandidate = Candidates.FirstOrDefault();
            Status = summarized
                ? $"Loaded {Candidates.Count:N0} previously flagged item(s)."
                : $"Loaded {Candidates.Count:N0} new flagged item(s).";
            AddLog(Status);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            AddLog($"Flagged queue load failed: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CommitSelection()
    {
        if (_previousMode) return;
        if (_datasetMode)
        {
            CommitDatasetSelection();
            return;
        }

        var item = SelectedCandidate;
        if (item is null) return;
        if (!TryGetMachine(item.Candidate, out var machine))
        {
            AddLog($"Commit skipped: machine not found for {item.LinePolarity}.");
            return;
        }

        var selections = SelectionOptions.Where(x => x.IsSelected).Select(x => x.Selection).ToArray();
        if (selections.Length == 0) return;
        item.ReviewStatus = "Copying";
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

    private async Task GenerateDatasetAsync()
    {
        if (_loadedCandidates.Count == 0 || _previousMode) return;
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
                Status = $"Cannot generate dataset: {missing.Length:N0} flagged item(s) are not reviewed.";
                AddLog(Status);
                return;
            }

            var items = await _dataset.BuildQueueAsync(_loadedCandidates, records, CancellationToken.None);
            var decisions = await _dataset.LoadDecisionsAsync(CancellationToken.None);
            _datasetDecisions.Clear();
            foreach (var decision in decisions.Values) _datasetDecisions[decision.ItemKey] = decision;
            _datasetItems = items;
            Candidates.Clear();
            ClearPreviews();
            _datasetMode = true;
            NotifyModeVisualsChanged();
            foreach (var item in items.Select(datasetItem => new IrsCandidateItem(datasetItem)
                     {
                         ReviewStatus = decisions.ContainsKey(datasetItem.Key) ? "Saved" : "Pending"
                     })
                     .OrderBy(IsUnclassified)
                     .ThenBy(x => x.Candidate.ProducedAt)
                     .ThenBy(x => x.LinePolarity, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.CellId, StringComparer.OrdinalIgnoreCase))
            {
                Candidates.Add(item);
            }

            SelectedCandidate = Candidates.FirstOrDefault();
            Status = Candidates.Count == 0
                ? "No crop dataset items were created. Rulebase/Undetectable-only summary can be generated now."
                : $"Flagged dataset queue ready: {Candidates.Count:N0} item(s).";
            AddLog(Status);
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

    private async Task GenerateSummaryAsync()
    {
        if (!_datasetMode || _previousMode) return;
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

            var result = await _flags.WriteSummaryAsync(_loadedFlags, _loadedCandidates, _loadedReviewRecords, _datasetItems, CancellationToken.None);
            Status = $"Flagged summary generated: {result.OutputFolder}";
            AddLog(Status);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            AddLog($"Generate Summary failed: {exception.Message}");
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
        for (var index = 0; index < paths.Count; index++)
        {
            PreviewImages.Add(new($"Image {index + 1}", paths[index]));
        }

        if (PreviewImages.Count == 0) PreviewImages.Add(new("Raw image not found", null));
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

    private bool CanCommitSelection() =>
        !_previousMode
        && SelectedCandidate is not null
        && (_datasetMode ? FinalClassOptions.Any(x => x.IsSelected) : SelectionOptions.Any(x => x.IsSelected));

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
                if (decision.NoNeedToRetrain) selectedClasses.Add("No Need to Retrain");
                else foreach (var finalClass in decision.FinalClasses) selectedClasses.Add(finalClass);
            }

            foreach (var klass in IrsReviewViewModel.FilterFinalClasses(item.DatasetItem))
            {
                var id = klass.Equals("No Need to Retrain", StringComparison.OrdinalIgnoreCase) ? "NO_NEED" : klass;
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
    }

    private void SelectionOption_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_restoringSelections || e.PropertyName != nameof(IrsSelectionOption.IsSelected)) return;
        if (_datasetMode)
        {
            if (sender is IrsSelectionOption option && option.IsSelected)
            {
                if (SelectedCandidate?.DatasetItem?.IsNeedToSimulate != true)
                {
                    foreach (var other in FinalClassOptions.Where(x => !ReferenceEquals(x, option))) other.IsSelected = false;
                    CommitDatasetSelection();
                }
            }
            CommandManager.InvalidateRequerySuggested();
            return;
        }

        if (sender is not IrsSelectionOption selection || !selection.IsSelected)
        {
            CommandManager.InvalidateRequerySuggested();
            return;
        }

        if (selection.Selection.Kind is IrsSelectionKind.Rulebase or IrsSelectionKind.Undetectable)
        {
            foreach (var other in SelectionOptions.Where(x => !ReferenceEquals(x, selection))) other.IsSelected = false;
        }
        else
        {
            foreach (var other in SelectionOptions.Where(x => x.Selection.Kind is IrsSelectionKind.Rulebase or IrsSelectionKind.Undetectable))
            {
                other.IsSelected = false;
            }
        }

        CommandManager.InvalidateRequerySuggested();
    }

    private void SelectExclusive(string id)
    {
        var option = SelectionOptions.FirstOrDefault(x => x.Id == id);
        if (option is not null) option.IsSelected = !option.IsSelected;
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

    private bool CanPrevious() => DisplayedIndexOf(SelectedCandidate) > 0;
    private bool CanNext()
    {
        var displayed = DisplayedCandidates();
        var index = DisplayedIndexOf(SelectedCandidate, displayed);
        return index >= 0 && index < displayed.Count - 1;
    }

    private IReadOnlyList<IrsCandidateItem> DisplayedCandidates()
    {
        var view = CollectionViewSource.GetDefaultView(Candidates);
        return view is null ? Candidates.ToArray() : view.Cast<IrsCandidateItem>().ToArray();
    }

    private int DisplayedIndexOf(IrsCandidateItem? item) => DisplayedIndexOf(item, DisplayedCandidates());
    private static int DisplayedIndexOf(IrsCandidateItem? item, IReadOnlyList<IrsCandidateItem> displayed)
    {
        if (item is null) return -1;
        for (var index = 0; index < displayed.Count; index++)
        {
            if (ReferenceEquals(displayed[index], item)) return index;
        }
        return -1;
    }

    private static bool IsUnclassified(IrsCandidateItem item) =>
        !item.ReviewStatus.StartsWith("Saved", StringComparison.OrdinalIgnoreCase);

    private bool TryGetMachine(IrsReviewCandidate candidate, out WeldingMachine machine)
    {
        var match = _machines.All.FirstOrDefault(x => x.OutputFolderName.Equals(candidate.LinePolarity, StringComparison.OrdinalIgnoreCase));
        machine = match!;
        return match is not null;
    }

    private void ClearPreviews()
    {
        PreviewImages.Clear();
        CurrentImageIndex = -1;
        OnPropertyChanged(nameof(CurrentPreview));
        OnPropertyChanged(nameof(ImagePositionText));
    }

    private void ClearSelections()
    {
        _restoringSelections = true;
        try
        {
            foreach (var option in SelectionOptions) option.IsSelected = false;
        }
        finally
        {
            _restoringSelections = false;
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private void ClearFinalSelections()
    {
        _restoringSelections = true;
        try
        {
            foreach (var option in FinalClassOptions) option.IsSelected = false;
        }
        finally
        {
            _restoringSelections = false;
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private void NotifyModeVisualsChanged()
    {
        OnPropertyChanged(nameof(FirstStageSelectionVisibility));
        OnPropertyChanged(nameof(FinalClassVisibility));
        OnPropertyChanged(nameof(SelectionPanelTitle));
        CommandManager.InvalidateRequerySuggested();
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
        AddLog($"Waiting for {pending.Length:N0} queued copy job(s) before dataset generation.");
        await Task.WhenAll(pending);
        lock (_pendingFirstStageCommits)
        {
            _pendingFirstStageCommits.RemoveAll(x => x.IsCompleted);
        }
    }

    private static IrsSelectionOption Option(IrsFirstStageSelectionSetting option) =>
        new(new(
            option.Id,
            option.DisplayName,
            option.CategoryFolder,
            option.Kind,
            option.MavinFolder,
            option.Token));

    private void AddLog(string message) => ActivityLog.Add($"[{DateTime.Now:HH:mm:ss}] {message}");

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
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
