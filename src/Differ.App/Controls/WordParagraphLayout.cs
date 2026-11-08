using System.Windows;
using System.Windows.Controls;

namespace Differ.App.Controls;

/// <summary>
/// Keeps a numbering label and its paragraph in disjoint measured regions.
/// Numbering may specify no hanging indent, or a label may outgrow the indent
/// (for example 12.34.56). A fixed margin cannot reserve its rendered width.
/// </summary>
public sealed class WordParagraphLayout : Panel
{
    public static readonly DependencyProperty MarkerOffsetProperty = DependencyProperty.Register(
        nameof(MarkerOffset), typeof(double), typeof(WordParagraphLayout),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty TextOffsetProperty = DependencyProperty.Register(
        nameof(TextOffset), typeof(double), typeof(WordParagraphLayout),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty MarkerGapProperty = DependencyProperty.Register(
        nameof(MarkerGap), typeof(double), typeof(WordParagraphLayout),
        new FrameworkPropertyMetadata(6d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MarkerOffset { get => (double)GetValue(MarkerOffsetProperty); set => SetValue(MarkerOffsetProperty, value); }
    public double TextOffset { get => (double)GetValue(TextOffsetProperty); set => SetValue(TextOffsetProperty, value); }
    public double MarkerGap { get => (double)GetValue(MarkerGapProperty); set => SetValue(MarkerGapProperty, value); }
    private double _markerLeft;
    private double _textLeft;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count != 2) return default;
        var marker = InternalChildren[0];
        var body = InternalChildren[1];
        var hasMarker = marker is TextBlock { Text: { } text } && !string.IsNullOrWhiteSpace(text);
        var markerLeft = hasMarker ? Math.Max(0, MarkerOffset) : 0;
        var textLeft = Math.Max(0, TextOffset);
        var gap = Math.Max(0, MarkerGap);
        // Long multilevel labels wrap in their own region in a narrow pane,
        // instead of painting over text or consuming the entire body width.
        var markerWidth = double.PositiveInfinity;
        if (double.IsFinite(availableSize.Width))
        {
            var bodyReserve = Math.Min(64, availableSize.Width / 2);
            var markerMinimum = hasMarker ? Math.Min(Math.Max(16, ((TextBlock)marker).FontSize * 2),
                Math.Max(0, availableSize.Width - bodyReserve - gap)) : 0;
            markerLeft = Math.Min(markerLeft, Math.Max(0, availableSize.Width - bodyReserve - gap - markerMinimum));
            markerWidth = Math.Max(0, availableSize.Width - markerLeft - bodyReserve - (hasMarker ? gap : 0));
            textLeft = Math.Min(textLeft, Math.Max(0, availableSize.Width - bodyReserve));
        }
        marker.Measure(new Size(markerWidth, double.PositiveInfinity));
        if (hasMarker) textLeft = Math.Max(textLeft, markerLeft + marker.DesiredSize.Width + gap);
        _markerLeft = markerLeft;
        _textLeft = textLeft;
        body.Measure(new Size(Math.Max(0, availableSize.Width - textLeft), double.PositiveInfinity));
        return new Size(Math.Max(markerLeft + marker.DesiredSize.Width, textLeft + body.DesiredSize.Width),
            Math.Max(marker.DesiredSize.Height, body.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 2) return finalSize;
        var marker = InternalChildren[0];
        var body = InternalChildren[1];
        marker.Arrange(new Rect(_markerLeft, 0, marker.DesiredSize.Width, marker.DesiredSize.Height));
        body.Arrange(new Rect(_textLeft, 0, Math.Max(0, finalSize.Width - _textLeft), finalSize.Height));
        return finalSize;
    }
}
