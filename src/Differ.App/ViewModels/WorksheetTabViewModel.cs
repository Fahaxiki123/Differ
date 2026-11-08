using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Differ.App.Resources;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.ViewModels;

public sealed class WorksheetTabViewModel : ObservableObject
{
    private static readonly CompositeFormat DisplayNameFormat =
        CompositeFormat.Parse(Strings.WorksheetTabWithCountFormat);
    private static readonly CompositeFormat FoldLabelFormat = CompositeFormat.Parse(Strings.CollapsedContextFormat);

    private readonly IReadOnlyList<SpreadsheetRowViewModel> _allRows;
    private readonly IReadOnlyList<DiffRowViewModel> _allOverviewRows;
    private readonly DiffRowViewModel[] _structureOverviewRows;
    private readonly DiffRowViewModel[] _changedOverviewRows;
    private IReadOnlyList<SpreadsheetRowViewModel> _rows;
    private IReadOnlyList<DiffRowViewModel> _overviewRows;
    private SpreadsheetRowViewModel? _selectedRow;
    private SpreadsheetCellSelectionViewModel? _selectedCell;
    private readonly Dictionary<(int? Old, int? New), SpreadsheetRowViewModel> _rowLookup;
    private readonly Dictionary<int, SpreadsheetRowViewModel> _oldRowLookup;
    private readonly Dictionary<int, SpreadsheetRowViewModel> _newRowLookup;
    private readonly Dictionary<string, int> _columnLookup;
    private readonly Dictionary<string, int> _differenceIndexes;
    private readonly Dictionary<string, SpreadsheetRowViewModel> _rowStableLookup;
    private readonly Dictionary<(int? Old, int? New, string Column), string> _cellDifferenceLookup;
    private string? _selectedDifferenceId;
    private readonly ContextFoldProjection<SpreadsheetRowViewModel> _foldProjection;
    private readonly Dictionary<string, int> _rowPositions;
    private readonly Dictionary<string, DiffRowViewModel> _overviewLookup;
    private readonly Dictionary<string, int> _firstChangedColumns;
    private Dictionary<string, SpreadsheetRowViewModel> _visibleRowLookup = new(StringComparer.Ordinal);
    private DiffDisplayMode _displayMode;
    private readonly HashSet<string> _frozenHeaderIds;
    private IReadOnlyList<SpreadsheetRowViewModel> _scrollableRows;

