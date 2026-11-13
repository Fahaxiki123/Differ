using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.Models;

namespace Differ.App.Controls;

/// <summary>The single vertical scroll surface for the two aligned worksheet panes.</summary>
public sealed class SpreadsheetScrollRail : FrameworkElement
{
    internal const double RailWidth = 16;
    private const double MarkerPitch = 4;
    private const double BandHitSlop = 4;
    private static readonly DependencyProperty ThemeRevisionProperty = DependencyProperty.Register(
        "ThemeRevision", typeof(object), typeof(SpreadsheetScrollRail),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (source, _) => ((SpreadsheetScrollRail)source)._markerDrawing = null));

    public static readonly DependencyProperty ReadingScaleProperty = DependencyProperty.Register(
        nameof(ReadingScale), typeof(double), typeof(SpreadsheetScrollRail),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsMeasure,
            (source, _) => ((SpreadsheetScrollRail)source).RebuildMarkers()));

    private IReadOnlyList<SpreadsheetRowViewModel> _rows = [];
    private readonly Dictionary<string, int> _changedPositions = new(StringComparer.Ordinal);
    private int[] _markers = [];
    private DrawingGroup? _markerDrawing;
    private string? _selectedRowId;
    private int _selectedIndex = -1;
    private int _hoverIndex = -1;
    private double _extent;
    private double _viewport;
    private double _offset;
    private double _dragOriginY;
    private double _dragOriginOffset;
    private double _pressY;
    private int _pressedMarker = -1;
    private bool _bandPressed;
    private bool _dragging;

    public SpreadsheetScrollRail()
    {
        Focusable = true;
        Cursor = Cursors.Arrow;
        SnapsToDevicePixels = true;
        SetResourceReference(ThemeRevisionProperty, ThemeService.ThemeRevisionKey);
    }

    public double ReadingScale
    {
        get => (double)GetValue(ReadingScaleProperty);
        set => SetValue(ReadingScaleProperty, value);
    }

    internal event Action? DragStarted;
    internal event Action? DragCompleted;
    internal event Action<double>? ScrollRequested;
    internal event Action<int>? WheelRequested;
    internal event Action<SpreadsheetRowViewModel>? MarkerInvoked;
    internal event Action<SpreadsheetRowViewModel?, double>? PreviewChanged;

    private double Scale => double.IsFinite(ReadingScale) && ReadingScale > 0 ? ReadingScale : 1;
    private double Maximum => Math.Max(0, _extent - _viewport);
    private double PositionFor(double offset) => _extent > 0 ? offset / _extent * ActualHeight : 0;
    internal Rect ViewportBand => _extent > 0
        ? new Rect(0, PositionFor(_offset), ActualWidth, PositionFor(_viewport))
        : new Rect(RenderSize);

    internal void UpdateRows(IReadOnlyList<SpreadsheetRowViewModel> rows)
    {
        if (ReferenceEquals(rows, _rows)) return;
        _rows = rows;
        _changedPositions.Clear();
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].Kind != DiffKind.Unchanged)
            {
                _changedPositions[rows[index].StableId] = index;
            }
        }
        _selectedIndex = _selectedRowId is null ? -1 : _changedPositions.GetValueOrDefault(_selectedRowId, -1);
        RebuildMarkers();
    }

    internal void UpdateSelection(string? stableId)
    {
        if (_selectedRowId == stableId) return;
        _selectedRowId = stableId;
        _selectedIndex = stableId is null ? -1 : _changedPositions.GetValueOrDefault(stableId, -1);
        _markerDrawing = null;
        InvalidateVisual();
    }

    internal void UpdateViewport(double extent, double viewport, double offset)
    {
        if (!double.IsFinite(extent) || !double.IsFinite(viewport) || !double.IsFinite(offset)) return;
        extent = Math.Max(0, extent);
        viewport = Math.Clamp(viewport, 0, extent);
        offset = Math.Clamp(offset, 0, extent - viewport);
        if (extent == _extent && viewport == _viewport && offset == _offset) return;
        var extentChanged = extent != _extent;
        (_extent, _viewport, _offset) = (extent, viewport, offset);
        if (extentChanged) RebuildMarkers();
        else InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(RailWidth / Scale, 0);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RebuildMarkers();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var line = 1 / Scale;
        var height = ActualHeight;
        var width = ActualWidth;
        drawingContext.DrawRectangle(BrushFor("PanelBrush") ?? Brushes.Transparent, null, new Rect(RenderSize));
        drawingContext.DrawRectangle(BrushFor("Diff.Rail.Divider"), null, new Rect(0, 0, line, height));

        var band = ViewportBand;
        var edge = BrushFor("Diff.Rail.Edge");
        if (band.Height > 0)
        {
            var bandTop = Math.Min(band.Top, Math.Max(0, height - line));
            var bandHeight = Math.Min(height - bandTop, Math.Max(line, band.Height));
            drawingContext.DrawRectangle(BrushFor(IsMouseOver || _dragging ? "Diff.Rail.BandHover" : "Diff.Rail.Band"),
                null, new Rect(line, bandTop, Math.Max(0, width - line), bandHeight));
            drawingContext.DrawRectangle(edge, null, new Rect(line, bandTop, Math.Max(0, width - line), Math.Min(line, bandHeight)));
            drawingContext.DrawRectangle(edge, null, new Rect(line, Math.Max(bandTop, bandTop + bandHeight - line),
                Math.Max(0, width - line), Math.Min(line, bandHeight)));
        }

        drawingContext.DrawDrawing(_markerDrawing ??= DrawMarkers());
    }

    private DrawingGroup DrawMarkers()
    {
        var drawing = new DrawingGroup();
        using var drawingContext = drawing.Open();
        var width = ActualWidth;
        var height = ActualHeight;
        var line = 1 / Scale;
        var previousCenter = double.NegativeInfinity;
        for (var bucket = 0; bucket < _markers.Length; bucket++)
        {
            var rowIndex = MarkerAt(bucket);
            if (rowIndex < 0) continue;
            var row = _rows[rowIndex];
            var center = PositionFor(rowIndex + 0.5);
            var nextCenter = double.PositiveInfinity;
            for (var next = bucket + 1; next < _markers.Length; next++)
            {
                var nextRow = MarkerAt(next);
                if (nextRow < 0) continue;
                nextCenter = PositionFor(nextRow + 0.5);
                break;
            }
            var gap = Math.Min(center - previousCenter, nextCenter - center);
            var thickness = Math.Min(height, Math.Clamp(gap * 0.45, 2 * line, 6 * line));
            var y = Math.Clamp(center - thickness / 2, 0, height - thickness);
            previousCenter = center;
            var start = row.Kind == DiffKind.Added ? width / 2 : 3 * line;
            var end = row.Kind == DiffKind.Deleted ? width / 2 : width - 2 * line;
            if (end > start)
            {
                drawingContext.DrawRectangle(MarkerBrush(row.Kind), null, new Rect(start, y, end - start, thickness));
            }
            if (rowIndex == _selectedIndex)
            {
                drawingContext.DrawRectangle(BrushFor("Dui.Text.Primary"), null,
                    new Rect(0, y, 2 * line, thickness));
            }
        }
        return drawing;
    }

    private void RebuildMarkers()
    {
        var bucketCount = Math.Max(1, (int)Math.Ceiling(ActualHeight * Scale / MarkerPitch));
        _markers = new int[bucketCount];
        Array.Fill(_markers, -1);
        if (_extent > 0)
        {
            foreach (var index in _changedPositions.Values)
            {
                var bucket = MarkerBucket(index);
                if (_markers[bucket] < 0) _markers[bucket] = index;
            }
        }
        ClearHover();
        _markerDrawing = null;
        InvalidateVisual();
    }

    private int MarkerBucket(int rowIndex) =>
        Math.Clamp((int)((rowIndex + 0.5) / _extent * _markers.Length), 0, _markers.Length - 1);

    private int MarkerAt(int bucket) =>
        _extent > 0 && _selectedIndex >= 0 && MarkerBucket(_selectedIndex) == bucket ? _selectedIndex : _markers[bucket];

    private int FindMarker(double y)
    {
        if (ActualHeight <= 0) return -1;
        var bucket = Math.Clamp((int)(y / ActualHeight * _markers.Length), 0, Math.Max(0, _markers.Length - 1));
        var nearest = -1;
        var distance = 4 / Scale;
        for (var index = Math.Max(0, bucket - 1); index <= Math.Min(_markers.Length - 1, bucket + 1); index++)
        {
            var row = MarkerAt(index);
            if (row < 0) continue;
            var candidate = Math.Abs(PositionFor(row + 0.5) - y);
            if (candidate <= distance)
            {
                nearest = row;
                distance = candidate;
            }
        }
        return nearest;
    }

    internal bool BeginViewportDrag(double pointerY)
    {
        if (_dragging || Maximum <= 0 || ActualHeight <= 0) return false;
        _dragging = true;
        _dragOriginY = pointerY;
        _dragOriginOffset = _offset;
        ClearHover();
        DragStarted?.Invoke();
        InvalidateVisual();
        return true;
    }

    internal void DragViewportTo(double pointerY)
    {
        if (!_dragging || ActualHeight <= 0) return;
        RequestOffset(_dragOriginOffset + (pointerY - _dragOriginY) / ActualHeight * _extent);
    }

    internal void EndViewportDrag()
    {
        if (!_dragging) return;
        CancelViewportDrag();
        DragCompleted?.Invoke();
    }

    internal void CancelViewportDrag()
    {
        if (!_dragging && !_bandPressed) return;
        _dragging = false;
        _bandPressed = false;
        _pressedMarker = -1;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var y = e.GetPosition(this).Y;
        var index = FindMarker(y);
        var band = ViewportBand;
        var hitSlop = BandHitSlop / Scale;
        if (Maximum > 0 && y >= band.Top - hitSlop && y <= band.Bottom + hitSlop && CaptureMouse())
        {
            // Retain the exact painted viewport on long sheets while making it
            // easy to grab. A stationary press still activates a marker.
            _bandPressed = true;
            _pressY = y;
            _pressedMarker = index;
            ClearHover();
        }
        else if (index >= 0)
        {
            MarkerInvoked?.Invoke(_rows[index]);
        }
        else if (ActualHeight > 0)
        {
            RequestOffset(y / ActualHeight * _extent - _viewport / 2);
        }
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging)
        {
            DragViewportTo(e.GetPosition(this).Y);
            EndViewportDrag();
        }
        else if (_bandPressed)
        {
            var index = _pressedMarker;
            CancelViewportDrag();
            if (index >= 0) MarkerInvoked?.Invoke(_rows[index]);
        }
        else return;
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging) EndViewportDrag();
        else CancelViewportDrag();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var y = e.GetPosition(this).Y;
        if (_bandPressed && !_dragging && Math.Abs(y - _pressY) >= SystemParameters.MinimumVerticalDragDistance / Scale)
        {
            BeginViewportDrag(_pressY);
        }
        if (_dragging)
        {
            DragViewportTo(y);
            e.Handled = true;
            return;
        }
        if (_bandPressed) return;
        var index = FindMarker(y);
        if (index == _hoverIndex) return;
        _hoverIndex = index;
        PreviewChanged?.Invoke(index >= 0 ? _rows[index] : null, y);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ClearHover();
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        WheelRequested?.Invoke(e.Delta);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var offset = e.Key switch
        {
            Key.Up => _offset - 1,
            Key.Down => _offset + 1,
            Key.PageUp => _offset - _viewport,
            Key.PageDown => _offset + _viewport,
            Key.Home => 0,
            Key.End => Maximum,
            _ => double.NaN
        };
        if (double.IsNaN(offset)) return;
        RequestOffset(offset);
        e.Handled = true;
    }

    private void RequestOffset(double offset)
    {
        if (double.IsFinite(offset)) ScrollRequested?.Invoke(Math.Clamp(offset, 0, Maximum));
    }

    private void ClearHover()
    {
        if (_hoverIndex < 0) return;
        _hoverIndex = -1;
        PreviewChanged?.Invoke(null, 0);
    }

    private Brush? BrushFor(string key) => TryFindResource(key) as Brush;

    private Brush? MarkerBrush(DiffKind kind) => BrushFor(kind switch
    {
        DiffKind.Added => "Diff.Overview.Added.Brush",
        DiffKind.Deleted => "Diff.Overview.Deleted.Brush",
        DiffKind.Moved => "Diff.Overview.Moved.Brush",
        DiffKind.Conflict => "Diff.Overview.Conflict.Brush",
        DiffKind.Unsupported => "Diff.Overview.Unsupported.Brush",
        _ => "Diff.Overview.Modified.Brush"
    });

    protected override AutomationPeer OnCreateAutomationPeer() => new ScrollRailAutomationPeer(this);

    private sealed class ScrollRailAutomationPeer(SpreadsheetScrollRail rail) : FrameworkElementAutomationPeer(rail), IRangeValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ScrollBar;
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);
        public bool IsReadOnly => !rail.IsEnabled;
        public double LargeChange => rail._viewport;
        public double SmallChange => 1;
        public double Maximum => rail.Maximum;
        public double Minimum => 0;
        public double Value => rail._offset;
        public void SetValue(double value)
        {
            if (!rail.IsEnabled) throw new System.Windows.Automation.ElementNotEnabledException();
            if (!double.IsFinite(value) || value < Minimum || value > Maximum) throw new ArgumentOutOfRangeException(nameof(value));
            rail.RequestOffset(value);
        }
    }
}
