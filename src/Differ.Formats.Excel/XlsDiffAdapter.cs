using System.Globalization;
using Differ.Core.Abstractions;
using Differ.Core.Diagnostics;
using Differ.Core.Models;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;

namespace Differ.Formats.Excel;

/// <summary>
/// NPOI HSSF based extractor for the legacy .xls binary format. It feeds the
/// same parser-agnostic workbook model as the Open XML adapter, so row
/// alignment, tabular sections and diff semantics stay identical.
/// </summary>
public sealed class XlsDiffAdapter : IFileDiffAdapter
{
    public FileKind FileKind => FileKind.Excel;

    public bool CanHandle(string path)
    {
        return string.Equals(
            Path.GetExtension(path),
            ".xls",
            StringComparison.OrdinalIgnoreCase);
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
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var workbook = new HSSFWorkbook(stream);
                var sheets = new List<ExcelSheet>(workbook.NumberOfSheets);
                var hasFormulas = false;

                for (var sheetIndex = 0; sheetIndex < workbook.NumberOfSheets; sheetIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var extracted = ExtractSheet(
                        workbook.GetSheetName(sheetIndex),
                        sheetIndex + 1,
                        workbook.GetSheetAt(sheetIndex),
                        workbook,
                        compareFormatting,
                        cancellationToken);
                    hasFormulas |= extracted.Rows
                        .SelectMany(row => row.Cells.Values)
                        .Any(cell => cell.Formula is not null);
                    sheets.Add(extracted);
                }

                return new ExcelLogicalWorkbook(sheets, [], hasFormulas);
            },
            cancellationToken);
    }

    private static ExcelSheet ExtractSheet(
        string name,
        int index,
        ISheet sheet,
        HSSFWorkbook workbook,
        bool compareFormatting,
        CancellationToken cancellationToken)
    {
        var rows = new List<ExcelRow>();
        var styles = new Dictionary<short, string>();
        var defaultStyle = compareFormatting
            ? ExcelStyleResolver.ResolveLegacy(workbook.GetCellStyleAt(0), workbook) : null;
        long cellCount = 0;
        for (var rowNumber = sheet.FirstRowNum; rowNumber <= sheet.LastRowNum; rowNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = sheet.GetRow(rowNumber);
            if (row is null)
            {
                continue;
            }

            var cells = new Dictionary<string, ExcelCell>(StringComparer.OrdinalIgnoreCase);
            for (var cellNumber = Math.Max((short)0, row.FirstCellNum); cellNumber < row.LastCellNum; cellNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cell = row.GetCell(cellNumber);
                if (cell is null)
                {
                    continue;
                }

                var column = SpreadsheetDiffEngine.GetColumnLetter(cellNumber);
                SpreadsheetGridBudget.Validate(rows.Count + 1, cells.Count + 1, ++cellCount);
                var address = $"{column}{rowNumber + 1}";
                var extracted = ExtractCell(cell);
                string? styleSignature = null;
                if (compareFormatting && !styles.TryGetValue(cell.CellStyle.Index, out styleSignature))
                {
                    styleSignature = ExcelStyleResolver.ResolveLegacy(cell.CellStyle, workbook);
                    styles.Add(cell.CellStyle.Index, styleSignature);
                }

                cells[column] = new ExcelCell(
                    address,
                    column,
                    extracted.RawValue,
                    extracted.DisplayValue,
                    extracted.Formula,
                    extracted.DataType,
                    (uint)cell.CellStyle.Index,
                    cell.CellComment?.String?.String,
                    styleSignature,
                    DefaultStyleSignature: defaultStyle);
            }

            rows.Add(new ExcelRow(
                (uint)(rowNumber + 1),
                row.ZeroHeight,
                cells,
                rows.Count < 100 ? SpreadsheetDiffEngine.BuildRowSignature(cells.Values) : string.Empty));
        }

        var mergedRanges = new string[sheet.NumMergedRegions];
        for (var regionIndex = 0; regionIndex < sheet.NumMergedRegions; regionIndex++)
        {
            var region = sheet.GetMergedRegion(regionIndex);
            mergedRanges[regionIndex] =
                $"{SpreadsheetDiffEngine.GetColumnLetter(region.FirstColumn)}{region.FirstRow + 1}"
                + $":{SpreadsheetDiffEngine.GetColumnLetter(region.LastColumn)}{region.LastRow + 1}";
        }

        var hiddenColumns = new List<string>();
        var rangeStart = -1;
        // HSSF has exactly 256 columns. Hidden empty columns beyond the last
        // populated cell still represent workbook structure.
        for (var columnNumber = 0; columnNumber < 256; columnNumber++)
        {
            var hidden = sheet.IsColumnHidden(columnNumber);
            if (hidden && rangeStart < 0)
            {
                rangeStart = columnNumber;
            }

            if ((!hidden || columnNumber == 255) && rangeStart >= 0)
            {
                var rangeEnd = hidden ? columnNumber : columnNumber - 1;
                hiddenColumns.Add($"{rangeStart + 1}:{rangeEnd + 1}");
                rangeStart = -1;
            }
        }

        return new ExcelSheet(
            name,
            index,
            SpreadsheetGridBudget.FillInternalRows(rows),
            mergedRanges,
            hiddenColumns,
            SpreadsheetDiffEngine.BuildSheetSignature(rows));
    }

    private static (string RawValue, string DisplayValue, string? Formula, string DataType)
        ExtractCell(ICell cell)
    {
        if (cell.CellType == CellType.Formula)
        {
            var (rawValue, dataType) = ExtractCachedValue(cell);
            return (rawValue, rawValue, cell.CellFormula, dataType);
        }

        var (value, token) = ExtractCachedValue(cell);
        return (value, value, null, token);
    }

    private static (string Value, string DataType) ExtractCachedValue(ICell cell)
    {
        var resultType = cell.CellType == CellType.Formula
            ? cell.CachedFormulaResultType
            : cell.CellType;
        return resultType switch
        {
            CellType.String => (cell.StringCellValue, "s"),
            CellType.Numeric => (
                cell.NumericCellValue.ToString("R", CultureInfo.InvariantCulture),
                "n"),
            CellType.Boolean => (cell.BooleanCellValue ? "TRUE" : "FALSE", "b"),
            CellType.Error => (
                cell.ErrorCellValue.ToString(CultureInfo.InvariantCulture),
                "e"),
            _ => (string.Empty, "blank")
        };
    }
}
