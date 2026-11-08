using System.Globalization;
using System.Text;
using Differ.Core.Algorithms;
using Differ.Formats.Excel.Resources;

namespace Differ.Formats.Excel;

/// <summary>
/// Explicit keys are authoritative only when every component exists and each
/// complete key is unique on each side. Validate both sides before pairing any
/// row, so an invalid key never partially changes the default alignment.
/// </summary>
internal static class KeyedRowAligner
{
    private static readonly CompositeFormat InvalidColumnsFormat =
        CompositeFormat.Parse(ExcelStrings.AdapterKeyColumnsInvalidFormat);
    private static readonly CompositeFormat MissingColumnFormat =
        CompositeFormat.Parse(ExcelStrings.AdapterKeyColumnMissingFormat);
    private static readonly CompositeFormat EmptyKeyFormat =
        CompositeFormat.Parse(ExcelStrings.AdapterKeyEmptyFormat);
    private static readonly CompositeFormat DuplicateKeyFormat =
        CompositeFormat.Parse(ExcelStrings.AdapterKeyDuplicateFormat);

    public static bool TryAlign(
        ExcelSheet oldSheet,
        ExcelSheet newSheet,
        IReadOnlyList<string> requestedColumns,
        CancellationToken cancellationToken,
        out IReadOnlyList<SequenceMatch<ExcelRow>> matches,
        out string? warning)
    {
        matches = [];
        if (!TryNormalizeColumns(requestedColumns, out var columns))
        {
            warning = string.Format(CultureInfo.CurrentCulture, InvalidColumnsFormat, newSheet.Name);
            return false;
        }

        if (!TryIndexRows(oldSheet, newSheet.Name, ExcelStrings.AdapterBaseSide, columns,
                cancellationToken, out var oldKeys, out var oldIndexes, out warning)
            || !TryIndexRows(newSheet, newSheet.Name, ExcelStrings.AdapterCurrentSide, columns,
                cancellationToken, out var newKeys, out var newIndexes, out warning))
        {
            return false;
        }

        var result = new List<SequenceMatch<ExcelRow>>(oldSheet.Rows.Count + newSheet.Rows.Count);
        for (var oldIndex = 0; oldIndex < oldSheet.Rows.Count; oldIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var oldRow = oldSheet.Rows[oldIndex];
            if (newIndexes.TryGetValue(oldKeys[oldIndex], out var newIndex))
            {
                var newRow = newSheet.Rows[newIndex];
                result.Add(new SequenceMatch<ExcelRow>(
                    string.Equals(oldRow.Signature, newRow.Signature, StringComparison.Ordinal)
                        ? SequenceMatchKind.Equal : SequenceMatchKind.Modified,
                    oldRow, newRow, oldIndex, newIndex));
            }
            else
            {
                result.Add(new SequenceMatch<ExcelRow>(SequenceMatchKind.Deleted,
                    oldRow, null, oldIndex, null));
            }
        }

        // Retain the BASE reading order. New records have no BASE position, so
        // append them in their original current-file order and retain both
        // physical row numbers in the shared tabular/diff models.
        for (var newIndex = 0; newIndex < newSheet.Rows.Count; newIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!oldIndexes.ContainsKey(newKeys[newIndex]))
            {
                result.Add(new SequenceMatch<ExcelRow>(SequenceMatchKind.Added,
                    null, newSheet.Rows[newIndex], null, newIndex));
            }
        }

        matches = result;
        warning = null;
        return true;
    }

    private static bool TryNormalizeColumns(IReadOnlyList<string> requested, out string[] columns)
    {
        columns = [];
        if (requested.Count == 0 || requested.Count > DiffPerformanceOptions.MaximumTabularColumns)
        {
            return false;
        }

        var normalized = new List<string>(requested.Count);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in requested)
        {
            var column = value?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(column) || column.Length > 3)
            {
                return false;
            }

            var number = 0;
            foreach (var character in column)
            {
                if (character is < 'A' or > 'Z')
                {
                    return false;
                }

                number = number * 26 + character - 'A' + 1;
            }

            if (number > DiffPerformanceOptions.MaximumTabularColumns || !unique.Add(column))
            {
                return false;
            }

            normalized.Add(column);
        }

        columns = normalized.ToArray();
        return true;
    }

    private static bool TryIndexRows(
        ExcelSheet sheet,
        string displaySheetName,
        string side,
        string[] columns,
        CancellationToken cancellationToken,
        out string[] keys,
        out Dictionary<string, int> indexes,
        out string? warning)
    {
        keys = new string[sheet.Rows.Count];
        indexes = new Dictionary<string, int>(sheet.Rows.Count, StringComparer.Ordinal);
        var availableColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in sheet.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            availableColumns.UnionWith(row.Cells.Keys);
        }

        foreach (var column in columns)
        {
            if (!availableColumns.Contains(column))
            {
                warning = string.Format(CultureInfo.CurrentCulture, MissingColumnFormat,
                    displaySheetName, side, column);
                return false;
            }
        }

        for (var index = 0; index < sheet.Rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = sheet.Rows[index];
            var builder = new StringBuilder();
            foreach (var column in columns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!row.Cells.TryGetValue(column, out var cell)
                    || string.IsNullOrWhiteSpace(cell.DisplayValue))
                {
                    warning = string.Format(CultureInfo.CurrentCulture, EmptyKeyFormat,
                        displaySheetName, side, row.RowIndex, column);
                    return false;
                }

                // Length prefixes make composite keys unambiguous even when a
                // value contains delimiters, newlines or another key's prefix.
                // DisplayValue is the full parsed semantic value, not the UI
                // text. Formula caches are compared as saved, never evaluated.
                AppendComponent(builder, cell.DataType);
                AppendComponent(builder, cell.DisplayValue);
            }

            var key = builder.ToString();
            if (!indexes.TryAdd(key, index))
            {
                warning = string.Format(CultureInfo.CurrentCulture, DuplicateKeyFormat,
                    displaySheetName, side, sheet.Rows[indexes[key]].RowIndex, row.RowIndex);
                return false;
            }

            keys[index] = key;
        }

        warning = null;
        return true;
    }

    private static void AppendComponent(StringBuilder builder, string value)
    {
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }
}
