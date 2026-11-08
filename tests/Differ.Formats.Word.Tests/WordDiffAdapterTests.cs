using Differ.Core.Files;
using Differ.Core.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Differ.Formats.Word.Tests;

public sealed class WordDiffAdapterTests
{
    [Fact]
    public async Task CompareGeneratedFixtureCopiedToTempIsUnchangedAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var source = CreateRepresentativeDocument(testRoot);
            var sourceBefore = await FileSnapshot.CreateAsync(source);
            var oldPath = Path.Combine(testRoot, "base.docx");
            var newPath = Path.Combine(testRoot, "working.docx");
            File.Copy(source, oldPath);
            File.Copy(source, newPath);
            var result = await new WordDiffAdapter().CompareAsync(
                new DiffRequest
                {
                    RelativePath = Path.GetFileName(source),
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath,
                    BaseRevision = "fixture"
                },
                CancellationToken.None);

            Assert.Equal(FileKind.Word, result.FileKind);
            Assert.Equal(0, result.Summary.Changed);
            Assert.Equal(sourceBefore, await FileSnapshot.CreateAsync(source));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CompareDetectsParagraphChangeInCopiedFixtureAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var source = CreateRepresentativeDocument(testRoot);
            var sourceBefore = await FileSnapshot.CreateAsync(source);
            var oldPath = Path.Combine(testRoot, "base.docx");
            var newPath = Path.Combine(testRoot, "working.docx");
            File.Copy(source, oldPath);
            File.Copy(source, newPath);

