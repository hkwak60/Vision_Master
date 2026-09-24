using System.Windows;
using System.Windows.Controls;
using KickoutMonitor.App.ViewModels;

namespace KickoutMonitor.App;

public partial class OverkillTrendView : UserControl
{
    public OverkillTrendView()
    {
        InitializeComponent();
    }
    private void Chart_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is OverkillLineChart chart)
        {
            chart.PointClicked -= Chart_PointClicked;
            chart.PointClicked += Chart_PointClicked;
        }
    }
    private void Chart_PointClicked(KickoutMonitor.Infrastructure.MachineDayCount point)
    {
        if (DataContext is OverkillTrendPanelViewModel vm) vm.ShowDetails(point.Line, point.Field, point.Day);
    }
    private void Field_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string field } && DataContext is OverkillTrendPanelViewModel vm) vm.SelectedField = field;
    }
    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TrendHeatCell cell } && DataContext is OverkillTrendPanelViewModel vm)
        { vm.SelectedField = cell.Field; vm.ShowDetails(cell.Line, cell.Field); }
    }
}
