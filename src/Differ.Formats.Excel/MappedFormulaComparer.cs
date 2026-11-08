using System.Text;
using System.Text.RegularExpressions;
using Differ.Core.Models;

namespace Differ.Formats.Excel;

internal sealed partial class MappedFormulaComparer
{
    private readonly Dictionary<string, string> _oldColumns;
    private readonly Dictionary<string, string> _newColumns;
    private readonly Dictionary<int, string> _oldRows;
    private readonly Dictionary<int, string> _newRows;

    public MappedFormulaComparer(IReadOnlyList<SpreadsheetColumnMatch> columns, IReadOnlyList<MappedSpreadsheetRow> rows)
    {
        _oldColumns = columns.Where(column => column.OldColumn is not null && column.NewColumn is not null
                && column.Confidence != MappingConfidence.Ambiguous)
            .ToDictionary(column => column.OldColumn!, column => column.LogicalId, StringComparer.OrdinalIgnoreCase);
        _newColumns = columns.Where(column => column.OldColumn is not null && column.NewColumn is not null
                && column.Confidence != MappingConfidence.Ambiguous)
            .ToDictionary(column => column.NewColumn!, column => column.LogicalId, StringComparer.OrdinalIgnoreCase);
        _oldRows = rows.Where(row => row.Old is not null && row.New is not null)
            .ToDictionary(row => row.Match.OldRow!.Value, row => row.Match.LogicalId);
        _newRows = rows.Where(row => row.Old is not null && row.New is not null)
            .ToDictionary(row => row.Match.NewRow!.Value, row => row.Match.LogicalId);
    }

    public bool Equivalent(string oldFormula, string newFormula, out bool supported)
    {
        var oldNormalized = Normalize(oldFormula, _oldColumns, _oldRows);
        var newNormalized = Normalize(newFormula, _newColumns, _newRows);
        supported = oldNormalized is not null && newNormalized is not null;
        return supported ? oldNormalized == newNormalized : oldFormula == newFormula;
    }

    private static string? Normalize(string formula, Dictionary<string, string> columns, Dictionary<int, string> rows)
    {
        if (formula.Length > 16384 || formula.IndexOfAny(['!', '[', ']', '{', '}', '#', '@']) >= 0) return null;
        var builder = new StringBuilder(formula.Length);
        var position = 0;
        while (position < formula.Length)
        {
            var character = formula[position];
            if (char.IsAsciiDigit(character) || character == '.' && position + 1 < formula.Length && char.IsAsciiDigit(formula[position + 1]))
            {
                // Consume numeric literals before A1 references: E3 inside
                // 1E3 is an exponent, never a cell that can move to E4.
                var number = NumberPattern().Match(formula, position);
                if (!number.Success || number.Index != position) return null;
                builder.Append(number.Value);
                position += number.Length;
                continue;
            }
            if (character == '"')
            {
                var end = position + 1;
                while (end < formula.Length)
                {
                    if (formula[end++] != '"') continue;
                    if (end < formula.Length && formula[end] == '"') { end++; continue; }
                    break;
                }
                builder.Append(formula.AsSpan(position, end - position));
                position = end;
                continue;
            }
            if (char.IsAsciiLetter(character) || character == '$' || character == '_')
            {
                var range = RangePattern().Match(formula, position);
                if (range.Success && range.Index == position)
                {
                    var normalizedRange = NormalizeRange(range, columns, rows);
                    if (normalizedRange is null) return null;
                    builder.Append(normalizedRange);
                    position += range.Length;
                    continue;
                }
                var match = ReferencePattern().Match(formula, position);
                if (match.Success && match.Index == position
                    && (match.Index + match.Length == formula.Length || formula[match.Index + match.Length] != '('))
                {
                    if (!columns.TryGetValue(match.Groups[2].Value, out var column)
                        || !int.TryParse(match.Groups[4].Value, out var rowNumber) || !rows.TryGetValue(rowNumber, out var row)) return null;
                    builder.Append('<').Append(match.Groups[1].Value).Append(column).Append(':')
                        .Append(match.Groups[3].Value).Append(row).Append('>');
                    position += match.Length;
                    continue;
                }
                var start = position;
                while (position < formula.Length && (char.IsAsciiLetterOrDigit(formula[position]) || formula[position] is '_' or '.')) position++;
                if (position == start) return null;
                var name = formula[start..position];
                var next = position;
                while (next < formula.Length && char.IsWhiteSpace(formula[next])) next++;
                if (name is not ("TRUE" or "FALSE") && (next == formula.Length || formula[next] != '(')) return null;
                builder.Append(name);
                continue;
            }
            builder.Append(character);
            position++;
        }
        return builder.ToString();
    }

    [GeneratedRegex(@"(\$?)([A-Za-z]{1,3})(\$?)([1-9][0-9]*)(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"(\$?)([A-Za-z]{1,3})(\$?)([1-9][0-9]*):(\$?)([A-Za-z]{1,3})(\$?)([1-9][0-9]*)(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex RangePattern();

    private static string? NormalizeRange(Match range, Dictionary<string, string> columns, Dictionary<int, string> rows)
    {
        var firstColumn = SpreadsheetGridBudget.ColumnNumber(range.Groups[2].Value);
        var lastColumn = SpreadsheetGridBudget.ColumnNumber(range.Groups[6].Value);
        if (!int.TryParse(range.Groups[4].Value, out var firstRow) || !int.TryParse(range.Groups[8].Value, out var lastRow)
            || lastColumn < firstColumn || lastRow < firstRow || (long)lastRow - firstRow + lastColumn - firstColumn > 4096) return null;
        var builder = new StringBuilder("<range:");
        builder.Append(range.Groups[1].Value).Append('/').Append(range.Groups[3].Value).Append('/')
            .Append(range.Groups[5].Value).Append('/').Append(range.Groups[7].Value).Append(':');
        for (var column = firstColumn; column <= lastColumn; column++)
        {
            if (!columns.TryGetValue(SpreadsheetDiffEngine.GetColumnLetter(column - 1), out var logical)) return null;
            builder.Append(logical).Append(',');
        }
        builder.Append(';');
        for (var row = firstRow; row <= lastRow; row++)
        {
            if (!rows.TryGetValue(row, out var logical)) return null;
            builder.Append(logical).Append(',');
        }
        return builder.Append('>').ToString();
    }
}
