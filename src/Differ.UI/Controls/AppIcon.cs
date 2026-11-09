using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Differ.UI.Icons;

namespace Differ.UI.Controls;

public enum AppIconKind
{
    Previous,
    Next,
    Search,
    Refresh,
    Theme,
    More,
    Filter,
    Undo,
    Redo,
    Edit,
    Save,
    ChevronDown,
    ChevronRight,
    Close,
    File,
    Check,
    Add,
    Remove,
    Warning,
    Info,
}

public sealed class AppIcon : Control
{
    private const double DesignSize = 16;

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(AppIconKind), typeof(AppIcon),
        new FrameworkPropertyMetadata(AppIconKind.File, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(AppIcon),
        new FrameworkPropertyMetadata(DesignSize,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender),
        static value => value is double size && double.IsFinite(size) && size > 0);

    static AppIcon()
    {
        FocusableProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(false));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(false));
        ForegroundProperty.OverrideMetadata(typeof(AppIcon),
            new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.AffectsRender));
    }

    public AppIconKind Kind
    {
        get => (AppIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    protected override Size MeasureOverride(Size constraint) =>
        new(Math.Min(Size, constraint.Width), Math.Min(Size, constraint.Height));

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double iconSize = Math.Min(Size, Math.Min(ActualWidth, ActualHeight));
        if (iconSize <= 0 || Foreground is null)
        {
            return;
        }

        var pen = new Pen(Foreground, 1.4)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        double scale = iconSize / DesignSize;
        drawingContext.PushTransform(new MatrixTransform(scale, 0, 0, scale,
            (ActualWidth - iconSize) / 2, (ActualHeight - iconSize) / 2));
        drawingContext.DrawGeometry(null, pen, IconGeometry.Get(Kind));
        drawingContext.Pop();
    }
}
