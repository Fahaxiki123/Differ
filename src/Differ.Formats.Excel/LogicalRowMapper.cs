using System.Globalization;
using System.Text;
using Differ.Core.Models;
using Differ.Formats.Excel.Resources;

namespace Differ.Formats.Excel;

internal sealed record MappedSpreadsheetRow(ExcelRow? Old, ExcelRow? New, SpreadsheetRowMatch Match);

internal static class LogicalRowMapper
{
    private const int MaximumLocalCandidates = 64;

    public static IReadOnlyList<MappedSpreadsheetRow> Build(SheetPair pair, ColumnMapping mapping,
        List<string> warnings, CancellationToken cancellationToken)
    {
        var oldRows = pair.OldSheet?.Rows ?? [];
        var newRows = pair.NewSheet?.Rows ?? [];
        var oldData = oldRows.Where(row => row.RowIndex >= mapping.Rule.DataStartRow).ToArray();
        var newData = newRows.Where(row => row.RowIndex >= mapping.Rule.DataStartRow).ToArray();
        var common = mapping.Columns.Where(column => column.OldColumn is not null && column.NewColumn is not null).ToArray();
        var oldSignatures = oldData.Select(row => Signature(row, common, true)).ToArray();
        var newSignatures = newData.Select(row => Signature(row, common, false)).ToArray();
        var pairs = new Dictionary<int, int>();
        var consumedOld = new HashSet<int>();
        var bases = new Dictionary<int, (string Basis, MappingConfidence Confidence)>();
        var requested = mapping.Rule.KeyColumns;
        var keyColumns = new List<SpreadsheetColumnMatch>();
        foreach (var key in requested)
        {
            var normalized = key.Trim().ToUpperInvariant();
            var matches = common.Where(column => column.OldColumn == normalized
                || string.Equals(column.FieldName, key, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || keyColumns.Contains(matches[0]))
            {
                warnings.Add(string.Format(CultureInfo.CurrentCulture, ExcelStrings.MappingInvalidKeyFormat, pair.NewSheet?.Name ?? pair.OldSheet!.Name, normalized));
                keyColumns.Clear();
                break;
            }
            keyColumns.Add(matches[0]);
        }

        string?[]? oldKeys = null;
        string?[]? newKeys = null;
        if (keyColumns.Count > 0)
        {
            oldKeys = oldData.Select(row => Key(row, keyColumns, true)).ToArray();
            newKeys = newData.Select(row => Key(row, keyColumns, false)).ToArray();
            var oldGroups = Group(oldKeys);
            var newGroups = Group(newKeys);
            foreach (var group in newGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (group.Value.Count == 1 && oldGroups.TryGetValue(group.Key, out var oldGroup) && oldGroup.Count == 1)
                    Pair(oldGroup[0], group.Value[0], ExcelStrings.MappingUniqueKey, MappingConfidence.Confirmed);
            }
            var invalidOld = oldKeys.Count(key => key is null) + oldGroups.Values.Where(group => group.Count > 1).Sum(group => group.Count);
            var invalidNew = newKeys.Count(key => key is null) + newGroups.Values.Where(group => group.Count > 1).Sum(group => group.Count);
            if (invalidOld + invalidNew > 0)
            {
                warnings.Add(string.Format(CultureInfo.CurrentCulture, ExcelStrings.MappingAnomalyFormat, pair.NewSheet?.Name ?? pair.OldSheet!.Name, invalidOld, invalidNew));
            }
        }

        // Exact mapped content can anchor a record even when adjacent rows were
        // inserted. Formula text/physical letters are not record identity.
        var oldContent = Group(oldSignatures);
        var newContent = Group(newSignatures);
        foreach (var entry in newContent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Value.Count != 1 || !oldContent.TryGetValue(entry.Key, out var originals) || originals.Count != 1) continue;
            var left = originals[0];
            var right = entry.Value[0];
            if (!consumedOld.Contains(left) && !pairs.ContainsKey(right) && CompatibleKeys(left, right))
                Pair(left, right, ExcelStrings.MappingUniqueContent, MappingConfidence.Inferred);
        }

        // Repeated identical rows carry no observable content distinction.
        // Preserve occurrence order directly instead of allocating and sorting
        // up to 64 equally good candidates per row in a large repeated table.
        foreach (var entry in newContent.Where(entry => entry.Value.Count > 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!oldContent.TryGetValue(entry.Key, out var originals)) continue;
            var leftCursor = 0;
            foreach (var right in entry.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pairs.ContainsKey(right)) continue;
                while (leftCursor < originals.Count && consumedOld.Contains(originals[leftCursor])) leftCursor++;
                if (leftCursor >= originals.Count) break;
                var left = originals[leftCursor];
                if (CompatibleKeys(left, right))
                {
                    Pair(left, right, ExcelStrings.MappingRepeatedContent, MappingConfidence.Inferred);
                    leftCursor++;
                }
            }
        }

