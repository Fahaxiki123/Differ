using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.Formats.Word;

internal sealed record WordAlignmentUnit(string Kind, IReadOnlyList<WordBlock> Blocks)
{
    public string Text => Blocks.Count == 1 ? Blocks[0].Text : string.Join('\n', Blocks.Select(block => block.Text));
}

internal sealed record WordAlignmentResult(
    IReadOnlyList<SequenceMatch<WordAlignmentUnit>> Matches,
    bool UsedBoundedFallback);

/// <summary>
/// Word has its own matching policy: headings delimit sections, exact unique
/// paragraphs anchor local windows, and a bounded weighted alignment pairs
/// edits inside those windows. The spreadsheet/binary sequence policy is not
/// changed by the language-specific paragraph heuristic.
/// </summary>
internal static class WordUnitAligner
{
    public static WordAlignmentResult Align(
        IReadOnlyList<WordAlignmentUnit> oldUnits,
        IReadOnlyList<WordAlignmentUnit> newUnits,
        DiffOptions options,
        CancellationToken cancellationToken)
    {
        var oldItems = Prepare(oldUnits, options, cancellationToken);
        var newItems = Prepare(newUnits, options, cancellationToken);
        var matches = new List<SequenceMatch<WordAlignmentUnit>>(Math.Max(oldItems.Length, newItems.Length));
        var budget = DiffPerformanceOptions.MaximumWordSimilarityCandidates;
        var fallback = false;

        var headingAnchors = FindAnchors(0, oldItems.Length, 0, newItems.Length, headingsOnly: true);
        var oldStart = 0;
        var newStart = 0;
        foreach (var (oldIndex, newIndex) in headingAnchors)
        {
            AlignSection(oldStart, oldIndex, newStart, newIndex);
            Add(oldIndex, newIndex);
            oldStart = oldIndex + 1;
            newStart = newIndex + 1;
        }
        AlignSection(oldStart, oldItems.Length, newStart, newItems.Length);
        return new WordAlignmentResult(matches, fallback);

        void AlignSection(int oldFrom, int oldTo, int newFrom, int newTo)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var anchors = FindAnchors(oldFrom, oldTo, newFrom, newTo, headingsOnly: false);
            foreach (var (oldIndex, newIndex) in anchors)
            {
                AlignLocal(oldFrom, oldIndex, newFrom, newIndex);
                Add(oldIndex, newIndex);
                oldFrom = oldIndex + 1;
                newFrom = newIndex + 1;
            }
            AlignLocal(oldFrom, oldTo, newFrom, newTo);
        }

