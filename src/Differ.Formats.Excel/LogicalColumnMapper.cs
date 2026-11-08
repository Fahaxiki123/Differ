using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using Differ.Core.Models;
using Differ.Formats.Excel.Resources;

namespace Differ.Formats.Excel;

internal sealed record ColumnMapping(
    SpreadsheetColumnMatch[] Columns, SpreadsheetRule Rule, string Description);

internal static class LogicalColumnMapper
{
    private const int SampleLimit = 128;

    public static ColumnMapping Build(SheetPair pair, DiffOptions options, string? fileName,
        List<string> warnings, CancellationToken cancellationToken)
    {
        var name = pair.NewSheet?.Name ?? pair.OldSheet!.Name;
        var explicitRule = options.SpreadsheetRules.Where(rule =>
            (rule.SheetName == "*" || rule.SheetName == name)
            && FileSystemName.MatchesSimpleExpression(rule.FilePattern,
                Path.GetFileName(fileName ?? string.Empty), ignoreCase: true))
            .OrderBy(rule => (rule.FilePattern == "*" ? 0 : 2) + (rule.SheetName == "*" ? 0 : 1)).LastOrDefault();
        var header = explicitRule?.HeaderRow ?? FindHeader(pair);
        var rule = explicitRule is null ? new SpreadsheetRule
        {
            HeaderRow = header,
            DataStartRow = header > 0 ? header + 1 : 1,
            KeyColumns = options.KeyColumns
        } : explicitRule with { KeyColumns = explicitRule.KeyColumns.Count > 0 ? explicitRule.KeyColumns : options.KeyColumns };
        if (rule.HeaderRow < 0 || rule.DataStartRow <= rule.HeaderRow || rule.DataStartRow < 1)
        {
            throw new InvalidDataException(ExcelStrings.MappingInvalidRows);
        }
        if (rule.HeaderRow > 0 && rule.DataStartRow > 9)
            warnings.Add(string.Format(CultureInfo.CurrentCulture, ExcelStrings.MappingHeaderLimitFormat, name));

        var oldColumns = GetColumns(pair.OldSheet);
        var newColumns = GetColumns(pair.NewSheet);
        var oldAvailable = new HashSet<string>(oldColumns, StringComparer.Ordinal);
        var newAvailable = new HashSet<string>(newColumns, StringComparer.Ordinal);
        var result = new List<SpreadsheetColumnMatch>();
        var nextId = 0;
        void Add(string? oldColumn, string? newColumn, string basis, MappingConfidence confidence)
        {
            if (oldColumn is not null) oldAvailable.Remove(oldColumn);
            if (newColumn is not null) newAvailable.Remove(newColumn);
            result.Add(new SpreadsheetColumnMatch
            {
                LogicalId = "field-" + (nextId++).ToString("D5", CultureInfo.InvariantCulture),
                OldColumn = oldColumn,
                NewColumn = newColumn,
                FieldName = GetValue(pair.NewSheet, rule.HeaderRow, newColumn)
                    ?? GetValue(pair.OldSheet, rule.HeaderRow, oldColumn),
                Kind = oldColumn is null ? DiffKind.Added : newColumn is null ? DiffKind.Deleted : DiffKind.Unchanged,
                Confidence = confidence,
                Basis = basis
            });
        }

        foreach (var item in rule.ColumnOverrides)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = item.OldColumn?.Trim().ToUpperInvariant();
            var right = item.NewColumn?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(left)) left = null;
            if (string.IsNullOrEmpty(right)) right = null;
            if ((left is null && right is null) || (left is not null && !oldAvailable.Contains(left))
                || (right is not null && !newAvailable.Contains(right)))
            {
                throw new InvalidDataException(ExcelStrings.MappingInvalidOverrides);
            }
            Add(left, right, ExcelStrings.MappingManual, MappingConfidence.Confirmed);
        }

        if (header == 0 && pair.OldSheet is not null && pair.NewSheet is not null)
        {
            foreach (var column in oldColumns.Union(newColumns, StringComparer.Ordinal).OrderBy(SpreadsheetGridBudget.ColumnNumber))
            {
                if ((oldAvailable.Contains(column) || !oldColumns.Contains(column, StringComparer.Ordinal))
                    && (newAvailable.Contains(column) || !newColumns.Contains(column, StringComparer.Ordinal)))
                    Add(column, column, ExcelStrings.MappingPhysical, MappingConfidence.Inferred);
            }
        }
        else
        {
            var oldProfiles = oldColumns.ToDictionary(column => column,
                column => Profile(pair.OldSheet, column, rule), StringComparer.Ordinal);
            var newProfiles = newColumns.ToDictionary(column => column,
                column => Profile(pair.NewSheet, column, rule), StringComparer.Ordinal);
            var candidates = new List<(string Old, string New, double Score)>();
            foreach (var oldColumn in oldColumns.Where(oldAvailable.Contains))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var oldProfile = oldProfiles[oldColumn];
                var sameFields = newColumns.Where(newAvailable.Contains)
                    .Where(column => header == 0 || oldProfile.Name == newProfiles[column].Name).ToArray();
                if (header > 0 && !string.IsNullOrEmpty(oldProfile.Name)
                    && oldColumns.Count(column => oldProfiles[column].Name == oldProfile.Name) == 1
                    && sameFields.Length == 1)
                {
                    Add(oldColumn, sameFields[0], ExcelStrings.MappingUniqueField, MappingConfidence.Inferred);
                    continue;
                }

                foreach (var newColumn in sameFields)
                {
                    var current = newProfiles[newColumn];
                    var score = Similarity(oldProfile.Values, current.Values);
                    if (oldProfile.Auxiliary.Length > 0 && oldProfile.Auxiliary == current.Auxiliary) score += 0.3;
                    if (score >= 0.35) candidates.Add((oldColumn, newColumn, score));
                }
            }

            // Only a unique best choice on both sides is accepted. Position is
            // deliberately not used to invent intent for indistinguishable duplicates.
            foreach (var candidate in candidates.OrderByDescending(item => item.Score))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!oldAvailable.Contains(candidate.Old) || !newAvailable.Contains(candidate.New)) continue;
                var tiedOld = candidates.Count(item => item.Old == candidate.Old && newAvailable.Contains(item.New)
                    && Math.Abs(item.Score - candidate.Score) < 0.001);
                var tiedNew = candidates.Count(item => item.New == candidate.New && oldAvailable.Contains(item.Old)
                    && Math.Abs(item.Score - candidate.Score) < 0.001);
                if (tiedOld == 1 && tiedNew == 1)
                    Add(candidate.Old, candidate.New, ExcelStrings.MappingSample, MappingConfidence.Inferred);
            }

            foreach (var oldColumn in oldColumns.Where(oldAvailable.Contains).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var field = oldProfiles[oldColumn].Name;
                var remaining = newColumns.Where(newAvailable.Contains)
                    .Where(column => header > 0 && field == newProfiles[column].Name).ToArray();
                if (remaining.Length == 0) continue;
                var newColumn = remaining.FirstOrDefault(column => column == oldColumn) ?? remaining[0];
                Add(oldColumn, newColumn, ExcelStrings.MappingAmbiguousBasis, MappingConfidence.Ambiguous);
                warnings.Add(string.Format(CultureInfo.CurrentCulture, ExcelStrings.MappingAmbiguousFormat, name, oldColumn));
            }
        }

        foreach (var column in oldColumns.Where(oldAvailable.Contains)) Add(column, null, ExcelStrings.MappingOldColumn, MappingConfidence.Inferred);
        foreach (var column in newColumns.Where(newAvailable.Contains)) Add(null, column, ExcelStrings.MappingNewColumn, MappingConfidence.Inferred);

        var ordered = result.Where(item => item.NewColumn is not null)
            .OrderBy(item => SpreadsheetGridBudget.ColumnNumber(item.NewColumn!)).ToList();
        foreach (var deleted in result.Where(item => item.NewColumn is null)
                     .OrderBy(item => SpreadsheetGridBudget.ColumnNumber(item.OldColumn!)))
        {
            var position = ordered.FindIndex(item => item.OldColumn is not null
                && SpreadsheetGridBudget.ColumnNumber(item.OldColumn) > SpreadsheetGridBudget.ColumnNumber(deleted.OldColumn!));
            ordered.Insert(position < 0 ? ordered.Count : position, deleted);
        }
        var common = ordered.Where(item => item.OldColumn is not null && item.NewColumn is not null).ToArray();
        var oldRanks = common.OrderBy(item => SpreadsheetGridBudget.ColumnNumber(item.OldColumn!))
            .Select((item, index) => (item.LogicalId, index)).ToDictionary(item => item.LogicalId, item => item.index);
        var newRanks = common.Select((item, index) => (item.LogicalId, index)).ToDictionary(item => item.LogicalId, item => item.index);
        for (var index = 0; index < ordered.Count; index++)
        {
            var column = ordered[index];
            if (oldRanks.TryGetValue(column.LogicalId, out var oldRank) && oldRank != newRanks[column.LogicalId])
                ordered[index] = column with { Kind = DiffKind.Moved };
        }
        var description = header > 0
            ? string.Format(CultureInfo.CurrentCulture, ExcelStrings.MappingDescriptionFormat, explicitRule is null ? ExcelStrings.MappingAutomatic : ExcelStrings.MappingExplicit, header, rule.DataStartRow)
            : ExcelStrings.MappingMissingHeader;
        return new ColumnMapping(ordered.ToArray(), rule, description);
    }

    private static string[] GetColumns(ExcelSheet? sheet)
    {
        if (sheet is null) return [];
        var first = int.MaxValue;
        var last = 0;
        foreach (var row in sheet.Rows)
        {
            foreach (var column in row.Cells.Keys)
            {
                var number = SpreadsheetGridBudget.ColumnNumber(column);
                first = Math.Min(first, number);
                last = Math.Max(last, number);
            }
        }
        if (last == 0) return sheet.Rows.Count > 0 ? ["A"] : [];
        return Enumerable.Range(first - 1, last - first + 1)
            .Select(SpreadsheetDiffEngine.GetColumnLetter).ToArray();
    }

    private static int FindHeader(SheetPair pair)
    {
        if (pair.OldSheet is null || pair.NewSheet is null) return 0;
        foreach (var row in pair.OldSheet.Rows.Take(20))
        {
            var current = pair.NewSheet.Rows.FirstOrDefault(item => item.RowIndex == row.RowIndex);
            if (current is null) continue;
            var fields = row.Cells.Values.Where(cell => cell.DataType == "text" || cell.DataType == "s")
                .Select(cell => cell.DisplayValue).Where(value => value.Length > 0).ToArray();
            if (fields.Length < 2) continue;
            var recognizable = fields.Count(value => value.Equals("id", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("_id", StringComparison.OrdinalIgnoreCase)
                || value.Contains('_', StringComparison.Ordinal)
                || value is "编号" or "字段名" or "ID" or "name" or "icon" or "portrait" or "value");
            var currentFields = current.Cells.Values.Select(cell => cell.DisplayValue).ToHashSet(StringComparer.Ordinal);
            if (recognizable >= 2 && fields.Count(currentFields.Contains) >= Math.Max(2, fields.Length / 2))
                return checked((int)row.RowIndex);
        }
        return 0;
    }

    private static string? GetValue(ExcelSheet? sheet, int row, string? column) =>
        column is not null && row > 0
        && sheet?.Rows.FirstOrDefault(item => item.RowIndex == row)?.Cells.TryGetValue(column, out var cell) == true
            ? cell.DisplayValue : null;

    private static ColumnProfile Profile(ExcelSheet? sheet, string column, SpreadsheetRule rule)
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in (sheet?.Rows ?? []).Where(row => row.RowIndex >= rule.DataStartRow).Take(SampleLimit))
        {
            if (row.Cells.TryGetValue(column, out var cell))
            {
                var value = cell.DataType + ":" + cell.DisplayValue;
                values[value] = values.GetValueOrDefault(value) + 1;
            }
        }
        var auxiliary = new StringBuilder();
        foreach (var row in rule.AuxiliaryHeaderRows)
        {
            var value = GetValue(sheet, row, column);
            if (!string.IsNullOrEmpty(value)) auxiliary.Append(value.Length).Append(':').Append(value);
        }
        return new ColumnProfile(GetValue(sheet, rule.HeaderRow, column) ?? string.Empty, auxiliary.ToString(), values);
    }

    private static double Similarity(Dictionary<string, int> left, Dictionary<string, int> right)
    {
        var total = left.Values.Sum() + right.Values.Sum();
        if (total == 0) return 0;
        var common = left.Sum(item => Math.Min(item.Value, right.GetValueOrDefault(item.Key)));
        return 2.0 * common / total;
    }

    private sealed record ColumnProfile(string Name, string Auxiliary, Dictionary<string, int> Values);
}
