using System.Globalization;
using Differ.Core.Diagnostics;
using Differ.Core.Models;
using Differ.Formats.Excel.Resources;

namespace Differ.Formats.Excel;

internal sealed record LogicalSpreadsheetComparison(TabularDiffSection Section, IReadOnlyList<DiffNode> Nodes);

/// <summary>A single mapped cell pass feeds the grid, counts and navigation.</summary>
internal static class LogicalSpreadsheetComparer
{
    public static LogicalSpreadsheetComparison Compare(SheetPair pair, DiffOptions options, string? fileName,
        List<string> warnings, long priorGridCells, CancellationToken cancellationToken)
    {
        var sheet = pair.NewSheet?.Name ?? pair.OldSheet!.Name;
        ColumnMapping mapping;
        using (ComparisonMetrics.Measure("excel.columns", "Excel"))
            mapping = LogicalColumnMapper.Build(pair, options, fileName, warnings, cancellationToken);
        var columns = mapping.Columns;
        SpreadsheetGridBudget.Validate(Math.Max(pair.OldSheet?.Rows.Count ?? 0, pair.NewSheet?.Rows.Count ?? 0), columns.Length,
            priorGridCells + (long)Math.Max(pair.OldSheet?.Rows.Count ?? 0, pair.NewSheet?.Rows.Count ?? 0) * columns.Length);
        IReadOnlyList<MappedSpreadsheetRow> matches;
        using (ComparisonMetrics.Measure("excel.rows", "Excel", pair.NewSheet?.Rows.Count ?? 0))
            matches = LogicalRowMapper.Build(pair, mapping, warnings, cancellationToken);
        SpreadsheetGridBudget.Validate(matches.Count, columns.Length, priorGridCells + (long)matches.Count * columns.Length);
        var formulas = new MappedFormulaComparer(columns, matches);
        var rows = new List<TabularDiffRow>(matches.Count);
        var nodes = new List<DiffNode>();
        var formulaUnsupported = false;
        using var cellMeasure = ComparisonMetrics.Measure("excel.cells", "Excel", (long)matches.Count * columns.Length);
        foreach (var column in columns)
        {
            if (column.Kind is DiffKind.Added or DiffKind.Deleted)
                nodes.Add(StructureNode($"column:{sheet}:{column.LogicalId}", sheet, ExcelStrings.MappingColumnLabel, column.Kind,
                    column.OldColumn, column.NewColumn, null, null));
            if (column.Confidence == MappingConfidence.Ambiguous)
                nodes.Add(StructureNode($"column-ambiguous:{sheet}:{column.LogicalId}", sheet, ExcelStrings.MappingPendingColumnLabel, DiffKind.Conflict,
                    column.OldColumn, column.NewColumn, null, null));
        }
        if (columns.Any(column => column.Kind == DiffKind.Moved))
            nodes.Add(StructureNode($"column-order:{sheet}", sheet, ExcelStrings.MappingColumnOrderLabel, DiffKind.Moved,
                string.Join(", ", columns.Where(column => column.OldColumn is not null).OrderBy(column => SpreadsheetGridBudget.ColumnNumber(column.OldColumn!))
                    .Select(column => column.OldColumn + " " + column.FieldName)),
                string.Join(", ", columns.Where(column => column.NewColumn is not null).Select(column => column.NewColumn + " " + column.FieldName)), null, null));
        if (matches.Any(row => row.Match.Kind == DiffKind.Moved))
            nodes.Add(StructureNode($"row-order:{sheet}", sheet, ExcelStrings.MappingRecordOrderLabel, DiffKind.Moved,
                string.Join(", ", matches.Where(row => row.Match.Kind == DiffKind.Moved).Take(40).Select(row => row.Match.OldRow)),
                string.Join(", ", matches.Where(row => row.Match.Kind == DiffKind.Moved).Take(40).Select(row => row.Match.NewRow)), null, null));

        foreach (var row in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowId = $"table-row:{sheet}:{row.Match.OldRow}:{row.Match.NewRow}";
            if (row.Match.Kind is DiffKind.Added or DiffKind.Deleted)
                nodes.Add(StructureNode(rowId, sheet, row.Match.IsHeader ? ExcelStrings.MappingHeaderRowLabel : ExcelStrings.MappingDataRowLabel, row.Match.Kind,
                    row.Match.OldRow?.ToString(CultureInfo.InvariantCulture), row.Match.NewRow?.ToString(CultureInfo.InvariantCulture),
                    row.Match.OldRow, row.Match.NewRow));
            var cells = new List<TabularDiffCell>(columns.Length);
            foreach (var column in columns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var left = Cell(row.Old, column.OldColumn);
                var right = Cell(row.New, column.NewColumn);
                var kind = SpreadsheetDiffEngine.DetermineCellKind(left, right, options.CompareFormatting);
                var referenceAdjustment = false;
                if (left?.Formula is not null && right?.Formula is not null)
                {
                    var equivalent = formulas.Equivalent(left.Formula, right.Formula, out var supported);
                    if (!supported && left.Formula != right.Formula) formulaUnsupported = true;
                    if (equivalent && left.Formula != right.Formula)
                    {
                        var normalizedRight = right with { Formula = left.Formula };
                        kind = SpreadsheetDiffEngine.DetermineCellKind(left, normalizedRight, options.CompareFormatting);
                        referenceAdjustment = true;
                    }
                    else if (!equivalent) kind = DiffKind.Modified;
                }
                if (column.Confidence == MappingConfidence.Ambiguous && (left is not null || right is not null)) kind = DiffKind.Conflict;
                var flags = CellChanges(left, right, kind, referenceAdjustment, options.CompareFormatting);
                cells.Add(new TabularDiffCell
                {
                    ColumnKey = column.LogicalId,
                    OldAddress = Address(row.Old, column.OldColumn),
                    NewAddress = Address(row.New, column.NewColumn),
                    OldValue = SpreadsheetDiffEngine.FormatGridCellValue(left),
                    NewValue = SpreadsheetDiffEngine.FormatGridCellValue(right),
                    OldDetails = CellDetails(left, flags),
                    NewDetails = CellDetails(right, flags),
                    Kind = kind,
                    IsStructuralReferenceAdjustment = referenceAdjustment,
                    ChangeFlags = flags
                });
                // Structural additions have one navigation event. Their values
                // remain complete in the grid, search and export projection.
                if (column.Kind is DiffKind.Added or DiffKind.Deleted || row.Match.Kind is DiffKind.Added or DiffKind.Deleted
                    || column.Confidence == MappingConfidence.Ambiguous) continue;
                if (referenceAdjustment)
                    nodes.Add(StructureNode($"formula-reference:{sheet}:{row.Match.LogicalId}:{column.LogicalId}", sheet,
                        ExcelStrings.MappingFormulaAdjustmentLabel, DiffKind.Moved, left?.Formula, right?.Formula, row.Match.OldRow, row.Match.NewRow));
                if (kind == DiffKind.Unchanged && !options.IncludeUnchanged) continue;
                if (left is null && right is null) continue;
                if (kind == DiffKind.Unchanged)
                {
                    // No per-cell metadata dictionaries for unchanged context.
                    nodes.Add(new DiffNode
                    {
                        StableId = $"cell:{sheet}:{row.Match.LogicalId}:{column.LogicalId}",
                        Path = $"{sheet}!{Address(row.New, column.NewColumn) ?? Address(row.Old, column.OldColumn)}",
                        Kind = kind,
                        OldValue = left?.DisplayValue,
                        NewValue = right?.DisplayValue,
                        OldIndex = row.Match.OldRow,
                        NewIndex = row.Match.NewRow,
                        SectionKey = sheet
                    });
                }
                else
                {
                    var node = SpreadsheetDiffEngine.CreateCellNode(pair.OldSheet?.Name ?? sheet, sheet, left, right, kind, options);
                    var metadata = new Dictionary<string, string>(node.Metadata, StringComparer.Ordinal)
                    {
                        ["column"] = column.LogicalId,
                        ["change.kind"] = flags.ToString()
                    };
                    nodes.Add(node with { Metadata = metadata });
                }
            }
            var rowKind = row.Match.Kind;
            if (rowKind == DiffKind.Unchanged && cells.Any(cell => cell.Kind != DiffKind.Unchanged))
                rowKind = cells.Any(cell => cell.Kind == DiffKind.Conflict) ? DiffKind.Conflict : DiffKind.Modified;
            if (row.Old is not null && row.New is not null && row.Old.Hidden != row.New.Hidden)
            {
                rowKind = DiffKind.Modified;
                nodes.Add(StructureNode($"row-hidden:{sheet}:{row.Match.LogicalId}", sheet, ExcelStrings.MappingHiddenRowLabel, DiffKind.Modified,
                    row.Old.Hidden.ToString(), row.New.Hidden.ToString(), row.Match.OldRow, row.Match.NewRow));
            }
            rows.Add(new TabularDiffRow
            {
                StableId = rowId,
                OldIndex = row.Match.OldRow,
                NewIndex = row.Match.NewRow,
                Kind = rowKind,
                Cells = cells
            });
        }
        if (formulaUnsupported)
            warnings.Add(string.Format(CultureInfo.CurrentCulture, ExcelStrings.MappingUnsupportedFormulaFormat, sheet));
        return new LogicalSpreadsheetComparison(new TabularDiffSection
        {
            Key = sheet,
            Columns = columns.Select(column => column.NewColumn ?? column.OldColumn!).ToArray(),
            Rows = rows,
            ColumnMatches = columns,
            RowMatches = matches.Select(row => row.Match).ToArray(),
            HeaderRow = mapping.Rule.HeaderRow,
            DataStartRow = mapping.Rule.DataStartRow,
            MappingDescription = mapping.Description,
            FrozenColumns = Math.Clamp(mapping.Rule.FrozenColumns, 0, columns.Length)
        }, nodes);
    }