    internal WorksheetTabViewModel(
        string name,
        int differenceCount,
        SpreadsheetSectionViewModel section,
        IReadOnlyList<DiffRowViewModel> overviewRows)
    {
        Name = name;
        DifferenceCount = differenceCount;
        Columns = section.Columns;
        FrozenColumnCount = 1 + Math.Clamp(section.FrozenColumns, 0, Columns.Count);
        FrozenHeaderRows = section.Rows.Take(section.FrozenHeaderRows).ToArray();
        _frozenHeaderIds = FrozenHeaderRows.Select(row => row.StableId).ToHashSet(StringComparer.Ordinal);
        _scrollableRows = section.Rows.Where(row => !_frozenHeaderIds.Contains(row.StableId)).ToArray();
        _allRows = section.Rows;
        _foldProjection = new ContextFoldProjection<SpreadsheetRowViewModel>(section.Rows,
            row => _frozenHeaderIds.Contains(row.StableId) || row.Kind != DiffKind.Unchanged);
        _rowPositions = section.Rows.Select((row, index) => (row.StableId, index))
            .ToDictionary(item => item.StableId, item => item.index, StringComparer.Ordinal);
        _firstChangedColumns = section.Rows.ToDictionary(row => row.StableId,
            row => Enumerable.Range(0, Columns.Count).FirstOrDefault(index =>
                row.BaseCells[index].HasDifference || row.MineCells[index].HasDifference, 0),
            StringComparer.Ordinal);
        _changedOverviewRows = overviewRows
            .Where(row => row.Kind != DiffKind.Unchanged)
            .ToArray();
        _rowLookup = section.Rows.ToDictionary(row => (row.BaseRowNumber, row.MineRowNumber));
        _oldRowLookup = section.Rows.Where(row => row.BaseRowNumber.HasValue)
            .ToDictionary(row => row.BaseRowNumber!.Value);
        _newRowLookup = section.Rows.Where(row => row.MineRowNumber.HasValue)
            .ToDictionary(row => row.MineRowNumber!.Value);
        _columnLookup = section.Columns.Select((column, index) => (column.Key, index))
            .ToDictionary(item => item.Key, item => item.index, StringComparer.Ordinal);
        _differenceIndexes = _changedOverviewRows.Select((row, index) => (row.StableId, index))
            .ToDictionary(item => item.StableId, item => item.index, StringComparer.Ordinal);
        _rowStableLookup = section.Rows.ToDictionary(row => row.StableId, StringComparer.Ordinal);
        _structureOverviewRows = _changedOverviewRows
            .Where(node => !node.Metadata.ContainsKey("column") && !_rowStableLookup.ContainsKey(node.StableId))
            .Select(node =>
            {
                var metadata = new Dictionary<string, string>(node.Metadata, StringComparer.Ordinal)
                {
                    ["overview.structure"] = "true"
                };
                return node with { Metadata = metadata };
            }).ToArray();
        _allOverviewRows = section.Rows.Select(CreateOverviewRow).Concat(_structureOverviewRows).ToArray();
        _overviewLookup = _allOverviewRows.ToDictionary(row => row.StableId, StringComparer.Ordinal);
        _cellDifferenceLookup = _changedOverviewRows.Where(node => node.Metadata.ContainsKey("column"))
            .ToDictionary(node => (node.OldIndex, node.NewIndex, node.Metadata["column"]), node => node.StableId);
        ChangedRows = section.Rows
            .Where(row => row.Kind != DiffKind.Unchanged)
            .ToArray();
        ChangedRowIndexes = ChangedRows
            .Select((row, index) => (row.StableId, index))
            .ToDictionary(
                item => item.StableId,
                item => item.index,
                StringComparer.Ordinal);
        _rows = _allRows;
        _visibleRowLookup = _rowStableLookup;
        _overviewRows = _allOverviewRows;
        _selectedRow = ChangedRows.FirstOrDefault();
        SelectFirstChangedCell(_selectedRow);
        if (_changedOverviewRows.Length > 0)
        {
            SelectDifference(_changedOverviewRows[0]);
        }
    }

    public string Name { get; }

    public int DifferenceCount { get; }

    public bool HasDifferences => DifferenceCount > 0;

    public int NavigableDifferenceCount => _changedOverviewRows.Length;

    public int SelectedDifferenceIndex => _selectedDifferenceId is not null
        && _differenceIndexes.TryGetValue(_selectedDifferenceId, out var index) ? index : -1;

    public string? SelectedDifferenceId => _selectedDifferenceId;

    public string DisplayName => HasDifferences
        ? string.Format(
            CultureInfo.CurrentCulture,
            DisplayNameFormat,
            Name,
            DifferenceCount)
        : Name;

    public IReadOnlyList<SpreadsheetColumnViewModel> Columns { get; }
    public int FrozenColumnCount { get; }
    public IReadOnlyList<SpreadsheetRowViewModel> FrozenHeaderRows { get; }
    public IReadOnlyList<SpreadsheetRowViewModel> ScrollableRows => _scrollableRows;
    public bool IsFrozenHeader(SpreadsheetRowViewModel row) => _frozenHeaderIds.Contains(row.StableId);

    public IReadOnlyList<SpreadsheetRowViewModel> AllRows => _allRows;

    public DiffDisplayMode DisplayMode => _displayMode;

    public IReadOnlyList<SpreadsheetRowViewModel> Rows
    {
        get => _rows;
        private set
        {
            if (SetProperty(ref _rows, value))
            {
                _scrollableRows = value.Where(row => !_frozenHeaderIds.Contains(row.StableId)).ToArray();
                OnPropertyChanged(nameof(ScrollableRows));
            }
        }
    }

    public SpreadsheetRowViewModel[] ChangedRows { get; }

    public IReadOnlyList<DiffRowViewModel> OverviewRows
    {
        get => _overviewRows;
        private set => SetProperty(ref _overviewRows, value);
    }

    public IReadOnlyDictionary<string, int> ChangedRowIndexes { get; }

