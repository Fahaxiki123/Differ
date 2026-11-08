using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Differ.Core.Security;
using Differ.Formats.Excel.Resources;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Differ.Formats.Excel;

/// <summary>
/// Expands A1 references without evaluating formulas. Quoted strings, sheet
/// names and structured-reference brackets are lexical units and are never
/// rewritten as cell references.
/// </summary>
internal sealed partial class SharedFormulaResolver
{
    private readonly Dictionary<uint, (string Address, string Formula, string Range)> masters = [];

    public SharedFormulaResolver(IEnumerable<Cell> cells)
    {
        foreach (var cell in cells)
        {
            if (cell.CellFormula is not { } formula
                || formula.FormulaType?.Value != CellFormulaValues.Shared
                || string.IsNullOrEmpty(formula.Text))
            {
                continue;
            }

            if (formula.SharedIndex?.Value is not { } index
                || cell.CellReference?.Value is not { } address
                || formula.Reference?.Value is not { } range
                || !masters.TryAdd(index, (address, formula.Text, range)))
            {
                throw InvalidFormula();
            }
        }
    }

    public string? Resolve(Cell cell)
    {
        if (cell.CellFormula is not { } formula)
        {
            return null;
        }

        if (formula.FormulaType?.Value != CellFormulaValues.Shared)
        {
            if (string.IsNullOrEmpty(formula.Text))
            {
                throw InvalidFormula();
            }

            return formula.Text;
        }

        if (formula.SharedIndex?.Value is not { } index
            || !masters.TryGetValue(index, out var master)
            || cell.CellReference?.Value is not { } address)
        {
            throw InvalidFormula();
        }

        var origin = SpreadsheetGridBudget.ParseAddress(master.Address);
        var target = SpreadsheetGridBudget.ParseAddress(address);
        var range = master.Range.Split(':');
        if (range.Length is < 1 or > 2)
        {
            throw InvalidFormula();
        }

        var start = SpreadsheetGridBudget.ParseAddress(range[0]);
        var end = SpreadsheetGridBudget.ParseAddress(range[^1]);
        if (target.Column < start.Column || target.Column > end.Column
            || target.Row < start.Row || target.Row > end.Row)
        {
            throw InvalidFormula();
        }

        return Translate(master.Formula, target.Column - origin.Column, target.Row - origin.Row);
    }

    private static string Translate(string formula, int columnOffset, int rowOffset)
    {
        if (columnOffset == 0 && rowOffset == 0)
        {
            return formula;
        }

        var result = new StringBuilder(formula.Length);
        for (var index = 0; index < formula.Length;)
        {
            var character = formula[index];
            if (character is '"' or '\'')
            {
                var start = index++;
                var closed = false;
                while (index < formula.Length)
                {
                    if (formula[index++] != character)
                    {
                        continue;
                    }

                    if (index < formula.Length && formula[index] == character)
                    {
                        index++;
                        continue;
                    }

                    closed = true;
                    break;
                }

                if (!closed)
                {
                    throw InvalidFormula();
                }

                result.Append(formula, start, index - start);
                continue;
            }

            if (character == '[')
            {
                var start = index++;
                var depth = 1;
                while (index < formula.Length && depth > 0)
                {
                    // Apostrophes escape brackets in structured column names.
                    if (formula[index] == '\'' && index + 1 < formula.Length)
                    {
                        index += 2;
                        continue;
                    }

                    depth += formula[index] == '[' ? 1 : formula[index] == ']' ? -1 : 0;
                    index++;
                }

                if (depth != 0)
                {
                    throw InvalidFormula();
                }

                result.Append(formula, start, index - start);
                continue;
            }

            var match = ReferenceToken().Match(formula, index);
            if (match.Success && match.Index == index
                && (index == 0 || !IsNameCharacter(formula[index - 1]))
                && (index + match.Length == formula.Length
                    || !IsNameCharacter(formula[index + match.Length])))
            {
                var end = index + match.Length;
                // LOG10(...), Table1[...] and A1!B2 use reference-looking
                // identifiers. A1:B2!C3 can also be a 3D sheet qualifier.
                if (end < formula.Length && formula[end] is '(' or '[' or '!'
                    || IsSheetRangeQualifier(formula, end))
                {
                    result.Append(match.Value);
                }
                else
                {
                    result.Append(TranslateToken(match.Value, columnOffset, rowOffset));
                }

                index = end;
                continue;
            }

            result.Append(character);
            index++;
        }

        return result.ToString();
    }

    private static bool IsSheetRangeQualifier(string formula, int index)
    {
        if (index >= formula.Length || formula[index] != ':')
        {
            return false;
        }

        for (var next = index + 1; next < formula.Length; next++)
        {
            if (formula[next] == '!')
            {
                return true;
            }

            if (!IsNameCharacter(formula[next]) && formula[next] != '$')
            {
                return false;
            }
        }

        return false;
    }

    private static string TranslateToken(string token, int columnOffset, int rowOffset)
    {
        if (token.Contains(':', StringComparison.Ordinal))
        {
            var halves = token.Split(':');
            return ShiftPart(halves[0], columnOffset, rowOffset)
                + ":" + ShiftPart(halves[1], columnOffset, rowOffset);
        }

        return ShiftPart(token, columnOffset, rowOffset);
    }

    private static string ShiftPart(string token, int columnOffset, int rowOffset)
    {
        var match = ReferenceParts().Match(token);
        var column = match.Groups["column"].Value;
        var row = match.Groups["row"].Value;
        var shiftedColumn = column;
        var shiftedRow = row;
        if (column.Length > 0)
        {
            var number = ColumnNumberOrName(column);
            if (number > 16384)
            {
                return token;
            }

            if (match.Groups["absoluteColumn"].Length == 0)
            {
                number += columnOffset;
                if (number is < 1 or > 16384)
                {
                    return "#REF!";
                }

                shiftedColumn = SpreadsheetDiffEngine.GetColumnLetter(number - 1);
            }
        }

        if (row.Length > 0)
        {
            if (!int.TryParse(row, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || number is < 1 or > 1048576)
            {
                return token;
            }

            if (match.Groups["absoluteRow"].Length == 0)
            {
                number += rowOffset;
                if (number is < 1 or > 1048576)
                {
                    return "#REF!";
                }

                shiftedRow = number.ToString(CultureInfo.InvariantCulture);
            }
        }

        return match.Groups["absoluteColumn"].Value + shiftedColumn
            + match.Groups["absoluteRow"].Value + shiftedRow;
    }

    private static int ColumnNumberOrName(string column)
    {
        var number = 0;
        foreach (var letter in column)
        {
            number = number * 26 + char.ToUpperInvariant(letter) - 'A' + 1;
        }

        return number;
    }

    private static bool IsNameCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '.' or '\\';

    private static StructuralParseException InvalidFormula() =>
        new(ExcelStrings.AdapterSharedFormulaInvalid);

    [GeneratedRegex(@"\G(?:\$?[A-Za-z]{1,3}\$?[1-9][0-9]*|\$?[A-Za-z]{1,3}:\$?[A-Za-z]{1,3}|\$?[1-9][0-9]*:\$?[1-9][0-9]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceToken();

    [GeneratedRegex(@"^(?:(?<absoluteColumn>\$?)(?<column>[A-Za-z]+))?(?<absoluteRow>\$?)(?<row>[0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceParts();
}
