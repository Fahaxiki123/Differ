using System.Globalization;
using Differ.Core.Files;
using Differ.Core.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SpreadsheetText = DocumentFormat.OpenXml.Spreadsheet.Text;

namespace Differ.Formats.Excel.Tests;

public sealed class ExcelDiffAdapterTests
{
    [Fact]
    public async Task CompareGeneratedFixtureCopiedToTempIsUnchangedAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var source = CreateRepresentativeWorkbook(testRoot);
            var sourceBefore = await FileSnapshot.CreateAsync(source);
            var oldPath = Path.Combine(testRoot, "base.xlsx");
            var newPath = Path.Combine(testRoot, "working.xlsx");
            File.Copy(source, oldPath);
            File.Copy(source, newPath);
            var result = await new ExcelDiffAdapter().CompareAsync(
                new DiffRequest
                {
                    RelativePath = Path.GetFileName(source),
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath,
                    BaseRevision = "fixture"
                },
                CancellationToken.None);

            Assert.Equal(FileKind.Excel, result.FileKind);
            Assert.Equal(0, result.Summary.Changed);
            Assert.All(
                result.TabularSections.SelectMany(section => section.Rows),
                row => Assert.Equal(DiffKind.Unchanged, row.Kind));
            Assert.Equal(sourceBefore, await FileSnapshot.CreateAsync(source));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CompareDetectsFormulaChangeInCopiedFixtureAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var source = CreateRepresentativeWorkbook(testRoot);
            var sourceBefore = await FileSnapshot.CreateAsync(source);
            var oldPath = Path.Combine(testRoot, "base.xlsx");
            var newPath = Path.Combine(testRoot, "working.xlsx");
            File.Copy(source, oldPath);
            File.Copy(source, newPath);

            using (var document = SpreadsheetDocument.Open(newPath, isEditable: true))
            {
                var worksheet = document.WorkbookPart?.WorksheetParts.FirstOrDefault()?.Worksheet;
                var cell = worksheet?.Descendants<Cell>().FirstOrDefault();
                Assert.NotNull(cell);
                cell.DataType = null;
                cell.InlineString = null;
                cell.CellFormula = new CellFormula("1+2");
                cell.CellValue = new CellValue("3");
                worksheet!.Save();
            }

            var result = await new ExcelDiffAdapter().CompareAsync(
                new DiffRequest
                {
                    RelativePath = Path.GetFileName(source),
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath
                },
                CancellationToken.None);

            Assert.True(result.Summary.Changed > 0);
            Assert.Contains(
                result.Nodes,
                node => node.Metadata.TryGetValue("new.formula", out var formula)
                    && string.Equals(formula, "1+2", StringComparison.Ordinal));
            Assert.Equal(sourceBefore, await FileSnapshot.CreateAsync(source));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CompareIgnoresFormattingOnlyChangeUnlessExplicitlyEnabledAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var source = CreateRepresentativeWorkbook(testRoot);
            var sourceBefore = await FileSnapshot.CreateAsync(source);
            var oldPath = Path.Combine(testRoot, "base.xlsx");
            var newPath = Path.Combine(testRoot, "working.xlsx");
            File.Copy(source, oldPath);
            File.Copy(source, newPath);

            using (var document = SpreadsheetDocument.Open(newPath, isEditable: true))
            {
                var worksheet = document.WorkbookPart?.WorksheetParts
                    .FirstOrDefault()?.Worksheet;
                var cell = worksheet?.Descendants<Cell>().FirstOrDefault();
                Assert.NotNull(cell);
                cell.StyleIndex = (cell.StyleIndex?.Value ?? 0U) + 1U;
                worksheet!.Save();
            }

            var adapter = new ExcelDiffAdapter();
            var defaultResult = await adapter.CompareAsync(
                new DiffRequest
                {
                    RelativePath = Path.GetFileName(source),
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath
                },
                CancellationToken.None);
            var formattingResult = await adapter.CompareAsync(
                new DiffRequest
                {
                    RelativePath = Path.GetFileName(source),
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath,
                    Options = new DiffOptions { CompareFormatting = true }
                },
                CancellationToken.None);

            Assert.Equal(0, defaultResult.Summary.Changed);
            Assert.True(formattingResult.Summary.Changed > 0);
            Assert.Equal(sourceBefore, await FileSnapshot.CreateAsync(source));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CompareIgnoresSharedStringIndexChangesAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(testRoot, "base.xlsx");
            var newPath = Path.Combine(testRoot, "working.xlsx");
            CreateSharedStringWorkbook(oldPath, ["Alpha"], 0);
            CreateSharedStringWorkbook(newPath, ["Unused", "Alpha"], 1);

