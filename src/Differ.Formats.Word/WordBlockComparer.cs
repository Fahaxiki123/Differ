using System.Globalization;
using Differ.Core.Algorithms;
using Differ.Core.Diagnostics;
using Differ.Core.Models;
using Differ.Formats.Word.Resources;

namespace Differ.Formats.Word;

/// <summary>
/// Block alignment and diff pipeline shared by every Word-family extractor.
/// </summary>
internal static class WordBlockComparer
{
    public static DiffDocument BuildDocument(
        DiffRequest request,
        WordLogicalDocument oldDocument,
        WordLogicalDocument newDocument,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var measurement = ComparisonMetrics.Measure("word.match", "Word", oldDocument.Blocks.Count + newDocument.Blocks.Count);
        var matches = AlignBlocks(oldDocument.Blocks, newDocument.Blocks, request.Options, cancellationToken, out var usedFallback);
        if (usedFallback)
            ComparisonMetrics.Log.Stage("word.match-bounded-fallback", "Word", 0, 0, matches.Count, 0, 0, 0, Environment.WorkingSet);
        cancellationToken.ThrowIfCancellationRequested();

        var nodes = new List<DiffNode>(matches.Count);
        for (var index = 0; index < matches.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var aligned = matches[index];
            var match = aligned.Match;
            var oldBlock = match.OldItem;
            var newBlock = match.NewItem;
            var kind = MapKind(match.Kind);
            if (kind == DiffKind.Unchanged
                && (!StructureEqual(oldBlock?.Metadata, newBlock?.Metadata)
                    || (request.Options.CompareFormatting
                        && !MetadataEqual(oldBlock?.Metadata, newBlock?.Metadata))))
            {
                kind = DiffKind.Modified;
            }

            nodes.Add(new DiffNode
            {
                StableId =
                    $"word:{match.OldIndex?.ToString(CultureInfo.InvariantCulture) ?? "-"}:"
                    + $"{match.NewIndex?.ToString(CultureInfo.InvariantCulture) ?? "-"}",
                Path = newBlock?.Path ?? oldBlock?.Path ?? $"Word 块 {index + 1}",
                Kind = kind,
                OldValue = oldBlock?.Text,
                NewValue = newBlock?.Text,
                OldIndex = match.OldIndex is null ? null : match.OldIndex + 1,
                NewIndex = match.NewIndex is null ? null : match.NewIndex + 1,
                Metadata = MergeMetadata(oldBlock?.Metadata, newBlock?.Metadata,
                    aligned.TableKey, aligned.TableRow)
            });
        }

        AssignChangeGroups(nodes, cancellationToken);
        if (!request.Options.IncludeUnchanged) nodes.RemoveAll(node => node.Kind == DiffKind.Unchanged);
        var warnings = oldDocument.Warnings
            .Concat(newDocument.Warnings)
            .Concat(usedFallback ? [WordStrings.AdapterBoundedAlignment] : [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var document = new DiffDocument
        {
            RelativePath = request.RelativePath,
            FileKind = FileKind.Word,
            BaseRevision = request.BaseRevision,
            Nodes = nodes,
            Summary = DiffSummary.FromNodes(nodes),
            Warnings = warnings
        };
        cancellationToken.ThrowIfCancellationRequested();
        return document;
    }

    private static List<AlignedBlock> AlignBlocks(
        IReadOnlyList<WordBlock> oldBlocks,
        IReadOnlyList<WordBlock> newBlocks,
        DiffOptions options,
        CancellationToken cancellationToken,
        out bool usedFallback)
    {
        var oldIndices = oldBlocks.Select((block, index) => (block, index))
            .ToDictionary(pair => pair.block, pair => pair.index,
                (IEqualityComparer<WordBlock>)ReferenceEqualityComparer.Instance);
        var newIndices = newBlocks.Select((block, index) => (block, index))
            .ToDictionary(pair => pair.block, pair => pair.index,
                (IEqualityComparer<WordBlock>)ReferenceEqualityComparer.Instance);
        var result = new List<AlignedBlock>();
        cancellationToken.ThrowIfCancellationRequested();
        var alignment = WordUnitAligner.Align(GroupUnits(oldBlocks), GroupUnits(newBlocks), options, cancellationToken);
        var units = alignment.Matches;
        usedFallback = alignment.UsedBoundedFallback;
        cancellationToken.ThrowIfCancellationRequested();
        var unitIndex = 0;
        foreach (var unit in units)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tableKey = $"table:{unitIndex++}";
            if (unit.OldItem?.Kind == "Table" && unit.NewItem?.Kind == "Table")
            {
                var rows = SequenceAligner.Align(
                    GroupRows(unit.OldItem.Blocks), GroupRows(unit.NewItem.Blocks),
                    row => Signature(row, options));
                cancellationToken.ThrowIfCancellationRequested();
                var rowIndex = 0;
                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tableRow = (++rowIndex).ToString(CultureInfo.InvariantCulture);
                    // Match a row before its cells. Absolute row numbers are only
                    // presentation data, so inserting a row cannot modify its tail.
                    var oldCells = row.OldItem ?? [];
                    var newCells = row.NewItem ?? [];
                    for (var index = 0; index < Math.Max(oldCells.Count, newCells.Count); index++)
                    {
                        Add(index < oldCells.Count ? oldCells[index] : null,
                            index < newCells.Count ? newCells[index] : null, tableKey, tableRow);
                    }
                }
            }
            else if (unit.OldItem?.Kind == "Table" || unit.NewItem?.Kind == "Table")
            {
                foreach (var block in unit.OldItem?.Blocks ?? [])
                {
                    Add(block, null, tableKey, block.Metadata.GetValueOrDefault("word.tableRow"));
                }

                foreach (var block in unit.NewItem?.Blocks ?? [])
                {
                    Add(null, block, tableKey, block.Metadata.GetValueOrDefault("word.tableRow"));
                }
            }
            else
            {
                Add(unit.OldItem?.Blocks[0], unit.NewItem?.Blocks[0]);
            }
        }

        return result;

        void Add(WordBlock? left, WordBlock? right, string? tableKey = null, string? tableRow = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = left is null ? SequenceMatchKind.Added
                : right is null ? SequenceMatchKind.Deleted
                : string.Equals(Normalize(left.Text, options), Normalize(right.Text, options), StringComparison.Ordinal)
                    ? SequenceMatchKind.Equal : SequenceMatchKind.Modified;
            result.Add(new AlignedBlock(new SequenceMatch<WordBlock>(kind, left, right,
                left is null ? null : oldIndices[left], right is null ? null : newIndices[right]),
                tableKey, tableRow));
        }
    }

