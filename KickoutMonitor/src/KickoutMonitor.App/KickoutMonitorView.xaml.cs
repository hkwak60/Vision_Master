using System.Windows;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using KickoutMonitor.App.ViewModels;

namespace KickoutMonitor.App;

public partial class KickoutMonitorView : UserControl
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
    private bool _resetViewForCell = true;
    private bool _restoreImageViewport;
    private double _savedZoom = DefaultZoom;
    private double _savedCenterX = 0.5;
    private double _savedCenterY = 0.5;

    public KickoutMonitorView()
    {
        InitializeComponent();
        DataContextChanged += MainWindow_DataContextChanged;
    }

    public event EventHandler? BackRequested;

    private void BackButton_Click(object sender, RoutedEventArgs e) =>
        BackRequested?.Invoke(this, EventArgs.Empty);

    private void MainWindow_DataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (_activityLogSource is not null)
        {
            _activityLogSource.CollectionChanged -= ActivityLog_CollectionChanged;
        }
        if (_viewModelSource is not null)
        {
            _viewModelSource.PropertyChanged -= ViewModel_PropertyChanged;
            if (_viewModelSource is MainViewModel oldViewModel)
            {
                oldViewModel.PreviewImageChanging -= ViewModel_PreviewImageChanging;
                oldViewModel.PreviewImageLoaded -= ViewModel_PreviewImageLoaded;
            }
        }

        var viewModel = e.NewValue as MainViewModel;
        _activityLogSource = viewModel?.ConnectionLog;
        _viewModelSource = viewModel;
        if (_activityLogSource is not null)
        {
            _activityLogSource.CollectionChanged += ActivityLog_CollectionChanged;
        }
        if (_viewModelSource is not null)
        {
            _viewModelSource.PropertyChanged += ViewModel_PropertyChanged;
            if (_viewModelSource is MainViewModel newViewModel)
            {
                newViewModel.PreviewImageChanging += ViewModel_PreviewImageChanging;
                newViewModel.PreviewImageLoaded += ViewModel_PreviewImageLoaded;
            }
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedCandidate))
        {
            _resetViewForCell = true;
            _restoreImageViewport = false;
        }
    }

    private void ViewModel_PreviewImageChanging(object? sender, EventArgs e)
    {
        if (!_resetViewForCell) CaptureImageViewport();
    }

    private void ViewModel_PreviewImageLoaded(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ApplyImageViewport);

    private void ActivityLog_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (ActivityLogList.Items.Count == 0) return;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () => ActivityLogList.ScrollIntoView(
                ActivityLogList.Items[ActivityLogList.Items.Count - 1]));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or DatePicker) return;
        if (DataContext is not MainViewModel viewModel) return;
        if (e.Key is Key.R or Key.O or Key.M or Key.I or Key.Left or Key.Right or Key.Up or Key.Down)
        {
            viewModel.HandleHotkey(e.Key);
            e.Handled = true;
        }
    }

    private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                ImageScrollViewer.ScrollToHorizontalOffset(
                    ImageScrollViewer.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
            return;
        }

        var pointer = e.GetPosition(ImageScrollViewer);
        var oldZoom = _zoom;
        var change = e.Delta > 0 ? 0.1 : -0.1;
        var newZoom = Math.Clamp(oldZoom + change, MinimumZoom, MaximumZoom);
        if (Math.Abs(newZoom - oldZoom) < 0.001) return;

        var contentX = ImageScrollViewer.HorizontalOffset + pointer.X;
        var contentY = ImageScrollViewer.VerticalOffset + pointer.Y;
        SetZoom(newZoom);

        var ratio = newZoom / oldZoom;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                ImageScrollViewer.ScrollToHorizontalOffset(contentX * ratio - pointer.X);
                ImageScrollViewer.ScrollToVerticalOffset(contentY * ratio - pointer.Y);
            });
        e.Handled = true;
    }

    private void ReviewImage_TargetUpdated(object sender, System.Windows.Data.DataTransferEventArgs e)
    {
    }

    private void ApplyImageViewport()
    {
        if (ReviewImage.Source is null) return;

        if (_restoreImageViewport)
        {
            SetZoom(_savedZoom);
            _restoreImageViewport = false;
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                () =>
                {
                    ImageScrollViewer.ScrollToHorizontalOffset(
                        _savedCenterX * Math.Max(1, ImageScrollViewer.ExtentWidth)
                        - ImageScrollViewer.ViewportWidth / 2);
                    ImageScrollViewer.ScrollToVerticalOffset(
                        _savedCenterY * Math.Max(1, ImageScrollViewer.ExtentHeight)
                        - ImageScrollViewer.ViewportHeight / 2);
                });
            return;
        }

        SetZoom(DefaultZoom);
        _resetViewForCell = false;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                ImageScrollViewer.ScrollToHorizontalOffset(0);
                ImageScrollViewer.ScrollToVerticalOffset(0);
            });
    }

    private void CaptureImageViewport()
    {
        if (ReviewImage.Source is null) return;
        _savedZoom = _zoom;
        _savedCenterX = ImageScrollViewer.ExtentWidth > 0
            ? (ImageScrollViewer.HorizontalOffset + ImageScrollViewer.ViewportWidth / 2)
              / ImageScrollViewer.ExtentWidth
            : 0.5;
        _savedCenterY = ImageScrollViewer.ExtentHeight > 0
            ? (ImageScrollViewer.VerticalOffset + ImageScrollViewer.ViewportHeight / 2)
              / ImageScrollViewer.ExtentHeight
            : 0.5;
        _savedCenterX = Math.Clamp(_savedCenterX, 0, 1);
        _savedCenterY = Math.Clamp(_savedCenterY, 0, 1);
        _restoreImageViewport = true;
    }

    private void ImageScrollViewer_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
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
        ImageScrollViewer.ScrollToHorizontalOffset(
            _dragHorizontalOffset - (current.X - _dragStart.X));
        ImageScrollViewer.ScrollToVerticalOffset(
            _dragVerticalOffset - (current.Y - _dragStart.Y));
        e.Handled = true;
    }

    private void ImageScrollViewer_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
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
