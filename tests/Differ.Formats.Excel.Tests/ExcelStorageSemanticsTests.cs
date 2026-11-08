using Differ.Core.Models;
using Differ.Core.Security;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using NPOI.HSSF.UserModel;
using Cell = DocumentFormat.OpenXml.Spreadsheet.Cell;
using CellType = NPOI.SS.UserModel.CellType;
using Font = DocumentFormat.OpenXml.Spreadsheet.Font;
using SpreadsheetText = DocumentFormat.OpenXml.Spreadsheet.Text;

namespace Differ.Formats.Excel.Tests;

public sealed class ExcelStorageSemanticsTests
{
    [Fact]
    public async Task SharedFormulaMasterChangeMarksEveryDependentCellAsync()
    {
        using var files = new WorkbookFiles();
        files.Create("old.xlsx", [Shared("A1", "B1+1", "A1:A2"), Shared("A2")]);
        files.Create("new.xlsx", [Shared("A1", "B1+2", "A1:A2"), Shared("A2")]);

        var result = await files.CompareAsync();

        Assert.Equal(2, result.Summary.Modified);
        Assert.Equal("=B2+1", result.TabularSections[0].Rows[1].Cells[0].OldValue);
        Assert.Equal("=B2+2", result.TabularSections[0].Rows[1].Cells[0].NewValue);
    }

    [Fact]
    public async Task SharedFormulaAndExpandedFormulaHaveIdenticalSemanticsAsync()
    {
        using var files = new WorkbookFiles();
        const string master = "SUM(A1,$A1,A$1,$A$1,A:A,$A:$A,1:1,$1:$1,'A1 sheet'!A1,[book.xlsx]Sheet1!A1,\"A1\",LOG10(A1),Table1[A1])";
        const string dependent = "SUM(B2,$A2,B$1,$A$1,B:B,$A:$A,2:2,$1:$1,'A1 sheet'!B2,[book.xlsx]Sheet1!B2,\"A1\",LOG10(B2),Table1[A1])";
        files.Create("old.xlsx", [Shared("B2", master, "B2:C3"), Shared("C3")]);
        files.Create("new.xlsx", [Formula("B2", master), Formula("C3", dependent)]);

        var result = await files.CompareAsync();

        Assert.Equal(0, result.Summary.Changed);
        Assert.Equal("=" + dependent, result.TabularSections[0].Rows[1].Cells[1].OldValue);
    }

    [Fact]
    public async Task MissingSharedFormulaMasterFailsExplicitlyAsync()
    {
        using var files = new WorkbookFiles();
        files.Create("old.xlsx", [Shared("A1")]);
        files.Create("new.xlsx", [Shared("A1")]);

        await Assert.ThrowsAsync<StructuralParseException>(() => files.CompareAsync());
    }

    [Theory]
    [InlineData("shared-inline")]
    [InlineData("shared-string")]
    [InlineData("implicit-number")]
    [InlineData("numeric-representation")]
    public async Task EquivalentStorageDoesNotBecomeAContentChangeAsync(string storage)
    {
        using var files = new WorkbookFiles();
        var oldCell = Number("A1", "1");
        var newCell = Number("A1", "1");
        if (storage.StartsWith("shared", StringComparison.Ordinal))
        {
            oldCell.DataType = CellValues.SharedString;
            oldCell.CellValue = new CellValue("0");
            newCell.DataType = storage == "shared-inline" ? CellValues.InlineString : CellValues.String;
            newCell.CellValue = storage == "shared-inline" ? null : new CellValue("Alpha");
            newCell.InlineString = storage == "shared-inline" ? new InlineString(new SpreadsheetText("Alpha")) : null;
        }
        else if (storage == "implicit-number")
        {
            newCell.DataType = CellValues.Number;
        }
        else
        {
            newCell.CellValue = new CellValue("1.0E+0");
        }

        files.Create("old.xlsx", [oldCell], sharedText: "Alpha");
        files.Create("new.xlsx", [newCell]);

        Assert.Equal(0, (await files.CompareAsync()).Summary.Changed);
    }

    [Fact]
    public async Task SameStyleIndexWithChangedFontIsComparedByContentAsync()
    {
        using var files = new WorkbookFiles();
        files.Create("old.xlsx", [Number("A1", "1")], styles: Styles(bold: false));
        files.Create("new.xlsx", [Number("A1", "1")], styles: Styles(bold: true));

        Assert.Equal(0, (await files.CompareAsync()).Summary.Changed);
        Assert.Equal(1, (await files.CompareAsync(formatting: true)).Summary.Modified);
    }

