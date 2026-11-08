using Differ.Core.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Differ.Formats.Word.Tests;

public sealed class WordNumberingIndentationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NumberingFormatUsesValAndDoesNotRenderWingdingsCodePointsAsMissingGlyphsAsync(bool bullet)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Differ.Word.Numbering", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "marker.docx");
        try
        {
            using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart();
                var numbering = main.AddNewPart<NumberingDefinitionsPart>();
                numbering.Numbering = new Numbering(new AbstractNum(new Level(
                    new NumberingFormat { Val = bullet ? NumberFormatValues.Bullet : NumberFormatValues.None },
                    new LevelText { Val = "\uf0d8" })
                { LevelIndex = 0 })
                { AbstractNumberId = 1 },
                    new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 });
                main.Document = new Document(new Body(Paragraph("标记与正文", null, 1)));
                main.Document.Save();
            }
            var result = await new WordDiffAdapter().CompareAsync(new DiffRequest
            { RelativePath = "marker.docx", BaseFilePath = path, CurrentFilePath = path }, CancellationToken.None);
            var paragraph = Assert.Single(result.Nodes);
            if (bullet) Assert.Equal("• ", paragraph.Metadata["old.word.listMarker"]);
            else Assert.False(paragraph.Metadata.ContainsKey("old.word.listMarker"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task NumberingIndentationIsInheritedAndDirectFirstLineOverridesHangingAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Differ.Word.Numbering", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "numbered.docx");
        try
        {
            using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart();
                var numbering = main.AddNewPart<NumberingDefinitionsPart>();
                numbering.Numbering = new Numbering(new AbstractNum(new Level(
                    new StartNumberingValue { Val = 1 },
                    new NumberingFormat { Val = NumberFormatValues.Decimal },
                    new LevelText { Val = "%1." },
                    new PreviousParagraphProperties(new Indentation { Left = "425", Hanging = "425" }))
                { LevelIndex = 0 })
                { AbstractNumberId = 1 },
                    new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 });
                main.Document = new Document(new Body(
                    Paragraph("标题未直接指定缩进", null, 1),
                    Paragraph("正文直接使用正首行", new Indentation { Left = "0", FirstLine = "142" }, 1),
                    Paragraph("段落明确移除编号", null, 0)));
                main.Document.Save();
            }
            var result = await new WordDiffAdapter().CompareAsync(new DiffRequest
            { RelativePath = "numbered.docx", BaseFilePath = path, CurrentFilePath = path }, CancellationToken.None);
            Assert.Equal(0, result.Summary.Changed);
            var first = Assert.Single(result.Nodes, node => node.OldValue == "标题未直接指定缩进");
            Assert.Equal("425", first.Metadata["old.word.leftTwips"]);
            Assert.Equal("425", first.Metadata["old.word.hangingTwips"]);
            Assert.Equal("1. ", first.Metadata["old.word.listMarker"]);
            var second = Assert.Single(result.Nodes, node => node.OldValue == "正文直接使用正首行");
            Assert.Equal("0", second.Metadata["old.word.leftTwips"]);
            Assert.Equal("142", second.Metadata["old.word.firstLineTwips"]);
            Assert.False(second.Metadata.ContainsKey("old.word.hangingTwips"));
            var cancelled = Assert.Single(result.Nodes, node => node.OldValue == "段落明确移除编号");
            Assert.False(cancelled.Metadata.ContainsKey("old.word.listMarker"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static Paragraph Paragraph(string text, Indentation? indentation, int numberId)
    {
        var properties = new ParagraphProperties(new NumberingProperties(
            new NumberingLevelReference { Val = 0 }, new NumberingId { Val = numberId }));
        if (indentation is not null) properties.Append(indentation);
        return new Paragraph(properties, new Run(new Text(text)));
    }
}
