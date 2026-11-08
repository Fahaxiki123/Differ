namespace Differ.Core.Algorithms;

public enum SequenceMatchKind
{
    Equal,
    Added,
    Deleted,
    Modified
}

public sealed record SequenceMatch<T>(
    SequenceMatchKind Kind,
    T? OldItem,
    T? NewItem,
    int? OldIndex,
    int? NewIndex);

public static class SequenceAligner
{
    // A quadratic LCS matrix is useful for small edits, but becomes both slow
    // and memory hungry for real-world workbooks and long documents. Large
    // inputs use the linear anchor path below after common edges are removed.
    public static IReadOnlyList<SequenceMatch<T>> Align<T>(
        IReadOnlyList<T> oldItems,
        IReadOnlyList<T> newItems,
        Func<T, string> keySelector,
        Func<T, T, double>? similarity = null,
        double replacementThreshold = 0.2)
    {
        ArgumentNullException.ThrowIfNull(oldItems);
        ArgumentNullException.ThrowIfNull(newItems);
        ArgumentNullException.ThrowIfNull(keySelector);

        // Materialize keys once. The previous implementation called the key
        // selector inside every LCS matrix comparison; for Word this meant
        // normalizing the same paragraph thousands of times.
        var oldKeys = oldItems.Select(keySelector).ToArray();
        var newKeys = newItems.Select(keySelector).ToArray();
        var raw = BuildRawMatches(oldItems, newItems, oldKeys, newKeys);
        return PairReplacements(raw, similarity, replacementThreshold);
    }

    private static List<SequenceMatch<T>> BuildRawMatches<T>(
        IReadOnlyList<T> oldItems,
        IReadOnlyList<T> newItems,
        string[] oldKeys,
        string[] newKeys)
    {
        var prefixLength = 0;
        while (prefixLength < oldKeys.Length
            && prefixLength < newKeys.Length
            && KeysEqual(oldKeys[prefixLength], newKeys[prefixLength]))
        {
            prefixLength++;
        }

        var suffixLength = 0;
        while (suffixLength < oldKeys.Length - prefixLength
            && suffixLength < newKeys.Length - prefixLength
            && KeysEqual(
                oldKeys[oldKeys.Length - suffixLength - 1],
                newKeys[newKeys.Length - suffixLength - 1]))
        {
            suffixLength++;
        }

        var oldMiddleCount = oldKeys.Length - prefixLength - suffixLength;
        var newMiddleCount = newKeys.Length - prefixLength - suffixLength;
        var result = new List<SequenceMatch<T>>(
            prefixLength + oldMiddleCount + newMiddleCount + suffixLength);

        for (var index = 0; index < prefixLength; index++)
        {
            result.Add(EqualMatch(
                oldItems[index],
                newItems[index],
                index,
                index));
        }

        if (oldMiddleCount > 0 || newMiddleCount > 0)
        {
            var matrixCells = (long)(oldMiddleCount + 1)
                * (newMiddleCount + 1);
            if (matrixCells <= DiffPerformanceOptions.MaximumSequenceMatrixCells)
            {
                var lcs = BuildLcs(
                    oldKeys,
                    newKeys,
                    prefixLength,
                    prefixLength,
                    oldMiddleCount,
                    newMiddleCount);
                result.AddRange(Backtrack(
                    oldItems,
                    newItems,
                    oldKeys,
                    newKeys,
                    prefixLength,
                    prefixLength,
                    oldMiddleCount,
                    newMiddleCount,
                    lcs));
            }
            else
            {
                result.AddRange(AlignLarge(
                    oldItems,
                    newItems,
                    oldKeys,
                    newKeys,
                    prefixLength,
                    prefixLength,
                    oldMiddleCount,
                    newMiddleCount));
            }
        }

        for (var index = 0; index < suffixLength; index++)
        {
            var oldIndex = oldKeys.Length - suffixLength + index;
            var newIndex = newKeys.Length - suffixLength + index;
            result.Add(EqualMatch(
                oldItems[oldIndex],
                newItems[newIndex],
                oldIndex,
                newIndex));
        }

        return result;
    }

