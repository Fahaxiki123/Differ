using System.Buffers;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Differ.App.ViewModels;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.Controls;

/// <summary>
/// Draws the two real worksheet viewports during a thumb gesture without
/// realizing DataGrid rows and cells for every new position.
/// </summary>
public sealed class SpreadsheetDragSurface : FrameworkElement
{
    internal readonly record struct Pane(Rect Body, Rect ColumnHeaders, Rect FrozenRows);

    private const int MaximumCachedTextCount = 512;
    private const int MaximumTextLength = 512;
    private const double CellPadding = 8;
    private static readonly DiffKind[] Kinds = Enum.GetValues<DiffKind>();
    private static readonly SearchValues<char> LineEndings = SearchValues.Create("\r\n\f\u0085\u2028\u2029");
    private static readonly Typeface CellTypeface = new(new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface GutterTypeface = new(new FontFamily("Cascadia Mono, Consolas"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private readonly DrawingVisual _drawing = new();
    private readonly List<ColumnSlot> _visibleColumns = [];
    private readonly Dictionary<TextKey, FormattedText> _plainText = [];
    private WorksheetTabViewModel? _worksheet;
    private Pane _basePane;
    private Pane _minePane;
    private double[] _columnStarts = [];
    private double _rowHeight;
    private double _pixelsPerDip;
    private double _bodyFontSize;
    private double _metadataFontSize;

    public SpreadsheetDragSurface()
    {
        IsHitTestVisible = false;
        Focusable = false;
        AddVisualChild(_drawing);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _drawing : throw new ArgumentOutOfRangeException(nameof(index));

    internal bool IsActive => _worksheet is not null;
    internal int RenderedStartIndex { get; private set; } = -1;
    internal double RenderedHorizontalOffset { get; private set; } = double.NaN;

    internal bool Begin(WorksheetTabViewModel worksheet, Pane basePane, Pane minePane, double rowHeight, double gutterWidth = 50)
    {
        End();
        if (!HasArea(basePane.Body) || !HasArea(minePane.Body)
            || !double.IsFinite(rowHeight) || rowHeight <= 0
            || !double.IsFinite(gutterWidth) || gutterWidth <= 0
            || TryFindResource("PanelBrush") is not Brush || TryFindResource("InkBrush") is not Brush
            || worksheet.ScrollableRows.Count == 0 || worksheet.Columns.Count == 0)
        {
            return false;
        }

        var starts = new double[worksheet.Columns.Count + 2];
        starts[1] = gutterWidth;
        for (var index = 0; index < worksheet.Columns.Count; index++)
        {
            var width = worksheet.Columns[index].Width;
            if (!double.IsFinite(width) || width <= 0)
            {
                return false;
            }
            starts[index + 2] = starts[index + 1] + width;
        }

        _worksheet = worksheet;
        _basePane = basePane;
        _minePane = minePane;
        _columnStarts = starts;
        _rowHeight = rowHeight;
        _bodyFontSize = TryFindResource("Ui.SpreadsheetFontSize") is double bodySize ? bodySize : 13;
        _metadataFontSize = TryFindResource("Ui.MetadataFontSize") is double metadataSize ? metadataSize : 11.5;
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        return true;
    }

    internal void Move(double verticalOffset, double horizontalOffset)
    {
        if (_worksheet is not { } worksheet || !double.IsFinite(verticalOffset) || !double.IsFinite(horizontalOffset))
        {
            return;
        }

        var start = (int)Math.Clamp(Math.Floor(verticalOffset), 0, Math.Max(0, worksheet.ScrollableRows.Count - 1));
        var horizontal = Math.Max(0, horizontalOffset);
        if (start == RenderedStartIndex && horizontal == RenderedHorizontalOffset)
        {
            return;
        }

        _plainText.Clear();
        var palette = ReadPalette();
        using (var context = _drawing.RenderOpen())
        {
            DrawPane(context, worksheet, _basePane, start, horizontal, palette, isBaseSide: true);
            DrawPane(context, worksheet, _minePane, start, horizontal, palette, isBaseSide: false);
        }
        RenderedStartIndex = start;
        RenderedHorizontalOffset = horizontal;
    }

    internal void End()
    {
        if (_worksheet is null)
        {
            return;
        }

        _worksheet = null;
        _columnStarts = [];
        _plainText.Clear();
        RenderedStartIndex = -1;
        RenderedHorizontalOffset = double.NaN;
        using var context = _drawing.RenderOpen();
    }

    private void DrawPane(DrawingContext context, WorksheetTabViewModel worksheet, Pane pane,
        int firstRow, double horizontalOffset, Palette palette, bool isBaseSide)
    {
        if (HasArea(pane.ColumnHeaders))
        {
            var area = pane.ColumnHeaders;
            context.DrawRectangle(palette.Header, null, area);
            PrepareColumns(area, worksheet.FrozenColumnCount, horizontalOffset);
            foreach (var column in _visibleColumns)
            {
                var cell = column.At(area.Top, area.Height);
                var clip = column.ClipAt(area.Top, area.Height);
                context.PushClip(new RectangleGeometry(clip));
                DrawGridLines(context, cell, palette);
                if (column.Index > 0)
                {
                    var model = worksheet.Columns[column.Index - 1];
                    var title = (isBaseSide ? model.BaseColumn : model.MineColumn) ?? "—";
                    DrawText(context, title, cell, palette.HeaderTypeface, _metadataFontSize,
                        model.HasDifferences ? palette.Inline[(int)DiffKind.Modified] : palette.Secondary,
                        TextAlignment.Center, emphasis: model.HasDifferences);
                }
                context.Pop();
            }
        }

        if (HasArea(pane.FrozenRows))
        {
            DrawRows(context, worksheet.FrozenHeaderRows, 0, pane.FrozenRows, worksheet, horizontalOffset, palette, isBaseSide);
        }
        DrawRows(context, worksheet.ScrollableRows, firstRow, pane.Body, worksheet, horizontalOffset, palette, isBaseSide);
    }

    private void DrawRows(DrawingContext context, IReadOnlyList<SpreadsheetRowViewModel> rows, int firstRow, Rect area,
        WorksheetTabViewModel worksheet, double horizontalOffset, Palette palette, bool isBaseSide)
    {
        context.DrawRectangle(palette.Panel, null, area);
        context.PushClip(new RectangleGeometry(area));
        PrepareColumns(area, worksheet.FrozenColumnCount, horizontalOffset);
        var selected = worksheet.SelectedCell;
        var top = area.Top;
        for (var index = firstRow; index < rows.Count && top < area.Bottom; index++, top += _rowHeight)
        {
            var row = rows[index];
            if (row.IsCollapsed)
            {
                var fold = new Rect(area.Left, top, area.Width, _rowHeight);
                context.DrawRectangle(palette.Fold, null, fold);
                DrawText(context, row.CollapsedLabel, fold, palette.HeaderTypeface, _metadataFontSize, palette.Secondary);
                continue;
            }

            var selectedColumn = selected is { } active && active.RowStableId == row.StableId ? active.ColumnIndex : -1;
            var cells = isBaseSide ? row.BaseCells : row.MineCells;
            foreach (var column in _visibleColumns)
            {
                var cell = column.At(top, _rowHeight);
                var clip = column.ClipAt(top, _rowHeight);
                var selectedCell = column.Index > 0 && column.Index - 1 == selectedColumn;
                if (column.Index == 0)
                {
                    context.DrawRectangle(palette.Gutter, null, clip);
                    if (row.Kind != DiffKind.Unchanged)
                    {
                        context.DrawRectangle(palette.RowMarker[(int)row.Kind], null, new Rect(cell.Left, top, 3, _rowHeight));
                    }
                    var number = isBaseSide ? row.BaseRowNumberText : row.MineRowNumberText;
                    DrawText(context, number, cell, GutterTypeface, _metadataFontSize, palette.Secondary, TextAlignment.Right);
                }
                else
                {
                    var indexInRow = column.Index - 1;
                    if (indexInRow < cells.Count)
                    {
                        var value = cells[indexInRow];
                        var kind = SpreadsheetCellViewModel.GetEmphasisKind(value.Kind, value.IsContentReplacement, isBaseSide);
                        if (kind != DiffKind.Unchanged)
                        {
                            context.DrawRectangle(palette.Fill[(int)kind], null, clip);
                        }
                        var clippedColumn = clip.Left > cell.Left;
                        if (clippedColumn)
                        {
                            context.PushClip(new RectangleGeometry(clip));
                        }
                        DrawText(context, value.Text, cell, CellTypeface, _bodyFontSize, palette.Ink,
                            segments: kind != DiffKind.Unchanged ? value.Segments : null, changedInk: palette.Inline[(int)kind]);
                        if (clippedColumn)
                        {
                            context.Pop();
                        }
                    }
                }

                // Draw the bottom line through the clipped slot and the right
                // line only where the actual column boundary is visible.
                context.DrawRectangle(palette.GridLine, null, new Rect(clip.Left, cell.Bottom - 1, clip.Width, 1));
                if (clip.Right == cell.Right)
                {
                    context.DrawRectangle(palette.GridLine, null, new Rect(cell.Right - 1, top, 1, _rowHeight));
                }
                if (selectedCell)
                {
                    context.PushClip(new RectangleGeometry(clip));
                    DrawSelection(context, cell, palette.Focus);
                    context.Pop();
                }
            }
        }
        context.Pop();
    }

    private void PrepareColumns(Rect area, int frozenColumnCount, double horizontalOffset)
    {
        _visibleColumns.Clear();
        var count = _columnStarts.Length - 1;
        var frozen = Math.Clamp(frozenColumnCount, 0, count);
        for (var index = 0; index < frozen && _columnStarts[index] < area.Width; index++)
        {
            AddColumn(index, area, 0, area.Left);
        }
        var scrollingLeft = area.Left + _columnStarts[frozen];
        if (scrollingLeft >= area.Right || frozen == count)
        {
            return;
        }

        var first = Array.BinarySearch(_columnStarts, _columnStarts[frozen] + horizontalOffset);
        first = Math.Max(frozen, first < 0 ? ~first - 1 : first);
        for (var index = first; index < count && area.Left + _columnStarts[index] - horizontalOffset < area.Right; index++)
        {
            AddColumn(index, area, horizontalOffset, scrollingLeft);
        }
    }

    private void AddColumn(int index, Rect area, double horizontalOffset, double clipStart)
    {
        var left = area.Left + _columnStarts[index] - horizontalOffset;
        var right = area.Left + _columnStarts[index + 1] - horizontalOffset;
        var clipLeft = Math.Max(clipStart, left);
        var clipRight = Math.Min(area.Right, right);
        if (clipRight > clipLeft)
        {
            _visibleColumns.Add(new ColumnSlot(index, left, right - left, clipLeft, clipRight - clipLeft));
        }
    }

    private void DrawText(DrawingContext context, string value, Rect cell, Typeface typeface, double size, Brush ink,
        TextAlignment alignment = TextAlignment.Left, bool emphasis = false,
        IReadOnlyList<InlineDiffSegment>? segments = null, Brush? changedInk = null)
    {
        var width = cell.Width - 2 * CellPadding;
        if (value.Length == 0 || width <= 0)
        {
            return;
        }

        var text = GetDisplayLine(value);
        if (text.Length == 0)
        {
            return;
        }

        var key = new TextKey(text, typeface, size, ink, emphasis);
        if (segments is not null || !_plainText.TryGetValue(key, out var formatted))
        {
            formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, size, ink, _pixelsPerDip);
            if (emphasis)
            {
                formatted.SetFontWeight(FontWeights.SemiBold);
            }
            if (segments is not null && changedInk is not null)
            {
                var offset = 0;
                foreach (var segment in segments)
                {
                    var length = Math.Min(segment.Text.Length, text.Length - offset);
                    if (segment.IsChanged && length > 0)
                    {
                        formatted.SetFontWeight(FontWeights.SemiBold, offset, length);
                        formatted.SetForegroundBrush(changedInk, offset, length);
                    }
                    offset += length;
                    if (offset == text.Length)
                    {
                        break;
                    }
                }
            }
            if (segments is null && _plainText.Count < MaximumCachedTextCount)
            {
                _plainText.Add(key, formatted);
            }
        }

        var measuredWidth = formatted.WidthIncludingTrailingWhitespace;
        var remaining = Math.Max(0, width - measuredWidth);
        var left = cell.Left + CellPadding + (alignment == TextAlignment.Right ? remaining
            : alignment == TextAlignment.Center ? remaining / 2 : 0);
        var clipped = measuredWidth > width;
        if (clipped)
        {
            context.PushClip(new RectangleGeometry(new Rect(cell.Left + CellPadding, cell.Top, width, cell.Height)));
        }
        context.DrawText(formatted, new Point(left, cell.Top + (cell.Height - formatted.Height) / 2));
        if (clipped)
        {
            context.Pop();
        }
    }

    private static string GetDisplayLine(string value)
    {
        var limit = Math.Min(value.Length, MaximumTextLength);
        var lineEnd = value.AsSpan(0, limit).IndexOfAny(LineEndings);
        if (lineEnd >= 0)
        {
            return value[..lineEnd];
        }
        if (limit == value.Length)
        {
            return value;
        }

        var offset = 0;
        while (offset < limit)
        {
            var next = StringInfo.GetNextTextElementLength(value.AsSpan(offset));
            if (offset + next > limit)
            {
                break;
            }
            offset += next;
        }
        return value[..offset];
    }

    private static void DrawGridLines(DrawingContext context, Rect rect, Palette palette)
    {
        context.DrawRectangle(palette.GridLine, null, new Rect(rect.Left, rect.Bottom - 1, rect.Width, 1));
        context.DrawRectangle(palette.GridLine, null, new Rect(rect.Right - 1, rect.Top, 1, rect.Height));
    }

    private static void DrawSelection(DrawingContext context, Rect rect, Brush brush)
    {
        var edge = Math.Min(SpreadsheetCellFrame.SelectionThickness, Math.Min(rect.Width, rect.Height) / 2);
        context.DrawRectangle(brush, null, new Rect(rect.Left, rect.Top, rect.Width, edge));
        context.DrawRectangle(brush, null, new Rect(rect.Left, rect.Bottom - edge, rect.Width, edge));
        context.DrawRectangle(brush, null, new Rect(rect.Left, rect.Top + edge, edge, rect.Height - 2 * edge));
        context.DrawRectangle(brush, null, new Rect(rect.Right - edge, rect.Top + edge, edge, rect.Height - 2 * edge));
    }

    private Palette ReadPalette()
    {
        Brush Read(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
        var panel = Read("PanelBrush", Brushes.Transparent);
        var ink = Read("InkBrush", Brushes.Transparent);
        var headerFont = TryFindResource("Dui.Metrics.UiFont") as FontFamily ?? SystemFonts.MessageFontFamily;
        return new Palette(panel, ink, Read("Dui.Text.Secondary", ink), Read("Dui.Surface.Tabs", panel),
            Read("Dui.Surface.PaneHeader", panel), Read("GutterBackgroundBrush", panel),
            Read("DataGrid.GridLine", panel), Read("Dui.Focus", ink),
            new Typeface(headerFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            Kinds.Select(kind => Read(SpreadsheetCellPresenter.BackgroundResource(kind), panel)).ToArray(),
            Kinds.Select(kind => Read(InlineDiffPalette.GetForegroundKey(kind), ink)).ToArray(),
            Kinds.Select(kind => kind == DiffKind.Unchanged ? Brushes.Transparent
                : Read(RowMarkerResource(kind), Brushes.Transparent)).ToArray());
    }

    private static string RowMarkerResource(DiffKind kind) => kind switch
    {
        DiffKind.Added => "Diff.Row.Added.Border",
        DiffKind.Deleted => "Diff.Row.Deleted.Border",
        DiffKind.Modified => "Diff.Row.Modified.Border",
        DiffKind.Moved => "Diff.Row.Moved.Border",
        DiffKind.Conflict => "Diff.Row.Conflict.Border",
        DiffKind.Unsupported => "Diff.Row.Unsupported.Border",
        _ => string.Empty
    };

    private static bool HasArea(Rect rect) =>
        !rect.IsEmpty && rect.Width > 0 && rect.Height > 0
        && double.IsFinite(rect.Left) && double.IsFinite(rect.Top)
        && double.IsFinite(rect.Width) && double.IsFinite(rect.Height);

    private readonly record struct ColumnSlot(int Index, double Left, double Width, double ClipLeft, double ClipWidth)
    {
        public Rect At(double top, double height) => new(Left, top, Width, height);
        public Rect ClipAt(double top, double height) => new(ClipLeft, top, ClipWidth, height);
    }

    private readonly record struct TextKey(string Text, Typeface Typeface, double Size, Brush Ink, bool Emphasis);

    private sealed record Palette(Brush Panel, Brush Ink, Brush Secondary, Brush Header, Brush Gutter, Brush Fold,
        Brush GridLine, Brush Focus, Typeface HeaderTypeface, Brush[] Fill, Brush[] Inline, Brush[] RowMarker);
}
