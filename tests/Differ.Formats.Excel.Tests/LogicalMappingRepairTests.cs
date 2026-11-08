using System.Text.Json;
using Differ.Core.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SText = DocumentFormat.OpenXml.Spreadsheet.Text;

namespace Differ.Formats.Excel.Tests;

public sealed class LogicalMappingRepairTests
{
    [Fact]
    public async Task E01InsertColumnProducesOneStructuralEventAndPreservesOriginalCellsAsync()
    {
        using var files = new Files();
        files.Write("old", [["id", "name", "icon"], ["1", "alpha", "a.png"], ["2", "beta", "b.png"]]);
        files.Write("new", [["id", "new_field", "name", "icon"], ["1", "x", "alpha", "a.png"], ["2", "y", "beta", "b.png"]]);
        var result = await files.CompareAsync();
        Assert.Equal(1, result.Summary.Added);
        Assert.Equal(0, result.Summary.Modified);
        Assert.Equal(0, result.Summary.Moved);
        var map = result.TabularSections[0].ColumnMatches.Single(column => column.FieldName == "icon");
        Assert.Equal("C", map.OldColumn); Assert.Equal("D", map.NewColumn);
        var cell = result.TabularSections[0].Rows[1].Cells.Single(cell => cell.ColumnKey == map.LogicalId);
        Assert.Equal("C2", cell.OldAddress); Assert.Equal("D2", cell.NewAddress); Assert.Equal(DiffKind.Unchanged, cell.Kind);
    }

    [Fact]
    public async Task E02DeleteColumnProducesOneStructuralEventAsync()
    {
        using var files = new Files();
        files.Write("old", [["id", "obsolete", "name"], ["1", "x", "alpha"]]);
        files.Write("new", [["id", "name"], ["1", "alpha"]]);
        var result = await files.CompareAsync();
        Assert.Equal(1, result.Summary.Deleted); Assert.Equal(0, result.Summary.Modified);
        Assert.Equal("C", result.TabularSections[0].ColumnMatches.Single(column => column.NewColumn == "B").OldColumn);
    }

    [Fact]
    public async Task E03DuplicateFactionIdUsesDistinctSamplesAndDoesNotLoseColumnsAsync()
    {
        using var files = new Files();
        string[] header = ["id", "name", "field_c", "field_d", "field_e", "field_f", "field_g", "faction_id", "icon", "portrait", "animation_name"];
        string[] row = ["1", "name", "c", "d", "e", "f", "g", "original", "i.png", "p.png", "walk"];
        files.Write("old", [header, row]);
        files.Write("new", [header.Take(8).Concat(["faction_id"]).Concat(header.Skip(8)).ToArray(),
            row.Take(8).Concat(["added"]).Concat(row.Skip(8)).ToArray()]);
        var result = await files.CompareAsync();
        Assert.Equal(1, result.Summary.Changed);
        var columns = result.TabularSections[0].ColumnMatches;
        Assert.Equal(12, columns.Count);
        Assert.Equal("H", columns.Single(column => column.OldColumn == "H").NewColumn);
        Assert.Null(columns.Single(column => column.NewColumn == "I").OldColumn);
        Assert.Equal("J", columns.Single(column => column.OldColumn == "I").NewColumn);
        Assert.Equal("K", columns.Single(column => column.OldColumn == "J").NewColumn);
    }

    [Fact]
    public async Task E04IdenticalDuplicateColumnsRequireManualConfirmationAndRulesRoundTripAsync()
    {
        using var files = new Files();
        files.Write("old", [["id", "faction_id", "icon"], ["1", "same", "i.png"]]);
        files.Write("new", [["id", "faction_id", "faction_id", "icon"], ["1", "same", "same", "i.png"]]);
        var ambiguous = await files.CompareAsync();
        Assert.Contains(ambiguous.Warnings, warning => warning.Contains("歧义", StringComparison.Ordinal));
        Assert.Contains(ambiguous.TabularSections[0].ColumnMatches, column => column.Confidence == MappingConfidence.Ambiguous);
        var rule = DefaultRule() with { ColumnOverrides = [new("B", "B"), new(null, "C")] };
        var saved = JsonSerializer.Deserialize<SpreadsheetRule>(JsonSerializer.Serialize(rule))!;
        var confirmed = await files.CompareAsync(saved);
        Assert.DoesNotContain(confirmed.Warnings, warning => warning.Contains("歧义", StringComparison.Ordinal));
        Assert.Equal(1, confirmed.Summary.Changed);
        Assert.Equal(MappingConfidence.Confirmed, confirmed.TabularSections[0].ColumnMatches.Single(column => column.OldColumn == "B").Confidence);
    }

