using Differ.Core.Abstractions;
using Differ.Core.Models;
using Differ.Formats.Text;
using Differ.Formats.Word;

namespace Differ.Formats.Rtf;

/// <summary>
/// RTF adapter. Control words are stripped and paragraph text is fed into
/// the shared Word block pipeline, so .rtf documents render in the same
/// Word-style document view as .docx. Content detected as binary falls back
/// to the byte-level adapter.
/// </summary>
public sealed class RtfDiffAdapter(ITextFileReader reader) : IFileDiffAdapter
{
    public FileKind FileKind => FileKind.Word;

    public bool CanHandle(string path)
    {
        return string.Equals(
            Path.GetExtension(path),
            ".rtf",
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task<DiffDocument> CompareAsync(
        DiffRequest request,
        CancellationToken cancellationToken)
    {
        var oldContent = await reader.ReadAsync(request.BaseFilePath, cancellationToken)
            .ConfigureAwait(false);
        var newContent = await reader.ReadAsync(request.CurrentFilePath, cancellationToken)
            .ConfigureAwait(false);

        if (oldContent.IsBinary || newContent.IsBinary)
        {
            return new DiffDocument
            {
                RelativePath = request.RelativePath,
                FileKind = FileKind.Binary,
                BaseRevision = request.BaseRevision,
                Nodes = [],
                Summary = new DiffSummary(0, 0, 0, 0, 0, 0, 0),
                Warnings = []
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        var oldDocument = BuildDocument(
            RtfParser.ExtractParagraphs(oldContent.Text, cancellationToken),
            oldContent.Warnings);
        var newDocument = BuildDocument(
            RtfParser.ExtractParagraphs(newContent.Text, cancellationToken),
            newContent.Warnings);
        return WordBlockComparer.BuildDocument(request, oldDocument, newDocument, cancellationToken);
    }

    private static WordLogicalDocument BuildDocument(
        List<string> paragraphs,
        IReadOnlyList<string> warnings)
    {
        var blocks = new List<WordBlock>(paragraphs.Count);
        for (var index = 0; index < paragraphs.Count; index++)
        {
            blocks.Add(new WordBlock(
                "Paragraph",
                $"正文/段落 {index + 1}",
                paragraphs[index],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["word.blockKind"] = "paragraph"
                }));
        }

        return new WordLogicalDocument(blocks, warnings);
    }
}
