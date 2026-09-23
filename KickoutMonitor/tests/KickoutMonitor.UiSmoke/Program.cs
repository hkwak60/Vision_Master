using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KickoutMonitor.App.Services;
using KickoutMonitor.App.ViewModels;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;
using KickoutMonitor.Infrastructure;

internal static class Program
{
    private static readonly string Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VisionMasterUiSmoke", Guid.NewGuid().ToString("N"));
    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new KickoutMonitor.App.App();
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            app.InitializeComponent(); // Resources only: do not run production startup or connect to shares.
            var machine = new WeldingMachine("1-1-an", "1-1", Polarity.Anode, "unused", ['F']);
            var flags = new Flags();
            var reviewStore = new DlngReviews();
            var vm = new DlngReviewViewModel(new Machines(machine), null!, reviewStore, null!, new Preview(), flags: flags);
            var defaults = new MachineRegistry();
            var kickout = new MainViewModel(defaults, null!, null!, null!, null!, null!, new Preview(), null!, new AppStorage(Root));
            var defaultDlng = new DlngReviewViewModel(defaults, null!, null!, null!, new Preview());
            Require(kickout.MachineOptions.Count(x => x.IsSelected) == 4, "Kickout four default lines");
            Require(defaultDlng.MachineOptions.Count(x => x.IsSelected) == 4, "DLNG four default lines");
            var now = DateTime.Now;
            var expectedKickout = QueueTimeRange.KickoutDefault(now);
            Require(kickout.StartTime == "06:00" && kickout.EndTime == "06:00" &&
                defaultDlng.StartTime == "06:00" && defaultDlng.EndTime == now.ToString("HH:mm"), "queue time defaults");
            Require(kickout.StartDate == expectedKickout.Start.Date && kickout.EndDate == expectedKickout.End.Date &&
                defaultDlng.StartDate == now.Date.AddDays(-1) && defaultDlng.EndDate == now.Date, "queue date defaults");
            kickout.StartTime = "invalid";
            kickout.LoadCommand.Execute(null);
            Require(kickout.Status.Contains("valid queue times") && !kickout.IsBusy, "invalid Kickout time handled before IO");
            defaultDlng.ModelOptions[0].IsSelected = true;
            defaultDlng.EndDate = defaultDlng.StartDate;
            defaultDlng.EndTime = defaultDlng.StartTime;
            defaultDlng.LoadCommand.Execute(null);
            Require(defaultDlng.Status.Contains("later than start") && !defaultDlng.IsBusy, "equal DLNG timeframe handled before IO");
            kickout.StartTime = "06:00";
            var first = Row(machine, "A", 1);
            var unrelated = Row(machine, "B", 2);
            var repeat = Row(machine, "A", 3);
            var sibling = new DlngCandidateItem(first.Item with { Key = first.Item.Key + "|SECOND", CropFolder = "HORNMARK" }, null);
            var ordered = new[] { unrelated, repeat, sibling, first }.ReworkOrder(x => x.ReviewStatus == "Pending");
            foreach (var row in ordered) vm.Candidates.Add(row);
            Require(ordered.Take(3).All(x => x.CellId == "A"), "default group adjacency");
            Require(ordered.Where(x => x.CellId == "A").All(x => x.ReworkLabel == "2 inputs"), "distinct inspection repeat count");

            var grid = new DataGrid { ItemsSource = vm.Candidates, AutoGenerateColumns = false };
            var time = new DataGridTextColumn { Binding = new Binding("Time"), SortMemberPath = "Time" };
            grid.Columns.Add(time);
            ReworkGrid.Sort(grid, new DataGridSortingEventArgs(time));
            ReworkGrid.Sort(grid, new DataGridSortingEventArgs(time)); // Descending groups, chronological attempts.
            var displayed = CollectionViewSource.GetDefaultView(vm.Candidates).Cast<DlngCandidateItem>().ToArray();
            Require(displayed[0].CellId == "B" && displayed.Skip(1).All(x => x.CellId == "A"), "column sort preserves group");
            Require(displayed[1].InspectionTime <= displayed[2].InspectionTime && displayed[2].InspectionTime <= displayed[3].InspectionTime,
                "attempt order remains chronological");

