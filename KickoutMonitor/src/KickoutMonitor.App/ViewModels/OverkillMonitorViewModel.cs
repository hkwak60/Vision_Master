using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;
using KickoutMonitor.Infrastructure;

namespace KickoutMonitor.App.ViewModels;

public sealed record HeatCell(string Field, int Count, string Rate, string Color);
public sealed record HeatLine(string Line, IReadOnlyList<HeatCell> Cells);
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
    private string _status = "Local history; no production connection is required.";
    private DateTime? _start = DateTime.Today.AddDays(-30), _end = DateTime.Today;
    private string _product = "All", _line = "All", _field = "All";
    private OverkillMetric? _selectedMetric;
    private TrainingBatch? _selectedBatch;
    public OverkillMonitorViewModel(OverkillHistoryService history, TrainingCollectionService collection, IDlngReviewStore reviews)
    {
        _history = history; _collection = collection; _reviews = reviews;
        RefreshCommand = new(RefreshAsync, () => !_busy);
        RetryCommand = new(RetryAsync, () => !_busy);
        MarkTrainedCommand = new(MarkTrainedAsync, () => !_busy && SelectedBatch is { TrainedAt: null });
        ExcludeCommand = new(ExcludeAsync, () => !_busy && SelectedBatch is { TrainedAt: null } && SelectedSample is not null);
    }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }
    public AsyncRelayCommand MarkTrainedCommand { get; }
    public AsyncRelayCommand ExcludeCommand { get; }
    public ObservableCollection<string> Products { get; } = ["All"];
    public ObservableCollection<string> Lines { get; } = ["All"];
    public ObservableCollection<string> Fields { get; } = ["All"];
    public ObservableCollection<OverkillMetric> KickoutRows { get; } = [];
    public ObservableCollection<OverkillMetric> DlngRows { get; } = [];
    public ObservableCollection<HeatLine> Heatmap { get; } = [];
    public ObservableCollection<DailyOverkill> Trends { get; } = [];
    public ObservableCollection<HistoryContribution> Contributions { get; } = [];
    public ObservableCollection<TrainingBatch> Batches { get; } = [];
    public ObservableCollection<TrainingSample> Samples { get; } = [];
    public ObservableCollection<CollectionCount> CollectionCounts { get; } = [];
    public string Status { get => _status; set => Set(ref _status,value); }
    public string Totals { get; private set; } = "";
    public DateTime? StartDate { get => _start; set { Set(ref _start,value); Filter(); } }
    public DateTime? EndDate { get => _end; set { Set(ref _end,value); Filter(); } }
    public string Product { get => _product; set { Set(ref _product,value); Filter(); } }
    public string Line { get => _line; set { Set(ref _line,value); Filter(); } }
    public string Field { get => _field; set { Set(ref _field,value); Filter(); } }
    public OverkillMetric? SelectedMetric { get => _selectedMetric; set { Set(ref _selectedMetric,value); DrillDown(); } }
    public TrainingBatch? SelectedBatch
    {
        get => _selectedBatch;
        set { Set(ref _selectedBatch,value); Replace(Samples,value?.Samples ?? []); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }
    private TrainingSample? _selectedSample;
    public TrainingSample? SelectedSample { get => _selectedSample; set { Set(ref _selectedSample,value); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    public async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try { await ReloadAsync(); Status = _history.Warnings.Count == 0 ? "Local history refreshed. Select a ranked row to inspect its contributing records." : string.Join("; ",_history.Warnings); }
        catch(Exception e) { Status = e.Message; }
        finally { _busy = false; System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }
    private async Task ReloadAsync()
    {
        _kickout = await Task.Run(() => _history.LoadKickoutAsync());
        _dlng = await _history.LoadDlngAsync();
        _batches = await _history.LoadBatchesAsync();
        // Preserve filter selections while refreshing option lists.
        AddOptions(Products,_kickout.SelectMany(x=>x.Rows).Select(x=>x.ProductModel).Concat(_dlng.Select(TrainingCollectionService.Product)));
        AddOptions(Lines,_kickout.SelectMany(x=>x.Rows).Select(x=>x.LinePolarity).Concat(_dlng.Select(x=>x.LinePolarity)));
        AddOptions(Fields,_kickout.SelectMany(x=>x.Rows).Where(x=>x.Defect!="ALL").Select(x=>x.Defect).Concat(_dlng.Select(x=>x.CropFolder)));
        Filter();
    }
    private bool Match(string actual, string filter) => filter == "All" || actual.Equals(filter,StringComparison.OrdinalIgnoreCase);
    private IReadOnlyList<KickoutHistorySnapshot> FilteredKickout() => _kickout.Where(h=>h.Day>=DateOnly.FromDateTime(StartDate!.Value) && h.Day<=DateOnly.FromDateTime(EndDate!.Value))
        .Select(h=>h with { Rows=h.Rows.Where(r=>Match(r.ProductModel,Product)&&Match(r.LinePolarity,Line)).ToArray() }).Where(h=>h.Rows.Count>0).ToArray();
    private IReadOnlyList<DlngReviewRecord> FilteredDlng() => _dlng.Where(r=>r.InspectedAt.Date>=StartDate!.Value.Date && r.InspectedAt.Date<=EndDate!.Value.Date
        && Match(TrainingCollectionService.Product(r),Product)&&Match(r.LinePolarity,Line)&&Match(r.CropFolder,Field)).ToArray();
    private void Filter()
    {
        if (StartDate is null || EndDate is null || EndDate < StartDate || (EndDate.Value-StartDate.Value).TotalDays>3660)
        { Status="Choose a valid date range of up to ten years."; return; }
        var kickout=FilteredKickout(); var dlng=FilteredDlng();
        Replace(KickoutRows,OverkillHistoryService.KickoutMetrics(kickout,Field=="All"?"":Field));
        Replace(DlngRows,OverkillHistoryService.DlngMetrics(dlng,_batches));
        Replace(Trends,OverkillHistoryService.Daily(kickout,DateOnly.FromDateTime(StartDate.Value),DateOnly.FromDateTime(EndDate.Value),Field=="All"?"":Field));
        var fields=KickoutRows.Select(x=>x.Field).Distinct().Order().ToArray();
        Replace(Heatmap,KickoutRows.GroupBy(x=>x.Line).Select(g=>new HeatLine(g.Key,fields.Select(f=>
        {
            var rows=g.Where(x=>x.Field==f).ToArray(); var count=rows.Sum(x=>x.Overkill); var reviewed=rows.Sum(x=>x.Reviewed);
            var fraction=reviewed==0?0:(double)count/reviewed;
            return new HeatCell(f,count,reviewed==0?"No reviewed rejects":$"{count}/{reviewed} ({fraction:P1})",
                reviewed==0?"#EDF1F5":$"#FF{(int)(245-115*fraction):X2}{(int)(245-135*fraction):X2}");
        }).ToArray())));
        Replace(Batches,_batches.Where(b=>Match(b.Product,Product)&&Match(b.Crop,Field)));
        var ready=_batches.Where(b=>b.TrainedAt is null).SelectMany(b=>b.Samples).Where(s=>s.State=="Ready"&&!s.Superseded
            && s.CollectedAt?.Date>=StartDate.Value.Date && s.CollectedAt?.Date<=EndDate.Value.Date
            && Match(TrainingCollectionService.Product(s.Review),Product)&&Match(s.Review.LinePolarity,Line)&&Match(s.Review.CropFolder,Field)).ToArray();
        Replace(CollectionCounts,ready.GroupBy(s=>(Product:TrainingCollectionService.Product(s.Review),s.Review.CropFolder,s.Review.LinePolarity,s.Review.FinalClass))
            .Select(g=>new CollectionCount(g.Key.Product,g.Key.CropFolder,g.Key.LinePolarity,g.Key.FinalClass,g.Count(),g.Sum(s=>s.Files.Count))));
        var all=kickout.SelectMany(h=>h.Rows).Where(r=>r.Defect==(Field=="All"?"ALL":Field)).ToArray();
        var inspected=all.Sum(r=>r.TotalInspected); var overkill=all.Sum(r=>r.Overkill); var reviewed=all.Sum(r=>r.RealNg+r.Overkill);
        Totals=$"Kickout: {overkill:N0} overkills / {inspected:N0} inspected / {reviewed:N0} reviewed rejects.   DLNG: {dlng.Count:N0} reviews, {DlngRows.Sum(r=>r.Unknown):N0} unknown.   New training: {ready.Length:N0} pairs ({ready.Sum(s=>s.Files.Count):N0} files).";
        PropertyChanged?.Invoke(this,new(nameof(Totals)));
        DrillDown();
    }
    private void DrillDown()
    {
        Contributions.Clear();
        if(SelectedMetric is not {} metric || StartDate is null || EndDate is null) return;
        if(metric.Kind=="DLNG")
        {
            foreach(var r in FilteredDlng().Where(r=>r.LinePolarity==metric.Line&&r.CropFolder==metric.Field&&TrainingCollectionService.Product(r)==metric.Product))
                Contributions.Add(new(DateOnly.FromDateTime(r.InspectedAt),"DLNG",metric.Product,r.LinePolarity,r.CropFolder,r.ItemKey,r.SourceClass,r.FinalClass,
                    string.Join("; ",r.ImagePaths),0,ReviewSemantics.Outcome(r)=="Unknown"?0:1,ReviewSemantics.Outcome(r)=="Overkill"?1:0));
        }
        else foreach(var snapshot in FilteredKickout())
        {
            var row=snapshot.Rows.FirstOrDefault(r=>r.LinePolarity==metric.Line&&r.Defect==metric.Field&&r.ProductModel==metric.Product);
            if(row is null) continue;
            var details=snapshot.Details.Where(d=>d.LinePolarity==metric.Line&&d.Defect==metric.Field).ToArray();
            if(details.Length==0) Contributions.Add(new(snapshot.Day,"Kickout",metric.Product,metric.Line,metric.Field,"Report snapshot","","",snapshot.Source,row.TotalInspected,row.RealNg+row.Overkill,row.Overkill));
            foreach(var d in details) Contributions.Add(new(snapshot.Day,"Kickout",metric.Product,metric.Line,metric.Field,
                string.Join(" | ",d.Headers.Zip(d.Values).Select(x=>$"{x.First}={x.Second}")),"NG",d.Decision.ToString(),snapshot.Source,0,1,d.Decision==ReviewDecision.Overkill?1:0));
        }
    }
    private async Task RetryAsync() => await Operate(async()=>await _collection.RecoverAsync((await _reviews.LoadAsync(default)).Values));
    private async Task MarkTrainedAsync()
    {
        var id=SelectedBatch?.Id; if(id is null)return;
        await Operate(()=>_collection.MarkTrainedAsync(id));
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
        try { await Task.Run(operation); await ReloadAsync(); Status="Collection updated."; }
        catch(Exception e){Status=e.Message;}
        finally{_busy=false;System.Windows.Input.CommandManager.InvalidateRequerySuggested();}
    }
    private static void AddOptions(ObservableCollection<string> target,IEnumerable<string> source)
    { foreach(var s in source.Distinct().Order()) if(!target.Contains(s))target.Add(s); }
    private static void Replace<T>(ObservableCollection<T> target,IEnumerable<T> source){target.Clear();foreach(var x in source)target.Add(x);}
    private void Set<T>(ref T field,T value,[CallerMemberName]string? name=null){field=value;PropertyChanged?.Invoke(this,new(name));}
    public event PropertyChangedEventHandler? PropertyChanged;
}
