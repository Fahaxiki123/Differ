using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Differ.App.Controls;

namespace Differ.App;

public partial class SpreadsheetWorksheetDiffView
{
    private const int DragSurfaceRowThreshold = 256;
    private const int DragSurfaceColumnThreshold = 32;
    private ThumbDrag? _thumbDrag;
    private DispatcherOperation? _thumbFrame;
    private DispatcherOperation? _thumbSettle;

    private void InitializeThumbDrag()
    {
        AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnThumbDragStarted), true);
        AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnThumbDragCompleted), true);
        CommandManager.AddPreviewExecutedHandler(this, OnThumbScrollCommand);
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) CancelThumbDrag();
        };
        SizeChanged += (_, _) => CancelThumbDrag();
        PreviewMouseDown += (_, e) =>
        {
            if (_thumbDrag?.Finishing == true) e.Handled = true;
        };
        PreviewMouseWheel += (_, e) =>
        {
            if (_thumbDrag is not null) e.Handled = true;
        };
    }

    private void OnThumbDragStarted(object sender, DragStartedEventArgs e)
    {
        if (e.OriginalSource is not Thumb thumb || FindAncestor<ScrollBar>(thumb) is not { } bar
            || bar.TemplatedParent is not ScrollViewer origin
            || _viewModel is not { } worksheet
            || (worksheet.ScrollableRows.Count < DragSurfaceRowThreshold && worksheet.Columns.Count < DragSurfaceColumnThreshold))
        {
            return;
        }

        TryBeginThumbDrag(thumb, origin, bar.Orientation == Orientation.Horizontal);
    }

    private void TryBeginThumbDrag(Thumb? thumb, ScrollViewer origin, bool horizontal)
    {
        if (_baseScrollViewer is not { } left || _mineScrollViewer is not { } right
            || (!ReferenceEquals(origin, left) && !ReferenceEquals(origin, right))
            || _viewModel is not { } worksheet
            || _thumbDrag is not null)
        {
            return;
        }

        var leftPane = GetDragPane(left, BaseSpreadsheetGrid, _baseHeaderGrid, horizontal);
        var rightPane = GetDragPane(right, MineSpreadsheetGrid, _mineHeaderGrid, horizontal);
        if (!DragSurface.Begin(worksheet, leftPane, rightPane, BaseSpreadsheetGrid.RowHeight))
        {
            return;
        }

        _scrollOperation?.Abort();
        _scrollOperation = null;
        _pendingVerticalScroll = _pendingHorizontalScroll = false;
        var follower = ReferenceEquals(origin, left) ? right : left;
        var drag = new ThumbDrag(thumb, origin, follower, horizontal);
        _thumbDrag = drag;
        if (thumb is not null)
        {
            SetDeferred(origin, true);
            SetDeferred(follower, true);
        }
        DragSurface.Move(drag.VerticalOffset, drag.HorizontalOffset);
    }

    private SpreadsheetDragSurface.Pane GetDragPane(ScrollViewer scroll, DataGrid grid, DataGrid? frozen, bool horizontal)
    {
        var body = BoundsInDragSurface(FindVisualChild<ScrollContentPresenter>(scroll));
        if (!horizontal || body.IsEmpty)
        {
            return new SpreadsheetDragSurface.Pane(body, Rect.Empty, Rect.Empty);
        }

        var headers = BoundsInDragSurface(FindVisualChild<DataGridColumnHeadersPresenter>(frozen ?? grid));
        var frozenRows = frozen is null ? Rect.Empty : BoundsInDragSurface(FindVisualChild<ScrollContentPresenter>(frozen));
        return new SpreadsheetDragSurface.Pane(body, ClipToBodyWidth(headers, body), ClipToBodyWidth(frozenRows, body));
    }

    private Rect BoundsInDragSurface(FrameworkElement? element) =>
        element is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 }
            ? element.TransformToVisual(DragSurface).TransformBounds(new Rect(element.RenderSize))
            : Rect.Empty;

    private static Rect ClipToBodyWidth(Rect area, Rect body)
    {
        if (!area.IsEmpty)
        {
            area.Intersect(new Rect(body.X, area.Y, body.Width, area.Height));
        }

        return area;
    }

    private void OnThumbScrollCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (_thumbDrag is not { Thumb: not null, Finishing: false } drag || !ReferenceEquals(e.OriginalSource, drag.Origin)
            || e.Parameter is not double offset || !double.IsFinite(offset))
        {
            return;
        }

        if (e.Command == drag.DeferredCommand)
        {
            // Let WPF move the native thumb without realizing grid containers.
            // The command parameter is the requested offset even before bindings update.
            QueueThumbFrame(drag, offset);
        }
        else if (e.Command == drag.CommitCommand)
        {
            // ScrollBar emits a final command before DragCompleted reaches us.
            // Commit the exact last requested position together on both grids.
            e.Handled = true;
        }
    }

    private void QueueThumbFrame(ThumbDrag drag, double offset)
    {
        drag.SetOffset(offset);
        if (_thumbFrame?.Status != DispatcherOperationStatus.Pending)
        {
            _thumbFrame = Dispatcher.BeginInvoke(RenderThumbFrame, DispatcherPriority.Render);
        }
    }

    private void RenderThumbFrame()
    {
        _thumbFrame = null;
        if (_thumbDrag is not { } drag)
        {
            return;
        }

        DragSurface.Move(drag.VerticalOffset, drag.HorizontalOffset);
        if (drag.Thumb is not null) drag.DeferredCommand.Execute(drag.Offset, drag.Follower);
        if (!drag.Horizontal) UpdateRailViewport();
    }

    private void OnThumbDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_thumbDrag is not { } drag || !ReferenceEquals(e.OriginalSource, drag.Thumb))
        {
            return;
        }

        CommitThumbDrag(drag);
    }

    private void CommitThumbDrag(ThumbDrag drag)
    {
        _thumbFrame?.Abort();
        RenderThumbFrame();
        drag.Finishing = true;
        if (drag.Thumb is not null) RestoreDeferral(drag);
        if (drag.Horizontal)
        {
            drag.Origin.ScrollToHorizontalOffset(drag.HorizontalOffset);
            drag.Follower.ScrollToHorizontalOffset(drag.HorizontalOffset);
            AlignHorizontalOffset(_baseHeaderScroll, drag.HorizontalOffset);
            AlignHorizontalOffset(_mineHeaderScroll, drag.HorizontalOffset);
        }
        else
        {
            var row = Math.Floor(drag.VerticalOffset);
            drag.Origin.ScrollToVerticalOffset(row);
            drag.Follower.ScrollToVerticalOffset(row);
        }

        // ScrollViewer applies its offset commands during layout, at Render
        // priority. Keep both painted panes visible until those passes finish.
        _thumbSettle = Dispatcher.BeginInvoke(EndThumbSurface, DispatcherPriority.ContextIdle);
    }

    private void CancelThumbDrag()
    {
        VerticalRail.CancelViewportDrag();
        if (_thumbDrag is not { } drag)
        {
            return;
        }

        EndThumbSurface();
        if (drag.Thumb is not null && !drag.Finishing)
        {
            // Restore the parked scrollbar indicators before leaving deferred mode.
            drag.DeferredCommand.Execute(drag.ParkedOffset(drag.Origin), drag.Origin);
            drag.DeferredCommand.Execute(drag.ParkedOffset(drag.Follower), drag.Follower);
            RestoreDeferral(drag);
        }
    }

    private void EndThumbSurface()
    {
        _thumbFrame?.Abort();
        _thumbSettle?.Abort();
        _thumbFrame = _thumbSettle = null;
        _thumbDrag = null;
        DragSurface.End();
        UpdateRailViewport();
    }

    private static void SetDeferred(ScrollViewer viewer, bool enabled) =>
        viewer.SetCurrentValue(ScrollViewer.IsDeferredScrollingEnabledProperty, enabled);

    private static void RestoreDeferral(ThumbDrag drag)
    {
        SetDeferred(drag.Origin, drag.OriginDeferred);
        SetDeferred(drag.Follower, drag.FollowerDeferred);
    }

    private sealed class ThumbDrag(Thumb? thumb, ScrollViewer origin, ScrollViewer follower, bool horizontal)
    {
        public Thumb? Thumb { get; } = thumb;
        public ScrollViewer Origin { get; } = origin;
        public ScrollViewer Follower { get; } = follower;
        public bool Horizontal { get; } = horizontal;
        public bool OriginDeferred { get; } = origin.IsDeferredScrollingEnabled;
        public bool FollowerDeferred { get; } = follower.IsDeferredScrollingEnabled;
        public double VerticalOffset { get; private set; } = origin.ContentVerticalOffset;
        public double HorizontalOffset { get; private set; } = origin.ContentHorizontalOffset;
        public double Offset => Horizontal ? HorizontalOffset : VerticalOffset;
        public bool Finishing { get; set; }
        public RoutedCommand DeferredCommand => Horizontal ? ScrollBar.DeferScrollToHorizontalOffsetCommand : ScrollBar.DeferScrollToVerticalOffsetCommand;
        public RoutedCommand CommitCommand => Horizontal ? ScrollBar.ScrollToHorizontalOffsetCommand : ScrollBar.ScrollToVerticalOffsetCommand;

        public void SetOffset(double value)
        {
            if (Horizontal) HorizontalOffset = Math.Clamp(value, 0, Origin.ScrollableWidth);
            else VerticalOffset = Math.Clamp(value, 0, Origin.ScrollableHeight);
        }

        public double ParkedOffset(ScrollViewer viewer) => Horizontal ? viewer.ContentHorizontalOffset : viewer.ContentVerticalOffset;
    }
}
