using System.Globalization;
using System.Text;
using Differ.Core.Algorithms;
using Differ.Core.Models;
using Differ.Formats.Excel.Resources;

namespace Differ.Formats.Excel;

internal sealed record SpreadsheetComparison(
    IReadOnlyList<DiffNode> Nodes,
    IReadOnlyList<string> Sections,
    IReadOnlyList<TabularDiffSection> TabularSections,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Comparison pipeline shared by every spreadsheet extractor. Sheet pairing,
/// row alignment, tabular sections and diff nodes only depend on the
/// parser-agnostic <see cref="ExcelLogicalWorkbook"/> model.
/// </summary>
internal static class SpreadsheetDiffEngine
{
    private static readonly string[] SingleLetterColumns =
        Enumerable.Range('A', 26).Select(letter => ((char)letter).ToString()).ToArray();
    private static readonly CompositeFormat FormulaDetailsFormat =
        CompositeFormat.Parse(ExcelStrings.AdapterFormulaDetailsFormat);

    public static SpreadsheetComparison Compare(
        ExcelLogicalWorkbook oldWorkbook,
        ExcelLogicalWorkbook newWorkbook,
        DiffOptions options,
        CancellationToken cancellationToken,
        bool logicalMapping = true,
        string? fileName = null)
    {
        var sheetPairs = PairSheets(oldWorkbook.Sheets, newWorkbook.Sheets)
            .OrderBy(pair => pair.NewSheet?.Index ?? int.MaxValue)
            .ThenBy(pair => pair.OldSheet?.Index ?? int.MaxValue)
            .ToList();
        var nodes = new List<DiffNode>();
        var sections = new List<string>(sheetPairs.Count);
        var tabularSections = new List<TabularDiffSection>(sheetPairs.Count);
        var warnings = oldWorkbook.Warnings
            .Concat(newWorkbook.Warnings)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        long totalGridCells = 0;

        foreach (var pair in sheetPairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sectionName = pair.NewSheet?.Name ?? pair.OldSheet!.Name;
            sections.Add(sectionName);
            if (logicalMapping)
            {
                var mapped = LogicalSpreadsheetComparer.Compare(pair, options, fileName, warnings,
                    totalGridCells, cancellationToken);
                totalGridCells += (long)mapped.Section.Rows.Count * mapped.Section.Columns.Count;
                var metadataStart = nodes.Count;
                AppendSheetDiff(pair, [], options, nodes, cancellationToken);
                for (var metadataIndex = metadataStart; metadataIndex < nodes.Count; metadataIndex++)
                    nodes[metadataIndex] = nodes[metadataIndex] with { SectionKey = sectionName };
                nodes.AddRange(mapped.Nodes);
                tabularSections.Add(mapped.Section);
                continue;
            }
            var columns = CreateColumns(pair);
            // Check the input rectangle before row alignment, then account
            // for insertion/deletion placeholders before allocating cells.
            SpreadsheetGridBudget.Validate(
                Math.Max(pair.OldSheet?.Rows.Count ?? 0, pair.NewSheet?.Rows.Count ?? 0),
                columns.Length,
                totalGridCells + (long)Math.Max(pair.OldSheet?.Rows.Count ?? 0,
                    pair.NewSheet?.Rows.Count ?? 0) * columns.Length);
            var rowMatches = AlignRows(pair, options, warnings, cancellationToken);
            totalGridCells += (long)rowMatches.Count * columns.Length;
            SpreadsheetGridBudget.Validate(rowMatches.Count, columns.Length, totalGridCells);
            var firstNodeIndex = nodes.Count;
            AppendSheetDiff(pair, rowMatches, options, nodes, cancellationToken);
            for (var nodeIndex = firstNodeIndex; nodeIndex < nodes.Count; nodeIndex++)
            {
                if (nodes[nodeIndex].SectionKey == sectionName)
                {
                    continue;
                }

                nodes[nodeIndex] = nodes[nodeIndex] with { SectionKey = sectionName };
            }

            tabularSections.Add(
                CreateTabularSection(pair, rowMatches, columns, options, cancellationToken));
        }

        if (oldWorkbook.HasFormulas || newWorkbook.HasFormulas)
        {
            warnings.Add(ExcelStrings.AdapterFormulaWarning);
        }

        return new SpreadsheetComparison(nodes, sections, tabularSections, warnings);
    }

    public static string BuildRowSignature(IEnumerable<ExcelCell> cells)
    {
        return string.Join(
            "\u001f",
            cells
                .OrderBy(cell => cell.Column, StringComparer.OrdinalIgnoreCase)
                .Where(cell => !string.IsNullOrEmpty(cell.DisplayValue) || cell.Formula is not null)
                .Select(cell =>
                    $"{cell.Column}:{Normalize(cell.DisplayValue)}:{Normalize(cell.Formula)}"));
    }

    public static string BuildSheetSignature(IEnumerable<ExcelRow> rows)
    {
        return string.Join(
            "\u001e",
            rows.Take(100).Select(row => row.Signature));
    }

    public static string GetColumnName(string address)
    {
        var length = 0;
        while (length < address.Length && char.IsLetter(address[length]))
        {
            length++;
        }

        if (length == 0) return address;
        if (length == 1 && char.ToUpperInvariant(address[0]) is >= 'A' and <= 'Z' and var letter)
        {
            return SingleLetterColumns[letter - 'A'];
        }

        return address[..length].ToUpperInvariant();
    }

    public static string GetColumnLetter(int zeroBasedColumn)
    {
        var name = string.Empty;
        var column = zeroBasedColumn;
        do
        {
            name = (char)('A' + (column % 26)) + name;
            column = (column / 26) - 1;
        }
        while (column >= 0);

        return name;
    }

    private static string Normalize(string? value)
    {
        return string.Join(
            ' ',
            (value ?? string.Empty).Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static List<SheetPair> PairSheets(
        IReadOnlyList<ExcelSheet> oldSheets,
        IReadOnlyList<ExcelSheet> newSheets)
    {
        var result = new List<SheetPair>();
        var unmatchedNew = new HashSet<ExcelSheet>(newSheets);

        foreach (var oldSheet in oldSheets)
        {
            var exact = unmatchedNew.FirstOrDefault(newSheet =>
                string.Equals(oldSheet.Name, newSheet.Name, StringComparison.Ordinal));
            if (exact is not null)
            {
                result.Add(new SheetPair(oldSheet, exact, IsRename: false));
                unmatchedNew.Remove(exact);
                continue;
            }

            var candidate = unmatchedNew
                .Select(newSheet => new
                {
                    Sheet = newSheet,
                    Score = TextSimilarity.Jaccard(oldSheet.Signature, newSheet.Signature)
                })
                .OrderByDescending(item => item.Score)
                .FirstOrDefault();
            if (candidate is not null && candidate.Score >= 0.6)
            {
                result.Add(new SheetPair(oldSheet, candidate.Sheet, IsRename: true));
                unmatchedNew.Remove(candidate.Sheet);
            }
            else
            {
                result.Add(new SheetPair(oldSheet, null, IsRename: false));
            }
        }

        result.AddRange(unmatchedNew.Select(newSheet =>
            new SheetPair(null, newSheet, IsRename: false)));
        return result;
    }

    private static IReadOnlyList<SequenceMatch<ExcelRow>> AlignRows(
        SheetPair pair,
        DiffOptions options,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (options.KeyColumns is { Count: > 0 }
            && pair.OldSheet is not null && pair.NewSheet is not null)
        {
            if (KeyedRowAligner.TryAlign(pair.OldSheet, pair.NewSheet, options.KeyColumns,
                    cancellationToken, out var keyedMatches, out var warning))
            {
                return keyedMatches;
            }

            if (warning is not null)
            {
                warnings.Add(warning);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return SequenceAligner.Align(
            pair.OldSheet?.Rows ?? [],
            pair.NewSheet?.Rows ?? [],
            row => row.Signature,
            (left, right) => TextSimilarity.Jaccard(
                left.Signature,
                right.Signature),
            replacementThreshold: 0.15);
    }

    private static TabularDiffSection CreateTabularSection(
        SheetPair pair,
        IReadOnlyList<SequenceMatch<ExcelRow>> rowMatches,
        string[] columns,
        DiffOptions options,
        CancellationToken cancellationToken)
    {
        var sectionName = pair.NewSheet?.Name ?? pair.OldSheet!.Name;
        var rows = new List<TabularDiffRow>(rowMatches.Count);
        foreach (var match in rowMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(CreateTabularRow(sectionName, match, columns, options.CompareFormatting));
        }

        return new TabularDiffSection
        {
            Key = sectionName,
            Columns = columns,
            Rows = rows
        };
    }

    private static string[] CreateColumns(SheetPair pair)
    {
        var present = (pair.OldSheet?.Rows ?? [])
            .SelectMany(row => row.Cells.Keys)
            .Union(
                (pair.NewSheet?.Rows ?? [])
                    .SelectMany(row => row.Cells.Keys),
                StringComparer.OrdinalIgnoreCase)
            .Select(SpreadsheetGridBudget.ColumnNumber)
            .ToArray();
        if (present.Length == 0)
        {
            return (pair.OldSheet?.Rows.Count ?? 0) + (pair.NewSheet?.Rows.Count ?? 0) > 0 ? ["A"] : [];
        }

        var first = present.Min();
        var last = present.Max();
        SpreadsheetGridBudget.Validate(0, last - first + 1, 0);
        return Enumerable.Range(first - 1, last - first + 1).Select(GetColumnLetter).ToArray();
    }

    private static TabularDiffRow CreateTabularRow(
        string sectionName,
        SequenceMatch<ExcelRow> match,
        string[] columns,
        bool compareFormatting)
    {
        var oldRow = match.OldItem;
        var newRow = match.NewItem;
        var cells = new List<TabularDiffCell>(columns.Length);
        foreach (var column in columns)
        {
            ExcelCell? oldCell = null;
            ExcelCell? newCell = null;
            if (oldRow is not null)
            {
                oldRow.Cells.TryGetValue(column, out oldCell);
            }

            if (newRow is not null)
            {
                newRow.Cells.TryGetValue(column, out newCell);
            }

            var kind = DetermineCellKind(
                oldCell,
                newCell,
                compareFormatting);

            cells.Add(new TabularDiffCell
            {
                ColumnKey = column,
                OldAddress = oldCell?.Address ?? (oldRow is null ? null : $"{column}{oldRow.RowIndex}"),
                NewAddress = newCell?.Address ?? (newRow is null ? null : $"{column}{newRow.RowIndex}"),
                OldValue = FormatGridCellValue(oldCell),
                NewValue = FormatGridCellValue(newCell),
                OldDetails = FormatCellValue(oldCell),
                NewDetails = FormatCellValue(newCell),
                Kind = kind
            });
        }

        var rowKind = DetermineRowKind(match, cells);
        return new TabularDiffRow
        {
            StableId = $"table-row:{sectionName}:{oldRow?.RowIndex}:{newRow?.RowIndex}",
            OldIndex = oldRow is null ? null : checked((int)oldRow.RowIndex),
            NewIndex = newRow is null ? null : checked((int)newRow.RowIndex),
            Kind = rowKind,
            Cells = cells
        };
    }

    private static DiffKind DetermineRowKind(
        SequenceMatch<ExcelRow> match,
        IReadOnlyList<TabularDiffCell> cells)
    {
        if (match.Kind == SequenceMatchKind.Added)
        {
            return DiffKind.Added;
        }

        if (match.Kind == SequenceMatchKind.Deleted)
        {
            return DiffKind.Deleted;
        }

        if (match.OldItem?.Hidden != match.NewItem?.Hidden)
        {
            return DiffKind.Modified;
        }

        var hasConflict = false;
        var hasUnsupported = false;
        var hasModified = false;
        var hasAdded = false;
        var hasDeleted = false;
        var hasMoved = false;
        foreach (var cell in cells)
        {
            switch (cell.Kind)
            {
                case DiffKind.Conflict:
                    hasConflict = true;
                    break;
                case DiffKind.Unsupported:
                    hasUnsupported = true;
                    break;
                case DiffKind.Modified:
                    hasModified = true;
                    break;
                case DiffKind.Added:
                    hasAdded = true;
                    break;
                case DiffKind.Deleted:
                    hasDeleted = true;
                    break;
                case DiffKind.Moved:
                    hasMoved = true;
                    break;
            }
        }

        if (hasConflict)
        {
            return DiffKind.Conflict;
        }

        if (hasUnsupported)
        {
            return DiffKind.Unsupported;
        }

        if (hasModified || (hasAdded && hasDeleted))
        {
            return DiffKind.Modified;
        }

        if (hasAdded)
        {
            return DiffKind.Added;
        }

        if (hasDeleted)
        {
            return DiffKind.Deleted;
        }

        return hasMoved
            ? DiffKind.Moved
            : DiffKind.Unchanged;
    }

    private static void AppendSheetDiff(
        SheetPair pair,
        IReadOnlyList<SequenceMatch<ExcelRow>> rowMatches,
        DiffOptions options,
        List<DiffNode> nodes,
        CancellationToken cancellationToken)
    {
        if (pair.OldSheet is null)
        {
            nodes.Add(new DiffNode
            {
                StableId = $"sheet:{DiffKind.Added}:{pair.NewSheet!.Index}",
                Path = $"工作表：{pair.NewSheet.Name}",
                Kind = DiffKind.Added,
                NewValue = pair.NewSheet.Name
            });
        }
        else if (pair.NewSheet is null)
        {
            nodes.Add(new DiffNode
            {
                StableId = $"sheet:{DiffKind.Deleted}:{pair.OldSheet.Index}",
                Path = $"工作表：{pair.OldSheet.Name}",
                Kind = DiffKind.Deleted,
                OldValue = pair.OldSheet.Name
            });
        }
        else
        {
            var oldSheet = pair.OldSheet;
            var newSheet = pair.NewSheet;
            if (pair.IsRename)
            {
                nodes.Add(new DiffNode
                {
                    StableId = $"sheet-rename:{oldSheet.Index}:{newSheet.Index}",
                    Path = $"工作表：{oldSheet.Name} → {newSheet.Name}",
                    Kind = DiffKind.Modified,
                    OldValue = oldSheet.Name,
                    NewValue = newSheet.Name
                });
            }

            if (oldSheet.Index != newSheet.Index)
            {
                nodes.Add(new DiffNode
                {
                    StableId = $"sheet-order:{oldSheet.Index}:{newSheet.Index}:{newSheet.Name}",
                    Path = $"工作表顺序：{newSheet.Name}",
                    Kind = DiffKind.Moved,
                    OldValue = oldSheet.Index.ToString(CultureInfo.InvariantCulture),
                    NewValue = newSheet.Index.ToString(CultureInfo.InvariantCulture),
                    OldIndex = oldSheet.Index,
                    NewIndex = newSheet.Index
                });
            }
        }

        var oldSheetName = pair.OldSheet?.Name ?? pair.NewSheet!.Name;
        var newSheetName = pair.NewSheet?.Name ?? pair.OldSheet!.Name;
        foreach (var match in rowMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendRowDiff(oldSheetName, newSheetName, match, options, nodes);
        }

        if (pair.OldSheet is not null && pair.NewSheet is not null)
        {
            AppendSetDiff(
                pair.NewSheet.Name,
                "合并区域",
                pair.OldSheet.MergedRanges,
                pair.NewSheet.MergedRanges,
                nodes);
            AppendSetDiff(
                pair.NewSheet.Name,
                "隐藏列",
                ExpandHiddenColumns(pair.OldSheet.HiddenColumns),
                ExpandHiddenColumns(pair.NewSheet.HiddenColumns),
                nodes);
        }
    }

    private static void AppendRowDiff(
        string oldSheetName,
        string newSheetName,
        SequenceMatch<ExcelRow> match,
        DiffOptions options,
        List<DiffNode> nodes)
    {
        if (match.Kind == SequenceMatchKind.Added)
        {
            var firstNode = nodes.Count;
            foreach (var cell in match.NewItem!.Cells.Values)
            {
                if (IsEmptyCell(cell, options.CompareFormatting))
                {
                    continue;
                }

                nodes.Add(CreateCellNode(
                    oldSheetName,
                    newSheetName,
                    null,
                    cell,
                    DiffKind.Added,
                    options));
            }

            if (nodes.Count == firstNode)
            {
                nodes.Add(CreateEmptyRowNode(newSheetName, match));
            }

            return;
        }

        if (match.Kind == SequenceMatchKind.Deleted)
        {
            var firstNode = nodes.Count;
            foreach (var cell in match.OldItem!.Cells.Values)
            {
                if (IsEmptyCell(cell, options.CompareFormatting))
                {
                    continue;
                }

                nodes.Add(CreateCellNode(
                    oldSheetName,
                    newSheetName,
                    cell,
                    null,
                    DiffKind.Deleted,
                    options));
            }

            if (nodes.Count == firstNode)
            {
                nodes.Add(CreateEmptyRowNode(oldSheetName, match));
            }

            return;
        }

        var oldRow = match.OldItem!;
        var newRow = match.NewItem!;
        var columns = oldRow.Cells.Keys
            .Union(newRow.Cells.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(column => column, ColumnNameComparer.Instance);
        foreach (var column in columns)
        {
            oldRow.Cells.TryGetValue(column, out var oldCell);
            newRow.Cells.TryGetValue(column, out var newCell);
            var kind = DetermineCellKind(oldCell, newCell, options.CompareFormatting);

            if (!options.IncludeUnchanged && kind == DiffKind.Unchanged)
            {
                continue;
            }

            nodes.Add(CreateCellNode(
                oldSheetName,
                newSheetName,
                oldCell,
                newCell,
                kind,
                options));
        }

        if (oldRow.Hidden != newRow.Hidden)
        {
            nodes.Add(new DiffNode
            {
                StableId = $"row-hidden:{oldSheetName}:{oldRow.RowIndex}:{newRow.RowIndex}",
                Path = $"{newSheetName}/行隐藏状态",
                Kind = DiffKind.Modified,
                OldValue = $"行 {oldRow.RowIndex}: {oldRow.Hidden}",
                NewValue = $"行 {newRow.RowIndex}: {newRow.Hidden}"
            });
        }
    }

    internal static DiffNode CreateCellNode(
        string oldSheetName,
        string newSheetName,
        ExcelCell? oldCell,
        ExcelCell? newCell,
        DiffKind kind,
        DiffOptions options)
    {
        var oldAddress = oldCell?.Address;
        var newAddress = newCell?.Address;
        var path = oldAddress == newAddress || oldAddress is null
            ? $"{newSheetName}!{newAddress ?? oldAddress}"
            : newAddress is null
                ? $"{oldSheetName}!{oldAddress}"
                : $"{oldSheetName}!{oldAddress} → {newSheetName}!{newAddress}";

        var metadata = new Dictionary<string, string>(options.CompareFormatting ? 11 : 9)
        {
            ["old.address"] = oldAddress ?? string.Empty,
            ["new.address"] = newAddress ?? string.Empty,
            ["column"] = newCell?.Column ?? oldCell?.Column ?? string.Empty,
            ["old.formula"] = oldCell?.Formula ?? string.Empty,
            ["new.formula"] = newCell?.Formula ?? string.Empty,
            ["old.type"] = oldCell?.DataType ?? string.Empty,
            ["new.type"] = newCell?.DataType ?? string.Empty,
            ["old.comment"] = oldCell?.Comment ?? string.Empty,
            ["new.comment"] = newCell?.Comment ?? string.Empty
        };
        if (options.CompareFormatting)
        {
            metadata["old.style"] =
                oldCell?.StyleSignature ?? string.Empty;
            metadata["new.style"] =
                newCell?.StyleSignature ?? string.Empty;
        }

        return new DiffNode
        {
            StableId = $"cell:{oldSheetName}:{oldAddress}:{newSheetName}:{newAddress}",
            Path = path,
            Kind = kind,
            OldValue = FormatCellValue(oldCell),
            NewValue = FormatCellValue(newCell),
            OldIndex = TryGetRowIndex(oldAddress),
            NewIndex = TryGetRowIndex(newAddress),
            SectionKey = newSheetName,
            Metadata = metadata
        };
    }

    internal static DiffKind DetermineCellKind(
        ExcelCell? oldCell,
        ExcelCell? newCell,
        bool compareFormatting)
    {
        if (IsEmptyCell(oldCell, compareFormatting) && IsEmptyCell(newCell, compareFormatting)
            && (!compareFormatting || oldCell is null || newCell is null
                || string.Equals(oldCell.StyleSignature, newCell.StyleSignature, StringComparison.Ordinal)))
        {
            return DiffKind.Unchanged;
        }

        if (oldCell is null && newCell is null)
        {
            return DiffKind.Unchanged;
        }

        if (oldCell is null)
        {
            return DiffKind.Added;
        }

        if (newCell is null)
        {
            return DiffKind.Deleted;
        }

        var equal = string.Equals(
                oldCell.DisplayValue,
                newCell.DisplayValue,
                StringComparison.Ordinal)
            && string.Equals(oldCell.Formula, newCell.Formula, StringComparison.Ordinal)
            && string.Equals(oldCell.DataType, newCell.DataType, StringComparison.Ordinal)
            && string.Equals(oldCell.Comment, newCell.Comment, StringComparison.Ordinal)
            && (!compareFormatting || string.Equals(
                oldCell.StyleSignature, newCell.StyleSignature, StringComparison.Ordinal));
        return equal ? DiffKind.Unchanged : DiffKind.Modified;
    }

    private static bool IsEmptyCell(ExcelCell? cell, bool compareFormatting)
    {
        return cell is null || !cell.PreserveEmpty
            && string.IsNullOrEmpty(cell.DisplayValue) && cell.Formula is null
            && string.IsNullOrEmpty(cell.Comment)
            && (!compareFormatting || string.Equals(
                cell.StyleSignature, cell.DefaultStyleSignature, StringComparison.Ordinal));
    }

    private static DiffNode CreateEmptyRowNode(string sheet, SequenceMatch<ExcelRow> match)
    {
        var added = match.Kind == SequenceMatchKind.Added;
        var row = added ? match.NewItem! : match.OldItem!;
        return new DiffNode
        {
            StableId = $"empty-row:{sheet}:{match.Kind}:{row.RowIndex}",
            Path = $"{sheet}!{row.RowIndex}",
            Kind = added ? DiffKind.Added : DiffKind.Deleted,
            OldIndex = added ? null : checked((int)row.RowIndex),
            NewIndex = added ? checked((int)row.RowIndex) : null,
            OldValue = added ? null : ExcelStrings.AdapterEmptyRow,
            NewValue = added ? ExcelStrings.AdapterEmptyRow : null
        };
    }

    private static string[] ExpandHiddenColumns(IReadOnlyList<string> ranges)
    {
        var columns = new SortedSet<int>();
        foreach (var range in ranges)
        {
            var bounds = range.Split(':');
            if (bounds.Length != 2
                || !int.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)
                || !int.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out var last)
                || first < 1 || last < first || last > DiffPerformanceOptions.MaximumTabularColumns)
            {
                throw new Differ.Core.Security.StructuralParseException(ExcelStrings.AdapterInvalidAddress);
            }

            for (var column = first; column <= last; column++)
            {
                columns.Add(column);
            }
        }

        return columns.Select(column => GetColumnLetter(column - 1)).ToArray();
    }

    private static void AppendSetDiff(
        string sheetName,
        string category,
        IReadOnlyList<string> oldValues,
        IReadOnlyList<string> newValues,
        List<DiffNode> nodes)
    {
        foreach (var removed in oldValues.Except(newValues, StringComparer.Ordinal))
        {
            nodes.Add(new DiffNode
            {
                StableId = $"{category}:deleted:{sheetName}:{removed}",
                Path = $"{sheetName}/{category}",
                Kind = DiffKind.Deleted,
                OldValue = removed
            });
        }

        foreach (var added in newValues.Except(oldValues, StringComparer.Ordinal))
        {
            nodes.Add(new DiffNode
            {
                StableId = $"{category}:added:{sheetName}:{added}",
                Path = $"{sheetName}/{category}",
                Kind = DiffKind.Added,
                NewValue = added
            });
        }
    }

    internal static string? FormatCellValue(ExcelCell? cell)
    {
        if (cell is null)
        {
            return null;
        }

        return cell.Formula is null
            ? cell.DisplayValue
            : string.Format(
                CultureInfo.CurrentCulture,
                FormulaDetailsFormat,
                cell.Formula,
                cell.DisplayValue);
    }

    internal static string? FormatGridCellValue(ExcelCell? cell)
    {
        if (cell is null)
        {
            return null;
        }

        return cell.Formula is null
            ? cell.DisplayValue
            : $"={cell.Formula}";
    }

    private static int? TryGetRowIndex(string? address)
    {
        if (address is null)
        {
            return null;
        }

        var prefixLength = 0;
        while (prefixLength < address.Length && char.IsLetter(address[prefixLength])) prefixLength++;
        var row = address.AsSpan(prefixLength);
        if (!row.IsEmpty && row.IndexOfAnyExceptInRange('0', '9') < 0)
        {
            return int.TryParse(row, NumberStyles.None, CultureInfo.InvariantCulture, out var rowIndex)
                ? rowIndex : null;
        }

        // Preserve the previous fallback for synthetic/noncanonical addresses.
        var digits = new string(address.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : null;
    }

    private sealed class ColumnNameComparer : IComparer<string>
    {
        public static ColumnNameComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (left is null)
            {
                return right is null ? 0 : -1;
            }

            if (right is null)
            {
                return 1;
            }

            return ColumnNumber(left).CompareTo(ColumnNumber(right));
        }

        private static int ColumnNumber(string value)
        {
            var result = 0;
            foreach (var character in value.ToUpperInvariant())
            {
                if (character is < 'A' or > 'Z')
                {
                    continue;
                }

                result = (result * 26) + (character - 'A' + 1);
            }

            return result;
        }
    }
}