            vm.ModelOptions[0].IsSelected = true;
            vm.StartTime = "25:00";
            vm.LoadCommand.Execute(null);
            Require(vm.Candidates.Count == 4 && !vm.IsBusy && vm.Status.Contains("valid queue times"),
                "invalid timeframe preserves existing queue");
            vm.StartTime = "06:00";
            vm.SelectedCandidate = displayed[0];
            vm.HandleHotkey(Key.Down);
            Require(ReferenceEquals(vm.SelectedCandidate, displayed[1]), "Down follows displayed order");
            vm.HandleHotkey(Key.Up);
            Require(ReferenceEquals(vm.SelectedCandidate, displayed[0]), "Up follows displayed order");
            vm.SelectedCandidate = repeat;
            Require(vm.CurrentPreview?.Image is not null, "local preview load");
            vm.FlagCommand.Execute(null);
            Require(flags.Items.Count == 1 && flags.Items[0].Inspection?.Identity == repeat.Item.Inspection?.Identity,
                "flag preserves capture context");
            Require(flags.Items[0].RawImagePaths.All(path => path.Contains("120003")), "flag retains original raw attempt");

            vm.AutoAdvanceAfterReview = false;
            vm.SelectedCandidate = displayed[0];
            vm.HandleHotkey(Key.Enter);
            Require(reviewStore.Saves == 0, "Enter without an explicit DLNG draft does nothing");
            vm.HandleHotkey(Key.O);
            Require(reviewStore.Saves == 0 && vm.CommitCommand.CanExecute(null), "DLNG draft is staged");
            vm.HandleHotkey(Key.Enter);
            Require(reviewStore.Saves == 1 && ReferenceEquals(vm.SelectedCandidate, displayed[1]), "Enter saves and advances once in displayed order");
            Require(displayed[0].JudgmentTone == "Overkill" && displayed[0].SavedClass == "Overkill", "saved OK/overkill row is red");
            vm.HandleHotkey(Key.Enter);
            Require(reviewStore.Saves == 1, "second Enter does not classify next row");
            vm.SelectedCandidate = displayed[0];
            Require(!vm.CommitCommand.CanExecute(null), "restoring a saved class does not arm commit");
            Require(!vm.IncludeInTraining, "new selection defaults unchecked");
            vm.IncludeInTraining = true;
            vm.HandleHotkey(Key.Enter);
            Require(reviewStore.Last!.IncludeInTraining, "training choice saved independently");
            vm.SelectedCandidate = displayed[0];
            Require(vm.IncludeInTraining && !vm.CommitCommand.CanExecute(null), "saved training selection restores without arming");
            reviewStore.Fail = true;
            vm.HandleHotkey(Key.R);
            vm.HandleHotkey(Key.Enter);
            Require(ReferenceEquals(vm.SelectedCandidate,displayed[0]) && vm.CommitCommand.CanExecute(null)
                && displayed[0].ReviewStatus == "Save failed", "failed save preserves row and draft");
            reviewStore.Fail = false;
            vm.HandleHotkey(Key.Enter);
            Require(displayed[0].JudgmentTone == "Real", "reclassification updates row tone");
            vm.AutoAdvanceAfterReview = true;
            var previousSaves=reviewStore.Saves;
            vm.HandleHotkey(Key.R);
            Require(reviewStore.Saves==previousSaves+1, "auto advance saves explicit class once");
            Require(vm.FinalClassOptions.All(x=>!x.DisplayName.Contains("No Need")), "No Need removed from new choices");
            Require(ReviewKeyboard.IsEditor(new TextBox()) && ReviewKeyboard.IsEditor(new DatePicker()) &&
                ReviewKeyboard.IsEditor(new ComboBox()) && !ReviewKeyboard.IsEditor(new Button()), "review editor guard");

