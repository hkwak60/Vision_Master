using System.Windows;
using System.Windows.Controls;
namespace KickoutMonitor.App;
public partial class OverkillMonitorView : UserControl
{
    public OverkillMonitorView() { InitializeComponent(); }
    public event EventHandler? BackRequested;
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
}
