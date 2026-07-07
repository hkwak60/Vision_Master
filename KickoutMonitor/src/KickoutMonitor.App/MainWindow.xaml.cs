using System.Windows;
using KickoutMonitor.App.ViewModels;

namespace KickoutMonitor.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _kickoutViewModel;
    private readonly IrsReviewViewModel _irsViewModel;
    private readonly DlngReviewViewModel _dlngViewModel;
    private readonly SettingsViewModel _settingsViewModel;

    public MainWindow(
        MainViewModel kickoutViewModel,
        IrsReviewViewModel irsViewModel,
        DlngReviewViewModel dlngViewModel,
        SettingsViewModel settingsViewModel)
    {
        InitializeComponent();
        _kickoutViewModel = kickoutViewModel;
        _irsViewModel = irsViewModel;
        _dlngViewModel = dlngViewModel;
        _settingsViewModel = settingsViewModel;
    }

    private void KickoutButton_Click(object sender, RoutedEventArgs e)
    {
        var view = new KickoutMonitorView { DataContext = _kickoutViewModel };
        view.BackRequested += (_, _) => ReturnToDashboard();
        ModuleHost.Content = view;
        ShowModule("KickoutMonitor", "Welding NG / overkill review");
    }

    private void IrsButton_Click(object sender, RoutedEventArgs e)
    {
        var view = new IrsReviewView { DataContext = _irsViewModel };
        view.BackRequested += (_, _) => ReturnToDashboard();
        ModuleHost.Content = view;
        ShowModule("IRS Review", "IRS crop collection and dataset review");
    }

    private void DlngButton_Click(object sender, RoutedEventArgs e)
    {
        var view = new DlngReviewView { DataContext = _dlngViewModel };
        view.BackRequested += (_, _) => ReturnToDashboard();
        ModuleHost.Content = view;
        ShowModule("DLNG Review", "DLNG crop review and report");
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var view = new SettingsView { DataContext = _settingsViewModel };
        view.BackRequested += (_, _) => ReturnToDashboard();
        ModuleHost.Content = view;
        ShowModule("Settings", "VisionMaster local configuration");
    }

    private void ReturnToDashboard()
    {
        ModuleHost.Content = null;
        ModuleHost.Visibility = Visibility.Collapsed;
        DashboardPanel.Visibility = Visibility.Visible;
        ShellHeader.Visibility = Visibility.Visible;
        TitleText.Text = "VisionMaster";
        SubtitleText.Text = "Select a review tool";
    }

    private void ShowModule(string title, string subtitle)
    {
        DashboardPanel.Visibility = Visibility.Collapsed;
        ModuleHost.Visibility = Visibility.Visible;
        ShellHeader.Visibility = Visibility.Collapsed;
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
    }
}