        // Bound every ambiguous region by nearby known anchors. This never
        // compares the complete workbook Cartesian product.
        var orderedAnchors = pairs.OrderBy(item => item.Key).ToArray();
        var monotonic = orderedAnchors.Zip(orderedAnchors.Skip(1)).All(items => items.First.Value < items.Second.Value);
        var localCandidates = new List<(int Old, int New, double Score)>();
        var anchorCursor = 0;
        for (var right = 0; right < newData.Length; right++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pairs.ContainsKey(right)) continue;
            while (anchorCursor < orderedAnchors.Length && orderedAnchors[anchorCursor].Key < right) anchorCursor++;
            var start = monotonic && anchorCursor > 0 ? orderedAnchors[anchorCursor - 1].Value + 1 : Math.Max(0, right - MaximumLocalCandidates / 2);
            var end = monotonic && anchorCursor < orderedAnchors.Length ? orderedAnchors[anchorCursor].Value : Math.Min(oldData.Length, right + MaximumLocalCandidates / 2 + 1);
            if (end - start > MaximumLocalCandidates) end = start + MaximumLocalCandidates;
            for (var left = start; left < end; left++)
            {
                if (consumedOld.Contains(left) || !CompatibleKeys(left, right)) continue;
                var score = oldSignatures[left] == newSignatures[right] ? 1.0
                    : Similarity(oldData[left], newData[right], common);
                // A single replacement bounded by two reliable anchors has
                // unambiguous sequence correspondence even with rewritten values.
                if (score < 0.3 && end - start == 1 && (anchorCursor == 0 ? right == 0
                        : orderedAnchors[anchorCursor - 1].Key + 1 == right)
                    && (anchorCursor == orderedAnchors.Length ? right + 1 == newData.Length
                        : orderedAnchors[anchorCursor].Key == right + 1)) score = 0.31;
                if (score >= 0.3) localCandidates.Add((left, right, score));
            }
        }
        foreach (var candidate in localCandidates.OrderByDescending(item => item.Score).ThenBy(item => Math.Abs(item.Old - item.New)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!consumedOld.Contains(candidate.Old) && !pairs.ContainsKey(candidate.New))
                Pair(candidate.Old, candidate.New, ExcelStrings.MappingLocalContent, MappingConfidence.Inferred);
        }

        var rows = new List<MappedSpreadsheetRow>(oldRows.Count + newRows.Count);
        // Header and metadata rows never participate in key uniqueness checks.
        var oldHeaders = oldRows.Where(row => row.RowIndex < mapping.Rule.DataStartRow).ToDictionary(row => row.RowIndex);
        var newHeaders = newRows.Where(row => row.RowIndex < mapping.Rule.DataStartRow).ToDictionary(row => row.RowIndex);
        foreach (var number in oldHeaders.Keys.Union(newHeaders.Keys).Order())
            Add(oldHeaders.GetValueOrDefault(number), newHeaders.GetValueOrDefault(number), ExcelStrings.MappingHeader, MappingConfidence.Confirmed, true, false);

        var matchedOldOrder = pairs.Values.Order().Select((index, rank) => (index, rank)).ToDictionary(item => item.index, item => item.rank);
        var matchedNewOrder = pairs.OrderBy(item => item.Key).Select((item, rank) => (item.Value, rank)).ToDictionary(item => item.Value, item => item.rank);
        var deleted = Enumerable.Range(0, oldData.Length).Where(index => !consumedOld.Contains(index)).ToArray();
        var deletedCursor = 0;
        for (var right = 0; right < newData.Length; right++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pairs.TryGetValue(right, out var left))
            {
                while (deletedCursor < deleted.Length && deleted[deletedCursor] < left)
                {
                    var removed = deleted[deletedCursor++];
                    Add(oldData[removed], null, ExcelStrings.MappingDeletedRecord, MappingConfidence.Inferred, false, false);
                }
                var basis = bases[right];
                Add(oldData[left], newData[right], basis.Basis, basis.Confidence, false,
                    matchedOldOrder[left] != matchedNewOrder[left]);
            }
            else Add(null, newData[right], ExcelStrings.MappingAddedRecord, MappingConfidence.Inferred, false, false);
        }
        foreach (var removed in deleted.Skip(deletedCursor))
            Add(oldData[removed], null, ExcelStrings.MappingDeletedRecord, MappingConfidence.Inferred, false, false);
        return rows;

        bool CompatibleKeys(int left, int right) => oldKeys is null || newKeys is null
            || string.Equals(oldKeys[left], newKeys[right], StringComparison.Ordinal);

        void Pair(int left, int right, string basis, MappingConfidence confidence)
        {
            pairs.Add(right, left);
            consumedOld.Add(left);
            bases.Add(right, (basis, confidence));
        }

        void Add(ExcelRow? oldRow, ExcelRow? newRow, string basis, MappingConfidence confidence, bool isHeader, bool moved)
        {
            rows.Add(new MappedSpreadsheetRow(oldRow, newRow, new SpreadsheetRowMatch
            {
                LogicalId = "record-" + rows.Count.ToString("D7", CultureInfo.InvariantCulture),
                OldRow = oldRow is null ? null : checked((int)oldRow.RowIndex),
                NewRow = newRow is null ? null : checked((int)newRow.RowIndex),
                Kind = oldRow is null ? DiffKind.Added : newRow is null ? DiffKind.Deleted : moved ? DiffKind.Moved : DiffKind.Unchanged,
                Basis = basis,
                Confidence = confidence,
                IsHeader = isHeader
            }));
        }
    }

    private static Dictionary<string, List<int>> Group(IEnumerable<string?> values)
    {
        var result = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var index = 0;
        foreach (var value in values)
        {
            if (value is not null)
            {
                if (!result.TryGetValue(value, out var group)) result.Add(value, group = []);
                group.Add(index);
            }
            index++;
        }
        return result;
    }

    private static string? Key(ExcelRow row, IReadOnlyList<SpreadsheetColumnMatch> columns, bool old)
    {
        var builder = new StringBuilder();
        foreach (var column in columns)
        {
            if (!row.Cells.TryGetValue((old ? column.OldColumn : column.NewColumn)!, out var cell)
                || string.IsNullOrWhiteSpace(cell.DisplayValue)) return null;
            Append(builder, cell.DataType);
            Append(builder, cell.DisplayValue);
        }
        return builder.ToString();
    }

    private static string Signature(ExcelRow row, IReadOnlyList<SpreadsheetColumnMatch> columns, bool old)
    {
        var builder = new StringBuilder();
        foreach (var column in columns)
        {
            if (row.Cells.TryGetValue((old ? column.OldColumn : column.NewColumn)!, out var cell))
            {
                Append(builder, cell.DataType);
                Append(builder, cell.Formula is null ? cell.DisplayValue : "<formula>");
            }
            else Append(builder, string.Empty);
        }
        return builder.ToString();
    }

    private static double Similarity(ExcelRow oldRow, ExcelRow newRow, IReadOnlyList<SpreadsheetColumnMatch> columns)
    {
        var comparable = 0;
        var equal = 0;
        foreach (var column in columns)
        {
            oldRow.Cells.TryGetValue(column.OldColumn!, out var left);
            newRow.Cells.TryGetValue(column.NewColumn!, out var right);
            if (left is null && right is null) continue;
            comparable++;
            if (left?.DisplayValue == right?.DisplayValue && left?.DataType == right?.DataType) equal++;
        }
        return comparable == 0 ? 1 : (double)equal / comparable;
    }

    private static void Append(StringBuilder builder, string value) => builder.Append(value.Length).Append(':').Append(value);
}
