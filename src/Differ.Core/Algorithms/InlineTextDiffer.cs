using System.Globalization;
using System.Text;

namespace Differ.Core.Algorithms;

public sealed record InlineDiffSegment(string Text, bool IsChanged);

public sealed record InlineDiffResult(
    IReadOnlyList<InlineDiffSegment> OldSegments,
    IReadOnlyList<InlineDiffSegment> NewSegments)
{
    public bool IsApproximate { get; init; }
}

public static class InlineTextDiffer
{
    public static InlineDiffResult Compare(string? oldText, string? newText)
    {
        return Compare(oldText, newText, ignoreWhitespace: false, ignoreCase: false);
    }

    public static InlineDiffResult Compare(
        string? oldText,
        string? newText,
        bool ignoreWhitespace,
        bool ignoreCase)
    {
        var originalOldElements = GetTextElements(oldText ?? string.Empty);
        var originalNewElements = GetTextElements(newText ?? string.Empty);
        var oldElements = ignoreWhitespace
            ? originalOldElements.Where(element => !string.IsNullOrWhiteSpace(element)).ToList()
            : originalOldElements;
        var newElements = ignoreWhitespace
            ? originalNewElements.Where(element => !string.IsNullOrWhiteSpace(element)).ToList()
            : originalNewElements;
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        InlineDiffResult result;
        if (oldElements.Count == 0 || newElements.Count == 0)
        {
            result = new InlineDiffResult(
                BuildSingleSegment(oldElements, isChanged: oldElements.Count > 0),
                BuildSingleSegment(newElements, isChanged: newElements.Count > 0));
        }
        else
        {
            result = (long)(oldElements.Count + 1) * (newElements.Count + 1)
                <= DiffPerformanceOptions.MaximumInlineMatrixCells
                ? CompareWithLongestCommonSubsequence(oldElements, newElements, comparison)
                : CompareWithAnchors(oldElements, newElements, comparison);
        }

        return ignoreWhitespace
            ? new InlineDiffResult(
                RestoreIgnoredWhitespace(originalOldElements, result.OldSegments),
                RestoreIgnoredWhitespace(originalNewElements, result.NewSegments))
            { IsApproximate = result.IsApproximate }
            : result;
    }

    /// <summary>
    /// Compares formatted hex lines whose bytes are rendered as "XX " groups.
    /// Tokens are whole bytes, so segment boundaries never split a byte and
    /// only full bytes get highlighted.
    /// </summary>
    public static InlineDiffResult CompareHex(string? oldHex, string? newHex)
    {
        return CompareWithLongestCommonSubsequence(
            SplitHexTokens(oldHex),
            SplitHexTokens(newHex));
    }

