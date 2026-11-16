namespace Differ.Formats.Text.Tests;

public sealed class ThreeWayTextMergerTests
{
    [Theory]
    [InlineData("", "", "", "")]
    [InlineData("", "新增\n", "", "新增\n")]
    [InlineData("a\n", "", "a\n", "")]
    [InlineData("a\nb\nc\n", "A\nb\nc\n", "a\nb\nC\n", "A\nb\nC\n")]
    [InlineData("a\nb\n", "A\nb\n", "a\nB\n", "A\nB\n")]
    [InlineData("a\nb\n", "a\n", "a\nb\nc\n", "a\nc\n")]
    [InlineData("a", "a\n", "a", "a\n")]
    [InlineData("a\n", "a", "a\n", "a")]
    [InlineData("a\nb\n", "\na\nb\n", "a\nb\n\n", "\na\nb\n\n")]
    [InlineData("a\nb\nc\n", "A\nb\nC\n", "a\nb\nc\n", "A\nb\nC\n")]
    public void IndependentChangesProduceExactResult(string basis, string mine, string theirs, string expected)
    {
        var result = ThreeWayTextMerger.Merge(basis, mine, theirs);
        Assert.Equal(expected, result.Text);
        Assert.DoesNotContain(result.Blocks, block => block.IsConflict);
        Assert.All(result.Blocks, block => Assert.True(block.IsResolved));
        AssertValidRanges(result);
    }

    [Theory]
    [InlineData("a", "b", "c")]
    [InlineData("", "mine\n", "theirs\n")]
    [InlineData("a\nb\nc\n", "a\nB\nc\n", "a\nX\nc\n")]
    [InlineData("a\nb\n", "a\n", "a\nB\n")]
    [InlineData("a\nb\n", "x\na\nb\n", "A\nb\n")]
    public void OverlappingChangesRemainExplicitUnresolved(string basis, string mine, string theirs)
    {
        var result = ThreeWayTextMerger.Merge(basis, mine, theirs);
        Assert.Equal(basis, result.Text);
        var block = Assert.Single(result.Blocks);
        Assert.True(block.IsConflict);
        Assert.False(block.IsResolved);
        Assert.Equal(MergeChoice.Unresolved, block.Choice);
        AssertValidRanges(result);
    }

    [Theory]
    [InlineData("", "a\n")]
    [InlineData("a\n", "")]
    [InlineData("a", "a\n")]
    [InlineData("a\n", "a")]
    [InlineData("a\nb\nc\n", "A\nb\nC\n")]
    public void IdenticalChangesAreAppliedOnce(string basis, string changed)
    {
        var result = ThreeWayTextMerger.Merge(basis, changed, changed);
        Assert.Equal(changed, result.Text);
        Assert.DoesNotContain(result.Blocks, block => block.IsConflict);
    }

    [Fact]
    public void DisjointChangesAndConflictsDoNotConsumeContext()
    {
        var result = ThreeWayTextMerger.Merge("a\nb\nc\nd\ne\n", "A\nb\nM\nd\ne\n", "a\nb\nT\nd\nE\n");
        Assert.Equal("A\nb\nc\nd\nE\n", result.Text);
        Assert.Equal(3, result.Blocks.Count);
        var conflict = Assert.Single(result.Blocks, block => block.IsConflict);
        Assert.Equal("c\n", conflict.BaseText);
        Assert.Equal("M\n", conflict.MineText);
        Assert.Equal("T\n", conflict.TheirsText);
        Assert.Equal(3, conflict.BaseStartLine);
        AssertValidRanges(result);
    }

    [Fact]
    public void SourceRangesReflectEarlierInsertions()
    {
        var result = ThreeWayTextMerger.Merge("a\nb\nc\n", "x\na\nb\nM\n", "a\nb\nT\n");
        Assert.Equal("x\na\nb\nc\n", result.Text);
        var conflict = Assert.Single(result.Blocks, block => block.IsConflict);
        Assert.Equal(4, conflict.MineStartLine);
        Assert.Equal(3, conflict.TheirsStartLine);
        Assert.Equal("M\n", conflict.MineText);
    }

    [Fact]
    public void OneSidedRandomEditsReconstructThatSideAndSymmetricConflicts()
    {
        var random = new Random(8701);
        for (var trial = 0; trial < 300; trial++)
        {
            var basis = string.Concat(Enumerable.Range(0, random.Next(0, 20)).Select(index => $"L{index}\n"));
            var changed = basis.Split('\n').ToList();
            for (var edit = 0; edit < 4; edit++)
            {
                var at = random.Next(changed.Count + 1);
                if (at < changed.Count && random.Next(2) == 0) changed.RemoveAt(at);
                else changed.Insert(at, $"new{trial}-{edit}");
            }
            var text = string.Join('\n', changed);
            Assert.Equal(text, ThreeWayTextMerger.Merge(basis, text, basis).Text);
            Assert.Equal(text, ThreeWayTextMerger.Merge(basis, basis, text).Text);
            Assert.Equal(text, ThreeWayTextMerger.Merge(basis, text, text).Text);
        }
    }

    [Fact]
    public void LargeRewritesWarnAndPreserveBothInputsForReview()
    {
        var basis = string.Concat(Enumerable.Range(0, 3000).Select(index => $"base {index}\n"));
        var mine = basis.Replace("base", "mine", StringComparison.Ordinal);
        var theirs = basis.Replace("base", "theirs", StringComparison.Ordinal);
        var result = ThreeWayTextMerger.Merge(basis, mine, theirs);
        Assert.Equal(basis, result.Text);
        Assert.NotEmpty(result.Warnings);
        Assert.Equal(mine, Assert.Single(result.Blocks).MineText);
        Assert.Equal(theirs, result.Blocks[0].TheirsText);
    }

    [Fact]
    public void CancellationDoesNotReturnPartialResult()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ThreeWayTextMerger.Merge("a", "b", "c", cancellation.Token));
    }

    private static void AssertValidRanges(TextMergeResult result)
    {
        var previousEnd = 0;
        foreach (var block in result.Blocks)
        {
            Assert.InRange(block.ResultOffset, previousEnd, result.Text.Length);
            Assert.InRange(block.ResultLength, 0, result.Text.Length - block.ResultOffset);
            previousEnd = block.ResultOffset + block.ResultLength;
        }
    }
}
