using System.Text;
using Differ.Core.Abstractions;
using Differ.Core.Models;
using Differ.Core.Security;
using Differ.Formats.Word.Resources;
using NPOI.HWPF;
using NPOI.HWPF.UserModel;

namespace Differ.Formats.Word;

/// <summary>
/// Vendored NPOI HWPF based extractor for the legacy Word 97 .doc binary
/// format. Per the development spec it only promises body paragraphs and
/// table cell text; it feeds the same block model as the Open XML adapter so
/// alignment and diff semantics stay identical.
/// </summary>
public sealed class DocDiffAdapter : IFileDiffAdapter
{
    private static readonly Dictionary<string, string> EmptyMetadata = new();

    static DocDiffAdapter()
    {
        // HWPF falls back to Windows code-page encodings (CP1252 etc.) for
        // 8-bit text pieces, which modern .NET does not register by default.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public FileKind FileKind => FileKind.Word;

    public bool CanHandle(string path)
    {
        return string.Equals(
            Path.GetExtension(path),
            ".doc",
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task<DiffDocument> CompareAsync(
        DiffRequest request,
        CancellationToken cancellationToken)
    {
        var oldDocument = await ExtractAsync(request.BaseFilePath, cancellationToken)
            .ConfigureAwait(false);
        var newDocument = await ExtractAsync(request.CurrentFilePath, cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return WordBlockComparer.BuildDocument(request, oldDocument, newDocument, cancellationToken);
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
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                HWPFDocument? document = null;
                try
                {
                    try
                    {
                        document = new HWPFDocument(stream);
                    }
                    catch (OldWordFileFormatException exception)
                    {
                        throw new StructuralParseException(
                            WordStrings.AdapterDocTooOld,
                            exception);
                    }
                    catch (NPOI.EncryptedDocumentException exception)
                    {
                        throw new StructuralParseException(
                            WordStrings.AdapterDocEncrypted,
                            exception);
                    }
                    catch (Exception exception) when (exception is
                        NPOI.POIFS.FileSystem.NotOLE2FileException
                        or NPOI.POIFS.FileSystem.OfficeXmlFileException)
                    {
                        throw new StructuralParseException(WordStrings.AdapterInvalidDoc, exception);
                    }

                    var blocks = new List<WordBlock>();
                    ExtractBody(document, blocks, cancellationToken);
                    return new WordLogicalDocument(
                        blocks,
                        [WordStrings.AdapterDocScopeOnly]);
                }
                finally
                {
                    // POIDocument exposes Close() instead of IDisposable.
                    document?.Close();
                }
            },
            cancellationToken);
    }

    private static void ExtractBody(
        HWPFDocument document,
        List<WordBlock> blocks,
        CancellationToken cancellationToken)
    {
        var range = document.GetRange();
        var paragraphIndex = 0;
        var tableIndex = 0;
        var index = 0;
        while (index < range.NumParagraphs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var paragraph = range.GetParagraph(index);
            if (paragraph.IsInTable())
            {
                Table table;
                try
                {
                    table = range.GetTable(paragraph);
                }
                catch (ArgumentException)
                {
                    // Row-end paragraphs report IsInTable but are rejected by
                    // GetTable; step over them one by one instead of looping.
                    index++;
                    continue;
                }

                tableIndex++;
                ExtractTable(
                    table,
                    $"正文/表格 {tableIndex}",
                    blocks,
                    cancellationToken);
                index += Math.Max(table.NumParagraphs, 1);
                continue;
            }

            index++;
            paragraphIndex++;
            var text = CleanText(paragraph.Text);
            if (text.Length == 0)
            {
                continue;
            }

            blocks.Add(new WordBlock(
                "Paragraph",
                $"正文/段落 {paragraphIndex}",
                text,
                EmptyMetadata));
        }
    }

    private static void ExtractTable(
        Table table,
        string prefix,
        List<WordBlock> blocks,
        CancellationToken cancellationToken)
    {
        for (var rowIndex = 0; rowIndex < table.NumRows; rowIndex++)
        {
            var row = table.GetRow(rowIndex);
            for (var columnIndex = 0; columnIndex < row.NumCells(); columnIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                blocks.Add(new WordBlock(
                    "TableCell",
                    $"{prefix}/R{rowIndex + 1}C{columnIndex + 1}",
                    CleanText(row.GetCell(columnIndex).Text),
                    new Dictionary<string, string>
                    {
                        ["word.blockKind"] = "table-cell",
                        ["word.tableKey"] = prefix,
                        ["word.tableRow"] = (rowIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["word.tableColumn"] = (columnIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["word.tableColumnCount"] = row.NumCells().ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["word.tableColumnSpan"] = "1"
                    }));
            }
        }
    }

    private static string CleanText(string? raw)
    {
        // HWPF text carries paragraph marks (\r), cell/row end marks (\u0007),
        // field control characters (\u0013..\u0015) and soft breaks (\u000B);
        // the docx extractor never emits those, so drop them here too.
        var builder = new StringBuilder(raw?.Length ?? 0);
        foreach (var character in raw ?? string.Empty)
        {
            if (character == '\r' || character == '\u000B')
            {
                builder.Append('\n');
            }
            else if (character >= ' ' || character == '\t')
            {
                builder.Append(character);
            }
        }

        return builder.ToString().TrimEnd('\n');
    }
}
