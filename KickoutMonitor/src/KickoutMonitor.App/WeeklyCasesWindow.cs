using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KickoutMonitor.Infrastructure;

namespace KickoutMonitor.App;

public sealed class WeeklyCasesWindow : Window
{
    private readonly WeeklyReportCaseStore _store;
    private readonly DateOnly _start, _end;
    private readonly ListBox _list = new() { MinWidth = 250 };
    private readonly ComboBox _images = new() { MinWidth = 200 };
    private readonly TextBox _reason = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 65, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _action = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 65, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Image _preview = new() { Height = 230, Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkOrange };
    private WeeklyReportCase? _current;
    public WeeklyCasesWindow(WeeklyReportCaseStore store, DateOnly start, DateOnly end, WeeklyReportCase? added = null)
    {
        _store = store; _start = start; _end = end;
        Title = $"대표 사례 · {start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}";
        Width = 1050; Height = 720; MinWidth = 760; MinHeight = 580;
        Background = Brushes.White; Foreground = new SolidColorBrush(Color.FromRgb(23, 43, 77));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(14), Background = Brushes.White };
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        Button Add(string text, Action action) { var b = new Button { Content = text, Margin = new Thickness(4), Padding = new Thickness(12, 5, 12, 5) }; b.Click += (_, _) => { try { action(); } catch (Exception e) { _status.Text = e.Message; } }; buttons.Children.Add(b); return b; }
        Add("저장 / 이미지 다시 시도", Save);
        Add("위로", () => Move(-1)); Add("아래로", () => Move(1));
        Add("삭제", () => { if (_current is null) return; _store.Remove(_current.Id); _current = null; Reload(); Select(null); });
        Add("닫기", Close);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        var title = new TextBlock { Text = "기간·호기별 최대 3개. 사진은 해당 검사 폴더에서만 선택합니다. 수정 후 저장하세요.", Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        _list.Width = 300; _list.Margin = new Thickness(0, 0, 15, 0); DockPanel.SetDock(_list, Dock.Left); root.Children.Add(_list);
        var editor = new StackPanel();
        editor.Children.Add(new TextBlock { Text = "대표 이미지" }); editor.Children.Add(_images); editor.Children.Add(_preview);
        editor.Children.Add(new TextBlock { Text = "과검 사유" }); editor.Children.Add(_reason);
        editor.Children.Add(new TextBlock { Text = "대책" }); editor.Children.Add(_action);
        root.Children.Add(new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        _list.SelectionChanged += (_, _) => Select(_list.SelectedItem as WeeklyReportCase);
        _images.SelectionChanged += (_, _) => Preview(_images.SelectedItem as string ?? _current?.LocalImage);
        Reload();
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        if (added is not null) { _list.Items.Add(added); _list.SelectedItem = added; }
    }
    private void Reload()
    {
        _list.Items.Clear();
        foreach (var item in _store.Load().Where(x => x.Start == _start && x.End == _end).OrderBy(x => x.Line).ThenBy(x => x.Order).ThenBy(x => x.Id))
            _list.Items.Add(item);
    }
    private void Select(WeeklyReportCase? item)
    {
        _current = item; _reason.Text = item?.Reason ?? ""; _action.Text = item?.Action ?? "";
        _images.ItemsSource = item is null ? Array.Empty<string>() : WeeklyReportCaseStore.Images(item.SourceFolder);
        _images.SelectedItem = item?.SourceImage;
        _status.Text = item?.Error ?? "";
        Preview(item?.LocalImage);
    }
    private void Preview(string? path)
    {
        _preview.Source = null;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
        try { var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path); image.EndInit(); image.Freeze(); _preview.Source = image; }
        catch (Exception e) { _status.Text = "이미지 표시 실패: " + e.Message; }
    }
    private void Save()
    {
        if (_current is null) return;
        var item = _current;
        item.Reason = _reason.Text; item.Action = _action.Text;
        item.SourceImage = _images.SelectedItem as string ?? item.SourceImage;
        _store.Save(item);
        Reload(); _list.SelectedItem = _list.Items.Cast<WeeklyReportCase>().First(x => x.Id == item.Id);
        _status.Text = string.IsNullOrEmpty(item.Error) ? "저장되었습니다." : item.Error;
    }
    private void Move(int direction)
    {
        if (_current is null || !_store.Load().Any(x => x.Id == _current.Id)) return;
        var id = _current.Id; _store.Move(id, direction); Reload();
        _list.SelectedItem = _list.Items.Cast<WeeklyReportCase>().First(x => x.Id == id);
    }
}
