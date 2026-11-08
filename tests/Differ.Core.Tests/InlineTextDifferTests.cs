using Differ.Core.Algorithms;

namespace Differ.Core.Tests;

public sealed class InlineTextDifferTests
{
    [Fact]
    public void CompareMarksOnlyDeletedChineseSuffix()
    {
        const string common = "$Lang$游戏中最基础的货币之一，一般来源于付费";
        const string deleted = "，可在部分情况下使用";

        var result = InlineTextDiffer.Compare(common + deleted, common);

        Assert.Equal(common + deleted, Join(result.OldSegments));
        Assert.Equal(common, Join(result.NewSegments));
        Assert.Contains(
            result.OldSegments,
            segment => segment.IsChanged && segment.Text == deleted);
        Assert.DoesNotContain(
            result.NewSegments,
            segment => segment.IsChanged);
    }

    [Fact]
    public void ComparePreservesUnicodeTextElements()
    {
        const string prefix = "A👩‍💻e\u0301";

        var result = InlineTextDiffer.Compare(prefix + "B", prefix + "C");

        Assert.Equal(prefix + "B", Join(result.OldSegments));
        Assert.Equal(prefix + "C", Join(result.NewSegments));
        Assert.Equal(
            ["B"],
            result.OldSegments
                .Where(segment => segment.IsChanged)
                .Select(segment => segment.Text));
        Assert.Equal(
            ["C"],
            result.NewSegments
                .Where(segment => segment.IsChanged)
                .Select(segment => segment.Text));
    }

    [Fact]
    public void CompareTreatsAddedTextAsChanged()
    {
        var result = InlineTextDiffer.Compare(string.Empty, "新增");

        Assert.Empty(result.OldSegments);
        var segment = Assert.Single(result.NewSegments);
        Assert.True(segment.IsChanged);
        Assert.Equal("新增", segment.Text);
    }

    [Fact]
    public void CompareKeepsIdenticalTextUnchanged()
    {
        var result = InlineTextDiffer.Compare("same", "same");

        Assert.All(result.OldSegments, segment => Assert.False(segment.IsChanged));
        Assert.All(result.NewSegments, segment => Assert.False(segment.IsChanged));
    }

    [Fact]
    public void CompareHexHighlightsWholeBytesOnly()
    {
        var result = InlineTextDiffer.CompareHex("48 65 6C 6C 6F", "48 FF 6C 6C 6F");

        Assert.Equal("48 65 6C 6C 6F", Join(result.OldSegments));
        Assert.Equal("48 FF 6C 6C 6F", Join(result.NewSegments));
        Assert.Equal(
            ["65 "],
            result.OldSegments
                .Where(segment => segment.IsChanged)
                .Select(segment => segment.Text));
        Assert.Equal(
            ["FF "],
            result.NewSegments
                .Where(segment => segment.IsChanged)
                .Select(segment => segment.Text));
    }

    [Fact]
    public void CompareHexTreatsEmptySideAsFullyChanged()
    {
        var result = InlineTextDiffer.CompareHex(string.Empty, "00 01 02");

        Assert.Empty(result.OldSegments);
        var segment = Assert.Single(result.NewSegments);
        Assert.True(segment.IsChanged);
        Assert.Equal("00 01 02", segment.Text);
    }

    [Fact]
    public void CompareIgnoresSpacingAndCaseAroundRealChangesWithoutRewritingText()
    {
        const string before = "  SELECT\tPrice = 100 ";
        const string after = "\tselect price = 200  ";

        var result = InlineTextDiffer.Compare(before, after, ignoreWhitespace: true, ignoreCase: true);

        Assert.Equal(before, Join(result.OldSegments));
        Assert.Equal(after, Join(result.NewSegments));
        Assert.Equal("1", Join(result.OldSegments.Where(segment => segment.IsChanged)));
        Assert.Equal("2", Join(result.NewSegments.Where(segment => segment.IsChanged)));
    }

    [Fact]
    public void CompareIgnoringCaseStillDetectsWhitespaceWhenRequested()
    {
        const string before = "A B";
        const string after = "a\tC";

        var result = InlineTextDiffer.Compare(before, after, ignoreWhitespace: false, ignoreCase: true);

        Assert.Equal(before, Join(result.OldSegments));
        Assert.Equal(after, Join(result.NewSegments));
        Assert.Equal(" B", Join(result.OldSegments.Where(segment => segment.IsChanged)));
        Assert.Equal("\tC", Join(result.NewSegments.Where(segment => segment.IsChanged)));
    }

