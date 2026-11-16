using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Differ.App.Controls;

/// <summary>Cell chrome with a layout-neutral outline created only for selected cells.</summary>
public sealed class SpreadsheetCellFrame : Border
{
    internal const double SelectionThickness = 2;

    public static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register(
        nameof(SelectionBrush), typeof(Brush), typeof(SpreadsheetCellFrame),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSelectionBrushChanged));

    private SelectionVisual? _selection;

    public Brush? SelectionBrush
    {
        get => (Brush?)GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    protected override int VisualChildrenCount => base.VisualChildrenCount + (_selection is null ? 0 : 1);

    protected override Visual GetVisualChild(int index) =>
        _selection is { } selection && index == base.VisualChildrenCount ? selection : base.GetVisualChild(index);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_selection is not { } selection || SelectionBrush is not { } brush) return;

        // The content paints its diff background. Draw above it in the last
        // visual child so the outline does not reserve cell space.
        using var outline = selection.RenderOpen();
        var (width, height) = (RenderSize.Width, RenderSize.Height);
        var edge = Math.Min(SelectionThickness, Math.Min(width, height) / 2);
        outline.DrawRectangle(brush, null, new Rect(0, 0, width, edge));
        outline.DrawRectangle(brush, null, new Rect(0, height - edge, width, edge));
        outline.DrawRectangle(brush, null, new Rect(0, edge, edge, height - 2 * edge));
        outline.DrawRectangle(brush, null, new Rect(width - edge, edge, edge, height - 2 * edge));
    }

    private static void OnSelectionBrushChanged(DependencyObject source, DependencyPropertyChangedEventArgs e)
    {
        var frame = (SpreadsheetCellFrame)source;
        if (e.NewValue is Brush)
        {
            if (frame._selection is not null) return;
            frame._selection = new SelectionVisual();
            frame.AddVisualChild(frame._selection);
        }
        else if (frame._selection is { } selection)
        {
            frame._selection = null;
            frame.RemoveVisualChild(selection);
        }
    }

    private sealed class SelectionVisual : DrawingVisual
    {
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;

        protected override GeometryHitTestResult? HitTestCore(GeometryHitTestParameters hitTestParameters) => null;
    }
}
