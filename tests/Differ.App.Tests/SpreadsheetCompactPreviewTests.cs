using Differ.App.ViewModels;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.Tests;

public sealed class SpreadsheetCompactPreviewTests
{
    [Fact]
    public void ReusesShortNeutralSegmentsWithoutChangingEmphasis()
    {
        InlineDiffSegment[] segments = [new("相同内容", false)];
        var selection = Select(segments, DiffKind.Unchanged);

        Assert.Same(segments, selection.BasePreview.Segments);
        Assert.Equal(DiffKind.Unchanged, selection.BasePreview.Kind);
        Assert.Equal("C5 ↔ C5", selection.PreviewAddress);
    }

    [Fact]
    public void NormalizesNewlinesAndTruncatesBeforeAWholeGrapheme()
    {
        var prefix = new string('x', 157);
        var original = prefix + "\r\n" + "e\u0301" + new string('y', 1000);
        InlineDiffSegment[] segments = [new(prefix + "\r\n", false), new(original[(prefix.Length + 2)..], true)];
        var selection = Select(segments, DiffKind.Modified) with { IsContentReplacement = true };

        Assert.Equal(new InlineDiffSegment[] { new(prefix + " ", false), new("…", false) }, selection.BasePreview.Segments);
        Assert.Equal(DiffKind.Deleted, selection.BasePreview.Kind);
        Assert.Equal(DiffKind.Added, selection.MinePreview.Kind);
        Assert.Same(segments, selection.BaseDetails.Segments);
        Assert.Equal(original, selection.BaseText);
    }

    [Fact]
    public void LimitsRunsAndPreservesVisibleChangeFlags()
    {
        var segments = Enumerable.Range(0, 100)
            .Select(index => new InlineDiffSegment("字", index % 2 == 0)).ToArray();
        var selection = Select(segments, DiffKind.Modified);
        var preview = selection.BasePreview.Segments;

        Assert.Equal(16, preview.Count);
        Assert.Equal(segments.Take(15), preview.Take(15));
        Assert.Equal(new InlineDiffSegment("…", false), preview[^1]);
        Assert.Same(segments, selection.BaseDetails.Segments);
    }

    private static SpreadsheetCellSelectionViewModel Select(IReadOnlyList<InlineDiffSegment> segments, DiffKind kind)
    {
        var text = string.Concat(segments.Select(segment => segment.Text));
        return new SpreadsheetCellSelectionViewModel("row", 2, "C", "C5", "C5", text, text, kind, segments, segments);
    }
}
