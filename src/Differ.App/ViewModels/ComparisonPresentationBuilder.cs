using System.Globalization;
using Differ.App.Resources;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.ViewModels;

internal sealed record ComparisonPresentation(
    FileKind FileKind,
    IReadOnlyList<DiffRowViewModel> AllRows,
    DiffRowViewModel[] ChangedRows,
    IReadOnlyList<TextDiffRowViewModel> AllTextRows,
    TextDiffRowViewModel[] ChangedTextRows,
    IReadOnlyList<WordDisplayRowViewModel> AllWordRows,
    IReadOnlyList<WordDisplayRowViewModel> ChangedWordRows,
    IReadOnlyList<WorksheetTabViewModel> WorksheetTabs,
    string? FirstChangedSection,
    IReadOnlyList<string> Warnings,
    DiffSummary Summary,
    ReviewInteractionIndex InteractionIndex);

internal static class ComparisonPresentationBuilder
{
    private const double SpreadsheetColumnPadding = 26;
    private const double SpreadsheetTextUnitWidth = 7.2;
    private const double MaximumSpreadsheetColumnWidth = 312;

    public static ComparisonPresentation Build(
        DiffDocument document,
        CancellationToken cancellationToken,
        DiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var allRows = Flatten(document.Nodes, cancellationToken)
            .Select(item => new DiffRowViewModel(
                item.Node.StableId,
                item.Node.Path,
                item.Node.Kind,
                item.Node.OldIndex,
                item.Node.OldValue ?? string.Empty,
                item.Node.NewIndex,
                item.Node.NewValue ?? string.Empty,
                item.Node.SectionKey,
                item.Depth)
            {
                Metadata = item.Node.Metadata
            })
            .ToArray();

        // Filtered variants are precomputed here on the worker thread so
        // toggling "show unchanged" later only swaps references on the UI
        // thread instead of re-filtering tens of thousands of rows.
        var changedRows = allRows
            .Where(row => row.Kind != DiffKind.Unchanged)
            .ToArray();

        IReadOnlyList<TextDiffRowViewModel> allTextRows = [];
        var changedTextRows = Array.Empty<TextDiffRowViewModel>();
        IReadOnlyList<WordDisplayRowViewModel> allWordRows = [];
        IReadOnlyList<WordDisplayRowViewModel> changedWordRows = [];
        if (document.FileKind is FileKind.Text or FileKind.Word or FileKind.Binary)
        {
            allTextRows = allRows
                .Select(row => BuildTextRow(row, document.FileKind, options, cancellationToken))
                .ToArray();
            changedTextRows = allTextRows
                .Where(row => row.Kind != DiffKind.Unchanged)
                .ToArray();
            if (document.FileKind == FileKind.Word)
            {
                allWordRows = WordDisplayRowViewModel.Build(allTextRows);
                changedWordRows = allWordRows.Where(row => row.HasDiffMarker).ToArray();
                changedTextRows = changedWordRows.Select(row => row.AnchorRow).ToArray();
            }
        }

        IReadOnlyDictionary<string, SpreadsheetSectionViewModel>
            spreadsheetSections =
                new Dictionary<string, SpreadsheetSectionViewModel>(
                    StringComparer.Ordinal);
        if (document.FileKind == FileKind.Excel)
        {
            spreadsheetSections = document.TabularSections.ToDictionary(
                section => section.Key,
                section => BuildSpreadsheetSection(section, cancellationToken),
                StringComparer.Ordinal);
        }

        var worksheetTabs = document.FileKind == FileKind.Excel
            ? BuildWorksheetTabs(
                document.Sections,
                spreadsheetSections,
                allRows)
            : [];

        var firstChangedSection = spreadsheetSections.Values
            .FirstOrDefault(section => section.Rows.Any(
                row => row.Kind != DiffKind.Unchanged))?.Name
            ?? allRows.FirstOrDefault(
                row => row.Kind != DiffKind.Unchanged)?.SectionKey;

        cancellationToken.ThrowIfCancellationRequested();
        return new ComparisonPresentation(
            document.FileKind,
            allRows,
            changedRows,
            allTextRows,
            changedTextRows,
            allWordRows,
            changedWordRows,
            worksheetTabs,
            firstChangedSection,
            document.Warnings.ToArray(),
            document.Summary,
            ReviewInteractionIndex.Create(document.FileKind, allRows, allTextRows, allWordRows,
                changedRows, changedTextRows, cancellationToken));
    }