            using (var document = WordprocessingDocument.Open(newPath, isEditable: true))
            {
                var mainPart = Assert.IsType<MainDocumentPart>(document.MainDocumentPart);
                var wordDocument = Assert.IsType<Document>(mainPart.Document);
                var text = wordDocument
                    .Descendants<Text>()
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value.Text));
                Assert.NotNull(text);
                text!.Text += "【Differ 测试变更】";
                wordDocument.Save();
            }

            var result = await new WordDiffAdapter().CompareAsync(
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
                node => node.NewValue?.Contains(
                    "Differ 测试变更",
                    StringComparison.Ordinal) == true);
            Assert.Equal(sourceBefore, await FileSnapshot.CreateAsync(source));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ComparePreservesPresentationMetadataForListsAndTablesAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var source = CreateRepresentativeDocument(testRoot);
            var oldPath = Path.Combine(testRoot, "base.docx");
            var newPath = Path.Combine(testRoot, "working.docx");
            File.Copy(source, oldPath);
            File.Copy(source, newPath);

            var result = await new WordDiffAdapter().CompareAsync(
                new DiffRequest
                {
                    RelativePath = Path.GetFileName(source),
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath
                },
                CancellationToken.None);

            Assert.Contains(
                result.Nodes,
                node => node.Metadata.TryGetValue(
                    "old.word.listMarker",
                    out var marker)
                    && !string.IsNullOrWhiteSpace(marker));
            Assert.Contains(
                result.Nodes,
                node => node.Metadata.TryGetValue(
                    "old.word.blockKind",
                    out var blockKind)
                    && string.Equals(
                        blockKind,
                        "table-cell",
                        StringComparison.Ordinal)
                    && node.Metadata.ContainsKey("old.word.tableRow")
                    && node.Metadata.ContainsKey("old.word.tableColumn")
                    && node.Metadata.ContainsKey("old.word.tableColumnCount"));
            Assert.Contains(
                result.Nodes,
                node => node.Metadata.TryGetValue(
                    "old.word.styleId",
                    out var styleId)
                    && !string.IsNullOrWhiteSpace(styleId));
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
            var source = CreateRepresentativeDocument(testRoot);
            var sourceBefore = await FileSnapshot.CreateAsync(source);
            var oldPath = Path.Combine(testRoot, "base.docx");
            var newPath = Path.Combine(testRoot, "working.docx");
            File.Copy(source, oldPath);
            File.Copy(source, newPath);

            using (var document = WordprocessingDocument.Open(newPath, isEditable: true))
            {
                var mainPart = Assert.IsType<MainDocumentPart>(document.MainDocumentPart);
                var wordDocument = Assert.IsType<Document>(mainPart.Document);
                var paragraph = wordDocument
                    .Descendants<Paragraph>()
                    .First(value => value.Descendants<Text>().Any());
                var run = paragraph
                    .Descendants<Run>()
                    .First(value => value.Descendants<Text>().Any());
                var properties = run.RunProperties
                    ?? run.PrependChild(new RunProperties());
                properties.FontSize = new FontSize { Val = "99" };
                wordDocument.Save();
            }

            var adapter = new WordDiffAdapter();
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
    public async Task CompareTreatsZeroLengthBaseAsEmptyDocumentAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var source = CreateRepresentativeDocument(testRoot);
            var emptyBase = Path.Combine(testRoot, "base.docx");
            var current = Path.Combine(testRoot, "working.docx");
            await File.WriteAllBytesAsync(emptyBase, []);
            File.Copy(source, current);

            var result = await new WordDiffAdapter().CompareAsync(
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
    public async Task CompareExtractsParagraphsWrappedInContentControlsAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(testRoot, "base.docx");
            var newPath = Path.Combine(testRoot, "working.docx");
            CreateDocument(oldPath, wrapInContentControl: false, "内容控件段落");
            CreateDocument(newPath, wrapInContentControl: true, "内容控件段落");

            var result = await new WordDiffAdapter().CompareAsync(
                new DiffRequest
                {
                    RelativePath = "sdt.docx",
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath
                },
                CancellationToken.None);

            Assert.Equal(0, result.Summary.Changed);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CompareDetectsChangesInsideContentControlsAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(testRoot, "base.docx");
            var newPath = Path.Combine(testRoot, "working.docx");
            CreateDocument(oldPath, wrapInContentControl: true, "内容控件段落");
            CreateDocument(newPath, wrapInContentControl: true, "内容控件段落已修改");

            var result = await new WordDiffAdapter().CompareAsync(
                new DiffRequest
                {
                    RelativePath = "sdt.docx",
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath
                },
                CancellationToken.None);

            Assert.True(result.Summary.Changed > 0);
            Assert.Contains(
                result.Nodes,
                node => node.NewValue?.Contains("已修改", StringComparison.Ordinal) == true);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CompareWarnsAboutUnrecognizedBlockLevelStructuresAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var oldPath = Path.Combine(testRoot, "base.docx");
            var newPath = Path.Combine(testRoot, "working.docx");
            CreateDocument(oldPath, wrapInContentControl: false, "正文段落");
            CreateDocument(
                newPath,
                wrapInContentControl: false,
                withUnsupportedBlock: true,
                "正文段落");

            var result = await new WordDiffAdapter().CompareAsync(
                new DiffRequest
                {
                    RelativePath = "unsupported.docx",
                    BaseFilePath = oldPath,
                    CurrentFilePath = newPath
                },
                CancellationToken.None);

            Assert.Contains(
                result.Warnings,
                warning => warning.Contains("块级结构", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("sample.docx", true)]
    [InlineData("sample.doc", false)]
    [InlineData("sample.xlsx", false)]
    public void CanHandleOnlyAcceptsDocx(string path, bool expected)
    {
        Assert.Equal(expected, new WordDiffAdapter().CanHandle(path));
    }

    private static void CreateDocument(
        string path,
        bool wrapInContentControl,
        params string[] paragraphTexts)
    {
        CreateDocument(path, wrapInContentControl, withUnsupportedBlock: false, paragraphTexts);
    }

    private static void CreateDocument(
        string path,
        bool wrapInContentControl,
        bool withUnsupportedBlock,
        params string[] paragraphTexts)
    {
        using var document = WordprocessingDocument.Create(
            path,
            WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        var body = new Body();
        foreach (var text in paragraphTexts)
        {
            var paragraph = new Paragraph(
                new Run(new Text(text)));
            if (wrapInContentControl)
            {
                body.Append(new SdtBlock(
                    new SdtProperties(),
                    new SdtContentBlock(paragraph)));
            }
            else
            {
                body.Append(paragraph);
            }
        }

        if (withUnsupportedBlock)
        {
            body.Append(new CommentRangeStart { Id = "1" });
        }

        body.Append(new SectionProperties());
        mainPart.Document = new Document(body);
        mainPart.Document.Save();
    }

    private static string CreateRepresentativeDocument(string testRoot)
    {
        var path = Path.Combine(testRoot, "source.docx");
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new Styles(
            new Style(new StyleName { Val = "Normal" })
            {
                Type = StyleValues.Paragraph,
                StyleId = "Normal",
                Default = true
            },
            new Style(
                new StyleName { Val = "Heading 1" },
                new BasedOn { Val = "Normal" },
                new StyleParagraphProperties(new OutlineLevel { Val = 0 }))
            {
                Type = StyleValues.Paragraph,
                StyleId = "Heading1"
            });

        var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();
        numberingPart.Numbering = new Numbering(
            new AbstractNum(
                new MultiLevelType { Val = MultiLevelValues.SingleLevel },
                new Level(
                    new StartNumberingValue { Val = 1 },
                    new NumberingFormat { Val = NumberFormatValues.Decimal },
                    new LevelText { Val = "%1." })
                { LevelIndex = 0 })
            { AbstractNumberId = 1 },
            new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 });

        // These constructed structures replace private business documents while
        // retaining the styles, list markers and table metadata under test.
        mainPart.Document = new Document(new Body(
            new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                new Run(new RunProperties(new FontSize { Val = "28" }), new Text("合成文档 Synthetic document"))),
            new Paragraph(new Run(new Text("中文与 English 正文，保留未修改上下文。"))),
            new Paragraph(
                new ParagraphProperties(
                    new ParagraphStyleId { Val = "Normal" },
                    new NumberingProperties(
                        new NumberingLevelReference { Val = 0 },
                        new NumberingId { Val = 1 })),
                new Run(new Text("列表项目"))),
            new Table(
                new TableGrid(new GridColumn { Width = "2400" }, new GridColumn { Width = "2400" }),
                new TableRow(
                    new TableCell(new Paragraph(new Run(new Text("名称")))),
                    new TableCell(new Paragraph(new Run(new Text("数量"))))),
                new TableRow(
                    new TableCell(new Paragraph(new Run(new Text("示例项目")))),
                    new TableCell(new Paragraph(new Run(new Text("12")))))),
            new SectionProperties()));
        mainPart.Document.Save();
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
}