    private static int[,] BuildLcs(
        string[] oldKeys,
        string[] newKeys,
        int oldOffset,
        int newOffset,
        int oldCount,
        int newCount)
    {
        var table = new int[oldCount + 1, newCount + 1];
        for (var oldIndex = oldCount - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = newCount - 1; newIndex >= 0; newIndex--)
            {
                table[oldIndex, newIndex] = KeysEqual(
                    oldKeys[oldOffset + oldIndex],
                    newKeys[newOffset + newIndex])
                    ? table[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(
                        table[oldIndex + 1, newIndex],
                        table[oldIndex, newIndex + 1]);
            }
        }

        return table;
    }

    private static List<SequenceMatch<T>> Backtrack<T>(
        IReadOnlyList<T> oldItems,
        IReadOnlyList<T> newItems,
        string[] oldKeys,
        string[] newKeys,
        int oldOffset,
        int newOffset,
        int oldCount,
        int newCount,
        int[,] lcs)
    {
        var result = new List<SequenceMatch<T>>();
        var oldIndex = 0;
        var newIndex = 0;

        while (oldIndex < oldCount && newIndex < newCount)
        {
            if (KeysEqual(
                oldKeys[oldOffset + oldIndex],
                newKeys[newOffset + newIndex]))
            {
                result.Add(EqualMatch(
                    oldItems[oldOffset + oldIndex],
                    newItems[newOffset + newIndex],
                    oldOffset + oldIndex,
                    newOffset + newIndex));
                oldIndex++;
                newIndex++;
            }
            else if (lcs[oldIndex + 1, newIndex] >= lcs[oldIndex, newIndex + 1])
            {
                result.Add(new SequenceMatch<T>(
                    SequenceMatchKind.Deleted,
                    oldItems[oldOffset + oldIndex],
                    default,
                    oldOffset + oldIndex,
                    null));
                oldIndex++;
            }
            else
            {
                result.Add(new SequenceMatch<T>(
                    SequenceMatchKind.Added,
                    default,
                    newItems[newOffset + newIndex],
                    null,
                    newOffset + newIndex));
                newIndex++;
            }
        }

        while (oldIndex < oldCount)
        {
            result.Add(new SequenceMatch<T>(
                SequenceMatchKind.Deleted,
                oldItems[oldOffset + oldIndex],
                default,
                oldOffset + oldIndex,
                null));
            oldIndex++;
        }

        while (newIndex < newCount)
        {
            result.Add(new SequenceMatch<T>(
                SequenceMatchKind.Added,
                default,
                newItems[newOffset + newIndex],
                null,
                newOffset + newIndex));
            newIndex++;
        }

        return result;
    }

    private static List<SequenceMatch<T>> AlignLarge<T>(
        IReadOnlyList<T> oldItems,
        IReadOnlyList<T> newItems,
        string[] oldKeys,
        string[] newKeys,
        int oldOffset,
        int newOffset,
        int oldCount,
        int newCount)
    {
        // Unique keys are safe anchors: matching them in monotonic order
        // preserves the document order while avoiding a quadratic matrix.
        var oldKeyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < oldCount; index++)
        {
            var key = oldKeys[oldOffset + index];
            oldKeyCounts[key] = oldKeyCounts.TryGetValue(key, out var count)
                ? count + 1
                : 1;
        }

        var oldUniquePositions = new Dictionary<string, int>(
            StringComparer.Ordinal);
        for (var index = 0; index < oldCount; index++)
        {
            var key = oldKeys[oldOffset + index];
            if (oldKeyCounts[key] == 1)
            {
                oldUniquePositions[key] = index;
            }
        }