            var result = await new ExcelDiffAdapter().CompareAsync(
                new DiffRequest
                {
                    RelativePath = "sample.xlsx",
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath,
                    Options = new DiffOptions { IncludeUnchanged = true }
                },
                CancellationToken.None);

            Assert.Equal(0, result.Summary.Changed);
            Assert.Equal(["Sheet1"], result.Sections);
            Assert.Single(result.Nodes);
            Assert.Equal(DiffKind.Unchanged, result.Nodes[0].Kind);
            Assert.Equal("Sheet1", result.Nodes[0].SectionKey);
            Assert.Equal("Alpha", result.Nodes[0].OldValue);
            Assert.Equal("Alpha", result.Nodes[0].NewValue);
            var table = Assert.Single(result.TabularSections);
            Assert.Equal("Sheet1", table.Key);
            Assert.Equal(["A"], table.Columns);
            var row = Assert.Single(table.Rows);
            Assert.Equal(1, row.OldIndex);
            Assert.Equal(1, row.NewIndex);
            Assert.Equal(DiffKind.Unchanged, row.Kind);
            var cell = Assert.Single(row.Cells);
            Assert.Equal("Alpha", cell.OldValue);
            Assert.Equal("Alpha", cell.NewValue);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CompareBuildsOriginalWorksheetContextAroundChangedCellAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(testRoot, "base.xlsx");
            var newPath = Path.Combine(testRoot, "working.xlsx");
            CreateTwoColumnWorkbook(
                oldPath,
                [
                    (7U, "2001", "未修改上下文"),
                    (8U, "2002", "一般来源于付费，可在部分情况下使用")
                ]);
            CreateTwoColumnWorkbook(
                newPath,
                [
                    (7U, "2001", "未修改上下文"),
                    (8U, "2002", "一般来源于付费")
                ]);

            var result = await new ExcelDiffAdapter().CompareAsync(
                new DiffRequest
                {
                    RelativePath = "sample.xlsx",
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath,
                    Options = new DiffOptions { IncludeUnchanged = true }
                },
                CancellationToken.None);

            var table = Assert.Single(result.TabularSections);
            Assert.Equal("物品表", table.Key);
            Assert.Equal(["B", "C", "D", "E"], table.Columns);
            Assert.Equal(2, table.Rows.Count);
            Assert.Equal(DiffKind.Unchanged, table.Rows[0].Kind);
            Assert.Equal(7, table.Rows[0].OldIndex);
            Assert.Equal(7, table.Rows[0].NewIndex);

            var changedRow = table.Rows[1];
            Assert.Equal(DiffKind.Modified, changedRow.Kind);
            Assert.Equal(8, changedRow.OldIndex);
            Assert.Equal(8, changedRow.NewIndex);
            Assert.Equal(DiffKind.Unchanged, changedRow.Cells[0].Kind);
            Assert.Equal(DiffKind.Unchanged, changedRow.Cells[1].Kind);
            Assert.Equal(DiffKind.Unchanged, changedRow.Cells[2].Kind);
            Assert.Equal(DiffKind.Modified, changedRow.Cells[3].Kind);
            Assert.Equal(
                "一般来源于付费，可在部分情况下使用",
                changedRow.Cells[3].OldValue);
            Assert.Equal("一般来源于付费", changedRow.Cells[3].NewValue);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CompareGroupsRowsByCurrentWorkbookSheetOrderAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(testRoot, "base.xlsx");
            var newPath = Path.Combine(testRoot, "working.xlsx");
            CreateInlineStringWorkbook(
                oldPath,
                [("Overview", "same"), ("Items", "before")]);
            CreateInlineStringWorkbook(
                newPath,
                [("Items", "after"), ("Overview", "same")]);

            var result = await new ExcelDiffAdapter().CompareAsync(
                new DiffRequest
                {
                    RelativePath = "sample.xlsx",
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath,
                    Options = new DiffOptions { IncludeUnchanged = true }
                },
                CancellationToken.None);