    private static List<string> SplitHexTokens(string? hex)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(hex))
        {
            return tokens;
        }

        for (var index = 0; index < hex.Length; index += 3)
        {
            tokens.Add(hex.Substring(index, Math.Min(3, hex.Length - index)));
        }

        return tokens;
    }

    private static InlineDiffResult CompareWithLongestCommonSubsequence(
        List<string> oldElements,
        List<string> newElements,
        StringComparison comparison = StringComparison.Ordinal)
    {
        var table = new int[oldElements.Count + 1, newElements.Count + 1];
        for (var oldIndex = oldElements.Count - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = newElements.Count - 1; newIndex >= 0; newIndex--)
            {
                table[oldIndex, newIndex] = string.Equals(
                    oldElements[oldIndex],
                    newElements[newIndex],
                    comparison)
                    ? table[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(
                        table[oldIndex + 1, newIndex],
                        table[oldIndex, newIndex + 1]);
            }
        }

        var oldSegments = new SegmentAccumulator();
        var newSegments = new SegmentAccumulator();
        var oldPosition = 0;
        var newPosition = 0;
        while (oldPosition < oldElements.Count
            && newPosition < newElements.Count)
        {
            if (string.Equals(
                oldElements[oldPosition],
                newElements[newPosition],
                comparison))
            {
                oldSegments.Append(oldElements[oldPosition], isChanged: false);
                newSegments.Append(newElements[newPosition], isChanged: false);
                oldPosition++;
                newPosition++;
            }
            else if (table[oldPosition + 1, newPosition]
                >= table[oldPosition, newPosition + 1])
            {
                oldSegments.Append(oldElements[oldPosition], isChanged: true);
                oldPosition++;
            }
            else
            {
                newSegments.Append(newElements[newPosition], isChanged: true);
                newPosition++;
            }
        }

        while (oldPosition < oldElements.Count)
        {
            oldSegments.Append(oldElements[oldPosition], isChanged: true);
            oldPosition++;
        }

        while (newPosition < newElements.Count)
        {
            newSegments.Append(newElements[newPosition], isChanged: true);
            newPosition++;
        }

        return new InlineDiffResult(oldSegments.ToSegments(), newSegments.ToSegments());
    }

    private static InlineDiffResult CompareWithAnchors(
        List<string> oldElements,
        List<string> newElements,
        StringComparison comparison)
    {
        var oldResult = new SegmentAccumulator();
        var newResult = new SegmentAccumulator();
        var budget = DiffPerformanceOptions.MaximumInlineAnchorComparisons;
        var approximate = false;
        CompareRange(0, oldElements.Count, 0, newElements.Count, 0);
        return new InlineDiffResult(oldResult.ToSegments(), newResult.ToSegments()) { IsApproximate = approximate };

        void CompareRange(int oldStart, int oldCount, int newStart, int newCount, int depth)
        {
            var prefix = 0;
            while (prefix < oldCount && prefix < newCount && Equal(oldStart + prefix, newStart + prefix)) prefix++;
            Emit(oldStart, prefix, newStart, prefix, false);
            oldStart += prefix; newStart += prefix; oldCount -= prefix; newCount -= prefix;
            var suffix = 0;
            while (suffix < oldCount && suffix < newCount
                && Equal(oldStart + oldCount - suffix - 1, newStart + newCount - suffix - 1)) suffix++;
            var leftCount = oldCount - suffix;
            var rightCount = newCount - suffix;
            if (leftCount == 0 || rightCount == 0)
            {
                Emit(oldStart, leftCount, newStart, rightCount, true);
            }
            else if ((long)(leftCount + 1) * (rightCount + 1) <= DiffPerformanceOptions.MaximumInlineMatrixCells)
            {
                var local = CompareWithLongestCommonSubsequence(oldElements.GetRange(oldStart, leftCount),
                    newElements.GetRange(newStart, rightCount), comparison);
                foreach (var segment in local.OldSegments) oldResult.Append(segment.Text, segment.IsChanged);
                foreach (var segment in local.NewSegments) newResult.Append(segment.Text, segment.IsChanged);
            }
            else if (depth < 16 && FindAnchor(oldStart, leftCount, newStart, rightCount) is { } anchor)
            {
                CompareRange(oldStart, anchor.Old - oldStart, newStart, anchor.New - newStart, depth + 1);
                Emit(anchor.Old, anchor.Length, anchor.New, anchor.Length, false);
                CompareRange(anchor.Old + anchor.Length, oldStart + leftCount - anchor.Old - anchor.Length,
                    anchor.New + anchor.Length, newStart + rightCount - anchor.New - anchor.Length, depth + 1);
            }
            else
            {
                approximate = true;
                Emit(oldStart, leftCount, newStart, rightCount, true);
            }
            Emit(oldStart + leftCount, suffix, newStart + rightCount, suffix, false);
        }

        (int Old, int New, int Length)? FindAnchor(int oldStart, int oldCount, int newStart, int newCount)
        {
            const int width = 16;
            if (Math.Min(oldCount, newCount) < width || budget <= 0) return null;
            var anchors = new Dictionary<int, int>();
            var comparer = comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var stride = Math.Max(width, newCount / 4096);
            for (var offset = 0; offset <= newCount - width; offset += stride)
                anchors.TryAdd(Hash(newElements, newStart + offset), newStart + offset);
            for (var oldIndex = oldStart; oldIndex <= oldStart + oldCount - width && budget > 0; oldIndex++)
            {
                budget--;
                if (!anchors.TryGetValue(Hash(oldElements, oldIndex), out var newIndex)) continue;
                var equal = true;
                for (var index = 0; index < width; index++)
                    if (!Equal(oldIndex + index, newIndex + index)) { equal = false; break; }
                if (!equal) continue;
                var startOld = oldIndex;
                var startNew = newIndex;
                while (startOld > oldStart && startNew > newStart && Equal(startOld - 1, startNew - 1)) { startOld--; startNew--; }
                var length = oldIndex - startOld + width;
                while (startOld + length < oldStart + oldCount && startNew + length < newStart + newCount
                    && Equal(startOld + length, startNew + length)) length++;
                return (startOld, startNew, length);
            }
            return null;

            int Hash(List<string> elements, int start)
            {
                var hash = 17;
                for (var index = 0; index < width; index++) hash = unchecked(hash * 31 + comparer.GetHashCode(elements[start + index]));
                return hash;
            }
        }

        bool Equal(int oldIndex, int newIndex) => string.Equals(oldElements[oldIndex], newElements[newIndex], comparison);

        void Emit(int oldStart, int oldCount, int newStart, int newCount, bool changed)
        {
            for (var index = 0; index < oldCount; index++) oldResult.Append(oldElements[oldStart + index], changed);
            for (var index = 0; index < newCount; index++) newResult.Append(newElements[newStart + index], changed);
        }
    }

    private static List<InlineDiffSegment> RestoreIgnoredWhitespace(
        List<string> originalElements,
        IReadOnlyList<InlineDiffSegment> significantSegments)
    {
        var accumulator = new SegmentAccumulator();
        var segmentIndex = 0;
        var offsetInSegment = 0;
        foreach (var element in originalElements)
        {
            if (string.IsNullOrWhiteSpace(element))
            {
                accumulator.Append(element, isChanged: false);
                continue;
            }

            // The diff contains these same original graphemes with only whitespace
            // omitted. Reinsert spacing without replacing case or splitting a
            // Unicode text element; segment boundaries already follow graphemes.
            var segment = significantSegments[segmentIndex];
            accumulator.Append(element, segment.IsChanged);
            offsetInSegment += element.Length;
            if (offsetInSegment == segment.Text.Length)
            {
                segmentIndex++;
                offsetInSegment = 0;
            }
        }

        return accumulator.ToSegments();
    }

    private static List<InlineDiffSegment> BuildEdgeSegments(
        List<string> elements,
        int prefixLength,
        int suffixLength)
    {
        var result = new List<InlineDiffSegment>(3);
        AppendRange(result, elements.Take(prefixLength), isChanged: false);
        AppendRange(
            result,
            elements.Skip(prefixLength).Take(elements.Count - prefixLength - suffixLength),
            isChanged: true);
        AppendRange(
            result,
            elements.Skip(elements.Count - suffixLength),
            isChanged: false);
        return result;
    }

    private static IReadOnlyList<InlineDiffSegment> BuildSingleSegment(
        List<string> elements,
        bool isChanged)
    {
        if (elements.Count == 0)
        {
            return [];
        }

        return [new InlineDiffSegment(string.Concat(elements), isChanged)];
    }

    private static List<string> GetTextElements(string value)
    {
        var result = new List<string>(value.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            result.Add(enumerator.GetTextElement());
        }

        return result;
    }

    private static void AppendRange(
        List<InlineDiffSegment> segments,
        IEnumerable<string> elements,
        bool isChanged)
    {
        var builder = new StringBuilder();
        foreach (var element in elements)
        {
            builder.Append(element);
        }

        if (builder.Length > 0)
        {
            Append(segments, builder.ToString(), isChanged);
        }
    }

    private static void Append(
        List<InlineDiffSegment> segments,
        string text,
        bool isChanged)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (segments.Count > 0 && segments[^1].IsChanged == isChanged)
        {
            segments[^1] = segments[^1] with
            {
                Text = segments[^1].Text + text
            };
            return;
        }

        segments.Add(new InlineDiffSegment(text, isChanged));
    }

    /// <summary>
    /// Merges consecutive same-kind pieces into segments while backtracking.
    /// The previous record-with replacement re-allocated the whole merged
    /// string for every element, which degraded long changed runs to O(n²).
    /// </summary>
    private sealed class SegmentAccumulator
    {
        private readonly List<InlineDiffSegment> _segments = [];
        private StringBuilder? _builder;
        private bool _currentChanged;

        public void Append(string text, bool isChanged)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (_builder is not null && _currentChanged == isChanged)
            {
                _builder.Append(text);
                return;
            }

            Flush();
            _builder = new StringBuilder(text);
            _currentChanged = isChanged;
        }

        public List<InlineDiffSegment> ToSegments()
        {
            Flush();
            return _segments;
        }

        private void Flush()
        {
            if (_builder is not { Length: > 0 })
            {
                _builder = null;
                return;
            }

            _segments.Add(new InlineDiffSegment(_builder.ToString(), _currentChanged));
            _builder = null;
        }
    }
}