    private static WorksheetTabViewModel[] BuildWorksheetTabs(
        IReadOnlyList<string> sections,
        IReadOnlyDictionary<string, SpreadsheetSectionViewModel>
            spreadsheetSections,
        IReadOnlyList<DiffRowViewModel> allRows)
    {
        var overviewRowsBySection = allRows
            .Where(row => row.SectionKey is not null)
            .GroupBy(row => row.SectionKey!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<DiffRowViewModel>)group.ToArray(),
                StringComparer.Ordinal);
        return sections
            .Where(spreadsheetSections.ContainsKey)
            .Select(section =>
            {
                var spreadsheetSection = spreadsheetSections[section];
                return new WorksheetTabViewModel(
                    section,
                    overviewRowsBySection.TryGetValue(section, out var sectionRows)
                        ? sectionRows.Count(row => row.Kind != DiffKind.Unchanged) : 0,
                    spreadsheetSection,
                    overviewRowsBySection.TryGetValue(
                        section,
                        out var overviewRows)
                        ? overviewRows
                        : []);
            })
            .ToArray();
    }

    private static SpreadsheetSectionViewModel BuildSpreadsheetSection(
        TabularDiffSection section,
        CancellationToken cancellationToken)
    {
        var rows = new List<SpreadsheetRowViewModel>(section.Rows.Count);
        var maximumVisualUnits = section.Columns
            .Select(GetVisualTextUnits)
            .ToArray();
        foreach (var row in section.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseCells = new List<SpreadsheetCellViewModel>(row.Cells.Count);
            var mineCells = new List<SpreadsheetCellViewModel>(row.Cells.Count);
            for (var columnIndex = 0; columnIndex < row.Cells.Count; columnIndex++)
            {
                var cell = row.Cells[columnIndex];
                var oldText = cell.OldValue ?? string.Empty;
                var newText = cell.NewValue ?? string.Empty;
                MeasureColumnText(ref maximumVisualUnits[columnIndex], oldText);
                if (!string.Equals(oldText, newText, StringComparison.Ordinal))
                {
                    MeasureColumnText(ref maximumVisualUnits[columnIndex], newText);
                }
                var inlineDiff = BuildInlineDiff(cell.Kind, oldText, newText);
                var oldDetailsText = GetCellDetailsText(
                    oldText,
                    cell.OldDetails);
                var newDetailsText = GetCellDetailsText(
                    newText,
                    cell.NewDetails);
                var detailsInlineDiff = string.Equals(
                        oldText,
                        oldDetailsText,
                        StringComparison.Ordinal)
                    && string.Equals(
                        newText,
                        newDetailsText,
                        StringComparison.Ordinal)
                    ? inlineDiff
                    : BuildInlineDiff(
                        cell.Kind,
                        oldDetailsText,
                        newDetailsText);
                var isContentReplacement = cell.Kind == DiffKind.Modified
                    && ((cell.ChangeFlags & (SpreadsheetCellChange.Value | SpreadsheetCellChange.Formula)) != 0
                        || (cell.ChangeFlags == SpreadsheetCellChange.None
                            && (!string.Equals(oldText, newText, StringComparison.Ordinal)
                                || !string.Equals(oldDetailsText, newDetailsText, StringComparison.Ordinal))));
                var baseCell = new SpreadsheetCellViewModel(
                    cell.OldAddress ?? string.Empty,
                    oldText,
                    oldDetailsText,
                    BuildCellToolTip(cell.OldAddress, oldText, cell.OldDetails),
                    cell.Kind,
                    inlineDiff.OldSegments,
                    detailsInlineDiff.OldSegments)
                {
                    IsContentReplacement = isContentReplacement
                };
                baseCells.Add(baseCell);
                // Most large worksheets are unchanged context. Sharing is
                // safe only when the full immutable presentation is equal;
                // aligned rows with shifted addresses must stay distinct.
                var canShareCell = cell.Kind == DiffKind.Unchanged
                    && string.Equals(cell.OldAddress, cell.NewAddress, StringComparison.Ordinal)
                    && string.Equals(cell.OldValue, cell.NewValue, StringComparison.Ordinal)
                    && string.Equals(cell.OldDetails, cell.NewDetails, StringComparison.Ordinal);
                mineCells.Add(canShareCell ? baseCell : new SpreadsheetCellViewModel(
                    cell.NewAddress ?? string.Empty,
                    newText,
                    newDetailsText,
                    BuildCellToolTip(cell.NewAddress, newText, cell.NewDetails),
                    cell.Kind,
                    inlineDiff.NewSegments,
                    detailsInlineDiff.NewSegments)
                {
                    IsContentReplacement = isContentReplacement
                });
            }

            rows.Add(new SpreadsheetRowViewModel(
                row.StableId,
                row.OldIndex,
                row.NewIndex,
                row.Kind,
                baseCells,
                mineCells));
        }

        var columns = section.Columns
            .Select((key, index) => new SpreadsheetColumnViewModel(
                section.ColumnMatches.Count > index ? section.ColumnMatches[index].LogicalId : key,
                Math.Clamp(SpreadsheetColumnPadding + (maximumVisualUnits[index] * SpreadsheetTextUnitWidth),
                    72, MaximumSpreadsheetColumnWidth),
                rows.Any(row => row.BaseCells[index].HasDifference
                    || row.MineCells[index].HasDifference))
            {
                BaseColumn = section.ColumnMatches.Count > index ? section.ColumnMatches[index].OldColumn : key,
                MineColumn = section.ColumnMatches.Count > index ? section.ColumnMatches[index].NewColumn : key,
                FieldName = section.ColumnMatches.Count > index ? section.ColumnMatches[index].FieldName : null,
                MappingToolTip = section.ColumnMatches.Count > index
                    ? string.Format(CultureInfo.CurrentCulture, Strings.SpreadsheetMappingToolTipFormat, section.ColumnMatches[index].OldColumn ?? "—", section.ColumnMatches[index].NewColumn ?? "—", section.ColumnMatches[index].FieldName, section.ColumnMatches[index].Basis, section.MappingDescription)
                    : null
            })
            .ToArray();
        return new SpreadsheetSectionViewModel(section.Key, columns, rows, section.FrozenColumns,
            section.HeaderRow > 0 ? Math.Min(8, section.Rows.TakeWhile(row =>
                (row.NewIndex ?? row.OldIndex) < section.DataStartRow).Count()) : 0);
    }

    private static void MeasureColumnText(ref int maximumVisualUnits, string text)
    {
        if (SpreadsheetColumnPadding + (maximumVisualUnits * SpreadsheetTextUnitWidth)
            < MaximumSpreadsheetColumnWidth)
        {
            maximumVisualUnits = Math.Max(maximumVisualUnits, GetVisualTextUnits(text));
        }
    }

    private static TextDiffRowViewModel BuildTextRow(
        DiffRowViewModel row,
        FileKind fileKind,
        DiffOptions? options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (fileKind == FileKind.Text && row.Metadata.ContainsKey("editorAuthority"))
        {
            return new TextDiffRowViewModel(row.StableId, row.Kind, row.OldIndex, row.NewIndex,
                row.OldValue, row.NewValue, [new InlineDiffSegment(row.OldValue, false)], [new InlineDiffSegment(row.NewValue, false)])
            { Path = row.Path, Depth = row.Depth, Metadata = row.Metadata };
        }
        if (fileKind == FileKind.Word && row.Kind != DiffKind.Unchanged)
        {
            var lazy = new Lazy<InlineDiffResult>(() => BuildInlineDiff(row.Kind, row.OldValue, row.NewValue,
                ignoreWhitespace: options?.IgnoreWhitespace == true, ignoreCase: options?.IgnoreCase == true));
            return new TextDiffRowViewModel(row.StableId, row.Kind, row.OldIndex, row.NewIndex,
                row.OldValue, row.NewValue, new DeferredInlineSegments(lazy, true), new DeferredInlineSegments(lazy, false))
            { Path = row.Path, Depth = row.Depth, Metadata = row.Metadata, InlineComparison = lazy };
        }
        var inlineDiff = BuildInlineDiff(
            row.Kind,
            row.OldValue,
            row.NewValue,
            fileKind == FileKind.Binary,
            fileKind == FileKind.Text && options?.IgnoreWhitespace == true,
            fileKind == FileKind.Text && options?.IgnoreCase == true);
        return new TextDiffRowViewModel(
            row.StableId,
            row.Kind,
            row.OldIndex,
            row.NewIndex,
            row.OldValue,
            row.NewValue,
            inlineDiff.OldSegments,
            inlineDiff.NewSegments)
        {
            Path = row.Path,
            Depth = row.Depth,
            Metadata = row.Metadata
        };
    }

    private static InlineDiffResult BuildInlineDiff(
        DiffKind kind,
        string oldText,
        string newText,
        bool hexMode = false,
        bool ignoreWhitespace = false,
        bool ignoreCase = false)
    {
        if (kind != DiffKind.Unchanged)
        {
            return hexMode
                ? InlineTextDiffer.CompareHex(oldText, newText)
                : InlineTextDiffer.Compare(oldText, newText, ignoreWhitespace, ignoreCase);
        }

        var oldSegments = string.IsNullOrEmpty(oldText)
            ? Array.Empty<InlineDiffSegment>()
            : [new InlineDiffSegment(oldText, false)];
        var newSegments = string.Equals(oldText, newText, StringComparison.Ordinal)
            ? oldSegments
            : string.IsNullOrEmpty(newText)
            ? Array.Empty<InlineDiffSegment>()
            : [new InlineDiffSegment(newText, false)];
        return new InlineDiffResult(oldSegments, newSegments);
    }

    private static string GetCellDetailsText(
        string displayValue,
        string? details)
    {
        return string.IsNullOrWhiteSpace(details) ? displayValue : details;
    }

    private static string BuildCellToolTip(
        string? address,
        string displayValue,
        string? details)
    {
        var content = string.IsNullOrWhiteSpace(details)
            ? displayValue
            : details;
        if (string.IsNullOrWhiteSpace(address))
        {
            return content;
        }

        return string.IsNullOrWhiteSpace(content)
            ? address
            : $"{address}{Environment.NewLine}{content}";
    }

    private static int GetVisualTextUnits(string value)
    {
        const int maximumMeasuredUnits =
            DiffPerformanceOptions.MaximumMeasuredTextUnits;
        var isAscii = true;
        for (var index = 0; index < Math.Min(value.Length, maximumMeasuredUnits); index++)
        {
            if (value[index] > 0x7f)
            {
                isAscii = false;
                break;
            }
        }

        if (isAscii)
        {
            return Math.Min(value.Length, maximumMeasuredUnits);
        }

        var units = 0;
        var offset = 0;
        while (units < maximumMeasuredUnits && offset < value.Length)
        {
            var remaining = value.AsSpan(offset);
            var length = StringInfo.GetNextTextElementLength(remaining);
            var nonAscii = false;
            foreach (var character in remaining[..length])
            {
                if (character > 0x7f)
                {
                    nonAscii = true;
                    break;
                }
            }

            units += nonAscii ? 2 : 1;
            offset += length;
        }

        return Math.Min(units, maximumMeasuredUnits);
    }

    private static IEnumerable<(DiffNode Node, int Depth)> Flatten(
        IEnumerable<DiffNode> nodes,
        CancellationToken cancellationToken,
        int depth = 0)
    {
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return (node, depth);
            foreach (var child in Flatten(
                         node.Children,
                         cancellationToken,
                         depth + 1))
            {
                yield return child;
            }
        }
    }
}
