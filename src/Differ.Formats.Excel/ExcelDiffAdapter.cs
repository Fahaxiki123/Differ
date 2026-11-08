using System.Globalization;
using System.Text;
using Differ.Core.Abstractions;
using Differ.Core.Diagnostics;
using Differ.Core.Models;
using Differ.Core.Security;
using Differ.Formats.Excel.Resources;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Differ.Formats.Excel;

/// <summary>
/// Open XML based extractor for .xlsx and the macro-enabled .xlsm container.
/// .xlsm is opened through the same read-only pipeline; VBA parts are simply
/// never loaded, so macros can neither run nor take part in the comparison.
/// </summary>
public sealed class ExcelDiffAdapter : IFileDiffAdapter
{
    private static readonly CompositeFormat SheetUnreadableFormat =
        CompositeFormat.Parse(ExcelStrings.AdapterSheetUnreadableFormat);
    private static readonly CompositeFormat SheetEmptyFormat =
        CompositeFormat.Parse(ExcelStrings.AdapterSheetEmptyFormat);

    public FileKind FileKind => FileKind.Excel;

    public bool CanHandle(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".xlsm", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<DiffDocument> CompareAsync(
        DiffRequest request,
        CancellationToken cancellationToken)
    {
        ExcelLogicalWorkbook oldWorkbook;
        ExcelLogicalWorkbook newWorkbook;
        using (ComparisonMetrics.Measure("excel.parse.base", "Excel"))
            oldWorkbook = await ExtractAsync(request.BaseFilePath, request.Options.CompareFormatting, cancellationToken).ConfigureAwait(false);
        using (ComparisonMetrics.Measure("excel.parse.mine", "Excel"))
            newWorkbook = await ExtractAsync(request.CurrentFilePath, request.Options.CompareFormatting, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        var comparison = SpreadsheetDiffEngine.Compare(
            oldWorkbook,
            newWorkbook,
            request.Options,
            cancellationToken,
            fileName: request.RelativePath);

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

    private static Task<ExcelLogicalWorkbook> ExtractAsync(
        string? path,
        bool compareFormatting,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !File.Exists(path)
            || new FileInfo(path).Length == 0)
        {
            return Task.FromResult(new ExcelLogicalWorkbook([], [], HasFormulas: false));
        }

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                OfficePackageGuard.Validate(path);

                using var document = SpreadsheetDocument.Open(
                    path,
                    isEditable: false,
                    new OpenSettings { AutoSave = false, MaxCharactersInPart = 64 * 1024 * 1024 });
                var workbookPart = document.WorkbookPart
                    ?? throw new InvalidDataException(ExcelStrings.AdapterWorkbookPartMissing);
                var workbook = workbookPart.Workbook
                    ?? throw new InvalidDataException(ExcelStrings.AdapterWorkbookEmpty);
                var sharedStrings = ReadSharedStrings(workbookPart);
                var styles = compareFormatting ? new ExcelStyleResolver(workbookPart) : null;
                var sheets = new List<ExcelSheet>();
                var warnings = new List<string>();
                var hasFormulas = false;

                var sheetIndex = 0;
                foreach (var sheet in workbook.Sheets?.Elements<Sheet>()
                    ?? Enumerable.Empty<Sheet>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sheetIndex++;
                    if (sheet.Id?.Value is not { Length: > 0 } relationshipId
                        || workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
                    {
                        warnings.Add(string.Format(
                            CultureInfo.CurrentCulture,
                            SheetUnreadableFormat,
                            sheet.Name?.Value ?? sheetIndex.ToString(CultureInfo.InvariantCulture)));
                        continue;
                    }

                    var extracted = ExtractSheet(
                        sheet.Name?.Value ?? $"Sheet{sheetIndex}",
                        sheetIndex,
                        worksheetPart,
                        sharedStrings,
                        styles,
                        cancellationToken);
                    hasFormulas |= extracted.Rows
                        .SelectMany(row => row.Cells.Values)
                        .Any(cell => cell.Formula is not null);
                    sheets.Add(extracted);
                }

                return new ExcelLogicalWorkbook(sheets, warnings, hasFormulas);
            },
            cancellationToken);
    }

    private static string[] ReadSharedStrings(WorkbookPart workbookPart)
    {
        return workbookPart.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>()
            .Select(item => item.InnerText)
            .ToArray()
            ?? [];
    }

    private static ExcelSheet ExtractSheet(
        string name,
        int index,
        WorksheetPart worksheetPart,
        IReadOnlyList<string> sharedStrings,
        ExcelStyleResolver? styles,
        CancellationToken cancellationToken)
    {
        var worksheet = worksheetPart.Worksheet
            ?? throw new InvalidDataException(string.Format(
                CultureInfo.CurrentCulture,
                SheetEmptyFormat,
                name));
        var comments = ReadComments(worksheetPart);
        var rows = new List<ExcelRow>();
        var sheetData = worksheet.GetFirstChild<SheetData>();
        var formulas = new SharedFormulaResolver(sheetData?.Descendants<Cell>() ?? []);
        var extractedCellCount = 0L;
        if (sheetData is not null)
        {
            foreach (var row in sheetData.Elements<Row>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cells = new Dictionary<string, ExcelCell>(StringComparer.OrdinalIgnoreCase);
                var firstAddress = row.Elements<Cell>().FirstOrDefault()?.CellReference?.Value;
                var rowIndex = row.RowIndex?.Value ?? (firstAddress is null
                    ? rows.Count == 0 ? 1U : rows[^1].RowIndex + 1
                    : (uint)SpreadsheetGridBudget.ParseAddress(firstAddress).Row);
                var nextColumn = 0;
                foreach (var cell in row.Elements<Cell>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SpreadsheetGridBudget.Validate(rows.Count + 1, cells.Count + 1, ++extractedCellCount);
                    var address = cell.CellReference?.Value
                        ?? $"{SpreadsheetDiffEngine.GetColumnLetter(nextColumn)}{rowIndex}";
                    var position = SpreadsheetGridBudget.ParseAddress(address);
                    if (position.Row != rowIndex || position.Column <= nextColumn)
                    {
                        throw new StructuralParseException(ExcelStrings.AdapterInvalidAddress);
                    }

                    nextColumn = position.Column;
                    var column = SpreadsheetDiffEngine.GetColumnName(address);
                    var rawValue = cell.CellValue?.Text ?? string.Empty;
                    var displayValue = ResolveValue(cell, rawValue, sharedStrings);
                    var formula = formulas.Resolve(cell);
                    comments.TryGetValue(address, out var comment);

                    cells[column] = new ExcelCell(
                        address,
                        column,
                        rawValue,
                        displayValue,
                        formula,
                        GetSemanticType(cell, rawValue),
                        cell.StyleIndex?.Value,
                        comment,
                        styles?.Resolve(cell.StyleIndex?.Value),
                        DefaultStyleSignature: styles?.Resolve(null));
                }

                rows.Add(new ExcelRow(
                    rowIndex,
                    row.Hidden?.Value ?? false,
                    cells,
                    rows.Count < 100 ? SpreadsheetDiffEngine.BuildRowSignature(cells.Values) : string.Empty));
            }
        }

        // Comments are stored in a separate part and can address an otherwise
        // omitted blank cell. They must survive the sparse sheet extraction.
        var rowsByIndex = new Dictionary<uint, ExcelRow>();
        foreach (var row in rows)
        {
            if (!rowsByIndex.TryAdd(row.RowIndex, row))
            {
                throw new StructuralParseException(ExcelStrings.AdapterInvalidAddress);
            }
        }

        foreach (var (address, comment) in comments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = SpreadsheetGridBudget.ParseAddress(address);
            rowsByIndex.TryGetValue((uint)position.Row, out var row);
            var column = SpreadsheetDiffEngine.GetColumnName(address);
            if (row?.Cells.ContainsKey(column) == true)
            {
                continue;
            }

            var cells = row is null
                ? new Dictionary<string, ExcelCell>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ExcelCell>(row.Cells, StringComparer.OrdinalIgnoreCase);
            cells[column] = new ExcelCell(address, column, string.Empty, string.Empty,
                null, "blank", null, comment, styles?.Resolve(null),
                DefaultStyleSignature: styles?.Resolve(null));
            rowsByIndex[(uint)position.Row] = new ExcelRow((uint)position.Row, row?.Hidden ?? false,
                cells, position.Row <= 100 ? SpreadsheetDiffEngine.BuildRowSignature(cells.Values) : string.Empty);
            SpreadsheetGridBudget.Validate(rowsByIndex.Count, cells.Count, ++extractedCellCount);
        }

        rows = rowsByIndex.Values.OrderBy(row => row.RowIndex).ToList();

        var mergedRanges = worksheet
            .Elements<MergeCells>()
            .SelectMany(mergeCells => mergeCells.Elements<MergeCell>())
            .Select(mergeCell => mergeCell.Reference?.Value)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Cast<string>()
            .ToArray();

        var hiddenColumns = worksheet
            .Elements<Columns>()
            .SelectMany(columns => columns.Elements<Column>())
            .Where(column => column.Hidden?.Value ?? false)
            .Select(column => $"{column.Min?.Value ?? 0}:{column.Max?.Value ?? 0}")
            .ToArray();

        return new ExcelSheet(
            name,
            index,
            SpreadsheetGridBudget.FillInternalRows(rows),
            mergedRanges,
            hiddenColumns,
            SpreadsheetDiffEngine.BuildSheetSignature(rows));
    }

    private static Dictionary<string, string> ReadComments(
        WorksheetPart worksheetPart)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var commentPart = worksheetPart.WorksheetCommentsPart;
        if (commentPart?.Comments?.CommentList is null)
        {
            return result;
        }

        foreach (var comment in commentPart.Comments.CommentList.Elements<Comment>())
        {
            if (comment.Reference?.Value is { Length: > 0 } reference)
            {
                result[reference] = comment.CommentText?.InnerText ?? string.Empty;
            }
        }

        return result;
    }

    private static string GetSemanticType(Cell cell, string rawValue)
    {
        var token = cell.DataType?.Value is IEnumValue dataType ? dataType.Value : "n";
        return token is "s" or "str" or "inlineStr" ? "s"
            : token == "n" && rawValue.Length == 0 && cell.CellFormula is null ? "blank"
            : token;
    }

    private static string ResolveValue(
        Cell cell,
        string rawValue,
        IReadOnlyList<string> sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.SharedString)
        {
            if (int.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out var sharedStringIndex)
                && sharedStringIndex >= 0 && sharedStringIndex < sharedStrings.Count)
            {
                return sharedStrings[sharedStringIndex];
            }

            throw new StructuralParseException(ExcelStrings.AdapterSharedStringInvalid);
        }

        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        if (cell.DataType?.Value == CellValues.Boolean)
        {
            return rawValue == "1" ? "TRUE" : "FALSE";
        }

        if ((cell.DataType is null || cell.DataType.Value == CellValues.Number)
            && double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric.ToString("R", CultureInfo.InvariantCulture);
        }

        return rawValue;
    }
}
