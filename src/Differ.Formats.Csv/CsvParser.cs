using System.Text;
using Differ.Formats.Csv.Resources;
using Differ.Formats.Excel;

namespace Differ.Formats.Csv;

/// <summary>RFC 4180 fields, including empty fields and records.</summary>
internal static class CsvParser
{
    public static List<string[]> Parse(string text, CancellationToken cancellationToken = default)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var afterQuote = false;
        var rowHasContent = false;
        var maximumColumns = 0;

        for (var index = 0; index < text.Length; index++)
        {
            if ((index & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var character = text[index];
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                        afterQuote = true;
                    }
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            if (afterQuote && character is not (',' or '\r' or '\n'))
            {
                throw new InvalidDataException(CsvStrings.InvalidQuotes);
            }

            switch (character)
            {
                case '"':
                    if (field.Length != 0)
                    {
                        throw new InvalidDataException(CsvStrings.InvalidQuotes);
                    }

                    inQuotes = true;
                    rowHasContent = true;
                    break;
                case ',':
                    EndField();
                    rowHasContent = true;
                    break;
                case '\r':
                case '\n':
                    if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index++;
                    }

                    EndField();
                    EndRow();
                    rowHasContent = false;
                    break;
                default:
                    field.Append(character);
                    rowHasContent = true;
                    break;
            }
        }

        if (inQuotes)
        {
            throw new InvalidDataException(CsvStrings.InvalidQuotes);
        }

        if (rowHasContent || field.Length > 0 || fields.Count > 0)
        {
            EndField();
            EndRow();
        }

        return rows;

        void EndField()
        {
            SpreadsheetGridBudget.Validate(rows.Count + 1, fields.Count + 1, 0);
            fields.Add(field.ToString());
            field.Clear();
            afterQuote = false;
        }

        void EndRow()
        {
            maximumColumns = Math.Max(maximumColumns, fields.Count);
            SpreadsheetGridBudget.Validate(rows.Count + 1, maximumColumns,
                (long)(rows.Count + 1) * maximumColumns);
            rows.Add(fields.ToArray());
            fields.Clear();
        }
    }
}
