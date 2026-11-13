using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Differ.App.Resources;
using Differ.App.ViewModels;

namespace Differ.App;

public partial class SpreadsheetWorksheetDiffView
{
    private ScrollContentPresenter? _baseBodyViewport;
    private ScrollContentPresenter? _mineBodyViewport;
    private DispatcherOperation? _railLayoutOperation;
    private bool _railEnabled;
    private int _railWheelRemainder;

    private void InitializeVerticalRail()
    {
        VerticalRail.DragStarted += BeginRailDrag;
        VerticalRail.DragCompleted += CompleteRailDrag;
        VerticalRail.ScrollRequested += ScrollFromRail;
        VerticalRail.WheelRequested += ScrollRailByWheel;
        VerticalRail.MarkerInvoked += SelectRailMarker;
        VerticalRail.PreviewChanged += ShowRailPreview;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) QueueRailLayout();
            else RailPreviewPopup.IsOpen = false;
        };
        SizeChanged += (_, _) => QueueRailLayout();
    }

    private void AttachRail()
    {
        _baseBodyViewport = _baseScrollViewer is { } left ? FindVisualChild<ScrollContentPresenter>(left) : null;
        _mineBodyViewport = _mineScrollViewer is { } right ? FindVisualChild<ScrollContentPresenter>(right) : null;
        if (_baseBodyViewport is not null) _baseBodyViewport.SizeChanged += OnRailViewportSizeChanged;
        if (_mineBodyViewport is not null) _mineBodyViewport.SizeChanged += OnRailViewportSizeChanged;
        QueueRailLayout();
    }

    private void DetachRail()
    {
        if (_baseBodyViewport is not null) _baseBodyViewport.SizeChanged -= OnRailViewportSizeChanged;
        if (_mineBodyViewport is not null) _mineBodyViewport.SizeChanged -= OnRailViewportSizeChanged;
        _baseBodyViewport = _mineBodyViewport = null;
        _railLayoutOperation?.Abort();
        _railLayoutOperation = null;
        RailPreviewPopup.IsOpen = false;
        VerticalRail.CancelViewportDrag();
        _railWheelRemainder = 0;
    }

    private void OnRailViewportSizeChanged(object sender, SizeChangedEventArgs e) => QueueRailLayout();

    private void QueueRailLayout()
    {
        if (!IsLoaded || !IsVisible || _railLayoutOperation?.Status == DispatcherOperationStatus.Pending)
        {
            return;
        }

        _railLayoutOperation = Dispatcher.BeginInvoke(UpdateRailLayout, DispatcherPriority.Render);
    }

    private void UpdateRailLayout()
    {
        _railLayoutOperation = null;
        if (!IsVisible) return;
        if (_baseBodyViewport is not { ActualHeight: > 0 } left
            || _mineBodyViewport is not { ActualHeight: > 0 } right
            || _mineScrollViewer is null || _baseScrollViewer is null)
        {
            SetRailEnabled(false);
            return;
        }

        // Both grids share the same layout row. Their real body presenters
        // exclude the column headers and the horizontal scrollbar.
        var leftBody = left.TransformToAncestor(BaseSpreadsheetGrid).TransformBounds(new Rect(left.RenderSize));
        var rightBody = right.TransformToAncestor(MineSpreadsheetGrid).TransformBounds(new Rect(right.RenderSize));
        var top = Math.Max(leftBody.Top, rightBody.Top);
        var bottom = Math.Min(leftBody.Bottom, rightBody.Bottom);
        if (top < 0 || bottom <= top)
        {
            SetRailEnabled(false);
            return;
        }

        var margin = new Thickness(0, top, 0, Math.Max(0, MineSpreadsheetGrid.ActualHeight - bottom));
        if (VerticalRail.Margin != margin)
        {
            CancelThumbDrag();
            VerticalRail.Margin = margin;
        }
        SetRailEnabled(true);
        UpdateRailViewport();
    }

    private void SetRailEnabled(bool enabled)
    {
        _railEnabled = enabled;
        VerticalRail.SetCurrentValue(VisibilityProperty, enabled ? Visibility.Visible : Visibility.Collapsed);
        var visibility = enabled ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        if (ScrollViewer.GetVerticalScrollBarVisibility(BaseSpreadsheetGrid) != visibility)
        {
            ScrollViewer.SetVerticalScrollBarVisibility(BaseSpreadsheetGrid, visibility);
            ScrollViewer.SetVerticalScrollBarVisibility(MineSpreadsheetGrid, visibility);
        }
        if (!enabled)
        {
            CancelThumbDrag();
            RailPreviewPopup.IsOpen = false;
        }
    }

    private void UpdateRailRows()
    {
        VerticalRail.UpdateRows(_viewModel?.ScrollableRows ?? []);
        VerticalRail.UpdateSelection(_viewModel?.SelectedRow?.StableId);
    }

    private void OnRailScrollChanged(ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
        {
            QueueRailLayout();
        }
        if (e.VerticalChange != 0 && _thumbDrag is null)
        {
            UpdateRailViewport(e.VerticalOffset);
        }
    }

    private void UpdateRailViewport(double? offset = null)
    {
        if (_railEnabled && _mineScrollViewer is { } scroll)
        {
            VerticalRail.UpdateViewport(scroll.ExtentHeight, scroll.ViewportHeight,
                offset ?? (_thumbDrag is { Horizontal: false } drag ? drag.VerticalOffset : scroll.ContentVerticalOffset));
        }
    }

    private void BeginRailDrag()
    {
        if (_mineScrollViewer is { } scroll)
        {
            TryBeginThumbDrag(null, scroll, horizontal: false);
        }
    }

    private void CompleteRailDrag()
    {
        if (_thumbDrag is { Thumb: null, Finishing: false } drag)
        {
            CommitThumbDrag(drag);
        }
    }

    private void ScrollFromRail(double offset)
    {
        if (_thumbDrag is { Thumb: null, Finishing: false } drag)
        {
            QueueThumbFrame(drag, Math.Floor(offset));
        }
        else if (_thumbDrag is null && _mineScrollViewer is { } scroll)
        {
            scroll.ScrollToVerticalOffset(Math.Floor(offset));
        }
    }

    private void ScrollRailByWheel(int delta)
    {
        if (_thumbDrag is not null || _mineScrollViewer is not { } scroll || delta == 0) return;
        var lines = SystemParameters.WheelScrollLines;
        const int wheelStep = 120;
        var unit = lines < 0 ? 1 : lines;
        var movement = (long)delta * unit + _railWheelRemainder;
        var steps = movement / wheelStep;
        _railWheelRemainder = (int)(movement % wheelStep);
        // Relative commands can queue multiple wheel ticks before the next layout pass.
        for (var step = 0L; step < Math.Abs(steps); step++)
        {
            if (lines < 0)
            {
                if (steps > 0) scroll.PageUp();
                else scroll.PageDown();
            }
            else if (steps > 0) scroll.LineUp();
            else scroll.LineDown();
        }
    }

    private void SelectRailMarker(SpreadsheetRowViewModel row)
    {
        var column = FirstChangedColumn(row);
        if (_viewModel is { } worksheet && column >= 0)
        {
            worksheet.SelectCell(row, column);
            QueueSelectionSynchronization();
        }
    }

    private void ShowRailPreview(SpreadsheetRowViewModel? row, double y)
    {
        if (!IsVisible || row is null)
        {
            RailPreviewPopup.IsOpen = false;
            return;
        }

        var column = FirstChangedColumn(row);
        if (column < 0)
        {
            RailPreviewPopup.IsOpen = false;
            return;
        }
        var left = row.BaseCells[column];
        var right = row.MineCells[column];
        RailPreviewAddress.Text = string.Join(" ↔ ", string.IsNullOrEmpty(left.Address) ? "—" : left.Address,
            string.IsNullOrEmpty(right.Address) ? "—" : right.Address);
        RailPreviewKind.Text = row.KindLabel;
        RailBasePreview.Text = PreviewCellText(left.Text);
        RailMinePreview.Text = PreviewCellText(right.Text);
        RailPreviewPopup.VerticalOffset = Math.Clamp(y * VerticalRail.ReadingScale - 46,
            0, Math.Max(0, VerticalRail.ActualHeight * VerticalRail.ReadingScale - 124));
        RailPreviewPopup.IsOpen = true;
    }

    private static int FirstChangedColumn(SpreadsheetRowViewModel row)
    {
        var count = Math.Min(row.BaseCells.Count, row.MineCells.Count);
        for (var index = 0; index < count; index++)
        {
            if (row.BaseCells[index].HasDifference || row.MineCells[index].HasDifference) return index;
        }
        return count > 0 ? 0 : -1;
    }

    private static string PreviewCellText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Strings.OverviewEmpty;
        const int limit = 180;
        if (value.Length <= limit) return value;
        var cut = char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit;
        return value[..cut] + "…";
    }
}
