using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Differ.Core.Abstractions;
using Differ.Core.Algorithms;
using Differ.Core.Diagnostics;
using Differ.Core.Models;
using Differ.Core.Security;
using Differ.Formats.Word.Resources;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Text = DocumentFormat.OpenXml.Wordprocessing.Text;

namespace Differ.Formats.Word;

public sealed class WordDiffAdapter : IFileDiffAdapter
{
    private static readonly CompositeFormat PendingRevisionsFormat =
        CompositeFormat.Parse(WordStrings.AdapterPendingRevisionsFormat);
    private static readonly CompositeFormat UnsupportedBlocksFormat =
        CompositeFormat.Parse(WordStrings.AdapterUnsupportedBlocksFormat);
    private static readonly string[] NestedStructureKeys =
        ["word.tableRow", "word.tableColumn", "word.tableColumnCount",
            "word.tableColumnSpan", "word.verticalMerge", "word.nestedStructure"];

    public FileKind FileKind => FileKind.Word;

    public bool CanHandle(string path)
    {
        return string.Equals(
            Path.GetExtension(path),
            ".docx",
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task<DiffDocument> CompareAsync(
        DiffRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var oldDocument = await ExtractAsync(request.BaseFilePath, cancellationToken)
                .ConfigureAwait(false);
            var newDocument = await ExtractAsync(request.CurrentFilePath, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return WordBlockComparer.BuildDocument(request, oldDocument, newDocument, cancellationToken);
        }
        catch (OpenXmlPackageException exception)
        {
            throw new StructuralParseException(WordStrings.AdapterInvalidPackage, exception);
        }
    }

    private static Task<WordLogicalDocument> ExtractAsync(
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !File.Exists(path)
            || new FileInfo(path).Length == 0)
        {
            return Task.FromResult(new WordLogicalDocument([], []));
        }

        return Task.Run(
            () =>
            {
                using var measurement = ComparisonMetrics.Measure("word.parse", "Word", new FileInfo(path).Length);
                cancellationToken.ThrowIfCancellationRequested();
                OfficePackageGuard.Validate(path);

                using var document = WordprocessingDocument.Open(
                    path,
                    isEditable: false,
                    new OpenSettings { AutoSave = false, MaxCharactersInPart = 32 * 1024 * 1024 });
                var mainPart = document.MainDocumentPart
                    ?? throw new InvalidDataException(WordStrings.AdapterMainPartMissing);
                var wordDocument = mainPart.Document
                    ?? throw new InvalidDataException(WordStrings.AdapterMainDocumentEmpty);
                var blocks = new List<WordBlock>();
                var warnings = new List<string>();
                var styles = WordStyleCatalog.From(
                    mainPart.StyleDefinitionsPart?.Styles);
                var numbering = WordNumberingCatalog.From(
                    mainPart.NumberingDefinitionsPart?.Numbering);
                var numberingState = new Dictionary<(int NumberId, int Level), int>();
                var unsupportedBlocks = 0;

                if (wordDocument.Body is { } body)
                {
                    var paragraphIndex = 0;
                    var tableIndex = 0;
                    ExtractContainer(
                        body,
                        "正文",
                        blocks,
                        styles,
                        numbering,
                        numberingState,
                        ref paragraphIndex,
                        ref tableIndex,
                        ref unsupportedBlocks,
                        cancellationToken);
                }

                var headerIndex = 0;
                foreach (var headerPart in mainPart.HeaderParts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    headerIndex++;
                    if (headerPart.Header is { } header)
                    {
                        var paragraphIndex = 0;
                        var tableIndex = 0;
                        ExtractContainer(
                            header,
                            $"页眉 {headerIndex}",
                            blocks,
                            styles,
                            numbering,
                            numberingState,
                            ref paragraphIndex,
                            ref tableIndex,
                            ref unsupportedBlocks,
                            cancellationToken);
                    }
                }

                var footerIndex = 0;
                foreach (var footerPart in mainPart.FooterParts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    footerIndex++;
                    if (footerPart.Footer is { } footer)
                    {
                        var paragraphIndex = 0;
                        var tableIndex = 0;
                        ExtractContainer(
                            footer,
                            $"页脚 {footerIndex}",
                            blocks,
                            styles,
                            numbering,
                            numberingState,
                            ref paragraphIndex,
                            ref tableIndex,
                            ref unsupportedBlocks,
                            cancellationToken);
                    }
                }

                if (unsupportedBlocks > 0)
                {
                    warnings.Add(string.Format(
                        CultureInfo.CurrentCulture,
                        UnsupportedBlocksFormat,
                        unsupportedBlocks));
                }

                ExtractImages(mainPart, blocks, cancellationToken);

                var insertedCount = wordDocument.Descendants<InsertedRun>().Count();
                var deletedCount = wordDocument.Descendants<DeletedRun>().Count();
                if (insertedCount > 0 || deletedCount > 0)
                {
                    warnings.Add(string.Format(
                        CultureInfo.CurrentCulture,
                        PendingRevisionsFormat,
                        insertedCount,
                        deletedCount));
                }

                return new WordLogicalDocument(blocks, warnings);
            },
            cancellationToken);
    }

    private static void ExtractContainer(
        OpenXmlElement container,
        string prefix,
        List<WordBlock> blocks,
        WordStyleCatalog styles,
        WordNumberingCatalog numbering,
        Dictionary<(int NumberId, int Level), int> numberingState,
        ref int paragraphIndex,
        ref int tableIndex,
        ref int unsupportedBlocks,
        CancellationToken cancellationToken)
    {
        foreach (var child in container.ChildElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (child)
            {
                case Paragraph paragraph:
                    paragraphIndex++;
                    var text = ExtractFinalText(paragraph);
                    var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                    if (!string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(style))
                    {
                        var metadata = WordFormattingExtractor.BuildParagraphMetadata(
                            paragraph,
                            styles,
                            numbering,
                            numberingState);
                        metadata["style"] = style ?? string.Empty;
                        metadata["numbered"] = (
                            paragraph.ParagraphProperties?.NumberingProperties is not null)
                            .ToString();
                        blocks.Add(new WordBlock(
                            "Paragraph",
                            $"{prefix}/段落 {paragraphIndex}",
                            text,
                            metadata));
                    }

                    break;

                case Table table:
                    tableIndex++;
                    ExtractTable(
                        table,
                        $"{prefix}/表格 {tableIndex}",
                        blocks,
                        styles,
                        numbering,
                        numberingState,
                        ref unsupportedBlocks,
                        cancellationToken);
                    break;

                case SdtBlock sdtBlock:
                    // Content controls wrap ordinary block content. Recurse
                    // into the wrapped content instead of silently dropping
                    // it, which real-world documents rely on.
                    if (sdtBlock.SdtContentBlock is { } sdtContent)
                    {
                        ExtractContainer(
                            sdtContent,
                            prefix,
                            blocks,
                            styles,
                            numbering,
                            numberingState,
                            ref paragraphIndex,
                            ref tableIndex,
                            ref unsupportedBlocks,
                            cancellationToken);
                    }

                    break;

                case SectionProperties:
                    break;

                default:
                    unsupportedBlocks++;
                    break;
            }
        }
    }

    private static void ExtractTable(
        Table table,
        string prefix,
        List<WordBlock> blocks,
        WordStyleCatalog styles,
        WordNumberingCatalog numbering,
        Dictionary<(int NumberId, int Level), int> numberingState,
        ref int unsupportedBlocks,
        CancellationToken cancellationToken)
    {
        var rowIndex = 0;
        var rows = ReadTableChildren<TableRow>(table, ref unsupportedBlocks);
        var rowCells = rows.Select(row =>
        {
            var unsupported = 0;
            var cells = ReadTableChildren<TableCell>(row, ref unsupported);
            return (Cells: cells, Unsupported: unsupported);
        }).ToArray();
        unsupportedBlocks += rowCells.Sum(row => row.Unsupported);
        var columnCount = Math.Max(
            table.GetFirstChild<TableGrid>()?.Elements<GridColumn>().Count() ?? 0,
            rowCells.Select(row => row.Cells.Sum(GetColumnSpan))
                .DefaultIfEmpty(1)
                .Max());
        var tableKey = prefix;
        foreach (var row in rowCells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowIndex++;
            var columnIndex = 0;
            foreach (var cell in row.Cells)
            {
                columnIndex++;
                var content = new List<string>();
                var nestedStructure = new List<string>();
                ExtractCellContent(
                    cell, content, nestedStructure, styles, numbering, numberingState,
                    ref unsupportedBlocks, cancellationToken);
                var text = string.Join("\n", content);
                var metadata = WordFormattingExtractor.BuildTableCellMetadata(
                    cell,
                    tableKey,
                    rowIndex,
                    columnIndex,
                    columnCount,
                    styles,
                    numbering,
                    numberingState);
                metadata["word.nestedStructure"] = string.Join(";", nestedStructure);
                var verticalMerge = cell.TableCellProperties?.VerticalMerge;
                metadata["word.verticalMerge"] = verticalMerge is null
                    ? string.Empty
                    : verticalMerge.Val?.InnerText ?? "continue";
                blocks.Add(new WordBlock(
                    "TableCell",
                    $"{prefix}/R{rowIndex}C{columnIndex}",
                    text,
                    metadata));
                columnIndex += GetColumnSpan(cell) - 1;
            }
        }
    }

    private static List<T> ReadTableChildren<T>(
        OpenXmlElement container,
        ref int unsupportedBlocks)
        where T : OpenXmlElement
    {
        var result = new List<T>();
        foreach (var child in container.ChildElements)
        {
            if (child is T item)
            {
                result.Add(item);
            }
            else if (child.LocalName is "sdt" or "sdtContent")
            {
                result.AddRange(ReadTableChildren<T>(child, ref unsupportedBlocks));
            }
            else if (child.LocalName is not ("tblPr" or "tblGrid" or "trPr" or "sdtPr" or "sdtEndPr"))
            {
                unsupportedBlocks++;
            }
        }

        return result;
    }

    private static void ExtractCellContent(
        OpenXmlElement container,
        List<string> content,
        List<string> nestedStructure,
        WordStyleCatalog styles,
        WordNumberingCatalog numbering,
        Dictionary<(int NumberId, int Level), int> numberingState,
        ref int unsupportedBlocks,
        CancellationToken cancellationToken)
    {
        foreach (var child in container.ChildElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (child)
            {
                case Paragraph paragraph:
                    content.Add(ExtractFinalText(paragraph));
                    break;
                case Table nestedTable:
                    var nestedBlocks = new List<WordBlock>();
                    ExtractTable(nestedTable, "nested", nestedBlocks, styles, numbering,
                        numberingState, ref unsupportedBlocks, cancellationToken);
                    content.AddRange(nestedBlocks.Select(block => block.Text));
                    // Keep topology separate from display text: equal words in a
                    // differently shaped nested table are still a structural edit.
                    nestedStructure.Add(string.Join("|", nestedBlocks.Select(block =>
                        string.Join(",", NestedStructureKeys
                            .Select(key => block.Metadata.GetValueOrDefault(key, string.Empty))))));
                    break;
                case SdtBlock sdt when sdt.SdtContentBlock is { } wrapped:
                    ExtractCellContent(wrapped, content, nestedStructure, styles, numbering,
                        numberingState, ref unsupportedBlocks, cancellationToken);
                    break;
                case TableCellProperties:
                    break;
                default:
                    unsupportedBlocks++;
                    break;
            }
        }
    }

    private static int GetColumnSpan(TableCell cell)
    {
        return Math.Max(1, cell.TableCellProperties?.GridSpan?.Val?.Value ?? 1);
    }

    private static void ExtractImages(
        MainDocumentPart mainPart,
        List<WordBlock> blocks,
        CancellationToken cancellationToken)
    {
        var imageIndex = 0;
        foreach (var imagePart in EnumerateImageParts(mainPart)
            .DistinctBy(part => part.Uri))
        {
            cancellationToken.ThrowIfCancellationRequested();
            imageIndex++;
            using var stream = imagePart.GetStream(FileMode.Open, FileAccess.Read);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            blocks.Add(new WordBlock(
                "Image",
                $"图片 {imageIndex}",
                hash,
                new Dictionary<string, string>
                {
                    ["contentType"] = imagePart.ContentType,
                    ["uri"] = imagePart.Uri.ToString()
                }));
        }
    }

    private static IEnumerable<ImagePart> EnumerateImageParts(MainDocumentPart mainPart)
    {
        foreach (var imagePart in mainPart.ImageParts)
        {
            yield return imagePart;
        }

        foreach (var headerPart in mainPart.HeaderParts)
        {
            foreach (var imagePart in headerPart.ImageParts)
            {
                yield return imagePart;
            }
        }

        foreach (var footerPart in mainPart.FooterParts)
        {
            foreach (var imagePart in footerPart.ImageParts)
            {
                yield return imagePart;
            }
        }
    }

    private static string ExtractFinalText(OpenXmlElement element)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var child in element.Descendants())
        {
            switch (child.LocalName)
            {
                case "t":
                    builder.Append(((Text)child).Text);
                    break;
                case "tab":
                    builder.Append('\t');
                    break;
                case "br":
                case "cr":
                    builder.Append('\n');
                    break;
                case "noBreakHyphen":
                    builder.Append('\u2011');
                    break;
                case "softHyphen":
                    builder.Append('\u00ad');
                    break;
            }
        }

        return builder.ToString();
    }
}
