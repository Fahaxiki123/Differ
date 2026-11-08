using System.Globalization;
using System.Text;
using Differ.Core.Algorithms;
using Differ.Core.Diagnostics;
using Differ.Core.Models;
using Differ.Formats.Text.Resources;

namespace Differ.Formats.Text;

/// <summary>Bounded DiffPlex regions plus unique stable anchors; Monaco never computes a second diff.</summary>
public static class EditorDiffEngine
{
    private static readonly CompositeFormat EncodingChangedFormat = CompositeFormat.Parse(TextStrings.AdapterEncodingChangedFormat);
    private static readonly CompositeFormat LineEndingChangedFormat = CompositeFormat.Parse(TextStrings.AdapterLineEndingChangedFormat);

    internal static IReadOnlyList<string> CreateWarnings(EditorTextSnapshot snapshot, DiffOptions options)
    {
        var warnings = snapshot.SourceWarnings.ToList();
        if (snapshot.OriginalEncodingName is not null && snapshot.CurrentEncodingName is not null
            && !string.Equals(snapshot.OriginalEncodingName, snapshot.CurrentEncodingName, StringComparison.OrdinalIgnoreCase))
            warnings.Add(string.Format(CultureInfo.CurrentCulture, EncodingChangedFormat, snapshot.OriginalEncodingName, snapshot.CurrentEncodingName));
        if (!options.IgnoreLineEndings)
        {
            if (snapshot.OriginalLineEnding is not null && snapshot.CurrentLineEnding is not null
                && !string.Equals(snapshot.OriginalLineEnding, snapshot.CurrentLineEnding, StringComparison.Ordinal))
                warnings.Add(string.Format(CultureInfo.CurrentCulture, LineEndingChangedFormat, snapshot.OriginalLineEnding, snapshot.CurrentLineEnding));
            if (snapshot.OriginalText.EndsWith('\n') != snapshot.Text.EndsWith('\n')) warnings.Add(TextStrings.AdapterFinalNewlineChanged);
        }
        return warnings.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static EditorComparisonSnapshot Compare(EditorTextSnapshot snapshot, DiffOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        using var measurement = ComparisonMetrics.Measure("editor-line-match", "Text", snapshot.Text.Length);
        var before = options.IgnoreLineEndings && snapshot.OriginalText.EndsWith('\n') ? snapshot.OriginalText[..^1] : snapshot.OriginalText;
        var after = options.IgnoreLineEndings && snapshot.Text.EndsWith('\n') ? snapshot.Text[..^1] : snapshot.Text;
        var oldLines = before.Length == 0 ? [] : before.Split('\n');
        var newLines = after.Length == 0 ? [] : after.Split('\n');
        var oldKeys = Keys(oldLines);
        var newKeys = Keys(newLines);
        var comparer = options.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var changes = new List<EditorLineChange>();
        var approximateRegions = 0;
        AppendRegion(0, oldLines.Length, 0, newLines.Length, true);
        cancellationToken.ThrowIfCancellationRequested();
        return new EditorComparisonSnapshot(snapshot.OriginalText, snapshot.Text, snapshot.Version,
            EditableTextSession.BuildHunks(snapshot, changes))
        { IgnoreFinalNewline = options.IgnoreLineEndings, ApproximateRegionCount = approximateRegions, Warnings = CreateWarnings(snapshot, options) };

        string[] Keys(string[] lines)
        {
            if (!options.IgnoreWhitespace) return lines;
            var result = new string[lines.Length];
            for (var i = 0; i < lines.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result[i] = new string(lines[i].Where(character => !char.IsWhiteSpace(character)).ToArray());
            }
            return result;
        }

        void Add(int oldStart, int oldCount, int newStart, int newCount)
        {
            if (oldCount == 0 && newCount == 0) return;
            changes.Add(new EditorLineChange(oldCount == 0 ? oldStart : oldStart + 1,
                oldCount == 0 ? 0 : oldStart + oldCount, newCount == 0 ? newStart : newStart + 1,
                newCount == 0 ? 0 : newStart + newCount));
        }

        void AppendRegion(int oldStart, int oldCount, int newStart, int newCount, bool findAnchors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (oldCount > 0 && newCount > 0 && comparer.Equals(oldKeys[oldStart], newKeys[newStart]))
            {
                if ((oldStart & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                oldStart++; newStart++; oldCount--; newCount--;
            }
            while (oldCount > 0 && newCount > 0 && comparer.Equals(oldKeys[oldStart + oldCount - 1], newKeys[newStart + newCount - 1]))
            {
                if ((oldCount & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                oldCount--; newCount--;
            }
            if (oldCount == 0 || newCount == 0) { Add(oldStart, oldCount, newStart, newCount); return; }
            var characters = 0L;
            for (var i = oldStart; i < oldStart + oldCount; i++) characters += oldLines[i].Length;
            for (var i = newStart; i < newStart + newCount; i++) characters += newLines[i].Length;
            if ((long)(oldCount + 1) * (newCount + 1) <= DiffPerformanceOptions.MaximumSequenceMatrixCells
                && characters <= DiffPerformanceOptions.MaximumEditorDiffRegionCharacters)
            {
                var oldPieces = oldKeys[oldStart..(oldStart + oldCount)];
                var newPieces = newKeys[newStart..(newStart + newCount)];
                // Custom line chunks retain a single empty line; joining it into "" would lose its physical range.
                var diff = new DiffPlex.Differ().CreateCustomDiffs("old", "new", false, options.IgnoreCase,
                    value => value == "old" ? oldPieces : newPieces);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var block in diff.DiffBlocks)
                    Add(oldStart + block.DeleteStartA, block.DeleteCountA, newStart + block.InsertStartB, block.InsertCountB);
                return;
            }
            if (!findAnchors) { approximateRegions++; Add(oldStart, oldCount, newStart, newCount); return; }
            var oldUnique = Unique(oldKeys, oldStart, oldCount);
            var newUnique = Unique(newKeys, newStart, newCount);
            var candidates = new List<(int Old, int New)>();
            for (var i = newStart; i < newStart + newCount; i++)
            {
                if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (newUnique[newKeys[i]] >= 0 && oldUnique.TryGetValue(newKeys[i], out var at) && at >= 0) candidates.Add((at, i));
            }
            var tails = new List<int>();
            var previous = new int[candidates.Count];
            for (var i = 0; i < candidates.Count; i++)
            {
                if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var low = 0; var high = tails.Count;
                while (low < high) { var middle = (low + high) / 2; if (candidates[tails[middle]].Old < candidates[i].Old) low = middle + 1; else high = middle; }
                previous[i] = low == 0 ? -1 : tails[low - 1];
                if (low == tails.Count) tails.Add(i); else tails[low] = i;
            }
            if (tails.Count == 0) { approximateRegions++; Add(oldStart, oldCount, newStart, newCount); return; }
            var anchors = new List<(int Old, int New)>();
            for (var i = tails[^1]; i >= 0; i = previous[i]) anchors.Add(candidates[i]);
            anchors.Reverse();
            var oldPosition = oldStart; var newPosition = newStart;
            foreach (var anchor in anchors)
            {
                AppendRegion(oldPosition, anchor.Old - oldPosition, newPosition, anchor.New - newPosition, false);
                oldPosition = anchor.Old + 1; newPosition = anchor.New + 1;
            }
            AppendRegion(oldPosition, oldStart + oldCount - oldPosition, newPosition, newStart + newCount - newPosition, false);
        }

        Dictionary<string, int> Unique(string[] keys, int start, int count)
        {
            var result = new Dictionary<string, int>(comparer);
            for (var i = start; i < start + count; i++)
            {
                if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!result.TryAdd(keys[i], i)) result[keys[i]] = -1;
            }
            return result;
        }
    }
}
