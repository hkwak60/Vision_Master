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

        var settingsStore = new JsonSettingsStore();
        var settings = settingsStore.LoadOrCreateAsync(CancellationToken.None).GetAwaiter().GetResult();
        var machines = new MachineRegistry(settings);
        var storage = new AppStorage(settings);
        storage.EnsureCreated(machines.All);
        var reviews = new JsonReviewStore(storage);
        var shares = new SharePathResolver();
        var locator = new DailyCsvLocator(shares, settings);
        var snapshots = new ReadOnlySnapshotService(storage);
        var imageLoader = new WpfPreviewImageLoader();
        var kickoutViewModel = new MainViewModel(
            machines,
            new KickoutQueueService(
                locator,
                snapshots,
                new WeldingKickoutCsvReader(shares, settings)),
            reviews,
            new ClassifiedFolderService(storage),
            new DiskPreviewCache(storage),
            new ConnectionProbe(shares),
            imageLoader,
            new SummaryReportService(
                locator,
                snapshots,
                new InspectionSummaryCsvReader(settings),
                reviews,
                new SummaryReportWriter(storage),
                settings),
            storage);
        var irsViewModel = new IrsReviewViewModel(
            new IrsReviewQueueService(
                machines,
                new IrsWorkbookReader(),
                new IrsRawImageLocator(shares, locator, settings)),
            imageLoader,
            machines,
            new IrsReviewCommitService(storage, locator, shares, settings),
            new IrsDatasetService(storage, settings),
            settings);
        var settingsViewModel = new SettingsViewModel(settingsStore, settings, settingsStore.LastWarning);
        var window = new MainWindow(kickoutViewModel, irsViewModel, settingsViewModel);
        window.Show();
    }
}
