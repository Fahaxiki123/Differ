using Differ.Core.Abstractions;
using Differ.Core.Models;
using Differ.Core.Services;
using Differ.Formats.Binary;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Differ.Formats.Word.Tests;

public sealed class WordStructureRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Differ.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedCellContentIsComparedAsync(bool contentControl)
    {
        OpenXmlElement Content(string text) => contentControl
            ? new SdtBlock(new SdtContentBlock(Paragraph(text)))
            : new Table(new TableRow(new TableCell(Paragraph(text))));
        var result = await CompareAsync(
            new Table(new TableRow(new TableCell(Paragraph("outer"), Content("old nested"), Paragraph("end")))),
            new Table(new TableRow(new TableCell(Paragraph("outer"), Content("new nested"), Paragraph("end")))));

        Assert.True(result.Summary.Changed > 0);
        Assert.Contains(result.Nodes, node => node.OldValue?.Contains("old nested", StringComparison.Ordinal) == true);
        Assert.Contains(result.Nodes, node => node.NewValue?.Contains("new nested", StringComparison.Ordinal) == true);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task UnknownCellBlockProducesWarningAsync()
    {
        var result = await CompareAsync(
            new Table(new TableRow(new TableCell(Paragraph("text")))),
            new Table(new TableRow(new TableCell(Paragraph("text"), new AltChunk { Id = "unsupported" }))));
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task ReshapingTableIsStructuralWithFormattingDisabledAsync()
    {
        var result = await CompareAsync(
            new Table(Row("A", "B"), Row("C", "D")),
            new Table(Row("A", "B", "C", "D")));
        Assert.True(result.Summary.Changed > 0);
        Assert.Contains(result.Nodes, node => node.Kind == DiffKind.Modified);
    }

    [Fact]
    public async Task ChangingMergeSpanIsStructuralAsync()
    {
        var result = await CompareAsync(
            new Table(new TableRow(new TableCell(Paragraph("merged")))),
            new Table(new TableRow(new TableCell(new TableCellProperties(new GridSpan { Val = 2 }), Paragraph("merged")))));
        Assert.Equal(1, result.Summary.Modified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InsertingTableRowLeavesFollowingRowsUnchangedAsync(bool compareFormatting)
    {
        var result = await CompareAsync(
            new Table(Row("A", "B"), Row("C", "D")),
            new Table(Row("new 1", "new 2"), Row("A", "B"), Row("C", "D")),
            new DiffOptions { CompareFormatting = compareFormatting });
        Assert.Equal(2, result.Summary.Added);
        Assert.Equal(0, result.Summary.Modified);
        Assert.Equal(0, result.Summary.Deleted);
        Assert.Equal(4, result.Nodes.Count(node => node.Kind == DiffKind.Unchanged));
    }

    [Fact]
    public async Task SplittingTableKeepsContentOwnershipVisibleAsync()
    {
        var result = await CompareAsync(
            new Table(Row("A", "B"), Row("C", "D")),
            new SdtBlock(new SdtContentBlock(new Table(Row("A", "B")), new Table(Row("C", "D")))));
        Assert.True(result.Summary.Changed > 0);
    }

    [Fact]
    public async Task DeletedRowHasItsOwnAlignedDisplayKeyAsync()
    {
        var result = await CompareAsync(
            new Table(Row("removed A", "removed B"), Row("keep A", "keep B")),
            new Table(Row("keep A", "keep B")));
        var removed = result.Nodes.First(node => node.Kind == DiffKind.Deleted);
        var kept = result.Nodes.First(node => node.Kind == DiffKind.Unchanged);
        Assert.NotEqual(removed.Metadata["word.alignedTableRow"], kept.Metadata["word.alignedTableRow"]);
        Assert.Equal("2", kept.Metadata["old.word.tableRow"]);
        Assert.Equal("1", kept.Metadata["new.word.tableRow"]);
    }

    [Fact]
    public async Task RowAndCellContentControlsDoNotHideTableTextAsync()
    {
        var before = new Table(new SdtRow(new SdtContentRow(new TableRow(
            new SdtCell(new SdtContentCell(new TableCell(Paragraph("old text"))))))));
        var after = new Table(new SdtRow(new SdtContentRow(new TableRow(
            new SdtCell(new SdtContentCell(new TableCell(Paragraph("new text"))))))));
        var result = await CompareAsync(before, after);
        Assert.True(result.Summary.Changed > 0);
        Assert.Contains(result.Nodes, node => node.NewValue == "new text");
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task WhitespaceIsOnlyIgnoredWhenRequestedAsync()
    {
        Assert.True((await CompareAsync(Paragraph("one two"), Paragraph("one  two"))).Summary.Changed > 0);
        Assert.Equal(0, (await CompareAsync(Paragraph("one two"), Paragraph("one  two"),
            new DiffOptions { IgnoreWhitespace = true })).Summary.Changed);
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".doc")]
    public async Task DamagedWordFallsBackToActualByteComparisonAsync(string extension)
    {
        Directory.CreateDirectory(_root);
        var oldPath = Path.Combine(_root, "old" + extension);
        var newPath = Path.Combine(_root, "new" + extension);
        await File.WriteAllTextAsync(oldPath, "not a document - old");
        await File.WriteAllTextAsync(newPath, "not a document - new");
        var coordinator = new DiffCoordinator(new IFileDiffAdapter[]
            { new WordDiffAdapter(), new DocDiffAdapter(), new BinaryDiffAdapter() });
        var result = await coordinator.CompareAsync(new DiffRequest
        { RelativePath = "broken" + extension, BaseFilePath = oldPath, CurrentFilePath = newPath });
        Assert.Equal(FileKind.Binary, result.FileKind);
        Assert.True(result.Summary.Changed > 0);
        Assert.NotEmpty(result.Warnings);
    }

    private async Task<DiffDocument> CompareAsync(OpenXmlElement before, OpenXmlElement after, DiffOptions? options = null)
    {
        Directory.CreateDirectory(_root);
        var oldPath = Path.Combine(_root, "old.docx");
        var newPath = Path.Combine(_root, "new.docx");
        Create(oldPath, before);
        Create(newPath, after);
        return await new WordDiffAdapter().CompareAsync(new DiffRequest
        {
            RelativePath = "document.docx",
            BaseFilePath = oldPath,
            CurrentFilePath = newPath,
            Options = options ?? new DiffOptions()
        }, CancellationToken.None);
    }

    private static void Create(string path, OpenXmlElement block)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new Document(new Body(block, new SectionProperties()));
        mainPart.Document.Save();
    }

    private static Paragraph Paragraph(string text) => new(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    private static TableRow Row(params string[] cells) => new(cells.Select(text => new TableCell(Paragraph(text))));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
