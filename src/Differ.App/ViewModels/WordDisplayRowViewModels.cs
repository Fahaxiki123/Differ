using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.ViewModels;

public sealed record WordTableCellViewModel(
    TextDiffRowViewModel? Row,
    int ColumnIndex)
{
    public DiffKind Kind => Row?.Kind ?? DiffKind.Unchanged;

    public string OldText => Row?.OldText ?? string.Empty;

    public string NewText => Row?.NewText ?? string.Empty;

    public IReadOnlyList<InlineDiffSegment> OldSegments =>
        Row?.OldSegments ?? [];

    public IReadOnlyList<InlineDiffSegment> NewSegments =>
        Row?.NewSegments ?? [];

    public bool IsPlaceholder => Row is null;
}

public sealed record WordDisplayRowViewModel
{
    private WordDisplayRowViewModel(
        TextDiffRowViewModel? paragraph,
        IReadOnlyList<WordTableCellViewModel> cells,
        int tableColumnCount,
        IReadOnlyList<TextDiffRowViewModel>? groupRows = null)
    {
        Paragraph = paragraph;
        Cells = cells;
        TableColumnCount = tableColumnCount;
        Group = groupRows is not null ? new WordChangeGroupViewModel(groupRows) : null;
        ContentRows = groupRows ?? (paragraph is not null ? [paragraph]
            : cells.Where(cell => cell.Row is not null).Select(cell => cell.Row!).ToArray());
    }

    public TextDiffRowViewModel? Paragraph { get; }

    public IReadOnlyList<WordTableCellViewModel> Cells { get; }

    public int TableColumnCount { get; }

    public WordChangeGroupViewModel? Group { get; }

    public bool IsGroup => Group is not null;

    public bool IsParagraph => Paragraph is not null && !IsCollapsed && !IsGroup;

    public bool IsCollapsed => Paragraph?.IsCollapsed == true;

    public int HiddenCount => Paragraph?.HiddenCount ?? 0;

    public string CollapsedLabel => Paragraph?.CollapsedLabel ?? string.Empty;

    public CommunityToolkit.Mvvm.Input.IRelayCommand? ExpandCommand => Paragraph?.ExpandCommand;

    public IReadOnlyList<TextDiffRowViewModel> ContentRows { get; }

    internal static WordDisplayRowViewModel CreateCollapsed(TextDiffRowViewModel placeholder) =>
        new(placeholder, [], 0);

    public bool IsTable => Cells.Count > 0;

    public IReadOnlyList<WordTableCellViewModel> BaseCells => Cells;

    public IReadOnlyList<WordTableCellViewModel> MineCells => Cells;

    public TextDiffRowViewModel AnchorRow =>
        Paragraph
        ?? (IsGroup ? ContentRows[0] : null)
        ?? Cells.FirstOrDefault(cell => cell.Row is { Kind: not DiffKind.Unchanged })?.Row
        ?? Cells.First(cell => cell.Row is not null).Row!;

    public string StableId => AnchorRow.StableId;

    public bool HasDiffMarker =>
        IsGroup || (Paragraph?.HasDiffMarker
        ?? Cells.Any(cell => cell.Row?.HasDiffMarker == true));

    public bool Contains(TextDiffRowViewModel row)
    {
        return ContentRows.Any(content => ReferenceEquals(content, row));
    }

    public static IReadOnlyList<WordDisplayRowViewModel> Build(
        IReadOnlyList<TextDiffRowViewModel> rows)
    {
        var result = new List<WordDisplayRowViewModel>();
        var index = 0;
        while (index < rows.Count)
        {
            var row = rows[index];
            if (!row.IsTableCell)
            {
                if (!string.IsNullOrEmpty(row.ChangeGroupKey))
                {
                    var grouped = new List<TextDiffRowViewModel>();
                    while (index < rows.Count && rows[index].ChangeGroupKey == row.ChangeGroupKey) grouped.Add(rows[index++]);
                    result.Add(new WordDisplayRowViewModel(null, [], 0, grouped));
                }
                else
                {
                    result.Add(new WordDisplayRowViewModel(row, [], 0));
                    index++;
                }
                continue;
            }

            var tableKey = row.TableKey;
            var tableRowKey = row.TableRowKey;
            var tableCells = new List<TextDiffRowViewModel>();
            while (index < rows.Count)
            {
                var candidate = rows[index];
                if (!candidate.IsTableCell
                    || !string.Equals(
                        candidate.TableKey,
                        tableKey,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        candidate.TableRowKey,
                        tableRowKey,
                        StringComparison.Ordinal))
                {
                    break;
                }

                tableCells.Add(candidate);
                index++;
            }

            var columnCount = Math.Max(
                1,
                tableCells.Max(cell => cell.TableColumnCount));
            var cells = Enumerable.Range(1, columnCount)
                .Select(column => new WordTableCellViewModel(
                    tableCells.FirstOrDefault(
                        cell => cell.TableColumnIndex == column),
                    column))
                .ToArray();
            result.Add(new WordDisplayRowViewModel(null, cells, columnCount));
        }

        return result;
    }
}