    private static ExcelCell? Cell(ExcelRow? row, string? column) => row is not null && column is not null
        ? row.Cells.GetValueOrDefault(column) : null;

    private static string? Address(ExcelRow? row, string? column) => row is not null && column is not null ? $"{column}{row.RowIndex}" : null;

    private static SpreadsheetCellChange CellChanges(ExcelCell? left, ExcelCell? right, DiffKind kind,
        bool referenceAdjustment, bool compareFormatting)
    {
        var result = referenceAdjustment ? SpreadsheetCellChange.StructuralReference : SpreadsheetCellChange.None;
        if (kind == DiffKind.Unchanged) return result;
        if (kind == DiffKind.Conflict) result |= SpreadsheetCellChange.Ambiguous;
        if (left?.DisplayValue != right?.DisplayValue) result |= SpreadsheetCellChange.Value;
        if (left is not null && right is not null && left.DataType != right.DataType) result |= SpreadsheetCellChange.Type;
        if (left?.Formula != right?.Formula && !referenceAdjustment) result |= SpreadsheetCellChange.Formula;
        if (left?.Comment != right?.Comment) result |= SpreadsheetCellChange.Comment;
        if (compareFormatting && left?.StyleSignature != right?.StyleSignature) result |= SpreadsheetCellChange.Formatting;
        return result;
    }