            var kr = new KickoutReviews();
            var kv = new MainViewModel(new Machines(machine),null!,kr,new Folders(),new Cache(),null!,new Preview(),null!,new AppStorage(Root));
            foreach(var row in new[]{first,unrelated,repeat})
                kv.Candidates.Add(new(machine,new(row.Item.Key,machine.Id,row.Item.InspectedAt,"E81C","LOT",row.CellId,"SEPA",NgSide.Upper,[],"","",1,row.Item.Inspection),null));
            kv.AutoAdvanceAfterReview=false;
            kv.SelectedCandidate=kv.Candidates[0];
            kv.HandleHotkey(Key.Enter);
            Require(kr.Saves==0,"Kickout Enter with no draft");
            kv.HandleHotkey(Key.O);
            Require(kr.Saves==0&&kv.DraftLabel.Contains("Overkill"),"Kickout stages explicit draft");
            kv.HandleHotkey(Key.Enter);
            Require(kr.Saves==1&&ReferenceEquals(kv.SelectedCandidate,kv.Candidates[1]),"Kickout commits once without reorder");
            kv.HandleHotkey(Key.Enter);
            Require(kr.Saves==1,"Kickout repeated Enter does not save next item");
            kr.Fail=true; kv.HandleHotkey(Key.R); kv.HandleHotkey(Key.Enter);
            Require(ReferenceEquals(kv.SelectedCandidate,kv.Candidates[1])&&kv.CommitCommand.CanExecute(null),"Kickout failure retains draft");
            kr.Fail=false; kv.HandleHotkey(Key.Enter);
            kv.AutoAdvanceAfterReview=true; kv.HandleHotkey(Key.O);
            Require(kr.Saves==3,"Kickout auto advance saves once");