    private static List<WordAlignmentUnit> GroupUnits(IReadOnlyList<WordBlock> blocks)
    {
        var units = new List<WordAlignmentUnit>();
        for (var index = 0; index < blocks.Count;)
        {
            var block = blocks[index++];
            if (block.Metadata.TryGetValue("word.tableKey", out var tableKey))
            {
                var table = new List<WordBlock> { block };
                while (index < blocks.Count
                    && blocks[index].Metadata.GetValueOrDefault("word.tableKey") == tableKey)
                {
                    table.Add(blocks[index++]);
                }

                units.Add(new WordAlignmentUnit("Table", table));
            }
            else
            {
                units.Add(new WordAlignmentUnit(block.Kind, [block]));
            }
        }

        return units;
    }

    private static List<IReadOnlyList<WordBlock>> GroupRows(IReadOnlyList<WordBlock> blocks)
    {
        return blocks.GroupBy(block => block.Metadata.GetValueOrDefault("word.tableRow"))
            .Select(row => (IReadOnlyList<WordBlock>)row.ToArray()).ToList();
    }

    private static string Signature(IReadOnlyList<WordBlock> blocks, DiffOptions options)
    {
        return string.Concat(blocks.Select(block =>
        {
            var text = Normalize(block.Text, options);
            return $"{text.Length}:{text}";
        }));
    }

    private static string Normalize(string value, DiffOptions options)
    {
        return WordUnitAligner.Normalize(value, options);
    }

    private static bool StructureEqual(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        return StructuralKeys.All(key => string.Equals(
            left?.GetValueOrDefault(key), right?.GetValueOrDefault(key), StringComparison.Ordinal));
    }

    private static readonly string[] StructuralKeys =
        ["word.tableColumn", "word.tableColumnCount", "word.tableColumnSpan",
            "word.verticalMerge", "word.nestedStructure"];

    private sealed record AlignedBlock(SequenceMatch<WordBlock> Match, string? TableKey, string? TableRow);

