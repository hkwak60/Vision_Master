using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using KickoutMonitor.App.ViewModels;

namespace KickoutMonitor.App;

public partial class FlaggedReviewView : UserControl
{
    private const double MinimumZoom = 0.2;
    private const double MaximumZoom = 5.0;
    private const double DefaultZoom = 0.6;
    private double _zoom = DefaultZoom;
    private bool _isDragging;
    private Point _dragStart;
    private double _dragHorizontalOffset;
    private double _dragVerticalOffset;
    private INotifyCollectionChanged? _activityLogSource;
    private INotifyPropertyChanged? _viewModelSource;

    public FlaggedReviewView()
    {
        InitializeComponent();
        DataContextChanged += FlaggedReviewView_DataContextChanged;
        Loaded += (_, _) => Focus();
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(FlaggedReviewView_PreviewKeyDown), true);
    }

    public event EventHandler? BackRequested;

    private void BackButton_Click(object sender, RoutedEventArgs e) =>
        BackRequested?.Invoke(this, EventArgs.Empty);

    private void FlaggedReviewView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_activityLogSource is not null) _activityLogSource.CollectionChanged -= ActivityLog_CollectionChanged;
        if (_viewModelSource is FlaggedReviewViewModel oldViewModel)
        {
            oldViewModel.PreviewImageChanging -= ViewModel_PreviewImageChanging;
            oldViewModel.PreviewImageLoaded -= ViewModel_PreviewImageLoaded;
        }

        var viewModel = e.NewValue as FlaggedReviewViewModel;
        _activityLogSource = viewModel?.ActivityLog;
        _viewModelSource = viewModel;
        if (_activityLogSource is not null) _activityLogSource.CollectionChanged += ActivityLog_CollectionChanged;
        if (viewModel is not null)
        {
            viewModel.PreviewImageChanging += ViewModel_PreviewImageChanging;
            viewModel.PreviewImageLoaded += ViewModel_PreviewImageLoaded;
        }
    }

    private void ViewModel_PreviewImageChanging(object? sender, EventArgs e)
    {
    }

    private void ViewModel_PreviewImageLoaded(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SetZoom(DefaultZoom));

    private void ActivityLog_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ActivityLogList.Items.Count == 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ActivityLogList.ScrollIntoView(ActivityLogList.Items[^1]));
    }

    private void FlaggedReviewView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers is not ModifierKeys.None) return;
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox) return;
        if (DataContext is not FlaggedReviewViewModel viewModel) return;
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.R or Key.U or Key.N or Key.Enter
            or Key.D1 or Key.D2 or Key.D3 or Key.D4 or Key.D5 or Key.D6
            or Key.NumPad1 or Key.NumPad2 or Key.NumPad3 or Key.NumPad4 or Key.NumPad5 or Key.NumPad6)
        {
            viewModel.HandleHotkey(e.Key);
            e.Handled = true;
        }
    }

    private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var oldZoom = _zoom;
        var newZoom = Math.Clamp(oldZoom + (e.Delta > 0 ? 0.1 : -0.1), MinimumZoom, MaximumZoom);
        if (Math.Abs(newZoom - oldZoom) < 0.001) return;
        SetZoom(newZoom);
        e.Handled = true;
    }

    private void ReviewImage_TargetUpdated(object sender, System.Windows.Data.DataTransferEventArgs e)
    {
    }

    private void ImageScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _dragStart = e.GetPosition(ImageScrollViewer);
        _dragHorizontalOffset = ImageScrollViewer.HorizontalOffset;
        _dragVerticalOffset = ImageScrollViewer.VerticalOffset;
        ImageScrollViewer.Cursor = Cursors.SizeAll;
        ImageScrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void ImageScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(ImageScrollViewer);
        ImageScrollViewer.ScrollToHorizontalOffset(_dragHorizontalOffset - (current.X - _dragStart.X));
        ImageScrollViewer.ScrollToVerticalOffset(_dragVerticalOffset - (current.Y - _dragStart.Y));
        e.Handled = true;
    }

    private void ImageScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndImageDrag();
        e.Handled = true;
    }

    private void ImageScrollViewer_LostMouseCapture(object sender, MouseEventArgs e) =>
        EndImageDrag();

    private void EndImageDrag()
    {
        if (!_isDragging) return;
        _isDragging = false;
        ImageScrollViewer.Cursor = Cursors.Hand;
        if (ImageScrollViewer.IsMouseCaptured) ImageScrollViewer.ReleaseMouseCapture();
    }

    private void SetZoom(double value)
    {
        _zoom = value;
        ImageScale.ScaleX = value;
        ImageScale.ScaleY = value;
        ZoomText.Text = $"{value:P0}";
    }
}