    [Fact]
    public async Task BlankCellDefaultStyleChangeStillCountsWhenFormattingIsEnabledAsync()
    {
        using var files = new WorkbookFiles();
        files.Create("old.xlsx", [new Cell { CellReference = "A1" }], styles: Styles(bold: false));
        files.Create("new.xlsx", [new Cell { CellReference = "A1" }], styles: Styles(bold: true));

        Assert.Equal(0, (await files.CompareAsync()).Summary.Changed);
        Assert.Equal(1, (await files.CompareAsync(formatting: true)).Summary.Modified);
    }

    [Fact]
    public async Task ReorderedStylesAndFontsDoNotChangeActualFormattingAsync()
    {
        using var files = new WorkbookFiles();
        var oldCell = Number("A1", "1");
        var newCell = Number("A1", "1");
        oldCell.StyleIndex = 0U;
        newCell.StyleIndex = 1U;
        var reordered = Styles(bold: false);
        reordered.Fonts!.PrependChild(new Font(new Bold()));
        reordered.CellFormats!.AppendChild(new CellFormat
        {
            FontId = 1U,
            FillId = 0U,
            BorderId = 0U,
            NumberFormatId = 0U
        });
        files.Create("old.xlsx", [oldCell], styles: Styles(bold: false));
        files.Create("new.xlsx", [newCell], styles: reordered);

        Assert.Equal(0, (await files.CompareAsync(formatting: true)).Summary.Changed);
    }

    [Fact]
    public async Task ThemePaletteChangesAreResolvedBeforeComparingFontColorAsync()
    {
        using var files = new WorkbookFiles();
        var oldStyle = Styles(bold: false);
        var newStyle = Styles(bold: false);
        oldStyle.Fonts!.GetFirstChild<Font>()!.AppendChild(new Color { Theme = 1U });
        newStyle.Fonts!.GetFirstChild<Font>()!.AppendChild(new Color { Theme = 1U });
        files.Create("old.xlsx", [Number("A1", "1")], styles: oldStyle, themeDarkColor: "000000");
        files.Create("new.xlsx", [Number("A1", "1")], styles: newStyle, themeDarkColor: "FF0000");

        Assert.Equal(0, (await files.CompareAsync()).Summary.Changed);
        Assert.Equal(1, (await files.CompareAsync(formatting: true)).Summary.Modified);
    }

    [Fact]
    public async Task ThemeAndRgbRepresentationsOfSameFontColorAreEquivalentAsync()
    {
        using var files = new WorkbookFiles();
        var oldStyle = Styles(bold: false);
        var newStyle = Styles(bold: false);
        oldStyle.Fonts!.GetFirstChild<Font>()!.AppendChild(new Color { Theme = 1U });
        newStyle.Fonts!.GetFirstChild<Font>()!.AppendChild(new Color { Rgb = "FFFF0000" });
        files.Create("old.xlsx", [Number("A1", "1")], styles: oldStyle, themeDarkColor: "FF0000");
        files.Create("new.xlsx", [Number("A1", "1")], styles: newStyle);

        Assert.Equal(0, (await files.CompareAsync(formatting: true)).Summary.Changed);
    }

    [Fact]
    public async Task SparseWorksheetPreservesInteriorBlankRowsColumnsAndAddressesAsync()
    {
        using var files = new WorkbookFiles();
        files.Create("old.xlsx", [Number("A1", "1"), Number("C3", "2")]);
        files.Create("new.xlsx", [Number("A1", "1"), new Cell { CellReference = "B2" }, Number("C3", "2")]);

        var result = await files.CompareAsync();

        Assert.Equal(0, result.Summary.Changed);
        var grid = Assert.Single(result.TabularSections);
        Assert.Equal(["A", "B", "C"], grid.Columns);
        Assert.Equal([1, 2, 3], grid.Rows.Select(row => row.OldIndex));
        Assert.Equal([1, 2, 3], grid.Rows.Select(row => row.NewIndex));
        Assert.Equal("B2", grid.Rows[1].Cells[1].OldAddress);
        Assert.Equal("B2", grid.Rows[1].Cells[1].NewAddress);
        Assert.All(grid.Rows[1].Cells, cell => Assert.Equal(DiffKind.Unchanged, cell.Kind));
        Assert.Equal(0, (await files.CompareAsync(formatting: true)).Summary.Changed);
    }

