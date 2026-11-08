using System.Globalization;
using Differ.Core.Algorithms;
using Differ.Core.Security;
using Differ.Formats.Excel.Resources;

namespace Differ.Formats.Excel;

internal static class SpreadsheetGridBudget
{
    public static void Validate(long rows, long columns, long cells)
    {
        if (rows > DiffPerformanceOptions.MaximumTabularRows
            || columns > DiffPerformanceOptions.MaximumTabularColumns
            || cells > DiffPerformanceOptions.MaximumTabularCells)
        {
            throw new ComparisonResourceLimitException(ExcelStrings.AdapterGridLimit);
        }
    }

    public static IReadOnlyList<ExcelRow> FillInternalRows(IReadOnlyList<ExcelRow> rows)
    {
        if (rows.Count == 0)
        {
            return rows;
        }

        var ordered = rows.OrderBy(row => row.RowIndex).ToArray();
        var count = (long)ordered[^1].RowIndex - ordered[0].RowIndex + 1;
        Validate(count, 0, 0);
        var result = new List<ExcelRow>((int)count);
        uint next = ordered[0].RowIndex;
        foreach (var row in ordered)
        {
            while (next < row.RowIndex)
            {
                result.Add(new ExcelRow(next++, false, EmptyCells, string.Empty));
            }

            if (next != row.RowIndex)
            {
                throw new StructuralParseException(ExcelStrings.AdapterInvalidAddress);
            }

            result.Add(row);
            next++;
        }

        return result;
    }

    public static int ColumnNumber(string column)
    {
        var result = 0;
        foreach (var value in column)
        {
            var letter = char.ToUpperInvariant(value);
            if (letter is < 'A' or > 'Z' || result > 16384)
            {
                throw new StructuralParseException(ExcelStrings.AdapterInvalidAddress);
            }

            result = (result * 26) + letter - 'A' + 1;
        }

        if (result is < 1 or > 16384)
        {
            throw new StructuralParseException(ExcelStrings.AdapterInvalidAddress);
        }

        return result;
    }

    public static (int Column, int Row) ParseAddress(string address)
    {
        var column = SpreadsheetDiffEngine.GetColumnName(address);
        if (!int.TryParse(address.AsSpan(column.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out var row)
            || row is < 1 or > 1048576)
        {
            throw new StructuralParseException(ExcelStrings.AdapterInvalidAddress);
        }

        return (ColumnNumber(column), row);
    }

    private static IReadOnlyDictionary<string, ExcelCell> EmptyCells { get; } =
        new Dictionary<string, ExcelCell>();
}
