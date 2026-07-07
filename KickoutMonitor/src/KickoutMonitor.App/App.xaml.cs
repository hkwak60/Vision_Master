using System.Threading;
using System.Windows;
using KickoutMonitor.App.Services;
using KickoutMonitor.App.ViewModels;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;
using KickoutMonitor.Infrastructure;

namespace KickoutMonitor.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private bool _ownsSingleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "Local\\VisionMaster.SingleInstance", out var createdNew);
        _ownsSingleInstance = createdNew;
        if (!createdNew)
        {
            MessageBox.Show(
                "VisionMaster is already running. Close the existing window before opening another copy.",
                "VisionMaster",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        base.OnStartup(e);

        VisionMasterSettings settings;
        var settingsStore = new JsonSettingsStore();
        try
        {
            settings = await settingsStore.LoadOrCreateAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"VisionMaster could not load settings: {exception.Message}",
                "VisionMaster",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var machines = new MachineRegistry(settings);
        var storage = new AppStorage(settings);
        storage.EnsureCreated(machines.All);
        var reviews = new JsonReviewStore(storage);
        var dlngReviews = new JsonDlngReviewStore(storage);
        var shares = new SharePathResolver();
        var locator = new DailyCsvLocator(shares, settings);
        var snapshots = new ReadOnlySnapshotService(storage);
        var imageLoader = new WpfPreviewImageLoader();
        var dlngQueue = new DlngQueueService(
            locator,
            snapshots,
            new DlngCsvReader(shares, settings),
            new DlngCropLocator(shares, settings));
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
        var dlngViewModel = new DlngReviewViewModel(
            machines,
            dlngQueue,
            dlngReviews,
            new DlngReportGenerator(dlngQueue, dlngReviews, storage),
            imageLoader,
            settings);
        var settingsViewModel = new SettingsViewModel(settingsStore, settings, settingsStore.LastWarning);
        var window = new MainWindow(kickoutViewModel, irsViewModel, dlngViewModel, settingsViewModel);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstance)
        {
            _singleInstance?.ReleaseMutex();
        }
        _singleInstance?.Dispose();
        _singleInstance = null;
        _ownsSingleInstance = false;
        base.OnExit(e);
    }
}