    [Fact]
    public async Task InsertedEmptyRowIsCountedWithoutChangingFollowingContentAsync()
    {
        using var files = new WorkbookFiles();
        files.Create("old.xlsx", [Number("A1", "1"), Number("A2", "2")]);
        files.Create("new.xlsx", [Number("A1", "1"), Number("A3", "2")]);

        var result = await files.CompareAsync();

        Assert.Equal(1, result.Summary.Added);
        Assert.Equal(1, result.Summary.Changed);
        Assert.Equal(DiffKind.Added, result.TabularSections[0].Rows[1].Kind);
        Assert.Equal(DiffKind.Unchanged, result.TabularSections[0].Rows[2].Kind);
    }

    [Theory]
    [InlineData("A1048576")]
    [InlineData("XFD1000")]
    public async Task SparseWorkbookBeyondExpansionBudgetFailsBeforeAllocationAsync(string address)
    {
        using var files = new WorkbookFiles();
        files.Create("old.xlsx", [Number("A1", "1"), Number(address, "2")]);
        files.Create("new.xlsx", [Number("A1", "1"), Number(address, "2")]);

        await Assert.ThrowsAsync<ComparisonResourceLimitException>(() => files.CompareAsync());
    }

    [Fact]
    public async Task LegacyBlankCellCommentsAreComparedAsync()
    {
        using var files = new WorkbookFiles();
        files.CreateLegacy("old.xls", "before");
        files.CreateLegacy("new.xls", "after");

        var result = await files.CompareLegacyAsync();

        Assert.Equal(1, result.Summary.Modified);
        var change = Assert.Single(result.Nodes, node => node.Kind == DiffKind.Modified);
        Assert.Equal("before", change.Metadata["old.comment"]);
        Assert.Equal("after", change.Metadata["new.comment"]);
        Assert.Equal("B1", change.Metadata["new.address"]);
        Assert.Equal(result.TabularSections[0].ColumnMatches.Single(column => column.NewColumn == "B").LogicalId, change.Metadata["column"]);
    }

    [Fact]
    public async Task LegacyHiddenEmptyRowsAndTrailingColumnsAreComparedAsync()
    {
        using var files = new WorkbookFiles();
        files.CreateLegacy("old.xls", "same");
        files.CreateLegacy("new.xls", "same", hideEmptyContext: true);

        var result = await files.CompareLegacyAsync();

        Assert.Equal(2, result.Summary.Changed);
        Assert.Contains(result.Nodes, node => node.StableId.StartsWith("row-hidden:", StringComparison.Ordinal));
        Assert.Contains(result.Nodes, node => node.NewValue == "Z");
    }

    [Fact]
    public async Task EquivalentHiddenColumnRangesDoNotProduceFalseDifferencesAsync()
    {
        using var files = new WorkbookFiles();
        files.Create("old.xlsx", [Number("A1", "1")], columns:
            new Columns(new Column { Min = 3U, Max = 4U, Hidden = true }));
        files.Create("new.xlsx", [Number("A1", "1")], columns:
            new Columns(new Column { Min = 3U, Max = 3U, Hidden = true },
                new Column { Min = 4U, Max = 4U, Hidden = true }));

        Assert.Equal(0, (await files.CompareAsync()).Summary.Changed);
    }

    [Fact]
    public async Task LegacySameStyleIndexWithChangedFontIsComparedByContentAsync()
    {
        using var files = new WorkbookFiles();
        files.CreateLegacy("old.xls", "same", bold: false);
        files.CreateLegacy("new.xls", "same", bold: true);

        Assert.Equal(0, (await files.CompareLegacyAsync()).Summary.Changed);
        Assert.Equal(1, (await files.CompareLegacyAsync(formatting: true)).Summary.Modified);
    }

    private static Cell Number(string address, string value) =>
        new() { CellReference = address, CellValue = new CellValue(value) };

    private static Cell Formula(string address, string formula) =>
        new() { CellReference = address, CellFormula = new CellFormula(formula), CellValue = new CellValue("1") };

    private static Cell Shared(string address, string? formula = null, string? range = null) =>
        new()
        {
            CellReference = address,
            CellFormula = new CellFormula(formula ?? string.Empty)
            {
                FormulaType = CellFormulaValues.Shared,
                SharedIndex = 0U,
                Reference = range
            },
            CellValue = new CellValue("1")
        };

