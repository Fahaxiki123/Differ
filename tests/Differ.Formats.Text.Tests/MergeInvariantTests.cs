using System.Text;

namespace Differ.Formats.Text.Tests;

public sealed class MergeInvariantTests
{
    [Fact]
    public void EverySourceCanBeReconstructedFromMergedBlocksWithoutLostContext()
    {
        var random = new Random(193510);
        for (var sample = 0; sample < 750; sample++)
        {
            var basis = RandomText(random);
            var mine = RandomText(random);
            var theirs = RandomText(random);
            var merge = ThreeWayTextMerger.Merge(basis, mine, theirs);
            Assert.Equal(basis, Reconstruct(merge, block => block.BaseText));
            Assert.Equal(mine, Reconstruct(merge, block => block.MineText));
            Assert.Equal(theirs, Reconstruct(merge, block => block.TheirsText));
            var reverse = ThreeWayTextMerger.Merge(basis, theirs, mine);
            Assert.Equal(merge.Text, reverse.Text);
            Assert.Equal(merge.Blocks.Count(block => block.IsConflict), reverse.Blocks.Count(block => block.IsConflict));
        }
    }

    private static string Reconstruct(TextMergeResult merge, Func<TextMergeBlock, string> source)
    {
        var result = new StringBuilder(merge.Text);
        foreach (var block in merge.Blocks.Reverse())
            result.Remove(block.ResultOffset, block.ResultLength).Insert(block.ResultOffset, source(block));
        return result.ToString();
    }

    private static string RandomText(Random random)
    {
        string[] lines = ["", "a", "b", "a", "中文", "x = 2", "  spaces  ", "😀"];
        var count = random.Next(9);
        if (count == 0) return string.Empty;
        return string.Join('\n', Enumerable.Range(0, count).Select(_ => lines[random.Next(lines.Length)]))
            + (random.Next(2) == 0 ? "\n" : string.Empty);
    }
}
