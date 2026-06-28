using System.Windows;
using KickoutMonitor.App.Services;
using KickoutMonitor.App.ViewModels;
using KickoutMonitor.Application;
using KickoutMonitor.Infrastructure;

namespace KickoutMonitor.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var machines = new MachineRegistry();
        var storage = new AppStorage();
        storage.EnsureCreated(machines.All);
        var reviews = new JsonReviewStore(storage);
        var shares = new SharePathResolver();
        var locator = new DailyCsvLocator(shares);
        var snapshots = new ReadOnlySnapshotService(storage);
        var imageLoader = new WpfPreviewImageLoader();
        var kickoutViewModel = new MainViewModel(
            machines,
            new KickoutQueueService(
                locator,
                snapshots,
                new WeldingKickoutCsvReader(shares)),
            reviews,
            new ClassifiedFolderService(storage),
            new DiskPreviewCache(storage),
            new ConnectionProbe(shares),
            imageLoader,
            new SummaryReportService(
                locator,
                snapshots,
                new InspectionSummaryCsvReader(),
                reviews,
                new SummaryReportWriter(storage)),
            storage);
        var irsViewModel = new IrsReviewViewModel(
            new IrsReviewQueueService(
                machines,
                new IrsWorkbookReader(),
                new IrsRawImageLocator(shares, locator)),
            imageLoader,
            machines,
            new IrsReviewCommitService(storage, locator, shares),
            new IrsDatasetService(storage));
        var window = new MainWindow(kickoutViewModel, irsViewModel);
        window.Show();
    }
}