            Assert.Equal(["Items", "Overview"], result.Sections);
            Assert.All(
                result.Nodes,
                node => Assert.Contains(node.SectionKey, result.Sections));
            Assert.Contains(
                result.Nodes,
                node => node.SectionKey == "Items"
                    && node.Kind == DiffKind.Modified);
            Assert.Contains(
                result.Nodes,
                node => node.SectionKey == "Overview"
                    && node.Kind == DiffKind.Unchanged);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CompareTreatsZeroLengthBaseAsEmptyWorkbookAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var source = CreateRepresentativeWorkbook(testRoot);
            var emptyBase = Path.Combine(testRoot, "base.xlsx");
            var current = Path.Combine(testRoot, "working.xlsx");
            await File.WriteAllBytesAsync(emptyBase, []);
            File.Copy(source, current);

            var result = await new ExcelDiffAdapter().CompareAsync(
                new DiffRequest
                {
                    RelativePath = Path.GetFileName(source),
                    BaseFilePath = emptyBase,
                    CurrentFilePath = current
                },
                CancellationToken.None);

            Assert.True(result.Summary.Added > 0);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InsertedRowDoesNotAmplifyMovedDifferencesAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(testRoot, "base.xlsx");
            var newPath = Path.Combine(testRoot, "working.xlsx");
            var values = Enumerable.Range(1, 50)
                .Select(index => $"数据行{index}")
                .ToList();
            CreateSingleColumnWorkbook(oldPath, values, null, asSharedString: false);
            CreateSingleColumnWorkbook(newPath, values, "新增的表头行", asSharedString: false);

            var result = await new ExcelDiffAdapter().CompareAsync(
                new DiffRequest
                {
                    RelativePath = "insert.xlsx",
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath
                },
                CancellationToken.None);

            Assert.Equal(1, result.Summary.Added);
            Assert.Equal(0, result.Summary.Deleted);
            Assert.Equal(0, result.Summary.Modified);
            Assert.Equal(0, result.Summary.Moved);
            Assert.Equal(values.Count, result.Summary.Unchanged);
            Assert.Equal(1, result.Summary.Changed);
            Assert.All(
                result.TabularSections.SelectMany(section => section.Rows),
                row => Assert.True(
                    row.Kind is DiffKind.Unchanged or DiffKind.Added,
                    $"Unexpected row kind {row.Kind}"));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SameDisplayValueWithDifferentSemanticTypeIsModifiedAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(testRoot, "base.xlsx");
            var newPath = Path.Combine(testRoot, "working.xlsx");
            CreateSingleColumnWorkbook(oldPath, ["123"], null, asSharedString: false);
            CreateSingleColumnWorkbook(newPath, ["123"], null, asSharedString: true);

            var result = await new ExcelDiffAdapter().CompareAsync(
                new DiffRequest
                {
                    RelativePath = "types.xlsx",
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath
                },
                CancellationToken.None);

