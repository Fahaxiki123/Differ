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
    IReadOnlyList<InlineDiffSegment> MineSegments);

internal sealed record SpreadsheetSectionViewModel(
    string Name,
    IReadOnlyList<SpreadsheetColumnViewModel> Columns,
    IReadOnlyList<SpreadsheetRowViewModel> Rows,
    int FrozenColumns = 1,
    int FrozenHeaderRows = 0);