    [Fact]
    public async Task E05MultiHeaderRowsAreNotKeysAndInsertedRecordKeepsCurrentPositionAsync()
    {
        using var files = new Files();
        string[][] headers = [["title"], [""], ["编号", "名称"], ["id", "name"], ["int", "string"], ["required", ""]];
        files.Write("old", headers.Concat([["1", "alpha"], ["2", "beta"]]).ToArray());
        files.Write("new", headers.Concat([["1", "alpha"], ["3", "inserted"], ["2", "beta"]]).ToArray());
        var result = await files.CompareAsync(DefaultRule() with { HeaderRow = 4, DataStartRow = 7, AuxiliaryHeaderRows = [3, 5, 6], KeyColumns = ["A"] });
        Assert.Empty(result.Warnings);
        Assert.Equal(1, result.Summary.Added); Assert.Equal(0, result.Summary.Moved); Assert.Equal(0, result.Summary.Modified);
        var rows = result.TabularSections[0].Rows;
        Assert.Null(rows[7].OldIndex); Assert.Equal(8, rows[7].NewIndex);
        Assert.Equal(8, rows[8].OldIndex); Assert.Equal(9, rows[8].NewIndex);
    }

    [Fact]
    public async Task E06EmptyAndDuplicateKeysOnlyDegradeTheirOwnRecordsAsync()
    {
        using var files = new Files();
        files.Write("old", [["id", "name"], ["0012", "string"], ["#12", "number"], ["good", "before"], ["", "empty"], ["dup", "one"], ["dup", "two"]]);
        files.Write("new", [["id", "name"], ["#12", "number"], ["0012", "string"], ["", "empty"], ["good", "after"], ["dup", "two"], ["dup", "one"]]);
        var result = await files.CompareAsync(DefaultRule() with { KeyColumns = ["A"] });
        Assert.Single(result.Warnings);
        Assert.Contains("仅异常组", result.Warnings[0], StringComparison.Ordinal);
        var rows = result.TabularSections[0].Rows;
        Assert.Equal(3, rows.Single(row => row.NewIndex == 2).OldIndex);
        Assert.Equal(2, rows.Single(row => row.NewIndex == 3).OldIndex);
        Assert.Equal(4, rows.Single(row => row.NewIndex == 5).OldIndex);
        Assert.Equal(1, result.Summary.Modified);
        Assert.Equal(7, rows.Count);
    }

    [Fact]
    public async Task E07InsertRowAndColumnAndModifyOneCellAreThreeIndependentEventsAsync()
    {
        using var files = new Files();
        files.Write("old", [["id", "name", "icon"], ["1", "alpha", "a"], ["2", "beta", "b"]]);
        files.Write("new", [["id", "new_field", "name", "icon"], ["1", "x", "ALPHA", "a"], ["3", "y", "new", "c"], ["2", "z", "beta", "b"]]);
        var result = await files.CompareAsync(DefaultRule() with { KeyColumns = ["id"] });
        Assert.Equal(2, result.Summary.Added); Assert.Equal(1, result.Summary.Modified); Assert.Equal(3, result.Summary.Changed);
    }

    [Fact]
    public async Task E08RowAndColumnReordersHaveStructuralEventsWithoutContentChangesAsync()
    {
        using var files = new Files();
        files.Write("old", [["id", "name", "icon"], ["1", "alpha", "a"], ["2", "beta", "b"]]);
        files.Write("new", [["icon", "id", "name"], ["b", "2", "beta"], ["a", "1", "alpha"]]);
        var result = await files.CompareAsync(DefaultRule() with { KeyColumns = ["id"] });
        Assert.Equal(0, result.Summary.Modified); Assert.Equal(2, result.Summary.Moved);
        Assert.Equal(3, result.TabularSections[0].Rows[1].OldIndex);
        Assert.All(result.TabularSections[0].Rows.SelectMany(row => row.Cells), cell => Assert.Equal(DiffKind.Unchanged, cell.Kind));
    }

