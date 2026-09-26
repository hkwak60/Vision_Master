using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;
using KickoutMonitor.Infrastructure;

namespace KickoutMonitor.App.ViewModels;

public sealed record CollectionCount(string Product, string Crop, string Line, string FinalClass, int Samples, int Files);
public sealed class OverkillMonitorViewModel : INotifyPropertyChanged
{
    private readonly OverkillHistoryService _history;
    private readonly TrainingCollectionService _collection;
    private readonly IDlngReviewStore _reviews;
    private IReadOnlyList<KickoutHistorySnapshot> _kickout = [];
    private IReadOnlyList<DlngReviewRecord> _dlng = [];
    private IReadOnlyList<TrainingBatch> _batches = [];
    private bool _busy;
    private string _status = "";
    private DateTime? _start = OverkillTrendService.RecentSeven(DateTime.Today).Start, _end = DateTime.Today;
    private int _activeTab;
    private bool _detailsOpen;
    private string _detailTitle = "";
    private TrainingBatch? _selectedBatch;
    private TrainingSample? _selectedSample;
    public OverkillMonitorViewModel(OverkillHistoryService history, TrainingCollectionService collection, IDlngReviewStore reviews)
    {
        _history = history; _collection = collection; _reviews = reviews;
        RefreshCommand = new(RefreshAsync, () => !_busy);
        RecentSevenCommand = new(() =>
        {
            var range = OverkillTrendService.RecentSeven(DateTime.Today);
            _start = range.Start; _end = range.End;
            PropertyChanged?.Invoke(this, new(nameof(StartDate))); PropertyChanged?.Invoke(this, new(nameof(EndDate)));
            Filter();
        });
        CloseDetailsCommand = new(() => DetailsOpen = false);
        RetryCommand = new(RetryAsync, () => !_busy);
        GenerateDatasetCommand = new(GenerateDatasetAsync, () => !_busy && SelectedBatch is not null);
        ExcludeCommand = new(ExcludeAsync, () => !_busy && SelectedBatch is { TrainedAt: null } && SelectedSample is not null);
        Kickout.DetailRequested += ShowDetails; Dlng.DetailRequested += ShowDetails;
        foreach (var panel in new[] { Kickout, Dlng })
            panel.PropertyChanged += (_, e) =>
            {
                if (panel == ActivePanel && e.PropertyName == nameof(panel.Fields))
                    PropertyChanged?.Invoke(this, new(nameof(Fields)));
                if (panel == ActivePanel && e.PropertyName == nameof(panel.SelectedField))
                    PropertyChanged?.Invoke(this, new(nameof(Field)));
            };
        Filter();
    }
    public OverkillTrendPanelViewModel Kickout { get; } = new("Kickout");
    public OverkillTrendPanelViewModel Dlng { get; } = new("DLNG");
    public OverkillTrendPanelViewModel ActivePanel => ActiveTab == 1 ? Dlng : Kickout;
    public int ActiveTab
    {
        get => _activeTab;
        set
        {
            Set(ref _activeTab, value); DetailsOpen = false;
            foreach (var name in new[] { nameof(ActivePanel), nameof(Fields), nameof(Field), nameof(IsTrendTab) })
                PropertyChanged?.Invoke(this, new(name));
        }
    }
    public bool IsTrendTab => ActiveTab < 2;
    public IReadOnlyList<string> Fields => ActivePanel.Fields;
    public string Field { get => ActivePanel.SelectedField; set => ActivePanel.SelectedField = value; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand RecentSevenCommand { get; }
    public RelayCommand CloseDetailsCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }
    public AsyncRelayCommand GenerateDatasetCommand { get; }
    public AsyncRelayCommand ExcludeCommand { get; }
    public ObservableCollection<HistoryContribution> Contributions { get; } = [];
    public ObservableCollection<TrainingBatch> Batches { get; } = [];
    public ObservableCollection<TrainingSample> Samples { get; } = [];
    public ObservableCollection<CollectionCount> CollectionCounts { get; } = [];
    public string Status { get => _status; set => Set(ref _status, value); }
    public DateTime? StartDate { get => _start; set { Set(ref _start, value); Filter(); } }
    public DateTime? EndDate { get => _end; set { Set(ref _end, value); Filter(); } }
    public bool DetailsOpen { get => _detailsOpen; private set => Set(ref _detailsOpen, value); }
    public string DetailTitle { get => _detailTitle; private set => Set(ref _detailTitle, value); }
    public TrainingBatch? SelectedBatch
    {
        get => _selectedBatch;
        set { Set(ref _selectedBatch, value); Replace(Samples, value?.Samples ?? []); UpdateCollectionCounts(); SelectedSample = null; System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }
    public TrainingSample? SelectedSample { get => _selectedSample; set { Set(ref _selectedSample, value); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    public async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try { await ReloadAsync(); }
        catch (Exception e) { Status = e.Message; }
        finally { _busy = false; System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }
    private async Task ReloadAsync()
    {
        var errors = new List<string>();
        try { _kickout = await Task.Run(() => _history.LoadKickoutAsync()); }
        catch (Exception e) { errors.Add("Kickout history: " + e.Message); }
        try { _dlng = await _history.LoadDlngAsync(); }
        catch (Exception e) { errors.Add("DLNG reviews: " + e.Message); }
        try { _batches = await Task.Run(() => _history.LoadBatchesAsync()); }
        catch (Exception e) { errors.Add("Training collection: " + e.Message); }
        Filter();
        if (ValidRange) Status = string.Join("; ", errors.Concat(_history.Warnings));
    }
    private bool ValidRange => StartDate is not null && EndDate is not null && EndDate >= StartDate
        && (EndDate.Value.Date - StartDate.Value.Date).TotalDays <= 3660;
    private void Filter()
    {
        DetailsOpen = false; Contributions.Clear();
        if (!ValidRange)
        {
            Kickout.SetData(new(Kickout.Fields.Where(f => f != OverkillTrendService.All).ToArray(), []));
            Dlng.SetData(new(Dlng.Fields.Where(f => f != OverkillTrendService.All).ToArray(), [])); CollectionCounts.Clear();
            Status = "시작일과 종료일을 확인하세요 (최대 10년)."; return;
        }
        Status = "";
        var start = DateOnly.FromDateTime(StartDate!.Value);
        var end = DateOnly.FromDateTime(EndDate!.Value);
        Kickout.SetData(OverkillTrendService.Kickout(_kickout, start, end));
        Dlng.SetData(OverkillTrendService.Dlng(_dlng, start, end));
        var batchId = SelectedBatch?.Id; var sampleId = SelectedSample?.Id;
        Replace(Batches, _batches);
        SelectedBatch = Batches.FirstOrDefault(b => b.Id == batchId);
        SelectedSample = Samples.FirstOrDefault(s => s.Id == sampleId);
        UpdateCollectionCounts();
    }
    private void UpdateCollectionCounts()
    {
        var ready = SelectedBatch is { } batch
            ? batch.Samples.Where(s => s.State == "Ready" && (batch.TrainedAt is not null || !s.Superseded)).ToArray()
            : _batches.Where(b => b.TrainedAt is null).SelectMany(b => b.Samples).Where(s => s.State == "Ready" && !s.Superseded
                && s.CollectedAt?.Date >= StartDate?.Date && s.CollectedAt?.Date <= EndDate?.Date).ToArray();
        // Product remains part of the batch/count identity even though it is no longer a UI filter.
        Replace(CollectionCounts, ready.GroupBy(s => (Product: TrainingCollectionService.Product(s.Review), s.Review.CropFolder, s.Review.LinePolarity, s.Review.FinalClass))
            .Select(g => new CollectionCount(g.Key.Product, g.Key.CropFolder, g.Key.LinePolarity, g.Key.FinalClass, g.Count(), g.Sum(s => s.Files.Count))));
    }
    private void ShowDetails(TrendDetailRequest request)
    {
        if (!ValidRange) return;
        Contributions.Clear();
        var start = request.Day ?? DateOnly.FromDateTime(StartDate!.Value);
        var end = request.Day ?? DateOnly.FromDateTime(EndDate!.Value);
        DetailTitle = $"{request.Kind} · {request.Line} · {request.Field} · {start:yyyy-MM-dd} — {end:yyyy-MM-dd}";
        if (request.Kind == "DLNG")
        {
            foreach (var r in OverkillTrendService.UniqueReviews(_dlng).Where(r => r.LinePolarity == request.Line
                && (request.Field == OverkillTrendService.All || r.CropFolder == request.Field)
                && OverkillTrendService.ProductionDay(r.InspectedAt) >= start && OverkillTrendService.ProductionDay(r.InspectedAt) <= end)
                .OrderBy(r => r.InspectedAt))
                Contributions.Add(new(OverkillTrendService.ProductionDay(r.InspectedAt), "DLNG", TrainingCollectionService.Product(r),
                    r.LinePolarity, r.CropFolder, $"{r.InspectedAt:yyyy-MM-dd HH:mm:ss} · {r.CellId} · {r.ItemKey}", r.SourceClass,
                    r.FinalClass, string.Join("; ", r.ImagePaths), 0, ReviewSemantics.IsApplicableOutcome(ReviewSemantics.Outcome(r)) ? 1 : 0, ReviewSemantics.Outcome(r) == "Overkill" ? 1 : 0));
        }
        else foreach (var snapshot in _kickout.Where(h => h.Day >= start && h.Day <= end).OrderBy(h => h.Day))
        {
            var rows = snapshot.Rows.Where(r => r.LinePolarity == request.Line && r.Defect == (request.Field == OverkillTrendService.All ? "ALL" : request.Field)).ToArray();
            foreach (var row in rows)
                Contributions.Add(new(snapshot.Day, "Kickout", row.ProductModel, row.LinePolarity, row.Defect,
                    "Report snapshot", "", "", snapshot.Source, row.TotalInspected, row.RealNg + row.Overkill, row.Overkill));
            var details = snapshot.Details.Where(d => d.LinePolarity == request.Line &&
                (request.Field == OverkillTrendService.All || d.Defect == request.Field));
            foreach (var d in details)
                Contributions.Add(new(snapshot.Day, "Kickout", "", d.LinePolarity, d.Defect,
                    string.Join(" | ", d.Headers.Zip(d.Values).Select(x => $"{x.First}={x.Second}")), "NG", d.Decision.ToString(),
                    snapshot.Source, 0, 1, d.Decision == ReviewDecision.Overkill ? 1 : 0));
            if (rows.Length == 0 && request.Field != OverkillTrendService.All)
            {
                var all = snapshot.Rows.FirstOrDefault(r => r.LinePolarity == request.Line && r.Defect == "ALL");
                if (all is not null) Contributions.Add(new(snapshot.Day, "Kickout", all.ProductModel, request.Line, request.Field,
                    "Report snapshot (0)", "", "", snapshot.Source, all.TotalInspected, 0, 0));
            }
        }
        DetailsOpen = true;
    }
    private async Task RetryAsync() => await Operate(async()=>await _collection.RecoverAsync((await _reviews.LoadAsync(default)).Values));
    private async Task GenerateDatasetAsync()
    {
        var id=SelectedBatch?.Id; if(id is null)return;
        string? folder = null;
        await Operate(async () => { folder = await _collection.GenerateDatasetAsync(id); });
        if (folder is not null) Status = "Dataset generated; batch trained: " + folder;
    }
    private async Task ExcludeAsync()
    {
        var batch=SelectedBatch; var sample=SelectedSample;
        if(batch is null||sample is null)return;
        await Operate(async()=>
        {
            var decisions=await _reviews.LoadAsync(default);
            var current=decisions.Values.Where(r=>ReviewSemantics.SampleId(r)==sample.Id).OrderByDescending(r=>r.UpdatedAt).FirstOrDefault()??sample.Review;
            var excluded=current with { IncludeInTraining=false,TrainingSelectedAt=null,UpdatedAt=DateTimeOffset.Now };
            await _reviews.SaveAsync(excluded,default);
            await _collection.ApplyAsync(excluded);
        });
    }
    private async Task Operate(Func<Task> operation)
    {
        if(_busy)return; _busy=true;
        try { await Task.Run(operation); await ReloadAsync(); if (string.IsNullOrEmpty(Status)) Status="Collection updated."; }
        catch(Exception e){Status=e.Message;}
        finally{_busy=false;System.Windows.Input.CommandManager.InvalidateRequerySuggested();}
    }
    private static void Replace<T>(ObservableCollection<T> target,IEnumerable<T> source){target.Clear();foreach(var x in source)target.Add(x);}
    private void Set<T>(ref T field,T value,[CallerMemberName]string? name=null){field=value;PropertyChanged?.Invoke(this,new(name));}
    public event PropertyChangedEventHandler? PropertyChanged;
}
