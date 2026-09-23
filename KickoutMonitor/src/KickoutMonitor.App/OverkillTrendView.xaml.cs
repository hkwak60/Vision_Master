using System.Windows;
using System.Windows.Controls;
using KickoutMonitor.App.ViewModels;

namespace KickoutMonitor.App;

public partial class OverkillTrendView : UserControl
{
    public OverkillTrendView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => HeatScroll.MaxHeight = Math.Clamp(ActualHeight - 230, 70, 286);
        Chart.PointClicked += point =>
        {
            if (DataContext is OverkillTrendPanelViewModel vm) vm.ShowDetails(point.Line, point.Field, point.Day);
        };
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