            Require(!ReviewKeyboard.ShouldDispatch(true,ModifierKeys.None) && ReviewKeyboard.ShouldDispatch(false,ModifierKeys.None),
                "held Enter suppressed");
            vm.AutoAdvanceAfterReview=false;
            vm.SelectedCandidate=displayed[0];
            vm.HandleHotkey(Key.O);
            reviewStore.Pending=new TaskCompletionSource();
            var countBefore=reviewStore.Saves;
            vm.HandleHotkey(Key.Enter);vm.HandleHotkey(Key.Enter);vm.CommitCommand.Execute(null);
            Require(reviewStore.Saves==countBefore+1 && ReferenceEquals(vm.SelectedCandidate,displayed[0]),"concurrent commits suppressed");
            reviewStore.Pending.SetResult();
            var timeout=DateTime.UtcNow.AddSeconds(3);
            while(vm.IsBusy && DateTime.UtcNow<timeout)
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.Background);
            Require(!vm.IsBusy,"pending save completes");
            reviewStore.Pending=null;
            var localStorage = new AppStorage(Root);
            var collection = new TrainingCollectionService(localStorage);
            var overkill = new OverkillMonitorViewModel(new OverkillHistoryService(localStorage,reviewStore,collection),collection,reviewStore);
            var dashboard = new KickoutMonitor.App.OverkillMonitorView {DataContext=overkill};
            var controls = new UserControl[] {
                new KickoutMonitor.App.KickoutMonitorView { DataContext = kickout },
                new KickoutMonitor.App.DlngReviewView { DataContext = vm },
                new KickoutMonitor.App.NgBypassMonitorView(),
                new KickoutMonitor.App.IrsReviewView(),
                new KickoutMonitor.App.FlaggedReviewView(),
                dashboard
            };
            foreach (var control in controls)
            {
                control.Measure(new Size(1100, 700));
                control.Arrange(new Rect(0, 0, 1100, 700));
                control.UpdateLayout();
            }
            var today = DateOnly.FromDateTime(DateTime.Today);
            Require(overkill.StartDate == DateTime.Today.AddDays(-6) && overkill.EndDate == DateTime.Today, "dashboard seven inclusive days");
            for (var dayIndex = 0; dayIndex < 7; dayIndex++)
            {
                if (dayIndex == 3) continue;
                var day = today.AddDays(dayIndex - 6);
                var rows = OverkillTrendService.Lines.SelectMany((line, lineIndex) => new[] {
                    new SummaryReportRow(line,"ALL",1000,50,20,dayIndex == 0 ? 0 : (lineIndex + dayIndex) % 9,0,0,0,"E81C"),
                    new SummaryReportRow(line,"SEPA",1000,20,10,dayIndex == 0 ? 0 : (lineIndex + dayIndex) % 5,0,0,0,"E81C"),
                    new SummaryReportRow(line,"BEAD",1000,20,10,(lineIndex * dayIndex) % 4,0,0,0,"E81C")
                }).ToArray();
                Complete(OverkillHistoryService.SaveSnapshotAsync(localStorage, new(day, day.ToDateTime(new(6,0)), day.AddDays(1).ToDateTime(new(6,0)), rows, [], "local fixture")));
            }
            var sampleReview = reviewStore.Records.Values.First();
            reviewStore.Records["trend-fixture"] = sampleReview with { ItemKey="trend-fixture", CellId="TREND",
                InspectedAt=DateTime.Today.AddHours(6), CropFolder="SEPA", FinalClass="Overkill", Inspection=null,
                ImagePaths=["trend-source.jpg","trend-mask.png"], ModelKind=DlngModelKind.Segmentation };
            Complete(overkill.RefreshAsync());
            Require(overkill.Kickout.Series.Count == 8 && overkill.Kickout.HeatRows.Count == 8, "eight machine series");
            Require(overkill.Kickout.Series.All(series => series.Points.Count == 7), "seven graph dates");
            Layout(dashboard);
            var trendView = Descendants<KickoutMonitor.App.OverkillTrendView>(dashboard).First();
            var chart = Descendants<KickoutMonitor.App.OverkillLineChart>(dashboard).First();
            Require(Descendants<ComboBox>(dashboard).Single().SelectedItem as string == OverkillTrendService.All, "all field displayed after refresh");
            Render(dashboard, "overkill-smoke.png");
            Require(chart.RenderedPointCount == 48 && chart.RenderedSegmentCount == 32, $"zeros render and missing day breaks lines ({chart.RenderedPointCount}/{chart.RenderedSegmentCount}; VM {overkill.Kickout.Series.Sum(s=>s.Points.Count(p=>p.HasData))}; status {overkill.Status})");
            var header = Descendants<Button>(trendView).First(b => b.Tag as string == "SEPA");
            header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(overkill.Field == "SEPA" && overkill.Kickout.Headers.Count == 2 && overkill.Kickout.Series.Count == 8, "field click keeps matrix and machines");
            overkill.Kickout.Machines[0].Visible = false;
            Require(overkill.Kickout.Series.Count == 7 && overkill.Kickout.HeatRows.Count == 7, "legend controls chart and matrix");
            overkill.Kickout.Machines[0].Visible = true;
            Layout(dashboard);
            var detailButton = Descendants<Button>(trendView).First(b => b.Content as string == "⋯" && b.DataContext is TrendHeatCell);
            detailButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(overkill.DetailsOpen && overkill.Contributions.Count > 0, "cell detail opens report records");
            Layout(dashboard);
            Render(dashboard, "overkill-detail-smoke.png");
            var smallerChartHeight = chart.ActualHeight;
            var close = Descendants<Button>(dashboard).Single(b => b.Content as string == "닫기 ×");
            close.Command.Execute(null);
            Layout(dashboard);
            Require(!overkill.DetailsOpen && chart.ActualHeight > smallerChartHeight, "closing detail restores chart space");
            overkill.Kickout.SelectedField = "SEPA";
            overkill.ActiveTab = 1;
            Layout(dashboard);
            Require(overkill.Field == OverkillTrendService.All, "independent DLNG field");
            overkill.Dlng.SelectedField = "SEPA";
            Render(dashboard, "overkill-dlng-smoke.png");
            overkill.Dlng.ShowDetails("1-1(-)", "SEPA", today);
            Require(overkill.DetailsOpen, "point detail opens production-day records");
            overkill.ActiveTab = 0;
            Require(overkill.Field == "SEPA" && !overkill.DetailsOpen, "tab restores selection");
            var customStart = DateTime.Today.AddDays(-2);
            overkill.StartDate = customStart;
            Complete(overkill.RefreshAsync());
            Require(overkill.StartDate == customStart && overkill.Field == "SEPA", "refresh preserves dates and field");
            overkill.EndDate = customStart.AddDays(-1);
            Require(overkill.Kickout.Series.All(series => series.Points.Count == 0), "invalid range clears graph");
            Require(overkill.Field == "SEPA", "invalid range preserves field choice");
            overkill.RecentSevenCommand.Execute(null);
            Require(overkill.StartDate == DateTime.Today.AddDays(-6) && overkill.EndDate == DateTime.Today, "recent seven reset");
            foreach (var option in overkill.Kickout.Machines) option.Visible = false;
            Require(overkill.Kickout.Series.Count == 0 && overkill.Kickout.HeatRows.Count == 0, "empty legend safe");
            foreach (var option in overkill.Kickout.Machines) option.Visible = true;
            overkill.ActiveTab = 2;
            Layout(dashboard);
            Require(Descendants<Button>(dashboard).Any(b => b.Content as string == "Retry pending copies"), "training controls retained");
            Require(Descendants<DataGrid>(dashboard).SelectMany(g => g.Columns).All(c => c.Header as string != "Product"), "product hidden");
            overkill.ActiveTab = 0;
            Layout(dashboard);
            Console.WriteLine("PASS: six WPF views construct/layout at 1100x700; trend gap/zero rendering, field selection, legend, detail, tab state and date refresh/reset; explicit drafts, auto advance, failure retention, saved-class colors, training selection restore, editor guard; grouping, crop sibling counts, column sorting, keyboard navigation, previews, flag context, queue time validation and default selections.");
            app.Shutdown();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (System.IO.Directory.Exists(Root)) System.IO.Directory.Delete(Root, true); }
    }

    private static void Complete(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => {}, System.Windows.Threading.DispatcherPriority.Background);
        if (!task.IsCompleted) throw new TimeoutException("UI fixture timeout.");
        task.GetAwaiter().GetResult();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Layout(FrameworkElement control)
    {
        control.Measure(new Size(1100, 700)); control.Arrange(new Rect(0, 0, 1100, 700)); control.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => {}, System.Windows.Threading.DispatcherPriority.Background);
    }
    private static void Render(FrameworkElement control, string file)
    {
        Layout(control);
        var image = new RenderTargetBitmap(1100, 700, 96, 96, PixelFormats.Pbgra32);
        image.Render(control);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        var artifact = System.IO.Path.Combine(Environment.CurrentDirectory, ".codex-work", file);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(artifact)!);
        using var output = System.IO.File.Create(artifact); png.Save(output);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Smoke test failed: " + message);
    }
    private static DlngCandidateItem Row(WeldingMachine machine, string cell, int second)
    {
        var at = new DateTime(2026, 9, 16, 12, 0, second);
        System.IO.Directory.CreateDirectory(Root);
        var raw = System.IO.Path.Combine(Root, $"{at:yyyyMMdd_HHmmss}_LOT_{cell}_EXT_0_0.jpg");
        System.IO.File.WriteAllBytes(raw, [0xff, 0xd8, 0xff, 0xd9]);
        var context = new InspectionContext(machine.Id, machine.Model, "LOT", cell, at, at, [raw]);
        return new(new DlngReviewItem(context.Identity, machine.Id, machine.OutputFolderName, machine.Polarity,
            at, machine.Model, "LOT", cell, "NG", "SEPA", "UPPER", "SEPA", "Segmentation",
            DlngModelKind.Segmentation, [new("SourceImg", raw, false)], "", 1, "",
            context, [new("Raw", raw, false)]), null);
    }
    private sealed class Machines(WeldingMachine machine) : IMachineRegistry
    {
        public IReadOnlyList<WeldingMachine> All => [machine];
        public WeldingMachine Get(string id) => machine;
    }
    private sealed class Preview : IPreviewImageLoader<BitmapSource>
    {
        public Task<BitmapSource?> LoadAsync(string path, int width, CancellationToken token)
            => Task.FromResult<BitmapSource?>(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Rgb24, null, new byte[3], 3));
    }
    private sealed class DlngReviews : IDlngReviewStore
    {
        public int Saves; public bool Fail; public DlngReviewRecord? Last; public TaskCompletionSource? Pending;
        public Dictionary<string,DlngReviewRecord> Records = [];
        public Task SaveAsync(DlngReviewRecord record,CancellationToken token)
        { if(Fail)throw new System.IO.IOException("fixture save failure"); Saves++; Last=record; Records[record.ItemKey]=record; return Pending?.Task ?? Task.CompletedTask; }
        public Task<IReadOnlyDictionary<string,DlngReviewRecord>> LoadAsync(CancellationToken token)=>Task.FromResult<IReadOnlyDictionary<string,DlngReviewRecord>>(Records);
    }
    private sealed class KickoutReviews : IReviewStore
    {
        public int Saves;public bool Fail;
        public Task SaveAsync(ReviewEntry record,CancellationToken token)
        { if(Fail)throw new System.IO.IOException("fixture save failure");Saves++;return Task.CompletedTask; }
        public Task<IReadOnlyDictionary<string,ReviewEntry>> LoadAsync(CancellationToken token)=>Task.FromResult<IReadOnlyDictionary<string,ReviewEntry>>(new Dictionary<string,ReviewEntry>());
    }
    private sealed class Folders : IClassifiedFolderService
    {
        public Task<CopyResult> ClassifyAsync(WeldingMachine m,KickoutCandidate c,ReviewDecision d,CancellationToken t)
            =>Task.FromResult(new CopyResult(CopyState.NotRequested,null,"fixture"));
    }
    private sealed class Cache : IPreviewCache
    {
        public Task<KickoutCandidate> EnsureCachedAsync(WeldingMachine m,KickoutCandidate c,CancellationToken t)=>Task.FromResult(c);
        public Task RemoveAsync(WeldingMachine m,KickoutCandidate c,CancellationToken t)=>Task.CompletedTask;
    }
    private sealed class Flags : IFlaggedItemStore
    {
        public List<FlaggedItem> Items { get; } = [];
        public Task SaveAsync(FlaggedItem item, CancellationToken token) { Items.Add(item); return Task.CompletedTask; }
        public Task<IReadOnlyDictionary<string,FlaggedItem>> LoadAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyDictionary<string,FlaggedItem>>(Items.ToDictionary(x => x.Key));
        public Task DeleteAsync(IReadOnlyList<string> keys, CancellationToken token) => Task.CompletedTask;
        public Task MarkActiveAsync(IReadOnlyList<string> keys, DateTimeOffset at, CancellationToken token) => Task.CompletedTask;
        public Task MarkSummarizedAsync(IReadOnlyList<string> keys, DateTimeOffset at, CancellationToken token) => Task.CompletedTask;
    }
}
