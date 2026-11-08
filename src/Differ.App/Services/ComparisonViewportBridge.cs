using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Differ.App.ViewModels;

namespace Differ.App.Services;

internal sealed class ComparisonViewportBridge : IDisposable
{
    private readonly FrameworkElement _view;
    private readonly ScrollViewer _scroll;
    private readonly ItemsControl _items;
    private readonly DiffWindowViewModel? _owner;
    private DispatcherOperation? _update;
    private DispatcherOperation? _center;
    private bool _disposed;
    private readonly int _leadingPinnedItems;

    public ComparisonViewportBridge(FrameworkElement view, ScrollViewer scroll, ItemsControl items, int leadingPinnedItems = 0)
    {
        _view = view;
        _scroll = scroll;
        _items = items;
        _leadingPinnedItems = leadingPinnedItems;
        _owner = FindOwner(view);
        _scroll.ScrollChanged += OnScrollChanged;
        _view.IsVisibleChanged += OnVisibilityChanged;
        _view.SizeChanged += OnSizeChanged;
        if (_owner is not null)
        {
            _owner.OverviewScrollRequested += OnScrollRequested;
        }

        QueueUpdate();
    }

    public void Dispose()
    {
        _disposed = true;
        _scroll.ScrollChanged -= OnScrollChanged;
        _view.IsVisibleChanged -= OnVisibilityChanged;
        _view.SizeChanged -= OnSizeChanged;
        if (_owner is not null)
        {
            _owner.OverviewScrollRequested -= OnScrollRequested;
        }

        _update?.Abort();
        _center?.Abort();
    }

    internal static DiffWindowViewModel? FindOwner(DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: DiffWindowViewModel owner })
            {
                return owner;
            }
        }

        return null;
    }

    internal static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => QueueUpdate();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => QueueUpdate();
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => QueueUpdate();

    private void QueueUpdate()
    {
        if (_disposed || _owner is null || !_view.IsVisible || _update?.Status == DispatcherOperationStatus.Pending)
        {
            return;
        }

        _update = _view.Dispatcher.BeginInvoke(UpdateViewport, DispatcherPriority.Render);
    }

    private void UpdateViewport()
    {
        _update = null;
        if (_disposed || !_view.IsVisible || _owner is null)
        {
            return;
        }

        var first = int.MaxValue;
        var last = -1;
        foreach (var container in RealizedContainers(_items))
        {
            var index = _items.ItemContainerGenerator.IndexFromContainer(container);
            if (index < 0 || !container.IsVisible || container.ActualHeight <= 0)
            {
                continue;
            }

            var bounds = container.TransformToAncestor(_scroll)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (bounds.Bottom > 0 && bounds.Top < _scroll.ActualHeight)
            {
                first = Math.Min(first, index);
                last = Math.Max(last, index);
            }
        }

        if (last >= first && _items.Items.Count > 0)
        {
            var overviewCount = Math.Max(_items.Items.Count + _leadingPinnedItems, _owner.DiffRows.Count);
            _owner.SetOverviewViewport((double)(first + _leadingPinnedItems) / overviewCount, (double)(last + 1 + _leadingPinnedItems) / overviewCount);
        }
        else
        {
            var extent = Math.Max(_scroll.ExtentHeight, _scroll.ViewportHeight);
            _owner.SetOverviewViewport(extent > 0 ? _scroll.VerticalOffset / extent : 0,
                extent > 0 ? (_scroll.VerticalOffset + _scroll.ViewportHeight) / extent : 1);
        }
    }

    private static IEnumerable<FrameworkElement> RealizedContainers(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is DataGridRow or ListBoxItem)
            {
                yield return (FrameworkElement)child;
            }
            else
            {
                foreach (var container in RealizedContainers(child))
                {
                    yield return container;
                }
            }
        }
    }

    private void OnScrollRequested(object? sender, OverviewScrollEventArgs e)
    {
        if (!_view.IsVisible || _disposed)
        {
            return;
        }

        if (e.TargetFraction is { } position)
        {
            _center?.Abort();
            _center = _view.Dispatcher.BeginInvoke(() =>
            {
                _center = null;
                if (!_disposed && _view.IsVisible)
                {
                    var gridPosition = _items.Items.Count > 0
                        ? Math.Max(0, position * Math.Max(_items.Items.Count + _leadingPinnedItems, _owner?.DiffRows.Count ?? 0) - _leadingPinnedItems) / _items.Items.Count
                        : position;
                    _scroll.ScrollToVerticalOffset(Math.Max(0, gridPosition * _scroll.ExtentHeight - _scroll.ViewportHeight / 2));
                }
            }, DispatcherPriority.Render);
            return;
        }

        var steps = Math.Clamp(Math.Abs(e.WheelDelta) / 40, 1, 12);
        for (var index = 0; index < steps; index++)
        {
            if (e.WheelDelta > 0)
            {
                _scroll.LineUp();
            }
            else
            {
                _scroll.LineDown();
            }
        }
    }
}