    [Theory]
    [InlineData("A2", "A3", true)]
    [InlineData("$A$2", "$A$3", true)]
    [InlineData("SUM(A2:B2)", "SUM(A3:B3)", true)]
    [InlineData("A2+1", "A3+2", false)]
    [InlineData("Other!A2", "Other!A3", false)]
    [InlineData("NamedRange+A2", "NamedRange+A3", false)]
    public async Task E09OnlyProvenMappedFormulaReferencesAreStructuralAsync(string before, string after, bool equivalent)
    {
        using var files = new Files();
        files.Write("old", [["id", "name", "value"], ["1", "alpha", "#7"], ["2", "beta", "=" + before]]);
        files.Write("new", [["id", "name", "value"], ["0", "inserted", "#9"], ["1", "alpha", "#7"], ["2", "beta", "=" + after]]);
        var result = await files.CompareAsync(DefaultRule() with { KeyColumns = ["A"] });
        Assert.Equal(equivalent ? 0 : 1, result.Summary.Modified);
        Assert.Equal(equivalent, result.TabularSections[0].Rows.Single(row => row.NewIndex == 4).Cells[2].IsStructuralReferenceAdjustment);
        Assert.Contains(result.Nodes, node => equivalent
            ? node.Path.Contains("公式结构引用调整", StringComparison.Ordinal) : node.Kind == DiffKind.Modified);
    }

