using Differ.Core.Abstractions;
using Differ.Core.Models;
using Differ.Formats.Excel;
using Differ.Formats.Text;

namespace Differ.Formats.Csv;

/// <summary>
/// CSV table adapter. Rows are parsed per RFC 4180 and mapped onto the same
/// parser-agnostic workbook model the spreadsheet adapters use, so alignment
/// semantics, the grid view and worksheet tabs stay identical to .xlsx.
/// Content detected as binary falls back to the byte-level adapter.
/// </summary>
public sealed class CsvDiffAdapter(ITextFileReader reader) : IFileDiffAdapter
{
    private const string SheetName = "CSV";

    public FileKind FileKind => FileKind.Excel;

    public bool CanHandle(string path)
    {
        return string.Equals(
            Path.GetExtension(path),
            ".csv",
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task<DiffDocument> CompareAsync(
        DiffRequest request,
        CancellationToken cancellationToken)
    {
        var oldContent = await reader.ReadWithEncodingAsync(
                request.BaseFilePath, request.Options.TextEncodingName, cancellationToken)
            .ConfigureAwait(false);
        var newContent = await reader.ReadWithEncodingAsync(
                request.CurrentFilePath, request.Options.TextEncodingName, cancellationToken)
            .ConfigureAwait(false);

        if (oldContent.IsBinary || newContent.IsBinary)
        {
            // Returning a Binary document hands the request to the byte-level
            // fallback through the coordinator re-route.
            return new DiffDocument
            {
                RelativePath = request.RelativePath,
                FileKind = FileKind.Binary,
                BaseRevision = request.BaseRevision,
                Nodes = [],
                Summary = new DiffSummary(0, 0, 0, 0, 0, 0, 0),
                Warnings = []
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        var oldWorkbook = BuildWorkbook(CsvParser.Parse(oldContent.Text, cancellationToken), oldContent.Warnings);
        var newWorkbook = BuildWorkbook(CsvParser.Parse(newContent.Text, cancellationToken), newContent.Warnings);
        var comparison = SpreadsheetDiffEngine.Compare(
            oldWorkbook,
            newWorkbook,
            request.Options,
            cancellationToken,
            logicalMapping: false);

        return new DiffDocument
        {
            RelativePath = request.RelativePath,
            FileKind = FileKind.Excel,
            BaseRevision = request.BaseRevision,
            Nodes = comparison.Nodes,
            Summary = DiffSummary.FromNodes(comparison.Nodes),
            Sections = comparison.Sections,
            TabularSections = comparison.TabularSections,
            Warnings = comparison.Warnings
        };
    }

    private static ExcelLogicalWorkbook BuildWorkbook(
        List<string[]> rows,
        IReadOnlyList<string> warnings)
    {
        var sheetRows = new List<ExcelRow>(rows.Count);
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var fields = rows[rowIndex];
            var cells = new Dictionary<string, ExcelCell>(StringComparer.OrdinalIgnoreCase);
            for (var columnIndex = 0; columnIndex < fields.Length; columnIndex++)
            {
                var value = fields[columnIndex];
                var column = SpreadsheetDiffEngine.GetColumnLetter(columnIndex);
                var address = $"{column}{rowIndex + 1}";
                cells[column] = new ExcelCell(
                    address,
                    column,
                    value,
                    value,
                    Formula: null,
                    DataType: "s",
                    StyleIndex: null,
                    Comment: null,
                    PreserveEmpty: true);
            }

            sheetRows.Add(new ExcelRow(
                (uint)(rowIndex + 1),
                Hidden: false,
                cells,
                $"{fields.Length}:{SpreadsheetDiffEngine.BuildRowSignature(cells.Values)}"));
        }

        var sheet = new ExcelSheet(
            SheetName,
            Index: 1,
            sheetRows,
            MergedRanges: [],
            HiddenColumns: [],
            SpreadsheetDiffEngine.BuildSheetSignature(sheetRows));
        return new ExcelLogicalWorkbook([sheet], warnings, HasFormulas: false);
    }
}