            Assert.Equal(1, result.Summary.Modified);
            Assert.Contains(
                result.Nodes,
                node => node.Kind == DiffKind.Modified
                    && string.Equals(node.OldValue, "123", StringComparison.Ordinal)
                    && string.Equals(node.NewValue, "123", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task XlsmWithSameContentReportsNoDifferencesAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(testRoot, "base.xlsm");
            var newPath = Path.Combine(testRoot, "working.xlsm");
            var values = Enumerable.Range(1, 10)
                .Select(index => $"宏工作簿行{index}")
                .ToList();
            CreateSingleColumnWorkbook(
                oldPath,
                values,
                null,
                asSharedString: false,
                SpreadsheetDocumentType.MacroEnabledWorkbook);
            CreateSingleColumnWorkbook(
                newPath,
                values,
                null,
                asSharedString: false,
                SpreadsheetDocumentType.MacroEnabledWorkbook);

            var result = await CompareWithOpenXmlAdapterAsync(oldPath, newPath);

            Assert.Equal(FileKind.Excel, result.FileKind);
            Assert.Equal(0, result.Summary.Changed);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task XlsInsertedRowKeepsFollowingRowsAlignedAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(testRoot, "base.xls");
            var newPath = Path.Combine(testRoot, "working.xls");
            var values = Enumerable.Range(1, 20)
                .Select(index => $"数据行{index}")
                .ToList();
            CreateXls(oldPath, values);
            CreateXls(newPath, ["插入的表头行", .. values]);

            var result = await new XlsDiffAdapter().CompareAsync(
                new DiffRequest
                {
                    RelativePath = "insert.xls",
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath
                },
                CancellationToken.None);

            Assert.Equal(1, result.Summary.Added);
            Assert.Equal(0, result.Summary.Moved);
            Assert.Equal(0, result.Summary.Modified);
            Assert.Equal(values.Count, result.Summary.Unchanged);
            Assert.Equal(1, result.Summary.Changed);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task XlsCellValueChangeIsDetectedAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(testRoot, "base.xls");
            var newPath = Path.Combine(testRoot, "working.xls");
            CreateXls(oldPath, ["第一行", "第二行"]);
            CreateXls(newPath, ["第一行", "第二行已修改"]);

            var result = await new XlsDiffAdapter().CompareAsync(
                new DiffRequest
                {
                    RelativePath = "change.xls",
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath
                },
                CancellationToken.None);

            Assert.True(result.Summary.Modified >= 1);
            Assert.Contains(
                result.Nodes,
                node => node.NewValue?.Contains("已修改", StringComparison.Ordinal) == true);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("sample.xlsx", true)]
    [InlineData("sample.xlsm", true)]
    [InlineData("sample.xls", false)]
    [InlineData("sample.docx", false)]
    public void CanHandleOnlyAcceptsXlsx(string path, bool expected)
    {
        Assert.Equal(expected, new ExcelDiffAdapter().CanHandle(path));
    }

    [Theory]
    [InlineData("sample.xls", true)]
    [InlineData("sample.xlsx", false)]
    [InlineData("sample.xlsm", false)]
    public void XlsCanHandleOnlyAcceptsLegacyXls(string path, bool expected)
    {
        Assert.Equal(expected, new XlsDiffAdapter().CanHandle(path));
    }

    private static async Task<DiffDocument> CompareWithOpenXmlAdapterAsync(
        string oldPath,
        string newPath)
    {
        return await new ExcelDiffAdapter().CompareAsync(
            new DiffRequest
            {
                RelativePath = Path.GetFileName(newPath),
                BaseFilePath = oldPath,
                CurrentFilePath = newPath
            },
            CancellationToken.None);
    }

    private static void CreateXls(string path, List<string> columnValues)
    {
        var workbook = new NPOI.HSSF.UserModel.HSSFWorkbook();
        var sheet = workbook.CreateSheet("Sheet1");
        for (var rowIndex = 0; rowIndex < columnValues.Count; rowIndex++)
        {
            sheet.CreateRow(rowIndex).CreateCell(0).SetCellValue(columnValues[rowIndex]);
        }

        using var stream = File.Create(path);
        workbook.Write(stream, leaveOpen: false);
    }

    private static void CreateSingleColumnWorkbook(
        string path,
        IReadOnlyList<string> rowValues,
        string? prependedRow,
        bool asSharedString,
        SpreadsheetDocumentType documentType = SpreadsheetDocumentType.Workbook)
    {
        var all = new List<string>();
        if (prependedRow is not null)
        {
            all.Add(prependedRow);
        }

        all.AddRange(rowValues);

        using var document = SpreadsheetDocument.Create(path, documentType);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();

        if (asSharedString)
        {
            var sharedStringPart = workbookPart.AddNewPart<SharedStringTablePart>();
            var items = all
                .Select(value => new SharedStringItem(new DocumentFormat.OpenXml.Spreadsheet.Text(value)))
                .ToList();
            sharedStringPart.SharedStringTable = new SharedStringTable(items)
            {
                Count = (uint)items.Count,
                UniqueCount = (uint)items.Count
            };
            sharedStringPart.SharedStringTable.Save();
        }

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        uint rowIndex = 1;
        foreach (var value in all)
        {
            var row = new Row { RowIndex = rowIndex };
            var cell = new Cell { CellReference = $"A{rowIndex}" };
            if (asSharedString)
            {
                cell.DataType = CellValues.SharedString;
                cell.CellValue = new CellValue(
                    all.IndexOf(value).ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                cell.DataType = CellValues.Number;
                cell.CellValue = new CellValue(value);
            }

            row.Append(cell);
            sheetData.Append(row);
            rowIndex++;
        }

        worksheetPart.Worksheet = new Worksheet(sheetData);
        worksheetPart.Worksheet.Save();

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "Sheet1"
        });
        workbookPart.Workbook.Save();
    }

