using Differ.Core.Models;

namespace Differ.App.ViewModels;

/// <summary>Prepared with the presentation on the worker thread, then adopted atomically.</summary>
internal sealed class ReviewInteractionIndex
{
    private ReviewInteractionIndex(
        IReadOnlyList<TextDiffRowViewModel> textRows,
        IReadOnlyList<WordDisplayRowViewModel> wordRows,
        CancellationToken cancellationToken)
    {
        TextProjection = new ContextFoldProjection<TextDiffRowViewModel>(textRows,
            row => row.HasDiffMarker, cancellationToken);
        WordProjection = new ContextFoldProjection<WordDisplayRowViewModel>(wordRows,
            row => row.HasDiffMarker, cancellationToken);
    }

    public Dictionary<string, TextDiffRowViewModel> TextLookup { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> TextPositions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> WordPositions { get; } = new(StringComparer.Ordinal);
    public Dictionary<int, TextDiffRowViewModel> OldLineLookup { get; } = [];
    public Dictionary<int, TextDiffRowViewModel> NewLineLookup { get; } = [];
    public Dictionary<string, DiffRowViewModel> OverviewLookup { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, DiffRowViewModel> WordOverviewLookup { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ChangedRowIndexes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ChangedTextRowIndexes { get; } = new(StringComparer.Ordinal);
    public ContextFoldProjection<TextDiffRowViewModel> TextProjection { get; }
    public ContextFoldProjection<WordDisplayRowViewModel> WordProjection { get; }
    public IReadOnlyList<DiffRowViewModel> WordOverviewRows { get; private set; } = [];
    public IReadOnlyList<DiffRowViewModel> ChangedWordOverviewRows { get; private set; } = [];
    public IReadOnlyList<TextDiffRowViewModel> ChangedWordTextRows { get; private set; } = [];

    public static ReviewInteractionIndex Create(
        FileKind kind,
        IReadOnlyList<DiffRowViewModel> rows,
        IReadOnlyList<TextDiffRowViewModel> textRows,
        IReadOnlyList<WordDisplayRowViewModel> wordRows,
        IReadOnlyList<DiffRowViewModel> changedRows,
        IReadOnlyList<TextDiffRowViewModel> changedTextRows,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new ReviewInteractionIndex(textRows, wordRows, cancellationToken);
        for (var index = 0; index < textRows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = textRows[index];
            result.TextLookup[row.StableId] = row;
            result.TextPositions[row.StableId] = index;
            if (row.OldIndex is { } oldLine) result.OldLineLookup.TryAdd(oldLine, row);
            if (row.NewIndex is { } newLine) result.NewLineLookup.TryAdd(newLine, row);
        }

        if (kind != FileKind.Excel)
        {
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.OverviewLookup[row.StableId] = row;
            }

            for (var index = 0; index < changedRows.Count; index++)
                result.ChangedRowIndexes[changedRows[index].StableId] = index;
        }
        for (var index = 0; index < changedTextRows.Count; index++)
            result.ChangedTextRowIndexes[changedTextRows[index].StableId] = index;

        var wordOverview = new List<DiffRowViewModel>(wordRows.Count);
        var changedWordOverview = new List<DiffRowViewModel>();
        var changedWordText = new List<TextDiffRowViewModel>();
        for (var index = 0; index < wordRows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wordRow = wordRows[index];
            foreach (var row in wordRow.ContentRows) result.WordPositions[row.StableId] = index;
            var overview = CreateWordOverview(wordRow);
            wordOverview.Add(overview);
            result.WordOverviewLookup[wordRow.StableId] = overview;
            if (wordRow.HasDiffMarker)
            {
                changedWordOverview.Add(overview);
                changedWordText.AddRange(wordRow.ContentRows);
                if (kind == FileKind.Word && result.ChangedTextRowIndexes.TryGetValue(wordRow.AnchorRow.StableId, out var groupIndex))
                    foreach (var child in wordRow.ContentRows) result.ChangedTextRowIndexes[child.StableId] = groupIndex;
            }
        }

        result.WordOverviewRows = wordOverview;
        result.ChangedWordOverviewRows = changedWordOverview;
        result.ChangedWordTextRows = changedWordText;
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public static DiffRowViewModel CreateWordOverview(WordDisplayRowViewModel row)
    {
        var anchor = row.AnchorRow;
        var kind = row.Group is { BaseCount: > 0, MineCount: > 0 } ? DiffKind.Modified : anchor.Kind;
        return new DiffRowViewModel(anchor.StableId, anchor.Path, kind, anchor.OldIndex,
            string.Join('\t', row.ContentRows.Select(cell => cell.OldText)), anchor.NewIndex,
            string.Join('\t', row.ContentRows.Select(cell => cell.NewText)), null, anchor.Depth);
    }
}
