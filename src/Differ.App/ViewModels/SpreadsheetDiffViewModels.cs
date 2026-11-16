using System.Buffers;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.ViewModels;

public sealed record SpreadsheetColumnViewModel(
    string Key,
    double Width,
    bool HasDifferences)
{
    public string? BaseColumn { get; init; }
    public string? MineColumn { get; init; }
    public string? FieldName { get; init; }
    public string? DisplayHeader { get; init; }
    public string? MappingToolTip { get; init; }
    public string HeaderText => DisplayHeader ?? Key;
}

public sealed record SpreadsheetCellViewModel(
    string Address,
    string Text,
    string DetailsText,
    string ToolTipText,
    DiffKind Kind,
    IReadOnlyList<InlineDiffSegment> Segments,
    IReadOnlyList<InlineDiffSegment> DetailsSegments)
{
    public bool HasDifference => Kind != DiffKind.Unchanged;

    public bool IsContentReplacement { get; init; }

    internal static DiffKind GetEmphasisKind(DiffKind kind, bool isContentReplacement, bool isBaseSide) => kind switch
    {
        DiffKind.Modified when isContentReplacement => isBaseSide ? DiffKind.Deleted : DiffKind.Added,
        DiffKind.Added when isBaseSide => DiffKind.Unchanged,
        DiffKind.Deleted when !isBaseSide => DiffKind.Unchanged,
        _ => kind
    };
}

public sealed record SpreadsheetRowViewModel(
    string StableId,
    int? BaseRowNumber,
    int? MineRowNumber,
    DiffKind Kind,
    IReadOnlyList<SpreadsheetCellViewModel> BaseCells,
    IReadOnlyList<SpreadsheetCellViewModel> MineCells)
{
    public bool IsCollapsed { get; init; }

    public int HiddenCount { get; init; }

    public string CollapsedLabel { get; init; } = string.Empty;

    public IRelayCommand? ExpandCommand { get; init; }

    public string BaseRowNumberText => BaseRowNumber?.ToString(
        System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;

    public string MineRowNumberText => MineRowNumber?.ToString(
        System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;

    public string KindLabel => DiffRowViewModel.Localize(Kind);
}

public sealed record SpreadsheetCellSelectionViewModel(
    string RowStableId,
    int ColumnIndex,
    string ColumnKey,
    string BaseAddress,
    string MineAddress,
    string BaseText,
    string MineText,
    DiffKind Kind,
    IReadOnlyList<InlineDiffSegment> BaseSegments,
    IReadOnlyList<InlineDiffSegment> MineSegments)
{
    private const int MaximumPreviewLength = 160;
    private const int MaximumPreviewSegments = 16;
    private static readonly SearchValues<char> LineEndings = SearchValues.Create("\r\n\f\u0085\u2028\u2029");
    private static readonly InlineDiffSegment PreviewEllipsis = new("…", false);

    public bool IsContentReplacement { get; init; }
    public string PreviewAddress => ColumnIndex >= 0 ? $"{BaseAddress} ↔ {MineAddress}" : ColumnKey;
    public DiffKind BaseEmphasisKind => SpreadsheetCellViewModel.GetEmphasisKind(Kind, IsContentReplacement, isBaseSide: true);
    public DiffKind MineEmphasisKind => SpreadsheetCellViewModel.GetEmphasisKind(Kind, IsContentReplacement, isBaseSide: false);
    public InlineDiffContent BasePreview => new(BaseEmphasisKind, ToSingleLine(BaseSegments));
    public InlineDiffContent MinePreview => new(MineEmphasisKind, ToSingleLine(MineSegments));
    public InlineDiffContent BaseDetails => new(BaseEmphasisKind, BaseSegments);
    public InlineDiffContent MineDetails => new(MineEmphasisKind, MineSegments);

    private static IReadOnlyList<InlineDiffSegment> ToSingleLine(IReadOnlyList<InlineDiffSegment> segments)
    {
        // Most selections already consist of short, single-line segments.
        var remaining = MaximumPreviewLength;
        if (segments.Count <= MaximumPreviewSegments)
        {
            for (var index = 0; index < segments.Count; index++)
            {
                var text = segments[index].Text;
                if (text.Length > remaining || text.AsSpan().ContainsAny(LineEndings)) return BuildPreview(segments);
                remaining -= text.Length;
            }
            return segments;
        }
        return BuildPreview(segments);
    }

    private static List<InlineDiffSegment> BuildPreview(IReadOnlyList<InlineDiffSegment> segments)
    {
        var preview = new List<InlineDiffSegment>(Math.Min(segments.Count, MaximumPreviewSegments));
        var remaining = MaximumPreviewLength - 1;
        var index = 0;
        for (; index < segments.Count && preview.Count < MaximumPreviewSegments - 1; index++)
        {
            var segment = segments[index];
            if (segment.Text.Length == 0) continue;
            if (segment.Text.Length > remaining)
            {
                var length = TextElementPrefixLength(segment.Text, remaining);
                if (length > 0)
                    preview.Add(segment with { Text = segment.Text[..length].ReplaceLineEndings(" ") });
                break;
            }

            var text = segment.Text.ReplaceLineEndings(" ");
            preview.Add(ReferenceEquals(text, segment.Text) ? segment : segment with { Text = text });
            remaining -= text.Length;
        }

        if (index < segments.Count) preview.Add(PreviewEllipsis);
        return preview;
    }

    private static int TextElementPrefixLength(string text, int limit)
    {
        var length = 0;
        while (length < limit)
        {
            var next = StringInfo.GetNextTextElementLength(text.AsSpan(length));
            if (length + next > limit) break;
            length += next;
        }
        return length;
    }
}

internal sealed record SpreadsheetSectionViewModel(
    string Name,
    IReadOnlyList<SpreadsheetColumnViewModel> Columns,
    IReadOnlyList<SpreadsheetRowViewModel> Rows,
    int FrozenColumns = 1,
    int FrozenHeaderRows = 0);
