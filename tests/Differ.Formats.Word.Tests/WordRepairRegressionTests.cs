using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.Formats.Word.Tests;

public sealed class WordRepairRegressionTests
{
    [Theory]
    [InlineData("每日可刷新3次", "每日可刷新5次", "3", "5")]
    [InlineData("任务奖励金币", "任务奖励银币", "金", "银")]
    [InlineData("每轮 refresh 3 次，奖励100。", "每轮 refresh 5 次，奖励100。", "3", "5")]
    public void W01ChineseMicroEditIsOneModifiedParagraph(string before, string after, string removed, string added)
    {
        var result = Compare([before], [after]);
        Assert.Equal(1, result.Summary.Modified);
        Assert.Equal(0, result.Summary.Added);
        Assert.Equal(0, result.Summary.Deleted);
        var inline = InlineTextDiffer.Compare(before, after);
        Assert.Equal(removed, string.Concat(inline.OldSegments.Where(segment => segment.IsChanged).Select(segment => segment.Text)));
        Assert.Equal(added, string.Concat(inline.NewSegments.Where(segment => segment.IsChanged).Select(segment => segment.Text)));
    }

    [Fact]
    public void W02InsertionBeforeTwoEditsDoesNotShiftPairing()
    {
        var result = Compare(["第一章 商店", "每日可刷新3次", "每次消耗10金币", "结束"],
            ["第一章 商店", "新增加的完全不同的说明", "每日可刷新5次", "每次消耗20金币", "结束"]);
        Assert.Equal(1, result.Summary.Added);
        Assert.Equal(2, result.Summary.Modified);
        Assert.Equal(0, result.Summary.Deleted);
        Assert.Contains(result.Nodes, node => node.OldValue == "每日可刷新3次" && node.NewValue == "每日可刷新5次");
        Assert.Contains(result.Nodes, node => node.OldValue == "每次消耗10金币" && node.NewValue == "每次消耗20金币");
    }

    [Fact]
    public void W03RepeatedNotesStayInsideTheirChapter()
    {
        var result = Compare(["第一章 商店", "注意", "每日可刷新3次", "第二章 副本", "注意", "每日可挑战4次"],
            ["第一章 商店", "每日可刷新5次", "第二章 副本", "注意", "每日可挑战6次"]);
        Assert.Contains(result.Nodes, node => node.OldIndex == 2 && node.NewIndex is null);
        Assert.Contains(result.Nodes, node => node.OldIndex == 5 && node.NewIndex == 4 && node.Kind == DiffKind.Unchanged);
        Assert.Equal(2, result.Summary.Modified);
    }

    [Fact]
    public void W03LargeUnanchoredRangeHasExplicitBoundedFallbackAndNoDataLoss()
    {
        var before = Enumerable.Range(0, 5000).Select(index => $"甲乙丙旧章节描述{index}").ToArray();
        var after = Enumerable.Range(0, 5000).Select(index => $"天地人新玩法说明{index}").ToArray();
        var result = Compare(before, after);
        Assert.Contains(result.Warnings, value => value.Contains("预算", StringComparison.Ordinal));
        Assert.Equal(before, result.Nodes.Where(node => node.OldIndex is not null).Select(node => node.OldValue));
        Assert.Equal(after, result.Nodes.Where(node => node.NewIndex is not null).Select(node => node.NewValue));
        Assert.Single(result.Nodes.Select(node => node.Metadata["word.changeGroup"]).Distinct());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void W04SplitOrMergePreservesAllContentAndHasOneGroup(bool reverse)
    {
        string[] single = ["前锚点", "每日可刷新3次，每次消耗10金币。", "后锚点"];
        string[] split = ["前锚点", "每日可刷新3次，", "每次消耗10金币。", "后锚点"];
        var result = Compare(reverse ? split : single, reverse ? single : split);
        var changed = result.Nodes.Where(node => node.Kind != DiffKind.Unchanged).ToArray();
        Assert.Single(changed.Select(node => node.Metadata["word.changeGroup"]).Distinct());
        Assert.All(changed, node => Assert.Equal("split-merge", node.Metadata["word.groupCorrespondence"]));
        Assert.Equal(string.Concat(changed.Select(node => node.OldValue)), string.Concat(changed.Select(node => node.NewValue)));
        Assert.Equal(2, result.Summary.Unchanged);
    }

    [Fact]
    public void W04OneParagraphCanSplitIntoManyWithoutLosingCorrespondence()
    {
        var result = Compare(["甲乙丙丁戊己"], ["甲乙", "丙丁", "戊己"]);
        Assert.All(result.Nodes, node => Assert.Equal("split-merge", node.Metadata["word.groupCorrespondence"]));
    }

    [Fact]
    public void W05RewriteTwentyToFiveKeepsAnchorsAndOneUnresolvedGroup()
    {
        var oldContent = Enumerable.Range(1, 20).Select(index => $"旧规则：商店货币兑换说明 {index}");
        var newContent = Enumerable.Range(1, 5).Select(index => $"新设计：全新任务系统奖励 {index}");
        var result = Compare(["第一章 商店", .. oldContent, "第二章 副本"],
            ["第一章 商店", .. newContent, "第二章 副本"]);
        Assert.Equal(DiffKind.Unchanged, result.Nodes[0].Kind);
        Assert.Equal(DiffKind.Unchanged, result.Nodes[^1].Kind);
        var changed = result.Nodes.Where(node => node.Kind != DiffKind.Unchanged).ToArray();
        Assert.Single(changed.Select(node => node.Metadata["word.changeGroup"]).Distinct());
        Assert.Equal(20, changed.Count(node => node.OldIndex is not null));
        Assert.Equal(5, changed.Count(node => node.NewIndex is not null));
    }

    [Fact]
    public void W08OptionsAffectMatchingWithoutChangingOriginalText()
    {
        var result = Compare(["任务 A 可刷新 3 次"], ["任务a可刷新3次"],
            new DiffOptions { IgnoreWhitespace = true, IgnoreCase = true });
        Assert.Equal(0, result.Summary.Changed);
        Assert.Equal("任务 A 可刷新 3 次", result.Nodes[0].OldValue);
        Assert.Equal("任务a可刷新3次", result.Nodes[0].NewValue);
    }

    [Fact]
    public void W08FilteringUnchangedNodesDoesNotJoinSeparateChangeGroups()
    {
        var result = Compare(["甲删除", "乙删除", "稳定锚点", "丙删除", "丁删除"], ["稳定锚点"],
            new DiffOptions { IncludeUnchanged = false });
        Assert.Equal(4, result.Summary.Deleted);
        Assert.Equal(2, result.Nodes.Select(node => node.Metadata["word.changeGroup"]).Distinct().Count());
    }

    private static DiffDocument Compare(IReadOnlyList<string> before, IReadOnlyList<string> after, DiffOptions? options = null)
    {
        static WordLogicalDocument Document(IReadOnlyList<string> text) => new(text.Select((value, index) =>
            new WordBlock("Paragraph", $"正文/段落 {index + 1}", value,
                new Dictionary<string, string> { ["word.blockKind"] = "paragraph" })).ToArray(), []);
        return WordBlockComparer.BuildDocument(new DiffRequest { RelativePath = "controlled.docx", Options = options ?? new() },
            Document(before), Document(after));
    }
}