        void AlignLocal(int oldFrom, int oldTo, int newFrom, int newTo)
        {
            while (oldFrom < oldTo && newFrom < newTo && oldItems[oldFrom].Key == newItems[newFrom].Key)
                Add(oldFrom++, newFrom++);
            var suffix = 0;
            while (oldFrom < oldTo - suffix && newFrom < newTo - suffix
                && oldItems[oldTo - suffix - 1].Key == newItems[newTo - suffix - 1].Key)
                suffix++;
            var oldCount = oldTo - oldFrom - suffix;
            var newCount = newTo - newFrom - suffix;
            if ((long)oldCount * newCount > DiffPerformanceOptions.MaximumWordLocalMatrixCells
                || (long)oldCount * newCount > budget)
            {
                // No reliable anchors and no safe budget: preserve both original
                // sequences in one explicit unresolved region, never zip indices.
                fallback = true;
                AddUnmatched(oldFrom, oldCount, newFrom, newCount);
            }
            else if (oldCount == 0 || newCount == 0)
            {
                AddUnmatched(oldFrom, oldCount, newFrom, newCount);
            }
            else
            {
                budget -= oldCount * newCount;
                var scores = new double[oldCount + 1, newCount + 1];
                var choices = new byte[oldCount, newCount];
                for (var left = oldCount - 1; left >= 0; left--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var right = newCount - 1; right >= 0; right--)
                    {
                        var similarity = Similarity(oldItems[oldFrom + left], newItems[newFrom + right]);
                        var paired = similarity >= 0.5
                            ? scores[left + 1, right + 1] + similarity * 2 : double.NegativeInfinity;
                        var deleted = scores[left + 1, right];
                        var added = scores[left, right + 1];
                        if (paired >= deleted && paired >= added)
                        {
                            scores[left, right] = paired;
                            choices[left, right] = 1;
                        }
                        else if (deleted >= added)
                        {
                            scores[left, right] = deleted;
                            choices[left, right] = 2;
                        }
                        else
                        {
                            scores[left, right] = added;
                            choices[left, right] = 3;
                        }
                    }
                }
                var oldPosition = 0;
                var newPosition = 0;
                while (oldPosition < oldCount && newPosition < newCount)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (choices[oldPosition, newPosition])
                    {
                        case 1: Add(oldFrom + oldPosition++, newFrom + newPosition++); break;
                        case 2: Add(oldFrom + oldPosition++, null); break;
                        default: Add(null, newFrom + newPosition++); break;
                    }
                }
                AddUnmatched(oldFrom + oldPosition, oldCount - oldPosition,
                    newFrom + newPosition, newCount - newPosition);
            }
            for (var index = suffix; index > 0; index--) Add(oldTo - index, newTo - index);
        }

        void AddUnmatched(int oldFrom, int oldCount, int newFrom, int newCount)
        {
            for (var index = 0; index < oldCount; index++) Add(oldFrom + index, null);
            for (var index = 0; index < newCount; index++) Add(null, newFrom + index);
        }

        void Add(int? oldIndex, int? newIndex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = oldIndex is null ? SequenceMatchKind.Added
                : newIndex is null ? SequenceMatchKind.Deleted
                : oldItems[oldIndex.Value].Key == newItems[newIndex.Value].Key
                    ? SequenceMatchKind.Equal : SequenceMatchKind.Modified;
            matches.Add(new SequenceMatch<WordAlignmentUnit>(kind,
                oldIndex is { } left ? oldUnits[left] : null,
                newIndex is { } right ? newUnits[right] : null, oldIndex, newIndex));
        }

        List<(int OldIndex, int NewIndex)> FindAnchors(int oldFrom, int oldTo, int newFrom, int newTo, bool headingsOnly)
        {
            var left = new Dictionary<string, int>(StringComparer.Ordinal);
            var right = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = oldFrom; index < oldTo; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (headingsOnly && !oldItems[index].IsHeading) continue;
                if (!left.TryAdd(oldItems[index].Key, index)) left[oldItems[index].Key] = -1;
            }
            for (var index = newFrom; index < newTo; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (headingsOnly && !newItems[index].IsHeading) continue;
                if (!right.TryAdd(newItems[index].Key, index)) right[newItems[index].Key] = -1;
            }
            var candidates = new List<(int OldIndex, int NewIndex)>();
            for (var index = newFrom; index < newTo; index++)
            {
                var key = newItems[index].Key;
                if (right.GetValueOrDefault(key, -1) == index && left.TryGetValue(key, out var position) && position >= 0)
                    candidates.Add((position, index));
            }
            // Patience/LIS selects a monotonic maximum set rather than greedily
            // anchoring a repeated section to the first matching occurrence.
            var tails = new List<int>();
            var previous = new int[candidates.Count];
            for (var index = 0; index < candidates.Count; index++)
            {
                var low = 0;
                var high = tails.Count;
                while (low < high)
                {
                    var middle = (low + high) / 2;
                    if (candidates[tails[middle]].OldIndex < candidates[index].OldIndex) low = middle + 1;
                    else high = middle;
                }
                previous[index] = low == 0 ? -1 : tails[low - 1];
                if (low == tails.Count) tails.Add(index); else tails[low] = index;
            }
            var result = new List<(int OldIndex, int NewIndex)>(tails.Count);
            for (var index = tails.Count == 0 ? -1 : tails[^1]; index >= 0; index = previous[index])
                result.Add(candidates[index]);
            result.Reverse();
            return result;
        }
    }

    internal static string Normalize(string value, DiffOptions options)
    {
        if (options.IgnoreWhitespace) value = string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
        return options.IgnoreCase ? value.ToUpperInvariant() : value;
    }

    private static PreparedUnit[] Prepare(IReadOnlyList<WordAlignmentUnit> units, DiffOptions options, CancellationToken cancellationToken)
    {
        var result = new PreparedUnit[units.Count];
        for (var index = 0; index < units.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unit = units[index];
            var text = Normalize(unit.Text, options);
            var block = unit.Blocks[0];
            var scope = block.Path.Contains('/') ? block.Path[..block.Path.IndexOf('/', StringComparison.Ordinal)] : string.Empty;
            var heading = IsHeading(block);
            result[index] = new PreparedUnit($"{scope}:{unit.Kind}:{heading}:{text}", text, unit.Kind, scope, heading);
        }
        return result;
    }

    private static bool IsHeading(WordBlock block)
    {
        var style = block.Metadata.GetValueOrDefault("word.styleId") ?? string.Empty;
        if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
            || style.StartsWith("标题", StringComparison.Ordinal)) return true;
        var text = block.Text.Trim();
        return text.Length is > 0 and <= 72 && text.StartsWith('第') && text.Contains('章', StringComparison.Ordinal);
    }

    private static double Similarity(PreparedUnit left, PreparedUnit right)
    {
        if (left.Key == right.Key) return 1;
        if (left.Kind != right.Kind || left.Scope != right.Scope || left.IsHeading != right.IsHeading) return 0;
        if (left.Text.Length == 0 || right.Text.Length == 0) return 0;
        var lengthRatio = (double)Math.Min(left.Text.Length, right.Text.Length) / Math.Max(left.Text.Length, right.Text.Length);
        if (lengthRatio < 0.4 && left.Kind != "Table") return 0;
        var a = left.Bigrams;
        var b = right.Bigrams;
        var overlap = a.Count < b.Count ? a.Count(b.Contains) : b.Count(a.Contains);
        var dice = a.Count + b.Count == 0 ? 0 : 2d * overlap / (a.Count + b.Count);
        if (left.Kind == "Table") return dice >= 0.12 ? Math.Max(0.5, dice) : 0;
        // Short strings benefit from bounded edit distance, especially a single
        // changed CJK character with no whitespace. Long paragraphs use sampled
        // character shingles and length evidence, not a quadratic character grid.
        if (Math.Max(left.Text.Length, right.Text.Length) <= 128)
            dice = Math.Max(dice, EditSimilarity(left.Text, right.Text));
        return dice * Math.Sqrt(lengthRatio);
    }

    private static double EditSimilarity(string left, string right)
    {
        Span<int> previous = stackalloc int[right.Length + 1];
        Span<int> current = stackalloc int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++) previous[index] = index;
        for (var oldIndex = 0; oldIndex < left.Length; oldIndex++)
        {
            current[0] = oldIndex + 1;
            for (var newIndex = 0; newIndex < right.Length; newIndex++)
                current[newIndex + 1] = Math.Min(Math.Min(current[newIndex] + 1, previous[newIndex + 1] + 1),
                    previous[newIndex] + (left[oldIndex] == right[newIndex] ? 0 : 1));
            current.CopyTo(previous);
        }
        return 1d - (double)previous[right.Length] / Math.Max(left.Length, right.Length);
    }

    private sealed record PreparedUnit(string Key, string Text, string Kind, string Scope, bool IsHeading)
    {
        private HashSet<string>? _bigrams;
        public HashSet<string> Bigrams => _bigrams ??= CreateBigrams(Text);
        private static HashSet<string> CreateBigrams(string text)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (text.Length < 2) { result.Add(text); return result; }
            var stride = Math.Max(1, text.Length / DiffPerformanceOptions.MaximumWordSimilarityTextSamples);
            for (var index = 0; index < text.Length - 1; index += stride) result.Add(text.Substring(index, 2));
            return result;
        }
    }
}
