namespace Differ.Formats.Word;

/// <summary>
/// Parser-agnostic logical document model shared by the Open XML (.docx) and
/// NPOI HWPF (.doc) extractors so both formats run through one block
/// alignment and comparison pipeline.
/// </summary>
internal sealed record WordLogicalDocument(
    IReadOnlyList<WordBlock> Blocks,
    IReadOnlyList<string> Warnings);

internal sealed record WordBlock(
    string Kind,
    string Path,
    string Text,
    IReadOnlyDictionary<string, string> Metadata);