    private static void AssignChangeGroups(List<DiffNode> nodes, CancellationToken cancellationToken)
    {
        var index = 0;
        while (index < nodes.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsChangedParagraph(nodes[index])) { index++; continue; }
            var start = index;
            while (index < nodes.Count && IsChangedParagraph(nodes[index])) index++;
            var oldCount = 0;
            var newCount = 0;
            for (var cursor = start; cursor < index; cursor++)
            {
                var node = nodes[cursor];
                if (node.OldIndex is not null) oldCount++;
                if (node.NewIndex is not null) newCount++;
            }
            var rewrite = oldCount > 0 && newCount > 0
                && Math.Min(oldCount, newCount) * 2 < Math.Max(oldCount, newCount);
            var splitOrMerge = oldCount != newCount && oldCount > 0 && newCount > 0
                && ConcatenatedTextEqual(nodes, start, index, cancellationToken);
            if (splitOrMerge || rewrite)
            {
                Mark(start, index, splitOrMerge ? "split-merge" : "unresolved");
                continue;
            }
            // Reliable one-to-one paragraphs retain their own horizontal rows.
            // Only the intervals without established correspondence are grouped.
            var position = start;
            while (position < index)
            {
                if (nodes[position].OldIndex is not null && nodes[position].NewIndex is not null) { position++; continue; }
                var groupStart = position++;
                while (position < index && (nodes[position].OldIndex is null || nodes[position].NewIndex is null)) position++;
                if (position - groupStart > 1) Mark(groupStart, position, "unresolved");
            }
        }

        static bool IsChangedParagraph(DiffNode node) => node.Kind != DiffKind.Unchanged
            && !node.Metadata.ContainsKey("word.alignedTableKey")
            && (node.Metadata.GetValueOrDefault("old.word.blockKind") == "paragraph"
                || node.Metadata.GetValueOrDefault("new.word.blockKind") == "paragraph");

        void Mark(int start, int end, string confidence)
        {
            var key = $"word:group:{start}";
            for (var position = start; position < end; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = new Dictionary<string, string>(nodes[position].Metadata, StringComparer.Ordinal)
                {
                    ["word.changeGroup"] = key,
                    ["word.groupCorrespondence"] = confidence
                };
                nodes[position] = nodes[position] with { Metadata = metadata };
            }
        }
    }

    private static bool ConcatenatedTextEqual(List<DiffNode> nodes, int start, int end, CancellationToken cancellationToken)
    {
        var left = start;
        var right = start;
        var leftOffset = 0;
        var rightOffset = 0;
        // Compare through paragraph boundaries without allocating two complete
        // document strings, including a one-to-many split with many fragments.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (left < end && leftOffset == (nodes[left].OldValue?.Length ?? 0)) { left++; leftOffset = 0; }
            while (right < end && rightOffset == (nodes[right].NewValue?.Length ?? 0)) { right++; rightOffset = 0; }
            if (left == end || right == end) return left == end && right == end;
            var leftText = nodes[left].OldValue!;
            var rightText = nodes[right].NewValue!;
            var count = Math.Min(leftText.Length - leftOffset, rightText.Length - rightOffset);
            if (!leftText.AsSpan(leftOffset, count).SequenceEqual(rightText.AsSpan(rightOffset, count))) return false;
            leftOffset += count;
            rightOffset += count;
        }
    }

    private static DiffKind MapKind(SequenceMatchKind kind)
    {
        return kind switch
        {
            SequenceMatchKind.Equal => DiffKind.Unchanged,
            SequenceMatchKind.Added => DiffKind.Added,
            SequenceMatchKind.Deleted => DiffKind.Deleted,
            SequenceMatchKind.Modified => DiffKind.Modified,
            _ => DiffKind.Unsupported
        };
    }

    private static bool MetadataEqual(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        left ??= new Dictionary<string, string>();
        right ??= new Dictionary<string, string>();
        // Coordinate metadata describes location, not formatting. An inserted
        // row/table may change these coordinates without changing any style.
        var leftFormat = left.Where(pair => !IsLocation(pair.Key)).ToArray();
        var rightFormat = right.Where(pair => !IsLocation(pair.Key)).ToArray();
        return leftFormat.Length == rightFormat.Length
            && leftFormat.All(pair =>
                right.TryGetValue(pair.Key, out var value)
                && string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    private static bool IsLocation(string key) =>
        key is "word.tableKey" or "word.tableRow";

    private static Dictionary<string, string> MergeMetadata(
        IReadOnlyDictionary<string, string>? oldMetadata,
        IReadOnlyDictionary<string, string>? newMetadata,
        string? tableKey,
        string? tableRow)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in oldMetadata ?? new Dictionary<string, string>())
        {
            merged[$"old.{pair.Key}"] = pair.Value;
        }

        foreach (var pair in newMetadata ?? new Dictionary<string, string>())
        {
            merged[$"new.{pair.Key}"] = pair.Value;
        }

        if (tableKey is not null && tableRow is not null)
        {
            merged["word.alignedTableKey"] = tableKey;
            merged["word.alignedTableRow"] = tableRow;
        }

        return merged;
    }
}