    [Fact]
    public async Task E10MultipleSheetsAreIndependentAndCancellationIsObservedAsync()
    {
        using var files = new Files();
        files.Write("old", [["id", "name"], ["1", "alpha"]], 3);
        files.Write("new", [["id", "extra", "name"], ["1", "x", "alpha"]], 3);
        var result = await files.CompareAsync();
        Assert.Equal(3, result.TabularSections.Count); Assert.Equal(3, result.Summary.Added);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.CompareAsync(token: cancellation.Token));
    }

    [Fact]
    public async Task E09RangeWithInsertedInteriorRecordCannotBeDeclaredEquivalentAsync()
    {
        using var files = new Files();
        files.Write("old", [["id", "name", "value"], ["1", "alpha", "#1"], ["2", "beta", "#2"], ["3", "total", "=SUM(C2:C3)"]]);
        files.Write("new", [["id", "name", "value"], ["1", "alpha", "#1"], ["4", "inserted", "#9"], ["2", "beta", "#2"], ["3", "total", "=SUM(C2:C4)"]]);
        var result = await files.CompareAsync(DefaultRule() with { KeyColumns = ["A"] });
        Assert.Equal(1, result.Summary.Modified);
        Assert.False(result.TabularSections[0].Rows.Single(row => row.NewIndex == 5).Cells[2].IsStructuralReferenceAdjustment);
        Assert.Contains(result.Warnings, warning => warning.Contains("不能证明等价", StringComparison.Ordinal));
    }

    [Fact]
    public async Task E10LargeRepeatedRowsRetainEveryOccurrenceWithoutInventingMovementAsync()
    {
        using var files = new Files();
        string[] repeated = ["duplicate", "same", "value"];
        var before = Enumerable.Repeat(repeated, 5000).Prepend(["id", "name", "value"]).ToArray();
        var after = before.Select(row => row.ToArray()).ToArray();
        after[^1][2] = "changed";
        files.Write("old", before); files.Write("new", after);
        var result = await files.CompareAsync();
        Assert.Equal(1, result.Summary.Modified);
        Assert.Equal(0, result.Summary.Moved);
        Assert.Equal(5001, result.TabularSections[0].Rows.Count);
        Assert.All(result.TabularSections[0].Rows, row => Assert.Equal(row.OldIndex, row.NewIndex));
    }

    [Fact]
    public async Task E09ScientificConstantsAreNeverTreatedAsMovedCellReferencesAsync()
    {
        using var files = new Files();
        files.Write("old", [["id", "name", "value", "field_d", "field_e"], ["1", "alpha", "=1E3", "d", "e"], ["2", "beta", "#7", "d", "e"]]);
        files.Write("new", [["id", "name", "value", "field_d", "field_e"], ["0", "inserted", "#9", "d", "e"], ["1", "alpha", "=1E4", "d", "e"], ["2", "beta", "#7", "d", "e"]]);
        var result = await files.CompareAsync(DefaultRule() with { KeyColumns = ["A"] });
        Assert.Equal(1, result.Summary.Modified);
        Assert.False(result.TabularSections[0].Rows.Single(row => row.NewIndex == 3).Cells[2].IsStructuralReferenceAdjustment);
    }

    [Fact]
    public async Task FormattingOnlyChangesHaveSeparateFlagsAndVisibleDetailsAsync()
    {
        using var files = new Files();
        files.Write("old", [["id", "name"], ["1", "same"]]);
        files.Write("new", [["id", "name"], ["1", "same"]]);
        files.SetStyle("old", "B2", 0);
        files.SetStyle("new", "B2", 1);
        Assert.Equal(0, (await files.CompareAsync()).Summary.Changed);
        var result = await files.CompareAsync(compareFormatting: true);
        Assert.Equal(1, result.Summary.Modified);
        var cell = result.TabularSections[0].Rows[1].Cells[1];
        Assert.Equal(SpreadsheetCellChange.Formatting, cell.ChangeFlags);
        Assert.Contains("基础格式", cell.NewDetails!, StringComparison.Ordinal);
        Assert.Equal("Formatting", result.Nodes.Single(node => node.Kind == DiffKind.Modified).Metadata["change.kind"]);
    }

    private static SpreadsheetRule DefaultRule() => new() { HeaderRow = 1, DataStartRow = 2 };

    private sealed class Files : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Differ.Tests", Guid.NewGuid().ToString("N"));
        public Files() => Directory.CreateDirectory(_root);
        public void Dispose() => Directory.Delete(_root, true);
        public void Write(string side, string[][] values, int sheetCount = 1)
        {
            using var document = SpreadsheetDocument.Create(Path.Combine(_root, side + ".xlsx"), SpreadsheetDocumentType.Workbook);
            var workbook = document.AddWorkbookPart(); workbook.Workbook = new Workbook();
            var sheets = workbook.Workbook.AppendChild(new Sheets());
            for (var sheetIndex = 0; sheetIndex < sheetCount; sheetIndex++)
            {
                var part = workbook.AddNewPart<WorksheetPart>(); var data = new SheetData(); part.Worksheet = new Worksheet(data);
                for (var rowIndex = 0; rowIndex < values.Length; rowIndex++)
                {
                    var row = new Row { RowIndex = (uint)rowIndex + 1 };
                    for (var index = 0; index < values[rowIndex].Length; index++)
                    {
                        var text = values[rowIndex][index];
                        var cell = new Cell { CellReference = $"{(char)('A' + index)}{rowIndex + 1}" };
                        if (text.StartsWith('#')) cell.CellValue = new CellValue(text[1..]);
                        else if (text.StartsWith('=')) { cell.CellFormula = new CellFormula(text[1..]); cell.CellValue = new CellValue("7"); }
                        else { cell.DataType = CellValues.InlineString; cell.InlineString = new InlineString(new SText(text)); }
                        row.Append(cell);
                    }
                    data.Append(row);
                }
                sheets.Append(new Sheet { Id = workbook.GetIdOfPart(part), Name = "Sheet" + (sheetIndex + 1), SheetId = (uint)sheetIndex + 1 });
            }
        }
        public void SetStyle(string side, string address, uint style)
        {
            using var document = SpreadsheetDocument.Open(Path.Combine(_root, side + ".xlsx"), true);
            var styles = document.WorkbookPart!.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new Stylesheet(new CellFormats(new CellFormat(),
                new CellFormat(new Alignment { Horizontal = HorizontalAlignmentValues.Center })));
            styles.Stylesheet.Save();
            var worksheet = document.WorkbookPart!.WorksheetParts.Single().Worksheet!;
            worksheet.Descendants<Cell>().Single(cell => cell.CellReference?.Value == address).StyleIndex = style;
            worksheet.Save();
        }
        public Task<DiffDocument> CompareAsync(SpreadsheetRule? rule = null, bool compareFormatting = false, CancellationToken token = default) =>
            new ExcelDiffAdapter().CompareAsync(new DiffRequest
            {
                RelativePath = "mapping.xlsx",
                BaseFilePath = Path.Combine(_root, "old.xlsx"),
                CurrentFilePath = Path.Combine(_root, "new.xlsx"),
                Options = new DiffOptions { SpreadsheetRules = [rule ?? DefaultRule()], CompareFormatting = compareFormatting }
            }, token);
    }
}