    private static string CreateRepresentativeWorkbook(string testRoot)
    {
        var path = Path.Combine(testRoot, "source.xlsx");
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = new Stylesheet(
            new Fonts(new Font(), new Font(new Bold())) { Count = 2U },
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }))
            { Count = 2U },
            new Borders(new Border()) { Count = 1U },
            new CellStyleFormats(new CellFormat()) { Count = 1U },
            new CellFormats(
                new CellFormat { FontId = 0U },
                new CellFormat { FontId = 1U, ApplyFont = true })
            { Count = 2U },
            new CellStyles(new CellStyle { Name = "Normal", FormatId = 0U, BuiltinId = 0U }) { Count = 1U });
        var stringsPart = workbookPart.AddNewPart<SharedStringTablePart>();
        stringsPart.SharedStringTable = new SharedStringTable(new SharedStringItem(new SpreadsheetText("共享文本 Shared text")))
        {
            Count = 1U,
            UniqueCount = 1U
        };

        // The first cell deliberately has a formula and a real style. Changes
        // must compare formula/value/style semantics rather than missing IDs.
        var mainSheetPart = workbookPart.AddNewPart<WorksheetPart>();
        mainSheetPart.Worksheet = new Worksheet(new SheetData(
            new Row(
                new Cell(new CellFormula("1+1"), new CellValue("2")) { CellReference = "A1", StyleIndex = 0U },
                new Cell(new InlineString(new SpreadsheetText("示例项目"))) { CellReference = "B1", DataType = CellValues.InlineString },
                new Cell(new CellValue("0")) { CellReference = "C1", DataType = CellValues.SharedString })
            { RowIndex = 1U },
            new Row(
                new Cell(new CellValue("20")) { CellReference = "A2", DataType = CellValues.Number },
                new Cell(new InlineString(new SpreadsheetText("未修改上下文"))) { CellReference = "B2", DataType = CellValues.InlineString },
                new Cell { CellReference = "C2" })
            { RowIndex = 2U }));
        var overviewPart = workbookPart.AddNewPart<WorksheetPart>();
        overviewPart.Worksheet = new Worksheet(new SheetData(new Row(
            new Cell(new InlineString(new SpreadsheetText("Overview"))) { CellReference = "A1", DataType = CellValues.InlineString })
        { RowIndex = 1U }));
        workbookPart.Workbook.Append(new Sheets(
            new Sheet
            {
                Id = workbookPart.GetIdOfPart(mainSheetPart),
                SheetId = 1U,
                Name = "合成表格"
            },
            new Sheet
            {
                Id = workbookPart.GetIdOfPart(overviewPart),
                SheetId = 2U,
                Name = "Overview"
            }));
        workbookPart.Workbook.Save();
        return path;
    }

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "Differ.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateSharedStringWorkbook(
        string path,
        IReadOnlyList<string> sharedStrings,
        int valueIndex)
    {
        using var document = SpreadsheetDocument.Create(
            path,
            SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();

        var sharedStringPart = workbookPart.AddNewPart<SharedStringTablePart>();
        sharedStringPart.SharedStringTable = new SharedStringTable(
            sharedStrings.Select(value => new SharedStringItem(new SpreadsheetText(value))));

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var row = new Row { RowIndex = 1U };
        row.Append(new Cell
        {
            CellReference = "A1",
            DataType = CellValues.SharedString,
            CellValue = new CellValue(valueIndex.ToString(CultureInfo.InvariantCulture))
        });
        worksheetPart.Worksheet = new Worksheet(new SheetData(row));

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = "Sheet1"
        });
        workbookPart.Workbook.Save();
    }

    private static void CreateInlineStringWorkbook(
        string path,
        IReadOnlyList<(string Name, string Value)> sheetValues)
    {
        using var document = SpreadsheetDocument.Create(
            path,
            SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());

        for (var index = 0; index < sheetValues.Count; index++)
        {
            var sheetValue = sheetValues[index];
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var row = new Row { RowIndex = 1U };
            row.Append(new Cell
            {
                CellReference = "A1",
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new SpreadsheetText(sheetValue.Value))
            });
            worksheetPart.Worksheet = new Worksheet(new SheetData(row));
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = (uint)(index + 1),
                Name = sheetValue.Name
            });
        }

        workbookPart.Workbook.Save();
    }

    private static void CreateTwoColumnWorkbook(
        string path,
        IReadOnlyList<(uint RowIndex, string ColumnB, string ColumnE)> values)
    {
        using var document = SpreadsheetDocument.Create(
            path,
            SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        foreach (var value in values)
        {
            var row = new Row { RowIndex = value.RowIndex };
            row.Append(
                new Cell
                {
                    CellReference = $"B{value.RowIndex}",
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(new SpreadsheetText(value.ColumnB))
                },
                new Cell
                {
                    CellReference = $"E{value.RowIndex}",
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(new SpreadsheetText(value.ColumnE))
                });
            sheetData.Append(row);
        }

        worksheetPart.Worksheet = new Worksheet(sheetData);
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = "物品表"
        });
        workbookPart.Workbook.Save();
    }
}