        var anchors = new List<(int OldIndex, int NewIndex)>();
        var lastOldIndex = -1;
        for (var newIndex = 0; newIndex < newCount; newIndex++)
        {
            var key = newKeys[newOffset + newIndex];
            if (!oldUniquePositions.TryGetValue(key, out var oldIndex)
                || oldIndex <= lastOldIndex)
            {
                continue;
            }

            anchors.Add((oldIndex, newIndex));
            lastOldIndex = oldIndex;
        }

        var result = new List<SequenceMatch<T>>(
            oldCount + newCount);
        var previousOld = 0;
        var previousNew = 0;
        foreach (var anchor in anchors)
        {
            AppendUnmatched(
                result,
                oldItems,
                newItems,
                oldOffset + previousOld,
                anchor.OldIndex - previousOld,
                newOffset + previousNew,
                anchor.NewIndex - previousNew);
            result.Add(EqualMatch(
                oldItems[oldOffset + anchor.OldIndex],
                newItems[newOffset + anchor.NewIndex],
                oldOffset + anchor.OldIndex,
                newOffset + anchor.NewIndex));
            previousOld = anchor.OldIndex + 1;
            previousNew = anchor.NewIndex + 1;
        }

        AppendUnmatched(
            result,
            oldItems,
            newItems,
            oldOffset + previousOld,
            oldCount - previousOld,
            newOffset + previousNew,
            newCount - previousNew);
        return result;
    }

    private static void AppendUnmatched<T>(
        List<SequenceMatch<T>> result,
        IReadOnlyList<T> oldItems,
        IReadOnlyList<T> newItems,
        int oldStart,
        int oldCount,
        int newStart,
        int newCount)
    {
        for (var index = 0; index < oldCount; index++)
        {
            result.Add(new SequenceMatch<T>(
                SequenceMatchKind.Deleted,
                oldItems[oldStart + index],
                default,
                oldStart + index,
                null));
        }

        for (var index = 0; index < newCount; index++)
        {
            result.Add(new SequenceMatch<T>(
                SequenceMatchKind.Added,
                default,
                newItems[newStart + index],
                null,
                newStart + index));
        }
    }

    private static SequenceMatch<T> EqualMatch<T>(
        T oldItem,
        T newItem,
        int oldIndex,
        int newIndex)
    {
        return new SequenceMatch<T>(
            SequenceMatchKind.Equal,
            oldItem,
            newItem,
            oldIndex,
            newIndex);
    }

    private static bool KeysEqual(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private static List<SequenceMatch<T>> PairReplacements<T>(
        List<SequenceMatch<T>> raw,
        Func<T, T, double>? similarity,
        double replacementThreshold)
    {
        var result = new List<SequenceMatch<T>>(raw.Count);
        var index = 0;

        while (index < raw.Count)
        {
            if (raw[index].Kind == SequenceMatchKind.Equal)
            {
                result.Add(raw[index]);
                index++;
                continue;
            }

            var run = new List<SequenceMatch<T>>();
            while (index < raw.Count && raw[index].Kind != SequenceMatchKind.Equal)
            {
                run.Add(raw[index]);
                index++;
            }

            var deleted = run.Where(item => item.Kind == SequenceMatchKind.Deleted).ToList();
            var added = run.Where(item => item.Kind == SequenceMatchKind.Added).ToList();
            var pairs = Math.Min(deleted.Count, added.Count);

            for (var pairIndex = 0; pairIndex < pairs; pairIndex++)
            {
                var oldItem = deleted[pairIndex].OldItem!;
                var newItem = added[pairIndex].NewItem!;
                var score = similarity?.Invoke(oldItem, newItem) ?? 1;
                if (score >= replacementThreshold)
                {
                    result.Add(new SequenceMatch<T>(
                        SequenceMatchKind.Modified,
                        oldItem,
                        newItem,
                        deleted[pairIndex].OldIndex,
                        added[pairIndex].NewIndex));
                }
                else
                {
                    result.Add(deleted[pairIndex]);
                    result.Add(added[pairIndex]);
                }
            }

            result.AddRange(deleted.Skip(pairs));
            result.AddRange(added.Skip(pairs));
        }

        return result;
    }
}
