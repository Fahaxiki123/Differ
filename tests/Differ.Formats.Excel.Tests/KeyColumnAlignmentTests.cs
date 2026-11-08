using System.Text;
using System.Text.Json;
using Differ.Core.Abstractions;
using Differ.Core.Models;
using Differ.Formats.Csv;
using Differ.Formats.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using NPOI.HSSF.UserModel;
using SpreadsheetText = DocumentFormat.OpenXml.Spreadsheet.Text;

namespace Differ.Formats.Excel.Tests;

public sealed class KeyColumnAlignmentTests
{
    [Theory]
    [InlineData(".xlsx")]
    [InlineData(".xlsm")]
    [InlineData(".xls")]
    [InlineData(".csv")]
    public async Task ReorderedKeysKeepPhysicalRowNumbersAndReportOrderSeparatelyAsync(string extension)
    {
        using var files = new KeyFixtures(extension);
        files.Create("old", [["alpha", "first"], ["beta", "second"], ["gamma", "third"]]);
        files.Create("new", [["gamma", "third"], ["alpha", "first"], ["beta", "second"]]);

        var result = await files.CompareAsync(["A"]);

        Assert.Equal(extension == ".csv" ? 0 : 1, result.Summary.Changed);
        Assert.Empty(result.Warnings);
        var grid = Assert.Single(result.TabularSections);
        Assert.Equal(extension == ".csv" ? [1, 2, 3] : [3, 1, 2], grid.Rows.Select(row => row.OldIndex));
        Assert.Equal(extension == ".csv" ? [2, 3, 1] : [1, 2, 3], grid.Rows.Select(row => row.NewIndex));
        Assert.Equal(extension == ".csv" ? "A1" : "A3", grid.Rows[0].Cells[0].OldAddress);
        Assert.Equal(extension == ".csv" ? "A2" : "A1", grid.Rows[0].Cells[0].NewAddress);
        Assert.All(grid.Rows.SelectMany(row => row.Cells), cell => Assert.Equal(DiffKind.Unchanged, cell.Kind));
    }

    [Fact]
    public async Task MatchedKeyDetectsEntirelyRewrittenPayloadWithoutInventingAddDeleteAsync()
    {
        using var files = new KeyFixtures(".xlsx");
        files.Create("old", [["1001", "old title", "old description"], ["1002", "same", "same"]]);
        files.Create("new", [["1002", "same", "same"], ["1001", "new heading", "new summary"]]);

        var result = await files.CompareAsync(["A"]);

        Assert.Equal(2, result.Summary.Modified);
        Assert.Equal(0, result.Summary.Added);
        Assert.Equal(0, result.Summary.Deleted);
        Assert.Equal(1, result.Summary.Moved);
        var changed = result.TabularSections[0].Rows.Single(row => row.OldIndex == 1);
        Assert.Equal(1, changed.OldIndex);
        Assert.Equal(2, changed.NewIndex);
        Assert.Equal("old description", changed.Cells[2].OldValue);
        Assert.Equal("new summary", changed.Cells[2].NewValue);
    }

    [Theory]
    [InlineData(".xlsx")]
    [InlineData(".csv")]
    public async Task AddedAndDeletedKeysRetainEveryRowExactlyOnceAsync(string extension)
    {
        using var files = new KeyFixtures(extension);
        files.Create("old", [["A", "alpha"], ["B", "beta"], ["C", "gamma"]]);
        files.Create("new", [["X", "added first"], ["C", "gamma"], ["A", "alpha"], ["Y", "added last"]]);

        var result = await files.CompareAsync(["A"]);

        var rows = result.TabularSections[0].Rows;
        Assert.Equal(extension == ".csv" ? [1, 2, 3, null, null] : [null, 2, 3, 1, null], rows.Select(row => row.OldIndex));
        Assert.Equal(extension == ".csv" ? [3, null, 2, 1, 4] : [1, null, 2, 3, 4], rows.Select(row => row.NewIndex));
        Assert.Equal(extension == ".csv" ? 2 : 1, result.Summary.Deleted);
        Assert.Equal(extension == ".csv" ? 4 : 2, result.Summary.Added);
        Assert.Equal(0, result.Summary.Modified);
        Assert.Equal(3, rows.Count(row => row.OldIndex.HasValue));
        Assert.Equal(4, rows.Count(row => row.NewIndex.HasValue));
    }

