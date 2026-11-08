using Differ.Core.Abstractions;
using Differ.Core.Files;
using Differ.Core.Models;
using Differ.Core.Services;
using Differ.Formats.Excel;
using Differ.Formats.Text;
using Differ.Formats.Word;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Spreadsheet = DocumentFormat.OpenXml.Spreadsheet;
using Word = DocumentFormat.OpenXml.Wordprocessing;

namespace Differ.IntegrationTests;

public sealed class DiffCoordinatorIntegrationTests
{
    [Fact]
    public async Task CompareRoutesTextWordAndExcelWithoutChangingFixturesAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var textBase = Path.Combine(testRoot, "base.cs");
            var textCurrent = Path.Combine(testRoot, "working.cs");
            var wordBase = Path.Combine(testRoot, "base.docx");
            var wordCurrent = Path.Combine(testRoot, "working.docx");
            var excelBase = Path.Combine(testRoot, "base.xlsx");
            var excelCurrent = Path.Combine(testRoot, "working.xlsx");
            await File.WriteAllTextAsync(textBase, "internal class Demo { }\n");
            await File.WriteAllTextAsync(textCurrent, "internal sealed class Demo { }\n");
            CreateWordFixture(wordBase, changed: false);
            CreateWordFixture(wordCurrent, changed: true);
            CreateExcelFixture(excelBase, changed: false);
            CreateExcelFixture(excelCurrent, changed: true);
            var sources = new Dictionary<string, FileSnapshot>(StringComparer.Ordinal);
            foreach (var path in new[] { textBase, textCurrent, wordBase, wordCurrent, excelBase, excelCurrent })
            {
                sources.Add(path, await FileSnapshot.CreateAsync(path));
            }

            var coordinator = new DiffCoordinator(
                new IFileDiffAdapter[]
                {
                    new WordDiffAdapter(),
                    new ExcelDiffAdapter(),
                    new TextDiffAdapter(new TextFileReader())
                });

            var textResult = await coordinator.CompareAsync(new DiffRequest
            {
                RelativePath = "Demo.cs",
                BaseFilePath = textBase,
                CurrentFilePath = textCurrent
            });
            Assert.Equal(FileKind.Text, textResult.FileKind);
            Assert.True(textResult.Summary.Changed > 0);

            var wordIdentical = await coordinator.CompareAsync(new DiffRequest
            {
                RelativePath = "sample.docx",
                BaseFilePath = wordBase,
                CurrentFilePath = wordBase
            });
            Assert.Equal(FileKind.Word, wordIdentical.FileKind);
            Assert.Equal(0, wordIdentical.Summary.Changed);

            var wordResult = await coordinator.CompareAsync(new DiffRequest
            {
                RelativePath = "sample.docx",
                BaseFilePath = wordBase,
                CurrentFilePath = wordCurrent
            });
            Assert.Equal(FileKind.Word, wordResult.FileKind);
            Assert.True(wordResult.Summary.Changed > 0);
            Assert.Contains(wordResult.Nodes, node => node.NewValue?.Contains("修订内容", StringComparison.Ordinal) == true);

            var excelIdentical = await coordinator.CompareAsync(new DiffRequest
            {
                RelativePath = "sample.xlsx",
                BaseFilePath = excelBase,
                CurrentFilePath = excelBase
            });
            Assert.Equal(FileKind.Excel, excelIdentical.FileKind);
            Assert.Equal(0, excelIdentical.Summary.Changed);

            var excelResult = await coordinator.CompareAsync(new DiffRequest
            {
                RelativePath = "sample.xlsx",
                BaseFilePath = excelBase,
                CurrentFilePath = excelCurrent
            });
            Assert.Equal(FileKind.Excel, excelResult.FileKind);
            Assert.True(excelResult.Summary.Changed > 0);
            Assert.Contains(excelResult.Nodes, node => node.Metadata.TryGetValue("new.formula", out var formula) && formula == "B2+2");
            var sheet = Assert.Single(excelResult.TabularSections);
            Assert.Equal("配置", sheet.Key);
            Assert.Contains(sheet.Rows, row => row.OldIndex == 4 && row.NewIndex == 4 && row.Kind == DiffKind.Unchanged);

            foreach (var source in sources)
            {
                Assert.Equal(source.Value, await FileSnapshot.CreateAsync(source.Key));
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static void CreateWordFixture(string path, bool changed)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Word.Document(new Word.Body(
            Paragraph("公开测试文档 / Public fixture"),
            Paragraph(changed ? "修订内容：预算由100调整为120。" : "原始内容：预算为100。"),
            new Word.Table(new Word.TableRow(
                new Word.TableCell(Paragraph("项目")),
                new Word.TableCell(Paragraph(changed ? "已完成" : "开发中")))),
            Paragraph("保持不变的上下文。")));
        main.Document.Save();
    }

    private static Word.Paragraph Paragraph(string text) => new(new Word.Run(new Word.Text(text)));

    private static void CreateExcelFixture(string path, bool changed)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new Spreadsheet.Workbook();
        var worksheet = workbook.AddNewPart<WorksheetPart>();
        worksheet.Worksheet = new Spreadsheet.Worksheet(new Spreadsheet.SheetData(
            new Spreadsheet.Row(TextCell("A1", "项目"), TextCell("B1", "数量"), TextCell("C1", "计算")) { RowIndex = 1U },
            new Spreadsheet.Row(
                TextCell("A2", "item-01"),
                new Spreadsheet.Cell { CellReference = "B2", CellValue = new Spreadsheet.CellValue(changed ? "120" : "100") },
                new Spreadsheet.Cell
                {
                    CellReference = "C2",
                    CellFormula = new Spreadsheet.CellFormula(changed ? "B2+2" : "B2+1"),
                    CellValue = new Spreadsheet.CellValue(changed ? "122" : "101")
                })
            { RowIndex = 2U },
            new Spreadsheet.Row { RowIndex = 3U },
            new Spreadsheet.Row(TextCell("A4", "item-02"), TextCell("B4", "保留上下文")) { RowIndex = 4U }));
        workbook.Workbook.Append(new Spreadsheet.Sheets(new Spreadsheet.Sheet
        {
            Id = workbook.GetIdOfPart(worksheet),
            SheetId = 1U,
            Name = "配置"
        }));
        worksheet.Worksheet.Save();
        workbook.Workbook.Save();
    }

    private static Spreadsheet.Cell TextCell(string address, string value) => new()
    {
        CellReference = address,
        DataType = Spreadsheet.CellValues.InlineString,
        InlineString = new Spreadsheet.InlineString(new Spreadsheet.Text(value))
    };

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Differ.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
