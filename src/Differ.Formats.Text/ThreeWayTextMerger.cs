using System.Text;
using Differ.Core.Algorithms;
using Differ.Core.Security;
using Differ.Formats.Text.Resources;

namespace Differ.Formats.Text;

public enum MergeChoice { Unresolved, Automatic, Mine, Base, Theirs, Manual }

public sealed record TextMergeBlock(
    int Index, int BaseStartLine, int BaseEndLine, int MineStartLine, int MineEndLine,
    int TheirsStartLine, int TheirsEndLine, int ResultOffset, int ResultLength,
    string BaseText, string MineText, string TheirsText, bool IsConflict, bool IsResolved,
    MergeChoice Choice)
{
    public bool CanChoose { get; init; } = true;
}

public sealed record TextMergeResult(string Text, IReadOnlyList<TextMergeBlock> Blocks,
    IReadOnlyList<string> Warnings);

/// <summary>Strict line merges retain line terminators in their tokens, including the final newline.</summary>
public static class ThreeWayTextMerger
{
    private sealed record Edit(int Start, int Count, int NewStart, int NewCount, bool Mine)
    {
        public int End => Start + Count;
    }

    public static TextMergeResult Merge(string baseText, string mineText, string theirsText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseText);
        ArgumentNullException.ThrowIfNull(mineText);
        ArgumentNullException.ThrowIfNull(theirsText);
        var basis = Lines(Normalize(baseText), cancellationToken);
        var mine = Lines(Normalize(mineText), cancellationToken);
        var theirs = Lines(Normalize(theirsText), cancellationToken);
        var warnings = new List<string>();
        var mineEdits = Changes(basis, mine, true, warnings, cancellationToken);
        var theirEdits = Changes(basis, theirs, false, warnings, cancellationToken);
        var edits = mineEdits.Concat(theirEdits).OrderBy(edit => edit.Start).ThenBy(edit => edit.End).ToArray();
        var blocks = new List<TextMergeBlock>();
        var result = new StringBuilder();
        var position = 0;
        var index = 0;
        var mineCursor = 0;
        var theirsCursor = 0;
        var mineDelta = 0;
        var theirsDelta = 0;
        while (index < edits.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = index;
            var start = edits[index].Start;
            var end = edits[index++].End;
            // Adjacent replacements remain independent; insertions at a replacement's
            // start share its anchor and conservatively conflict, unlike insertions at its end.
            while (index < edits.Length && (edits[index].Start < end || edits[index].Start == start))
                end = Math.Max(end, edits[index++].End);
            Append(result, basis, position, start - position);
            var group = edits[first..index];
            var mineStart = MapStart(start, mineEdits, ref mineCursor, ref mineDelta);
            var theirsStart = MapStart(start, theirEdits, ref theirsCursor, ref theirsDelta);
            var mineCount = end - start + group.Where(edit => edit.Mine).Sum(edit => edit.NewCount - edit.Count);
            var theirsCount = end - start + group.Where(edit => !edit.Mine).Sum(edit => edit.NewCount - edit.Count);
            var original = Join(basis, start, end - start);
            var local = Join(mine, mineStart, mineCount);
            var remote = Join(theirs, theirsStart, theirsCount);
            var mineChanged = !string.Equals(original, local, StringComparison.Ordinal);
            var theirsChanged = !string.Equals(original, remote, StringComparison.Ordinal);
            var conflict = mineChanged && theirsChanged && !string.Equals(local, remote, StringComparison.Ordinal);
            var selected = conflict ? original : mineChanged ? local : remote;
            blocks.Add(new TextMergeBlock(blocks.Count, start + 1, end, mineStart + 1, mineStart + mineCount,
                theirsStart + 1, theirsStart + theirsCount, result.Length, selected.Length,
                original, local, remote, conflict, !conflict, conflict ? MergeChoice.Unresolved : MergeChoice.Automatic));
            result.Append(selected);
            if (result.Length > DiffPerformanceOptions.MaximumTextCharacters)
                throw new ComparisonResourceLimitException(MergeTextStrings.TooLarge);
            position = end;
        }
        Append(result, basis, position, basis.Length - position);
        if (result.Length > DiffPerformanceOptions.MaximumTextCharacters)
            throw new ComparisonResourceLimitException(MergeTextStrings.TooLarge);
        return new TextMergeResult(result.ToString(), blocks.AsReadOnly(), warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    internal static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string[] Lines(string text, CancellationToken token)
    {
        if (text.Length > DiffPerformanceOptions.MaximumTextCharacters)
            throw new ComparisonResourceLimitException(MergeTextStrings.TooLarge);
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
            if (text[i] != '\n') continue;
            lines.Add(text[start..(i + 1)]);
            start = i + 1;
            if (lines.Count > DiffPerformanceOptions.MaximumTextLines)
                throw new ComparisonResourceLimitException(MergeTextStrings.TooLarge);
        }
        if (start < text.Length) lines.Add(text[start..]);
        token.ThrowIfCancellationRequested();
        return lines.ToArray();
    }

    private static List<Edit> Changes(string[] basis, string[] side, bool mine, List<string> warnings, CancellationToken token)
    {
        var start = 0;
        while (start < basis.Length && start < side.Length && basis[start] == side[start])
        { if ((start & 255) == 0) token.ThrowIfCancellationRequested(); start++; }
        var oldEnd = basis.Length;
        var newEnd = side.Length;
        while (oldEnd > start && newEnd > start && basis[oldEnd - 1] == side[newEnd - 1])
        { if ((oldEnd & 255) == 0) token.ThrowIfCancellationRequested(); oldEnd--; newEnd--; }
        var oldCount = oldEnd - start;
        var newCount = newEnd - start;
        if (oldCount == 0 && newCount == 0) return [];
        if (oldCount == 0 || newCount == 0) return [new(start, oldCount, start, newCount, mine)];
        var characters = 0L;
        for (var i = start; i < oldEnd; i++) characters += basis[i].Length;
        for (var i = start; i < newEnd; i++) characters += side[i].Length;
        if ((long)(oldCount + 1) * (newCount + 1) > DiffPerformanceOptions.MaximumSequenceMatrixCells
            || characters > DiffPerformanceOptions.MaximumEditorDiffRegionCharacters)
        {
            warnings.Add(MergeTextStrings.CoarseMerge);
            return [new(start, oldCount, start, newCount, mine)];
        }
        var oldPieces = basis[start..oldEnd];
        var newPieces = side[start..newEnd];
        var diff = new DiffPlex.Differ().CreateCustomDiffs("base", "side", false, false,
            value => value == "base" ? oldPieces : newPieces);
        token.ThrowIfCancellationRequested();
        return diff.DiffBlocks.Select(block => new Edit(start + block.DeleteStartA, block.DeleteCountA,
            start + block.InsertStartB, block.InsertCountB, mine)).ToList();
    }

    private static int MapStart(int position, List<Edit> edits, ref int cursor, ref int delta)
    {
        while (cursor < edits.Count && edits[cursor].Start < position)
        {
            var edit = edits[cursor++];
            if (edit.End <= position) delta += edit.NewCount - edit.Count;
        }
        return position + delta;
    }

    private static string Join(string[] lines, int start, int count) => string.Concat(lines.AsSpan(start, count).ToArray());
    private static void Append(StringBuilder builder, string[] lines, int start, int count)
    { for (var i = start; i < start + count; i++) builder.Append(lines[i]); }
}