    [Theory]
    [InlineData(".xlsx")]
    [InlineData(".csv")]
    public async Task CompositeKeysDisambiguateRepeatedIdsWithoutDelimiterCollisionsAsync(string extension)
    {
        using var files = new KeyFixtures(extension);
        string[][] original = [["item", "one", "north"], ["item", "two", "south"],
            ["foo:bar", "three", "baz"], ["foo", "four", "bar:baz"]];
        files.Create("old", original);
        files.Create("new", original.Reverse().ToArray());

        var result = await files.CompareAsync(["A", "C"]);

        Assert.Empty(result.Warnings);
        Assert.Equal(extension == ".csv" ? 0 : 1, result.Summary.Changed);
        Assert.Equal([4, 3, 2, 1], result.TabularSections[0].Rows.Select(row => extension == ".csv" ? row.NewIndex : row.OldIndex));
    }

    [Theory]
    [InlineData("A1", "设置无效")]
    [InlineData("XFE", "设置无效")]
    [InlineData("A,A", "设置无效")]
    [InlineData(" ", "设置无效")]
    [InlineData("D", "不存在关键列 D")]
    public async Task InvalidOrMissingColumnsWarnAndKeepDefaultAlignmentAsync(string columns, string reason)
    {
        using var files = new KeyFixtures(".xlsx");
        files.Create("old", [["one", "alpha"], ["two", "beta"]]);
        files.Create("new", [["two", "beta"], ["one", "alpha"]]);

        var result = await files.CompareAsync(columns.Split(','));
        var defaultResult = await files.CompareAsync([]);

        AssertFallback(result, defaultResult, reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateKeyOnEitherSideKeepsOtherUniqueKeysAsync(bool duplicateOnCurrent)
    {
        using var files = new KeyFixtures(".xlsx");
        string[][] unique = [["one", "alpha"], ["two", "beta"], ["three", "gamma"]];
        string[][] duplicate = [["one", "alpha"], ["two", "beta"], ["one", "gamma"]];
        files.Create("old", duplicateOnCurrent ? unique : duplicate);
        files.Create("new", duplicateOnCurrent ? duplicate : unique);

        var result = await files.CompareAsync(["A"]);

        Assert.Contains("关键列组合重复", Assert.Single(result.Warnings), StringComparison.Ordinal);
        Assert.Contains("仅异常组", result.Warnings[0], StringComparison.Ordinal);
        var uniqueRow = result.TabularSections[0].Rows.Single(row => row.NewIndex == 2);
        Assert.Equal(2, uniqueRow.OldIndex);
        Assert.All(uniqueRow.Cells, cell => Assert.Equal(DiffKind.Unchanged, cell.Kind));
        Assert.DoesNotContain("alpha", result.Warnings[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".xlsx", "")]
    [InlineData(".xlsx", "  ")]
    [InlineData(".csv", "")]
    public async Task EmptyKeyComponentFallsBackWithoutDroppingBlankRecordsAsync(string extension, string empty)
    {
        using var files = new KeyFixtures(extension);
        files.Create("old", [["id1", "alpha", "north"], ["id2", "beta", empty]]);
        files.Create("new", [["id2", "beta", empty], ["id1", "alpha", "north"]]);

        var result = await files.CompareAsync(["A", "C"]);

        if (extension == ".csv") AssertFallback(result, await files.CompareAsync([]), "关键列 C 为空或缺失");
        else
        {
            Assert.Contains("仅异常组", Assert.Single(result.Warnings), StringComparison.Ordinal);
            Assert.All(result.TabularSections[0].Rows.SelectMany(row => row.Cells), cell => Assert.Equal(DiffKind.Unchanged, cell.Kind));
        }
        Assert.Equal(2, result.TabularSections[0].Rows.Count(row => row.OldIndex.HasValue));
        Assert.Equal(2, result.TabularSections[0].Rows.Count(row => row.NewIndex.HasValue));
    }

    [Fact]
    public async Task MissingKeyCellWarnsEvenWhenTheColumnExistsInOtherRowsAsync()
    {
        using var files = new KeyFixtures(".xlsx");
        files.Create("old", [["id1", "alpha", "north"], ["id2", "beta"]]);
        files.Create("new", [["id2", "beta"], ["id1", "alpha", "north"]]);

        var result = await files.CompareAsync(["A", "C"]);

        Assert.Contains("仅异常组", Assert.Single(result.Warnings), StringComparison.Ordinal);
        Assert.All(result.TabularSections[0].Rows.SelectMany(row => row.Cells), cell => Assert.Equal(DiffKind.Unchanged, cell.Kind));
    }

    [Fact]
    public async Task FallbackIsLocalToTheInvalidWorksheetAsync()
    {
        using var files = new KeyFixtures(".xlsx");
        files.CreateSheets("old",
            new SheetRows("Good", [["a", "one"], ["b", "two"]]),
            new SheetRows("Bad", [["duplicate", "first"], ["duplicate", "second"]]));
        files.CreateSheets("new",
            new SheetRows("Good", [["b", "two"], ["a", "one"]]),
            new SheetRows("Bad", [["duplicate", "second"], ["duplicate", "first"]]));

        var result = await files.CompareAsync(["A"]);
        var defaultResult = await files.CompareAsync([]);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("Bad", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Good", warning, StringComparison.Ordinal);
        Assert.All(result.TabularSections[0].Rows.SelectMany(row => row.Cells), cell => Assert.Equal(DiffKind.Unchanged, cell.Kind));
        Assert.Equal([2, 1], result.TabularSections[0].Rows.Select(row => row.OldIndex));
        Assert.Equal(defaultResult.TabularSections[1].Rows.Count, result.TabularSections[1].Rows.Count);
        Assert.All(result.TabularSections[1].Rows.SelectMany(row => row.Cells), cell => Assert.Equal(DiffKind.Unchanged, cell.Kind));
    }

    [Fact]
    public async Task KeyMatchingUsesFullValuesAndKeepsSignificantWhitespaceAndCaseAsync()
    {
        using var files = new KeyFixtures(".xlsx");
        var prefix = new string('x', 200);
        string[][] original = [[prefix + "a", "one"], [prefix + "b", "two"],
            ["ID", "three"], ["id", "four"], ["id ", "five"]];
        files.Create("old", original);
        files.Create("new", original.Reverse().ToArray());

        var result = await files.CompareAsync([" a "]);

        Assert.Empty(result.Warnings);
        Assert.Equal(0, result.Summary.Modified);
        Assert.Equal(1, result.Summary.Moved);
        Assert.Equal([5, 4, 3, 2, 1], result.TabularSections[0].Rows.Select(row => row.OldIndex));
    }

    [Fact]
    public async Task KeysRespectSemanticTypesAndIgnoreCellStyleIndicesAsync()
    {
        using var files = new KeyFixtures(".xlsx");
        files.Create("old", [["1", "numeric"], ["1", "text"]]);
        files.Create("new", [["1", "text"], ["1", "numeric"]]);
        files.EditCell("old", "A1", cell =>
        {
            cell.DataType = CellValues.Number;
            cell.InlineString = null;
            cell.CellValue = new CellValue("1.0");
        });
        files.EditCell("new", "A2", cell =>
        {
            cell.DataType = null;
            cell.InlineString = null;
            cell.CellValue = new CellValue("1");
            cell.StyleIndex = 42U;
        });

        var result = await files.CompareAsync(["A"]);

        Assert.Empty(result.Warnings);
        Assert.Equal(0, result.Summary.Modified);
        Assert.Equal(1, result.Summary.Moved);
        Assert.Equal([2, 1], result.TabularSections[0].Rows.Select(row => row.OldIndex));
    }

    [Fact]
    public async Task FormulaKeyUsesSavedValueWhileFormulaChangesRemainVisibleAsync()
    {
        using var files = new KeyFixtures(".xlsx");
        files.Create("old", [["11", "alpha"], ["12", "beta"]]);
        files.Create("new", [["12", "beta"], ["11", "alpha"]]);
        files.EditCell("old", "A1", cell => SetFormula(cell, "10+1", "11"));
        files.EditCell("old", "A2", cell => SetFormula(cell, "10+2", "12"));
        files.EditCell("new", "A1", cell => SetFormula(cell, "10+2", "12"));
        files.EditCell("new", "A2", cell => SetFormula(cell, "5+6", "11"));

        var result = await files.CompareAsync(["A"]);

        Assert.Equal(1, result.Summary.Modified);
        Assert.Equal(2, result.Summary.Changed);
        Assert.Equal(1, result.Summary.Moved);
        Assert.Equal([2, 1], result.TabularSections[0].Rows.Select(row => row.OldIndex));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("回退", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("不会重新计算", StringComparison.Ordinal));
    }

    private static void SetFormula(Cell cell, string formula, string cache)
    {
        cell.DataType = CellValues.Number;
        cell.InlineString = null;
        cell.CellFormula = new CellFormula(formula);
        cell.CellValue = new CellValue(cache);
    }

    private static void AssertFallback(DiffDocument result, DiffDocument defaultResult, string reason)
    {
        var warning = Assert.Single(result.Warnings);
        Assert.Contains(reason, warning, StringComparison.Ordinal);
        Assert.Contains("已回退默认序列对齐", warning, StringComparison.Ordinal);
        Assert.Equal(defaultResult.Summary, result.Summary);
        Assert.Equal(JsonSerializer.Serialize(defaultResult.TabularSections), JsonSerializer.Serialize(result.TabularSections));
        Assert.Equal(JsonSerializer.Serialize(defaultResult.Nodes), JsonSerializer.Serialize(result.Nodes));
    }

    private sealed record SheetRows(string Name, string[][] Rows);

    private sealed class KeyFixtures(string extension) : IDisposable
    {
        private readonly string directory = CreateDirectory();

        public void Create(string side, string[][] rows) => CreateSheets(side, new SheetRows("Sheet1", rows));

        public void CreateSheets(string side, params SheetRows[] sheets)
        {
            var path = FilePath(side);
            if (extension == ".csv")
            {
                File.WriteAllText(path, string.Join('\n', sheets.Single().Rows.Select(row =>
                    string.Join(',', row.Select(value => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")))),
                    new UTF8Encoding(false));
                return;
            }

            if (extension == ".xls")
            {
                using var workbook = new HSSFWorkbook();
                foreach (var input in sheets)
                {
                    var sheet = workbook.CreateSheet(input.Name);
                    for (var rowIndex = 0; rowIndex < input.Rows.Length; rowIndex++)
                    {
                        var row = sheet.CreateRow(rowIndex);
                        for (var column = 0; column < input.Rows[rowIndex].Length; column++)
                        {
                            row.CreateCell(column).SetCellValue(input.Rows[rowIndex][column]);
                        }
                    }
                }

                using var stream = File.Create(path);
                workbook.Write(stream);
                return;
            }

            using var document = SpreadsheetDocument.Create(path, extension == ".xlsm"
                ? SpreadsheetDocumentType.MacroEnabledWorkbook : SpreadsheetDocumentType.Workbook);
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var sheetList = workbookPart.Workbook.AppendChild(new Sheets());
            for (var sheetIndex = 0; sheetIndex < sheets.Length; sheetIndex++)
            {
                var input = sheets[sheetIndex];
                var worksheet = workbookPart.AddNewPart<WorksheetPart>();
                var data = new SheetData();
                for (var rowIndex = 0; rowIndex < input.Rows.Length; rowIndex++)
                {
                    var row = new Row { RowIndex = (uint)(rowIndex + 1) };
                    for (var column = 0; column < input.Rows[rowIndex].Length; column++)
                    {
                        row.Append(new Cell
                        {
                            CellReference = $"{(char)('A' + column)}{rowIndex + 1}",
                            DataType = CellValues.InlineString,
                            InlineString = new InlineString(new SpreadsheetText(input.Rows[rowIndex][column]))
                        });
                    }

                    data.Append(row);
                }

                worksheet.Worksheet = new Worksheet(data);
                sheetList.Append(new Sheet
                {
                    Name = input.Name,
                    SheetId = (uint)(sheetIndex + 1),
                    Id = workbookPart.GetIdOfPart(worksheet)
                });
            }
        }

        public void EditCell(string side, string address, Action<Cell> edit)
        {
            using var document = SpreadsheetDocument.Open(FilePath(side), true);
            var worksheet = document.WorkbookPart!.WorksheetParts.Single().Worksheet!;
            edit(worksheet.Descendants<Cell>().Single(cell => cell.CellReference?.Value == address));
            worksheet.Save();
        }

        public Task<DiffDocument> CompareAsync(IReadOnlyList<string> keys)
        {
            IFileDiffAdapter adapter = extension switch
            {
                ".xls" => new XlsDiffAdapter(),
                ".csv" => new CsvDiffAdapter(new TextFileReader()),
                _ => new ExcelDiffAdapter()
            };
            return adapter.CompareAsync(new DiffRequest
            {
                RelativePath = "sample" + extension,
                BaseFilePath = FilePath("old"),
                CurrentFilePath = FilePath("new"),
                Options = new DiffOptions { KeyColumns = keys }
            }, CancellationToken.None);
        }

        private string FilePath(string side) => Path.Combine(directory, side + extension);

        private static string CreateDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "Differ.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