    [Fact]
    public void CompareIgnoringWhitespaceStillDetectsCaseWhenRequested()
    {
        const string before = "A B";
        const string after = "a   C";

        var result = InlineTextDiffer.Compare(before, after, ignoreWhitespace: true, ignoreCase: false);

        Assert.Equal(before, Join(result.OldSegments));
        Assert.Equal(after, Join(result.NewSegments));
        Assert.Equal("AB", Join(result.OldSegments.Where(segment => segment.IsChanged)));
        Assert.Equal("aC", Join(result.NewSegments.Where(segment => segment.IsChanged)));
        Assert.Contains(result.OldSegments, segment => segment.Text == " " && !segment.IsChanged);
        Assert.Contains(result.NewSegments, segment => segment.Text == "   " && !segment.IsChanged);
    }

    [Theory]
    [InlineData("\t \u2003", "", "")]
    [InlineData("", "\t \u2003", "")]
    [InlineData("\t ", " x ", "x")]
    public void CompareRetainsIgnoredWhitespaceWhenSignificantSideIsEmpty(
        string before, string after, string changedAfter)
    {
        var result = InlineTextDiffer.Compare(before, after, ignoreWhitespace: true, ignoreCase: true);

        Assert.Equal(before, Join(result.OldSegments));
        Assert.Equal(after, Join(result.NewSegments));
        Assert.DoesNotContain(result.OldSegments, segment => segment.IsChanged);
        Assert.Equal(changedAfter, Join(result.NewSegments.Where(segment => segment.IsChanged)));
    }

    [Fact]
    public void CompareOptionsPreserveEmojiAndCombiningGraphemes()
    {
        const string before = " A👩‍💻e\u0301 Z ";
        const string after = "\ta 👩‍💻E\u0301 Q";

        var result = InlineTextDiffer.Compare(before, after, ignoreWhitespace: true, ignoreCase: true);

        Assert.Equal(before, Join(result.OldSegments));
        Assert.Equal(after, Join(result.NewSegments));
        Assert.Equal("Z", Join(result.OldSegments.Where(segment => segment.IsChanged)));
        Assert.Equal("Q", Join(result.NewSegments.Where(segment => segment.IsChanged)));
        Assert.Contains(result.OldSegments, segment => !segment.IsChanged
            && segment.Text.Contains("👩‍💻e\u0301", StringComparison.Ordinal));
        Assert.Contains(result.NewSegments, segment => !segment.IsChanged
            && segment.Text.Contains("👩‍💻E\u0301", StringComparison.Ordinal));
    }

    [Fact]
    public void CommonEdgeFallbackHonorsOptionsAndKeepsOriginalWhitespace()
    {
        var before = new string('a', 600) + " X " + new string('b', 600);
        var after = string.Concat(Enumerable.Repeat("A ", 600)) + "\tY\t" + new string('B', 600);

        var result = InlineTextDiffer.Compare(before, after, ignoreWhitespace: true, ignoreCase: true);

        Assert.Equal(before, Join(result.OldSegments));
        Assert.Equal(after, Join(result.NewSegments));
        Assert.Equal("X", Join(result.OldSegments.Where(segment => segment.IsChanged)));
        Assert.Equal("Y", Join(result.NewSegments.Where(segment => segment.IsChanged)));
    }

    private static string Join(IEnumerable<InlineDiffSegment> segments)
    {
        return string.Concat(segments.Select(segment => segment.Text));
    }

    [Fact]
    public void LongParagraphWithTwoEditsKeepsItsUnchangedMiddle()
    {
        var middle = string.Concat(Enumerable.Range(0, 2500).Select(index => $"共同内容{index}，"));
        var before = "起点3" + middle + "终点10";
        var after = "起点5" + middle + "终点20";
        var result = InlineTextDiffer.Compare(before, after);
        Assert.Equal(before, Join(result.OldSegments));
        Assert.Equal(after, Join(result.NewSegments));
        Assert.Equal("31", Join(result.OldSegments.Where(segment => segment.IsChanged)));
        Assert.Equal("52", Join(result.NewSegments.Where(segment => segment.IsChanged)));
        Assert.False(result.IsApproximate);
    }

    [Fact]
    public void UnrelatedLongParagraphReportsApproximateRefinement()
    {
        var result = InlineTextDiffer.Compare(new string('甲', 1000), new string('乙', 1000));
        Assert.True(result.IsApproximate);
        Assert.Equal(new string('甲', 1000), Join(result.OldSegments));
        Assert.Equal(new string('乙', 1000), Join(result.NewSegments));
    }
}
