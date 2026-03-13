using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using carton.ViewModels;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace carton.Views.Pages;

public partial class LogsView : UserControl
{
    private const double BottomThreshold = 4;

    private ListBox? _logsListBox;
    private ScrollViewer? _scrollViewer;
    private LogsViewModel? _viewModel;
    private bool _autoScrollToBottom = true;
    private bool _pendingScrollToBottom;
    private bool _suppressScrollTracking;

    public LogsView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += OnDataContextChanged;
        LayoutUpdated += OnLayoutUpdated;
        AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _logsListBox ??= this.FindControl<ListBox>("LogsListBox");
        EnsureScrollViewerHooked();
        AttachViewModel(DataContext as LogsViewModel);
        RequestScrollToBottom();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachViewModel();
        if (_scrollViewer != null)
        {
            _scrollViewer.PropertyChanged -= OnScrollViewerPropertyChanged;
        }

        _scrollViewer = null;
        _pendingScrollToBottom = false;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        AttachViewModel(DataContext as LogsViewModel);
    }

    private void AttachViewModel(LogsViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        DetachViewModel();
        _viewModel = viewModel;
        if (_viewModel != null)
        {
            _viewModel.Logs.CollectionChanged += OnLogsCollectionChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _autoScrollToBottom = _viewModel.IsAutoScrollToLatest;
        }
    }

    private void DetachViewModel()
    {
        if (_viewModel != null)
        {
            _viewModel.Logs.CollectionChanged -= OnLogsCollectionChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }
    }

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_autoScrollToBottom)
        {
            RequestScrollToBottom();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LogsViewModel.IsAutoScrollToLatest) || _viewModel == null)
        {
            return;
        }

        _autoScrollToBottom = _viewModel.IsAutoScrollToLatest;
        if (_autoScrollToBottom)
        {
            RequestScrollToBottom();
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_pendingScrollToBottom)
        {
            return;
        }

        EnsureScrollViewerHooked();
        if (_scrollViewer == null || !_autoScrollToBottom)
        {
            return;
        }

        var maxOffsetY = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        _suppressScrollTracking = true;
        _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, maxOffsetY);
        _suppressScrollTracking = false;
        _pendingScrollToBottom = false;
    }

    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ScrollViewer.OffsetProperty ||
            _suppressScrollTracking ||
            sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        _autoScrollToBottom = IsAtBottom(scrollViewer);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_viewModel == null || !_viewModel.IsAutoScrollToLatest || _scrollViewer == null)
        {
            return;
        }

        if (e.Source is Visual sourceVisual &&
            sourceVisual.GetSelfAndVisualAncestors().Contains(_scrollViewer))
        {
            _viewModel.IsAutoScrollToLatest = false;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel == null || !_viewModel.IsAutoScrollToLatest)
        {
            return;
        }

        if (e.Source is Visual sourceVisual &&
            sourceVisual.GetSelfAndVisualAncestors().Any(visual => visual is ScrollBar or Thumb or Track))
        {
            _viewModel.IsAutoScrollToLatest = false;
        }
    }

    private void RequestScrollToBottom()
    {
        _pendingScrollToBottom = true;
        Dispatcher.UIThread.Post(() => { }, DispatcherPriority.Background);
    }

    private static bool IsAtBottom(ScrollViewer scrollViewer)
    {
        var maxOffsetY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        return maxOffsetY - scrollViewer.Offset.Y <= BottomThreshold;
    }

    private void EnsureScrollViewerHooked()
    {
        _logsListBox ??= this.FindControl<ListBox>("LogsListBox");
        var scrollViewer = _logsListBox?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (ReferenceEquals(_scrollViewer, scrollViewer))
        {
            return;
        }

        if (_scrollViewer != null)
        {
            _scrollViewer.PropertyChanged -= OnScrollViewerPropertyChanged;
        }

        _scrollViewer = scrollViewer;
        if (_scrollViewer != null)
        {
            _scrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
        }
    }
}
