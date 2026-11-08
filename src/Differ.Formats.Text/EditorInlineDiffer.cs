using System.Globalization;
using Differ.Core.Algorithms;

namespace Differ.Formats.Text;

/// <summary>Trim grapheme-safe common edges before budgeting the expensive inner comparison.</summary>
public static class EditorInlineDiffer
{
    public static InlineDiffResult Compare(string? original, string? current, bool ignoreWhitespace, bool ignoreCase,
        CancellationToken cancellationToken = default)
    {
        original ??= string.Empty;
        current ??= string.Empty;
        cancellationToken.ThrowIfCancellationRequested();
        if ((long)original.Length + current.Length <= DiffPerformanceOptions.MaximumEditorDetailedLineCharacters)
            return InlineTextDiffer.Compare(original, current, ignoreWhitespace, ignoreCase);
        if ((long)original.Length + current.Length > DiffPerformanceOptions.MaximumInlineAnchorComparisons)
            return Coarse(original, current);

        var oldBoundaries = StringInfo.ParseCombiningCharacters(original);
        var newBoundaries = StringInfo.ParseCombiningCharacters(current);
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = 0;
        while (prefix < oldBoundaries.Length && prefix < newBoundaries.Length && Equal(prefix, prefix))
        {
            if ((prefix & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            prefix++;
        }
        var suffix = 0;
        while (suffix < oldBoundaries.Length - prefix && suffix < newBoundaries.Length - prefix
            && Equal(oldBoundaries.Length - suffix - 1, newBoundaries.Length - suffix - 1))
        {
            if ((suffix & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            suffix++;
        }
        var oldStart = Boundary(oldBoundaries, prefix, original.Length);
        var newStart = Boundary(newBoundaries, prefix, current.Length);
        var oldEnd = Boundary(oldBoundaries, oldBoundaries.Length - suffix, original.Length);
        var newEnd = Boundary(newBoundaries, newBoundaries.Length - suffix, current.Length);
        var oldMiddle = original[oldStart..oldEnd];
        var newMiddle = current[newStart..newEnd];
        cancellationToken.ThrowIfCancellationRequested();
        InlineDiffResult middle;
        if ((long)oldMiddle.Length + newMiddle.Length > DiffPerformanceOptions.MaximumEditorDetailedLineCharacters)
            middle = Coarse(oldMiddle, newMiddle);
        else if (FindAlignedAnchor() is { } anchor)
        {
            var oldAnchor = oldBoundaries[anchor.Start];
            var newAnchor = newBoundaries[anchor.Start];
            var oldAnchorEnd = Boundary(oldBoundaries, anchor.Start + anchor.Count, original.Length);
            var newAnchorEnd = Boundary(newBoundaries, anchor.Start + anchor.Count, current.Length);
            var leading = InlineTextDiffer.Compare(original[oldStart..oldAnchor], current[newStart..newAnchor], ignoreWhitespace, ignoreCase);
            var trailing = InlineTextDiffer.Compare(original[oldAnchorEnd..oldEnd], current[newAnchorEnd..newEnd], ignoreWhitespace, ignoreCase);
            middle = new InlineDiffResult([.. leading.OldSegments, new InlineDiffSegment(original[oldAnchor..oldAnchorEnd], false), .. trailing.OldSegments],
                [.. leading.NewSegments, new InlineDiffSegment(current[newAnchor..newAnchorEnd], false), .. trailing.NewSegments])
            { IsApproximate = leading.IsApproximate || trailing.IsApproximate };
        }
        else middle = InlineTextDiffer.Compare(oldMiddle, newMiddle, ignoreWhitespace, ignoreCase);
        return new InlineDiffResult(Combine(original, oldStart, oldEnd, middle.OldSegments),
            Combine(current, newStart, newEnd, middle.NewSegments))
        { IsApproximate = middle.IsApproximate };

        bool Equal(int oldIndex, int newIndex)
        {
            var oldOffset = oldBoundaries[oldIndex];
            var newOffset = newBoundaries[newIndex];
            return original.AsSpan(oldOffset, Boundary(oldBoundaries, oldIndex + 1, original.Length) - oldOffset)
                .Equals(current.AsSpan(newOffset, Boundary(newBoundaries, newIndex + 1, current.Length) - newOffset), comparison);
        }

        (int Start, int Count)? FindAlignedAnchor()
        {
            if (oldBoundaries.Length != newBoundaries.Length) return null;
            var bestStart = 0;
            var bestCount = 0;
            var runStart = prefix;
            var runCount = 0;
            for (var i = prefix; i < oldBoundaries.Length - suffix; i++)
            {
                if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (Equal(i, i))
                {
                    if (runCount == 0) runStart = i;
                    runCount++;
                    if (runCount > bestCount) { bestStart = runStart; bestCount = runCount; }
                }
                else runCount = 0;
            }
            return bestCount >= 16 ? (bestStart, bestCount) : null;
        }
    }

    private static int Boundary(int[] boundaries, int index, int length) => index < boundaries.Length ? boundaries[index] : length;

    private static InlineDiffResult Coarse(string original, string current) => new(
        [new InlineDiffSegment(original, original.Length > 0)], [new InlineDiffSegment(current, current.Length > 0)])
    { IsApproximate = true };

    private static List<InlineDiffSegment> Combine(string text, int start, int end, IReadOnlyList<InlineDiffSegment> middle)
    {
        var result = new List<InlineDiffSegment>(middle.Count + 2);
        if (start > 0) result.Add(new InlineDiffSegment(text[..start], false));
        result.AddRange(middle);
        if (end < text.Length) result.Add(new InlineDiffSegment(text[end..], false));
        return result;
    }
}