    public SpreadsheetRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                SelectFirstChangedCell(value);
            }
        }
    }

    public SpreadsheetCellSelectionViewModel? SelectedCell
    {
        get => _selectedCell;
        private set => SetProperty(ref _selectedCell, value);
    }

    public void ApplyFilter(bool showUnchanged)
    {
        ApplyFilter(showUnchanged ? DiffDisplayMode.All : DiffDisplayMode.DifferencesOnly);
    }

    public void ApplyFilter(DiffDisplayMode mode)
    {
        if (_displayMode == mode)
        {
            return;
        }

        _displayMode = mode;
        RebuildVisibleRows();
        OnPropertyChanged(nameof(DisplayMode));
    }

    private void RebuildVisibleRows()
    {
        var selectedDifference = _selectedDifferenceId;
        var selectedStableId = SelectedRow?.StableId;
        Rows = _foldProjection.Build(_displayMode, CreateCollapsedRow);
        _visibleRowLookup = Rows.ToDictionary(row => row.StableId, StringComparer.Ordinal);
        OverviewRows = _displayMode == DiffDisplayMode.All ? _allOverviewRows
            : Rows.Select(row => _overviewLookup.GetValueOrDefault(row.StableId) ?? CreateOverviewRow(row))
                .Concat(_structureOverviewRows).ToArray();
        SelectedRow = selectedStableId is null
            ? ChangedRows.FirstOrDefault()
            : _visibleRowLookup.GetValueOrDefault(selectedStableId)
                ?? ChangedRows.FirstOrDefault();
        if (selectedDifference is not null
            && _differenceIndexes.TryGetValue(selectedDifference, out var selectedIndex))
        {
            SelectDifference(_changedOverviewRows[selectedIndex]);
        }
    }

    public void SelectCell(SpreadsheetRowViewModel? row, int columnIndex)
    {
        if (row?.IsCollapsed == true)
        {
            row.ExpandCommand?.Execute(null);
            return;
        }

        if (row is null
            || columnIndex < 0
            || columnIndex >= Columns.Count
            || columnIndex >= row.BaseCells.Count
            || columnIndex >= row.MineCells.Count)
        {
            SelectedCell = null;
            return;
        }

        EnsureVisible(row);

        if (!ReferenceEquals(SelectedRow, row))
        {
            _selectedRow = row;
            OnPropertyChanged(nameof(SelectedRow));
        }

        var baseCell = row.BaseCells[columnIndex];
        var mineCell = row.MineCells[columnIndex];
        var columnKey = Columns[columnIndex].Key;
        if (!_cellDifferenceLookup.TryGetValue((row.BaseRowNumber, row.MineRowNumber, columnKey), out var selectedId)
            && !_cellDifferenceLookup.TryGetValue((null, row.MineRowNumber, columnKey), out selectedId))
        {
            _cellDifferenceLookup.TryGetValue((row.BaseRowNumber, null, columnKey), out selectedId);
        }
        SetSelectedDifference(selectedId
            ?? (_differenceIndexes.ContainsKey(row.StableId) ? row.StableId : null));
        SelectedCell = new SpreadsheetCellSelectionViewModel(
            row.StableId,
            columnIndex,
            Columns[columnIndex].Key,
            baseCell.Address,
            mineCell.Address,
            baseCell.DetailsText,
            mineCell.DetailsText,
            baseCell.Kind,
            baseCell.DetailsSegments,
            mineCell.DetailsSegments);
    }

    public void MoveDifference(int offset)
    {
        if (_changedOverviewRows.Length == 0)
        {
            return;
        }

        var current = SelectedDifferenceIndex;
        var next = current < 0 ? 0 : (current + offset + _changedOverviewRows.Length) % _changedOverviewRows.Length;
        SelectDifference(_changedOverviewRows[next]);
    }

    public void SelectDifference(DiffRowViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Metadata.TryGetValue("column", out var column)
            && _columnLookup.TryGetValue(column, out var columnIndex)
            && TryGetRow(node, out var row))
        {
            SelectCell(row, columnIndex);
        }
        else if (_rowStableLookup.TryGetValue(node.StableId, out var matchingRow))
        {
            EnsureVisible(matchingRow);
            SelectedRow = matchingRow;
            SelectFirstChangedCell(matchingRow);
        }
        else
        {
            _selectedRow = null;
            OnPropertyChanged(nameof(SelectedRow));
            var oldText = $"{node.Path}{Environment.NewLine}{node.OldValue}";
            var newText = $"{node.Path}{Environment.NewLine}{node.NewValue}";
            var inline = InlineTextDiffer.Compare(oldText, newText);
            SelectedCell = new SpreadsheetCellSelectionViewModel(
                node.StableId, -1, Strings.StructuralDifference, string.Empty, string.Empty,
                oldText, newText, node.Kind, inline.OldSegments, inline.NewSegments);
        }

        SetSelectedDifference(node.Kind == DiffKind.Unchanged ? null : node.StableId);
    }

    public bool TryGetRowAt(int rowNumber, out SpreadsheetRowViewModel? row) =>
        _newRowLookup.TryGetValue(rowNumber, out row) || _oldRowLookup.TryGetValue(rowNumber, out row);

    public bool TryGetColumnIndex(string column, out int index)
    {
        var normalized = column.ToUpperInvariant();
        for (var candidate = 0; candidate < Columns.Count; candidate++)
        {
            if (Columns[candidate].MineColumn == normalized)
            {
                index = candidate;
                return true;
            }
        }
        for (var candidate = 0; candidate < Columns.Count; candidate++)
        {
            if (Columns[candidate].BaseColumn == normalized)
            {
                index = candidate;
                return true;
            }
        }
        return _columnLookup.TryGetValue(normalized, out index);
    }

    public bool TryGetRowByStableId(string stableId, out SpreadsheetRowViewModel? row) =>
        _rowStableLookup.TryGetValue(stableId, out row);

    public void SelectOverviewRow(DiffRowViewModel node)
    {
        if (_visibleRowLookup.TryGetValue(node.StableId, out var visible) && visible.IsCollapsed)
        {
            visible.ExpandCommand?.Execute(null);
        }
        else if (_rowStableLookup.TryGetValue(node.StableId, out var row))
        {
            EnsureVisible(row);
            SelectedRow = row;
            SelectFirstChangedCell(row);
        }
        else
        {
            SelectDifference(node);
        }
    }

    public void EnsureVisible(SpreadsheetRowViewModel row)
    {
        if (IsFrozenHeader(row)) return;
        if (!_rowPositions.TryGetValue(row.StableId, out var index)
            || _foldProjection.IsVisible(index, _displayMode))
        {
            return;
        }

        if (_displayMode == DiffDisplayMode.DifferencesOnly)
        {
            ApplyFilter(DiffDisplayMode.All);
        }
        else if (_foldProjection.Reveal(index))
        {
            RebuildVisibleRows();
        }
    }

    private SpreadsheetRowViewModel CreateCollapsedRow(int start, int count)
    {
        var label = string.Format(CultureInfo.CurrentCulture, FoldLabelFormat, count);
        return new SpreadsheetRowViewModel($"fold:{Name}:{start}:{count}", null, null,
            DiffKind.Unchanged, [], [])
        {
            IsCollapsed = true,
            HiddenCount = count,
            CollapsedLabel = label,
            ExpandCommand = new RelayCommand(() =>
            {
                _foldProjection.Reveal(start);
                RebuildVisibleRows();
                SelectedRow = _allRows[start];
            })
        };
    }

    private static DiffRowViewModel CreateOverviewRow(SpreadsheetRowViewModel row) =>
        new(row.StableId, string.Empty, row.Kind, row.BaseRowNumber,
            row.IsCollapsed ? row.CollapsedLabel : string.Join('\t', row.BaseCells.Select(cell => cell.Text)),
            row.MineRowNumber,
            row.IsCollapsed ? row.CollapsedLabel : string.Join('\t', row.MineCells.Select(cell => cell.Text)),
            null, 0);

    private void SetSelectedDifference(string? stableId)
    {
        _selectedDifferenceId = stableId;
        OnPropertyChanged(nameof(SelectedDifferenceIndex));
        OnPropertyChanged(nameof(SelectedDifferenceId));
    }

    private bool TryGetRow(DiffRowViewModel node, out SpreadsheetRowViewModel? row)
    {
        return _rowLookup.TryGetValue((node.OldIndex, node.NewIndex), out row)
            || (node.NewIndex is { } newIndex && _newRowLookup.TryGetValue(newIndex, out row))
            || (node.OldIndex is { } oldIndex && _oldRowLookup.TryGetValue(oldIndex, out row));
    }

    private void SelectFirstChangedCell(SpreadsheetRowViewModel? row)
    {
        if (row is null || row.IsCollapsed || Columns.Count == 0)
        {
            SelectedCell = null;
            return;
        }

        SelectCell(row, _firstChangedColumns.GetValueOrDefault(row.StableId));
    }
}
