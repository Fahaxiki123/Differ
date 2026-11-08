namespace Differ.Formats.Excel;

/// <summary>
/// Parser-agnostic workbook model shared by the Open XML (.xlsx/.xlsm) and
/// NPOI HSSF (.xls) extractors so both formats run through one comparison
/// engine and one grid presentation.
/// </summary>
internal sealed record ExcelLogicalWorkbook(
    IReadOnlyList<ExcelSheet> Sheets,
    IReadOnlyList<string> Warnings,
    bool HasFormulas);

internal sealed record ExcelSheet(
    string Name,
    int Index,
    IReadOnlyList<ExcelRow> Rows,
    IReadOnlyList<string> MergedRanges,
    IReadOnlyList<string> HiddenColumns,
    string Signature);

internal sealed record ExcelRow(
    uint RowIndex,
    bool Hidden,
    IReadOnlyDictionary<string, ExcelCell> Cells,
    string Signature);

internal sealed record ExcelCell(
    string Address,
    string Column,
    string RawValue,
    string DisplayValue,
    string? Formula,
    string DataType,
    uint? StyleIndex,
    string? Comment,
    string? StyleSignature = null,
    bool PreserveEmpty = false,
    string? DefaultStyleSignature = null);

internal sealed record SheetPair(
    ExcelSheet? OldSheet,
    ExcelSheet? NewSheet,
    bool IsRename);
