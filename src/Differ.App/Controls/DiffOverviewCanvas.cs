using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Differ.App.ViewModels;
using Differ.Core.Models;

namespace Differ.App.Controls;

public sealed class DiffOverviewCanvas : FrameworkElement
{
    public static readonly DependencyProperty RowsProperty =
        DependencyProperty.Register(
            nameof(Rows),
            typeof(IReadOnlyList<DiffRowViewModel>),
            typeof(DiffOverviewCanvas),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnRowsChanged));

    public static readonly DependencyProperty SelectRowCommandProperty =
        DependencyProperty.Register(
            nameof(SelectRowCommand),
            typeof(ICommand),
            typeof(DiffOverviewCanvas),
            new PropertyMetadata(null));

    public static readonly DependencyProperty UnchangedBrushProperty =
        RegisterBrushProperty(nameof(UnchangedBrush));
    public static readonly DependencyProperty AddedBrushProperty =
        RegisterBrushProperty(nameof(AddedBrush));
    public static readonly DependencyProperty DeletedBrushProperty =
        RegisterBrushProperty(nameof(DeletedBrush));
    public static readonly DependencyProperty ModifiedBrushProperty =
        RegisterBrushProperty(nameof(ModifiedBrush));
    public static readonly DependencyProperty MovedBrushProperty =
        RegisterBrushProperty(nameof(MovedBrush));
    public static readonly DependencyProperty ConflictBrushProperty =
        RegisterBrushProperty(nameof(ConflictBrush));
    public static readonly DependencyProperty UnsupportedBrushProperty =
        RegisterBrushProperty(nameof(UnsupportedBrush));
    public static readonly DependencyProperty HoverBrushProperty =
        RegisterBrushProperty(nameof(HoverBrush));
    public static readonly DependencyProperty HoverPenBrushProperty =
        RegisterBrushProperty(nameof(HoverPenBrush));

    public static readonly DependencyProperty ViewportStartProperty = RegisterDoubleProperty(nameof(ViewportStart), 0);
    public static readonly DependencyProperty ViewportEndProperty = RegisterDoubleProperty(nameof(ViewportEnd), 1);
    public static readonly DependencyProperty ViewportBrushProperty = RegisterBrushProperty(nameof(ViewportBrush));
    public static readonly DependencyProperty ViewportBorderBrushProperty = RegisterBrushProperty(nameof(ViewportBorderBrush));

    private const double TopPadding = 16;
    private const double NaturalRowPitch = 7;
    private OverviewBucket[] _buckets = [];
    private int _hoverBucketIndex = -1;

    public event EventHandler<OverviewPreviewEventArgs>? PreviewChanged;
    public event EventHandler<OverviewScrollEventArgs>? ScrollRequested;
    public event EventHandler<OverviewNavigateEventArgs>? NavigationRequested;

    public double ViewportStart { get => (double)GetValue(ViewportStartProperty); set => SetValue(ViewportStartProperty, value); }
    public double ViewportEnd { get => (double)GetValue(ViewportEndProperty); set => SetValue(ViewportEndProperty, value); }
    public Brush? ViewportBrush { get => (Brush?)GetValue(ViewportBrushProperty); set => SetValue(ViewportBrushProperty, value); }
    public Brush? ViewportBorderBrush { get => (Brush?)GetValue(ViewportBorderBrushProperty); set => SetValue(ViewportBorderBrushProperty, value); }

    internal double ContentHeight => Math.Max(0, Math.Min(Math.Max(0, ActualHeight - TopPadding * 2), _buckets.Length * NaturalRowPitch));
    internal int HoverBucketIndex => _hoverBucketIndex;
    private int _pressedBucketIndex = -1;

    public DiffOverviewCanvas()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
        SnapsToDevicePixels = true;
        SizeChanged += OnSizeChanged;
        Unloaded += (_, _) => ClearHover();
        IsEnabledChanged += (_, _) =>
        {
            if (IsEnabled) return;
            if (IsMouseCaptured) ReleaseMouseCapture();
            ClearPressedBucket();
            ClearHover();
        };
        SetResourceReference(ViewportBrushProperty, "Diff.Overview.ViewportBrush");
        SetResourceReference(ViewportBorderBrushProperty, "Diff.Overview.ViewportBorderBrush");
        SetResourceReference(
            UnchangedBrushProperty,
            "Diff.Overview.Unchanged.Brush");
        SetResourceReference(AddedBrushProperty, "Diff.Overview.Added.Brush");
        SetResourceReference(DeletedBrushProperty, "Diff.Overview.Deleted.Brush");
        SetResourceReference(
            ModifiedBrushProperty,
            "Diff.Overview.Modified.Brush");
        SetResourceReference(MovedBrushProperty, "Diff.Overview.Moved.Brush");
        SetResourceReference(
            ConflictBrushProperty,
            "Diff.Overview.Conflict.Brush");
        SetResourceReference(
            UnsupportedBrushProperty,
            "Diff.Overview.Unsupported.Brush");
        SetResourceReference(HoverBrushProperty, "Diff.Overview.HoverBrush");
        SetResourceReference(
            HoverPenBrushProperty,
            "Diff.Overview.HoverPenBrush");
    }

    public IReadOnlyList<DiffRowViewModel>? Rows
    {
        get => (IReadOnlyList<DiffRowViewModel>?)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public ICommand? SelectRowCommand
    {
        get => (ICommand?)GetValue(SelectRowCommandProperty);
        set => SetValue(SelectRowCommandProperty, value);
    }

    public Brush? UnchangedBrush
    {
        get => (Brush?)GetValue(UnchangedBrushProperty);
        set => SetValue(UnchangedBrushProperty, value);
    }

    public Brush? AddedBrush
    {
        get => (Brush?)GetValue(AddedBrushProperty);
        set => SetValue(AddedBrushProperty, value);
    }

    public Brush? DeletedBrush
    {
        get => (Brush?)GetValue(DeletedBrushProperty);
        set => SetValue(DeletedBrushProperty, value);
    }

    public Brush? ModifiedBrush
    {
        get => (Brush?)GetValue(ModifiedBrushProperty);
        set => SetValue(ModifiedBrushProperty, value);
    }

    public Brush? MovedBrush
    {
        get => (Brush?)GetValue(MovedBrushProperty);
        set => SetValue(MovedBrushProperty, value);
    }

    public Brush? ConflictBrush
    {
        get => (Brush?)GetValue(ConflictBrushProperty);
        set => SetValue(ConflictBrushProperty, value);
    }

    public Brush? UnsupportedBrush
    {
        get => (Brush?)GetValue(UnsupportedBrushProperty);
        set => SetValue(UnsupportedBrushProperty, value);
    }

    public Brush? HoverBrush
    {
        get => (Brush?)GetValue(HoverBrushProperty);
        set => SetValue(HoverBrushProperty, value);
    }

    public Brush? HoverPenBrush
    {
        get => (Brush?)GetValue(HoverPenBrushProperty);
        set => SetValue(HoverPenBrushProperty, value);
    }

    public static int VisualElementCount => 1;

    internal int RenderBucketCount => _buckets.Length;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_buckets.Length == 0 || ActualWidth <= 0 || ContentHeight <= 0)
        {
            return;
        }

        var start = double.IsFinite(ViewportStart) ? Math.Clamp(ViewportStart, 0, 1) : 0;
        var end = double.IsFinite(ViewportEnd) ? Math.Clamp(ViewportEnd, start, 1) : 1;
        var viewportY = TopPadding + start * ContentHeight;
        var viewportHeight = Math.Clamp((end - start) * ContentHeight, Math.Min(6, ContentHeight), ContentHeight);
        drawingContext.DrawRoundedRectangle(ViewportBrush, null,
            new Rect(3, Math.Min(viewportY, TopPadding + ContentHeight - viewportHeight), Math.Max(0, ActualWidth - 6), viewportHeight), 3, 3);
        drawingContext.DrawRoundedRectangle(ViewportBorderBrush, null,
            new Rect(Math.Max(0, ActualWidth - 5), Math.Min(viewportY, TopPadding + ContentHeight - viewportHeight), 2, viewportHeight), 1, 1);

        var pitch = ContentHeight / _buckets.Length;
        for (var index = 0; index < _buckets.Length; index++)
        {
            var bucket = _buckets[index];
            var y = TopPadding + (index + 0.5) * pitch;
            var distance = _hoverBucketIndex < 0 ? double.PositiveInfinity : Math.Abs(index - _hoverBucketIndex);
            var emphasis = Math.Pow(Math.Max(0, 1 - distance / 5d), 2);
            var indent = Math.Min(3, LeadingWhitespace(bucket.Row.NewValue.Length > 0 ? bucket.Row.NewValue : bucket.Row.OldValue) * 0.3);
            var width = Math.Min(Math.Max(5, bucket.Row.OverviewWidth * 0.4) * (1 + emphasis * 0.25), Math.Max(0, ActualWidth - 18 - indent));
            var isDifference = bucket.Row.Kind != DiffKind.Unchanged;
            var thickness = Math.Min(Math.Max(1, pitch - 1), (isDifference ? 1.5 : 1) + emphasis * 0.5);
            if (index == _pressedBucketIndex)
            {
                drawingContext.DrawRoundedRectangle(HoverBrush, null,
                    new Rect(3, y - Math.Max(3, pitch / 2), Math.Max(0, ActualWidth - 6), Math.Max(6, pitch)), 3, 3);
            }

            // Keep the content silhouette quieter than the separate difference marker.
            drawingContext.PushOpacity(isDifference ? 0.55 + emphasis * 0.15 : 1);
            drawingContext.DrawRoundedRectangle(GetBrush(bucket.Row.Kind), null,
                new Rect(12 + indent, y - thickness / 2, width, thickness), thickness / 2, thickness / 2);
            drawingContext.Pop();
            if (isDifference)
            {
                drawingContext.DrawRoundedRectangle(GetBrush(bucket.Row.Kind), null,
                    new Rect(5, y - 1, 3, 2), 1, 1);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pointerY = e.GetPosition(this).Y;
        UpdateHover(pointerY);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
        {
            SelectBucketAt(pointerY);
            e.Handled = true;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsMouseCaptured)
        {
            ClearHover();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollRequested?.Invoke(this, new OverviewScrollEventArgs(e.Delta));
        e.Handled = true;
    }

    internal void UpdateHover(double pointerY)
    {
        if (!IsEnabled) return;
        var index = pointerY < TopPadding - 4 || pointerY > TopPadding + ContentHeight + 4 ? -1 : GetBucketIndex(pointerY);
        if (index == _hoverBucketIndex)
        {
            return;
        }

        _hoverBucketIndex = index;
        InvalidateVisual();
        if (index < 0 || Rows is null)
        {
            PreviewChanged?.Invoke(this, new OverviewPreviewEventArgs(null, string.Empty, string.Empty, pointerY, 0, Rows?.Count ?? 0));
            return;
        }

        var bucket = _buckets[index];
        PreviewChanged?.Invoke(this, new OverviewPreviewEventArgs(bucket.Row,
            BuildPreview(Rows, bucket.RepresentativeIndex, true), BuildPreview(Rows, bucket.RepresentativeIndex, false), pointerY, bucket.RepresentativeIndex + 1, Rows.Count));
    }

    internal static string BuildPreview(IReadOnlyList<DiffRowViewModel> rows, int index, bool baseSide)
    {
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var lines = new List<string>(3);
        for (var item = Math.Clamp(index, 0, rows.Count - 1); item <= Math.Min(rows.Count - 1, index + 2); item++)
        {
            var value = baseSide ? rows[item].OldValue : rows[item].NewValue;
            if (value.Length > 180)
            {
                var cut = 180;
                if (char.IsHighSurrogate(value[cut - 1]))
                {
                    cut--;
                }
                value = value[..cut] + "…";
            }
            value = value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            lines.Add(value);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void ClearHover()
    {
        _hoverBucketIndex = -1;
        PreviewChanged?.Invoke(this, new OverviewPreviewEventArgs(null, string.Empty, string.Empty, 0, 0, 0));
        InvalidateVisual();
    }

    private static int LeadingWhitespace(string value)
    {
        var count = 0;
        while (count < Math.Min(value.Length, 16) && char.IsWhiteSpace(value[count]))
        {
            count++;
        }
        return count;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!SelectBucketAt(e.GetPosition(this).Y))
        {
            return;
        }

        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        ClearPressedBucket();
        if (!IsMouseOver) ClearHover();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        ClearPressedBucket();
    }

    private static void OnRowsChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        ((DiffOverviewCanvas)dependencyObject).RebuildBuckets();
    }

    private static DependencyProperty RegisterDoubleProperty(string name, double value)
    {
        return DependencyProperty.Register(name, typeof(double), typeof(DiffOverviewCanvas),
            new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));
    }

    private static DependencyProperty RegisterBrushProperty(string name)
    {
        return DependencyProperty.Register(
            name,
            typeof(Brush),
            typeof(DiffOverviewCanvas),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender));
    }

    private void OnSizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        RebuildBuckets();
    }

    private void RebuildBuckets()
    {
        _buckets = BuildBuckets(
            Rows ?? [],
            Math.Max(1, (int)Math.Floor((ActualHeight - TopPadding * 2) / 3)));
        _pressedBucketIndex = -1;
        ClearHover();
    }

    internal static OverviewBucket[] BuildBuckets(
        IReadOnlyList<DiffRowViewModel> rows,
        int availablePixels)
    {
        if (rows.Count == 0 || availablePixels <= 0)
        {
            return [];
        }

        var bucketCount = Math.Min(rows.Count, availablePixels);
        var result = new OverviewBucket[bucketCount];
        for (var bucketIndex = 0;
             bucketIndex < bucketCount;
             bucketIndex++)
        {
            var start = (int)((long)bucketIndex * rows.Count / bucketCount);
            var end = Math.Max(
                start + 1,
                (int)((long)(bucketIndex + 1) * rows.Count / bucketCount));
            var representative = rows[start];
            var representativeIndex = start;
            var priority = GetPriority(representative.Kind);
            for (var rowIndex = start + 1;
                 rowIndex < end && rowIndex < rows.Count;
                 rowIndex++)
            {
                var candidate = rows[rowIndex];
                var candidatePriority = GetPriority(candidate.Kind);
                if (candidatePriority > priority
                    || (candidatePriority == priority
                        && candidate.OverviewWidth
                            > representative.OverviewWidth))
                {
                    representative = candidate;
                    representativeIndex = rowIndex;
                    priority = candidatePriority;
                }
            }

            result[bucketIndex] = new OverviewBucket(representative, start, end, representativeIndex);
        }

        return result;
    }

    private OverviewBucket? GetBucket(double y)
    {
        var index = GetBucketIndex(y);
        return index < 0 ? null : _buckets[index];
    }

    private int GetBucketIndex(double y)
    {
        return ResolveBucketIndex(y - TopPadding, ContentHeight, _buckets.Length);
    }

    internal static int ResolveBucketIndex(
        double y,
        double height,
        int bucketCount)
    {
        if (bucketCount <= 0
            || height <= 0
            || !double.IsFinite(y)
            || !double.IsFinite(height))
        {
            return -1;
        }

        return Math.Clamp(
            (int)Math.Floor(y / height * bucketCount),
            0,
            bucketCount - 1);
    }

    internal bool SelectBucketAt(double y)
    {
        if (!IsEnabled) return false;
        var bucketIndex = GetBucketIndex(y);
        if (bucketIndex < 0)
        {
            return false;
        }

        if (_pressedBucketIndex == bucketIndex)
        {
            return true;
        }

        _pressedBucketIndex = bucketIndex;
        InvalidateVisual();
        var bucket = _buckets[bucketIndex];
        var sourceRows = Rows;
        var row = bucket.Row;
        if (SelectRowCommand?.CanExecute(row) == true)
        {
            SelectRowCommand.Execute(row);
            // Expanding a context row replaces the map synchronously. Its new
            // bucket index no longer identifies this target; the view's normal
            // selection scroll handles that transition instead.
            if (ReferenceEquals(sourceRows, Rows) && !row.Metadata.ContainsKey("overview.structure"))
            {
                NavigationRequested?.Invoke(this,
                    new OverviewNavigateEventArgs((bucket.RepresentativeIndex + 0.5) / Math.Max(1, sourceRows?.Count ?? 1)));
            }
        }

        return true;
    }

    private void ClearPressedBucket()
    {
        if (_pressedBucketIndex < 0)
        {
            return;
        }

        _pressedBucketIndex = -1;
        InvalidateVisual();
    }

    private static int GetPriority(DiffKind kind)
    {
        return kind switch
        {
            DiffKind.Conflict => 6,
            DiffKind.Modified => 5,
            DiffKind.Added or DiffKind.Deleted => 4,
            DiffKind.Moved => 3,
            DiffKind.Unsupported => 2,
            _ => 1
        };
    }

    private Brush? GetBrush(DiffKind kind)
    {
        return kind switch
        {
            DiffKind.Added => AddedBrush,
            DiffKind.Deleted => DeletedBrush,
            DiffKind.Modified => ModifiedBrush,
            DiffKind.Moved => MovedBrush,
            DiffKind.Conflict => ConflictBrush,
            DiffKind.Unsupported => UnsupportedBrush,
            _ => UnchangedBrush
        };
    }

    internal readonly record struct OverviewBucket(
        DiffRowViewModel Row, int StartIndex = 0, int EndIndex = 0, int RepresentativeIndex = 0);
}

public sealed class OverviewPreviewEventArgs(
    DiffRowViewModel? row, string basePreview, string minePreview, double pointerY, int position, int total) : EventArgs
{
    public DiffRowViewModel? Row { get; } = row;
    public string BasePreview { get; } = basePreview;
    public string MinePreview { get; } = minePreview;
    public double PointerY { get; } = pointerY;
    public int Position { get; } = position;
    public int Total { get; } = total;
}

public sealed class OverviewNavigateEventArgs(double position) : EventArgs
{
    public double Position { get; } = position;
}