    private static string? CellDetails(ExcelCell? cell, SpreadsheetCellChange flags)
    {
        var value = SpreadsheetDiffEngine.FormatCellValue(cell);
        if (cell is null) return value;
        if ((flags & SpreadsheetCellChange.Formatting) != 0)
            value += Environment.NewLine + string.Format(CultureInfo.CurrentCulture, ExcelStrings.MappingFormatDetails, cell.StyleSignature ?? string.Empty);
        if ((flags & SpreadsheetCellChange.Type) != 0)
            value += Environment.NewLine + string.Format(CultureInfo.CurrentCulture, ExcelStrings.MappingTypeDetails, cell.DataType);
        if ((flags & SpreadsheetCellChange.Comment) != 0)
            value += Environment.NewLine + string.Format(CultureInfo.CurrentCulture, ExcelStrings.MappingCommentDetails, cell.Comment ?? string.Empty);
        if ((flags & SpreadsheetCellChange.StructuralReference) != 0)
            value += Environment.NewLine + ExcelStrings.MappingFormulaAdjustmentLabel;
        return value;
    }

    private static DiffNode StructureNode(string id, string sheet, string category, DiffKind kind,
        string? oldValue, string? newValue, int? oldIndex, int? newIndex) => new()
        {
            StableId = id,
            Path = $"{sheet}/{category}",
            Kind = kind,
            SectionKey = sheet,
            OldValue = oldValue,
            NewValue = newValue,
            OldIndex = oldIndex,
            NewIndex = newIndex,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["structure.kind"] = category }
        };
}
