using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KickoutMonitor.App.ViewModels;
using KickoutMonitor.Infrastructure;

namespace KickoutMonitor.App;

/// <summary>Shared WPF count chart. Nulls deliberately break segments.</summary>
public sealed class OverkillLineChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series),
        typeof(IReadOnlyList<OverkillSeries>), typeof(OverkillLineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<OverkillSeries>? Series { get => (IReadOnlyList<OverkillSeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    private readonly List<(Point Position, MachineDayCount Data)> _hits = [];
    public event Action<MachineDayCount>? PointClicked;
    public int RenderedPointCount => _hits.Count;
    public int RenderedSegmentCount { get; private set; }
    public OverkillLineChart() { ClipToBounds = true; MinHeight = 150; ToolTipService.SetInitialShowDelay(this, 100); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _hits.Clear(); RenderedSegmentCount = 0;
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        var series = Series ?? [];
        var points = series.SelectMany(s => s.Points).ToArray();
        var days = points.Select(p => p.Day).Distinct().Order().ToArray();
        var plot = new Rect(54, 27, Math.Max(1, ActualWidth - 78), Math.Max(1, ActualHeight - 69));
        void Text(string text, double x, double y, double size = 11, Brush? brush = null)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, brush ?? Brushes.SlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(x, y));
        }
        Text("과검 건수", 8, 4);
        var maximum = points.Select(p => p.Count ?? 0).DefaultIfEmpty(0).Max();
        var roughStep = Math.Max(1, maximum / 4.0);
        var power = Math.Pow(10, Math.Floor(Math.Log10(roughStep)));
        var step = new[] { 1d, 2d, 5d, 10d }.First(v => v * power >= roughStep) * power;
        var ceiling = Math.Max(step, Math.Ceiling(maximum / step) * step);
        double Y(int count) => plot.Bottom - count / ceiling * plot.Height;
        double X(int index) => days.Length <= 1 ? plot.Left + plot.Width / 2 : plot.Left + index * plot.Width / (days.Length - 1);
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(226, 232, 240)), 1);
        for (double tick = 0; tick <= ceiling; tick += step)
        {
            var y = plot.Bottom - tick / ceiling * plot.Height;
            dc.DrawLine(gridPen, new(plot.Left, y), new(plot.Right, y));
            Text(tick.ToString("N0"), 5, y - 8);
        }
        var stride = Math.Max(1, (int)Math.Ceiling(days.Length / Math.Max(2, plot.Width / 85)));
        for (var i = 0; i < days.Length; i++)
            if (i % stride == 0 || i == days.Length - 1)
                Text(days[i].ToString(i == 0 ? "yyyy-MM-dd" : "MM-dd"), Math.Clamp(X(i) - 25, 0, Math.Max(0, ActualWidth - 68)), plot.Bottom + 12);
        foreach (var s in series)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.Color));
            var pen = new Pen(brush, 1.8);
            Point? previous = null;
            foreach (var p in s.Points)
            {
                if (p.Count is not { } count) { previous = null; continue; }
                var position = new Point(X(Array.BinarySearch(days, p.Day)), Y(count));
                if (previous is { } last) { dc.DrawLine(pen, last, position); RenderedSegmentCount++; }
                dc.DrawEllipse(brush, new Pen(Brushes.White, 1), position, 4, 4);
                _hits.Add((position, p)); previous = position;
            }
        }
        if (_hits.Count == 0) Text(series.Count == 0 ? "범례에서 기계를 선택하세요" : "선택 기간에 자료가 없습니다", plot.Left + 18, plot.Top + 18, 13);
    }
    private IReadOnlyList<MachineDayCount> Near(Point position) => _hits
        .Where(h => (h.Position - position).Length <= 9).OrderBy(h => (h.Position - position).Length).Select(h => h.Data).ToArray();
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var points = Near(e.GetPosition(this));
        Cursor = points.Count > 0 ? Cursors.Hand : Cursors.Arrow;
        ToolTip = points.Count == 0 ? null : string.Join("\n", points.Select(p =>
            $"{p.Day:yyyy-MM-dd} · {p.Line} · {p.Field} · {p.Count:N0}건"));
    }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); ToolTip = null; }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var points = Near(e.GetPosition(this));
        if (points.Count == 1) PointClicked?.Invoke(points[0]);
        else if (points.Count > 1)
        {
            // Coincident zeros/counts stay individually accessible.
            var menu = new ContextMenu();
            foreach (var point in points)
            {
                var item = new MenuItem { Header = $"{point.Line} · {point.Count:N0}건" };
                item.Click += (_, _) => PointClicked?.Invoke(point);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }
        e.Handled = points.Count > 0;
    }
}
