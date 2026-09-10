using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace KickoutMonitor.App;

public partial class LogMonitorView : UserControl
{
    private static readonly Regex TimestampPattern = new(@"^\[(?<time>\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\]", RegexOptions.Compiled);
    private static readonly Regex RetryPattern = new(@"Image NG ReTry Cnt\s*=\s*(?<count>\d+)\s*/\s*(?<limit>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MinusPattern = new(@"\[Lead Minus Seq\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PlusPattern = new(@"\[Lead Plus Seq\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ObservableCollection<LogRetryResult> Results { get; } = new();
    public ObservableCollection<string> ActivityLog { get; } = new();
    public ICollectionView ResultsView { get; }
    public event EventHandler? BackRequested;

    public LogMonitorView()
    {
        ResultsView = CollectionViewSource.GetDefaultView(Results);
        ResultsView.SortDescriptions.Add(new SortDescription(nameof(LogRetryResult.Started), ListSortDirection.Ascending));
        ResultsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LogRetryResult.Date)));
        InitializeComponent();
        DataContext = this;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private async void SelectFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select event log files", Filter = "Log files (*.log)|*.log|All files (*.*)|*.*", Multiselect = true, InitialDirectory = Directory.Exists(@"D:\260901") ? @"D:\260901" : null };
        if (dialog.ShowDialog() != true) return;

        Results.Clear(); ActivityLog.Clear(); ExportButton.IsEnabled = false;
        StatusText.Text = $"Reviewing {dialog.FileNames.Length:N0} file(s)...";
        try
        {
            foreach (var path in dialog.FileNames)
            {
                AddActivity($"Reviewing {Path.GetFileName(path)}");
                var found = await Task.Run(() => AnalyzeFile(path));
                foreach (var result in found) Results.Add(result);
                AddActivity($"Reviewed {Path.GetFileName(path)} — {found.Count:N0} retry instance(s)");
            }

            FilesText.Text = dialog.FileNames.Length == 1 ? dialog.FileNames[0] : $"{dialog.FileNames.Length:N0} files selected from {Path.GetDirectoryName(dialog.FileNames[0])}";
            InstanceCountText.Text = Results.Count.ToString("N0");
            AnodeCountText.Text = Results.Count(x => x.Side == "Anode").ToString("N0");
            CathodeCountText.Text = Results.Count(x => x.Side == "Cathode").ToString("N0");
            ReachedFourCountText.Text = Results.Count(x => x.ReachedFour).ToString("N0");
            ExportButton.IsEnabled = Results.Count > 0;
            StatusText.Text = Results.Count == 0 ? $"No retry entries found in {dialog.FileNames.Length:N0} file(s)." : $"Found {Results.Count:N0} retry instance(s) across {Results.Select(x => x.Date).Distinct().Count():N0} date(s).";
        }
        catch (Exception exception) { StatusText.Text = $"Log analysis failed: {exception.Message}"; MessageBox.Show(exception.Message, "LOG Monitor", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void AddActivity(string message)
    {
        ActivityLog.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        Dispatcher.BeginInvoke(() => ActivityLogList.ScrollIntoView(ActivityLogList.Items[^1]));
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Export retry summary", Filter = "Excel workbook (*.xlsx)|*.xlsx", FileName = $"Image_NG_Retry_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            ExcelRetryReportWriter.Write(dialog.FileName, Results);
            StatusText.Text = $"Exported {Results.Count:N0} result(s) with daily summary charts to {dialog.FileName}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Excel export failed: {exception.Message}";
            MessageBox.Show(exception.Message, "LOG Monitor", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal static List<LogRetryResult> AnalyzeFile(string path)
    {
        var output = new List<LogRetryResult>();
        RetryGroup? group = null;
        foreach (var line in File.ReadLines(path))
        {
            var timeMatch = TimestampPattern.Match(line);
            if (!timeMatch.Success || !DateTime.TryParseExact(timeMatch.Groups["time"].Value, "yyyy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)) continue;

            var retryMatch = RetryPattern.Match(line);
            if (retryMatch.Success && int.TryParse(retryMatch.Groups["count"].Value, out var count) && int.TryParse(retryMatch.Groups["limit"].Value, out var limit))
            {
                var startsNew = group is null || count <= group.LastCount || timestamp - group.LastTimestamp > TimeSpan.FromSeconds(2.5);
                if (startsNew) { if (group is not null) output.Add(group.ToResult(path)); group = new RetryGroup(timestamp, count, limit); } else group!.Add(timestamp, count, limit);
                continue;
            }

            if (group is not null && group.Side == "Unknown" && timestamp - group.LastTimestamp <= TimeSpan.FromMilliseconds(250))
            {
                if (MinusPattern.IsMatch(line)) group.Side = "Anode";
                else if (PlusPattern.IsMatch(line)) group.Side = "Cathode";
            }
        }
        if (group is not null) output.Add(group.ToResult(path));
        return output;
    }

    private sealed class RetryGroup
    {
        private readonly DateTime _started;
        public DateTime LastTimestamp { get; private set; }
        public int LastCount { get; private set; }
        public int MaximumCount { get; private set; }
        public int Limit { get; private set; }
        public string Side { get; set; } = "Unknown";
        public RetryGroup(DateTime timestamp, int count, int limit) { _started = timestamp; Add(timestamp, count, limit); }
        public void Add(DateTime timestamp, int count, int limit) { LastTimestamp = timestamp; LastCount = count; MaximumCount = Math.Max(MaximumCount, count); Limit = Math.Max(Limit, limit); }
        public LogRetryResult ToResult(string path) => new(Path.GetFileName(path), path, _started, LastTimestamp, MaximumCount, Limit, Side);
    }
}

public sealed record LogRetryResult(string FileName, string FullPath, DateTime Started, DateTime Ended, int MaximumCount, int Limit, string Side)
{
    public DateTime Date => Started.Date;
    public TimeSpan Duration => Ended - Started;
    public bool ReachedFour => MaximumCount >= 4;
    public string DateText => Started.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
    public string TimeText => Started.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    public string DurationDisplay => $"{Duration.TotalSeconds:0.000} sec";
}