    private static Stylesheet Styles(bool bold)
    {
        var font = new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" });
        if (bold)
        {
            font.AppendChild(new Bold());
        }

        return new Stylesheet(new Fonts(font), new Fills(new Fill()), new Borders(new Border()),
            new CellFormats(new CellFormat { FontId = 0U, FillId = 0U, BorderId = 0U, NumberFormatId = 0U }));
    }

    private sealed class WorkbookFiles : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "Differ.Tests", Guid.NewGuid().ToString("N"));

        public WorkbookFiles() => Directory.CreateDirectory(directory);

        public void Create(string name, IEnumerable<Cell> cells, Stylesheet? styles = null,
            string? sharedText = null, string? themeDarkColor = null, Columns? columns = null)
        {
            using var document = SpreadsheetDocument.Create(Path.Combine(directory, name), SpreadsheetDocumentType.Workbook);
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            if (styles is not null)
            {
                workbook.AddNewPart<WorkbookStylesPart>().Stylesheet = styles;
            }

            if (sharedText is not null)
            {
                workbook.AddNewPart<SharedStringTablePart>().SharedStringTable =
                    new SharedStringTable(new SharedStringItem(new SpreadsheetText(sharedText)));
            }

            if (themeDarkColor is not null)
            {
                workbook.AddNewPart<ThemePart>().Theme = new DocumentFormat.OpenXml.Drawing.Theme(
                    "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Test\">"
                    + "<a:themeElements><a:clrScheme name=\"Test\"><a:dk1><a:srgbClr val=\""
                    + themeDarkColor + "\"/></a:dk1><a:lt1><a:srgbClr val=\"FFFFFF\"/></a:lt1>"
                    + "</a:clrScheme></a:themeElements></a:theme>");
            }

            var sheet = workbook.AddNewPart<WorksheetPart>();
            var rows = cells.GroupBy(cell => uint.Parse(new string(cell.CellReference!.Value!.Where(char.IsDigit).ToArray()),
                    System.Globalization.CultureInfo.InvariantCulture))
                .OrderBy(group => group.Key)
                .Select(group => new Row(group) { RowIndex = group.Key });
            sheet.Worksheet = new Worksheet(new SheetData(rows));
            if (columns is not null)
            {
                sheet.Worksheet.PrependChild(columns);
            }
            workbook.Workbook.AppendChild(new Sheets(new Sheet
            {
                Name = "Sheet1",
                SheetId = 1U,
                Id = workbook.GetIdOfPart(sheet)
            }));
        }

        public void CreateLegacy(string name, string comment, bool hideEmptyContext = false, bool bold = false)
        {
            using var workbook = new HSSFWorkbook();
            var sheet = workbook.CreateSheet("Sheet1");
            var row = sheet.CreateRow(0);
            var visible = row.CreateCell(0);
            visible.SetCellValue("visible");
            var style = workbook.CreateCellStyle();
            var font = workbook.CreateFont();
            font.IsBold = bold;
            style.SetFont(font);
            visible.CellStyle = style;
            var cell = row.CreateCell(1, CellType.Blank);
            var note = sheet.CreateDrawingPatriarch().CreateCellComment(workbook.GetCreationHelper().CreateClientAnchor());
            note.String = workbook.GetCreationHelper().CreateRichTextString(comment);
            cell.CellComment = note;
            sheet.CreateRow(1).ZeroHeight = hideEmptyContext;
            sheet.SetColumnHidden(25, hideEmptyContext);
            using var stream = File.Create(Path.Combine(directory, name));
            workbook.Write(stream);
        }

        public Task<DiffDocument> CompareAsync(bool formatting = false) =>
            new ExcelDiffAdapter().CompareAsync(Request("xlsx", formatting), CancellationToken.None);

        public Task<DiffDocument> CompareLegacyAsync(bool formatting = false) =>
            new XlsDiffAdapter().CompareAsync(Request("xls", formatting), CancellationToken.None);

        private DiffRequest Request(string extension, bool formatting) => new()
        {
            RelativePath = "sample." + extension,
            BaseFilePath = Path.Combine(directory, "old." + extension),
            CurrentFilePath = Path.Combine(directory, "new." + extension),
            Options = new DiffOptions { CompareFormatting = formatting }
        };

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
