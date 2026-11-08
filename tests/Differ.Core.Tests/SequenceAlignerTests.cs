using Differ.Core.Algorithms;

namespace Differ.Core.Tests;

public sealed class SequenceAlignerTests
{
    private static readonly string[] UnrelatedOldItems = ["old"];
    private static readonly string[] UnrelatedNewItems = ["new"];

    [Fact]
    public void AlignIdentifiesEqualModifiedAndAddedItems()
    {
        var oldItems = new[] { "alpha", "bravo", "charlie" };
        var newItems = new[] { "alpha", "bravo changed", "charlie", "delta" };

        var result = SequenceAligner.Align(
            oldItems,
            newItems,
            value => value,
            (left, right) => left[0] == right[0] ? 0.8 : 0,
            replacementThreshold: 0.5);

        Assert.Collection(
            result,
            item => Assert.Equal(SequenceMatchKind.Equal, item.Kind),
            item =>
            {
                Assert.Equal(SequenceMatchKind.Modified, item.Kind);
                Assert.Equal("bravo", item.OldItem);
                Assert.Equal("bravo changed", item.NewItem);
            },
            item => Assert.Equal(SequenceMatchKind.Equal, item.Kind),
            item =>
            {
                Assert.Equal(SequenceMatchKind.Added, item.Kind);
                Assert.Equal("delta", item.NewItem);
            });
    }

    [Fact]
    public void AlignDoesNotForceUnrelatedItemsIntoModification()
    {
        var result = SequenceAligner.Align(
            UnrelatedOldItems,
            UnrelatedNewItems,
            value => value,
            (_, _) => 0,
            replacementThreshold: 0.5);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, item => item.Kind == SequenceMatchKind.Deleted);
        Assert.Contains(result, item => item.Kind == SequenceMatchKind.Added);
    }

    [Fact]
    public void AlignUsesLinearPathForLargeUnrelatedSequences()
    {
        var oldItems = Enumerable
            .Range(0, 3_000)
            .Select(index => $"old-{index}")
            .ToArray();
        var newItems = Enumerable
            .Range(0, 3_000)
            .Select(index => $"new-{index}")
            .ToArray();

        var result = SequenceAligner.Align(
            oldItems,
            newItems,
            value => value,
            (_, _) => 1,
            replacementThreshold: 0.5);

        Assert.Equal(3_000, result.Count);
        Assert.All(result, item =>
            Assert.Equal(SequenceMatchKind.Modified, item.Kind));
    }
}
