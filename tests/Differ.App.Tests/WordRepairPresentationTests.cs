using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using Differ.App.Controls;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.Tests;

public sealed class WordRepairPresentationTests
{
    [Fact]
    public void OrdinaryParagraphsUseTextBlockAndCharacterSelectionIsCreatedOnlyOnRequest()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var host = new WordParagraphHost { Segments = [new InlineDiffSegment("普通中文段落", false)] };
            var text = Assert.IsType<WordInlineDiffTextBlock>(host.Content);
            Assert.Equal("普通中文段落", text.Text);
            host.EnableCharacterSelection();
            var rich = Assert.IsType<WordParagraphPresenter>(host.Content);
            Pump();
            Assert.Equal("普通中文段落", new TextRange(rich.Document.Blocks.FirstBlock!.ContentStart,
                rich.Document.Blocks.FirstBlock.ContentEnd).Text);
            host.Segments = [new InlineDiffSegment("回收后的新段落", false)];
            Assert.Same(text, host.Content);
            Assert.Equal("回收后的新段落", text.Text);
            host.FirstLineIndent = -24;
            Assert.IsType<WordParagraphPresenter>(host.Content);
            host.FirstLineIndent = 0;
            Assert.IsType<WordInlineDiffTextBlock>(host.Content);
        });
    }

    [Fact]
    public void W05RewriteIsOneReviewRegionWithIndependentSidesAndRecoverablePreview()
    {
        var presentation = ComparisonPresentationBuilder.Build(GroupDocument(20, 5), CancellationToken.None);
        var display = Assert.Single(presentation.AllWordRows);
        var group = Assert.IsType<WordChangeGroupViewModel>(display.Group);
        Assert.Equal(20, group.BaseCount);
        Assert.Equal(5, group.MineCount);
        Assert.Equal(25, display.ContentRows.Count);
        Assert.Single(presentation.ChangedTextRows);
        Assert.Equal(3, group.VisibleBaseRows.Count);
        Assert.Equal(3, group.VisibleMineRows.Count);
        Assert.Contains("17", group.WindowLabel, StringComparison.Ordinal);
        var count = presentation.Summary.Changed;
        group.ToggleCommand.Execute(null);
        Assert.Equal(20, group.VisibleBaseRows.Count);
        Assert.Equal(5, group.VisibleMineRows.Count);
        Assert.Equal(count, presentation.Summary.Changed);
        Assert.Equal(string.Join(Environment.NewLine, Enumerable.Range(1, 20).Select(index => $"旧段落{index}")), DiffClipboard.FormatWordSide(display, true));
        Assert.Equal(string.Join(Environment.NewLine, Enumerable.Range(1, 5).Select(index => $"新段落{index}")), DiffClipboard.FormatWordSide(display, false));
        Assert.All(display.ContentRows, row => Assert.Equal(0, presentation.InteractionIndex.ChangedTextRowIndexes[row.StableId]));
    }

    [Fact]
    public void W05LargeGroupMaterializesOnlyBoundedWindowAndCanRevealEveryParagraph()
    {
        var presentation = ComparisonPresentationBuilder.Build(GroupDocument(5000, 1), CancellationToken.None);
        var display = Assert.Single(presentation.AllWordRows);
        var group = Assert.IsType<WordChangeGroupViewModel>(display.Group);
        var target = display.ContentRows[4000];
        group.Reveal(target);
        Assert.True(group.IsExpanded);
        Assert.Contains(group.VisibleBaseRows, row => row.StableId == target.StableId);
        Assert.Equal(target.StableId, group.VisibleBaseRows[0].StableId);
        Assert.InRange(group.VisibleBaseRows.Count, 1, DiffPerformanceOptions.MaximumWordGroupWindowParagraphs);
        Assert.True(group.PreviousCommand.CanExecute(null));
        Assert.True(group.NextCommand.CanExecute(null));
        Assert.Equal(5001, presentation.AllTextRows.Count);
        Assert.Equal(5001, presentation.InteractionIndex.TextLookup.Count);
    }

    [Fact]
    public void W05SearchAtWindowEndBecomesFirstVisibleParagraph()
    {
        var presentation = ComparisonPresentationBuilder.Build(GroupDocument(100, 1), CancellationToken.None);
        var display = Assert.Single(presentation.AllWordRows);
        var group = Assert.IsType<WordChangeGroupViewModel>(display.Group);
        group.ToggleCommand.Execute(null);
        Assert.Equal(24, group.VisibleBaseRows.Count);
        var target = display.ContentRows[23];
        group.Reveal(target);
        Assert.Equal(target.StableId, group.VisibleBaseRows[0].StableId);
        Assert.InRange(group.VisibleBaseRows.Count, 1, 24);
        Assert.Equal(101, display.ContentRows.Count);
        group.NextCommand.Execute(null);
        Assert.Equal(display.ContentRows[47].StableId, group.VisibleBaseRows[0].StableId);
        group.PreviousCommand.Execute(null);
        Assert.Equal(target.StableId, group.VisibleBaseRows[0].StableId);
    }

    [Fact]
    public void W07WordCharacterRefinementIsLazyAndHasDiagnosticFallback()
    {
        var node = new DiffNode
        {
            StableId = "word:0",
            Path = "正文/段落1",
            Kind = DiffKind.Modified,
            OldValue = new string('甲', 2000),
            NewValue = new string('乙', 2000),
            OldIndex = 1,
            NewIndex = 1
        };
        var presentation = ComparisonPresentationBuilder.Build(Document([node]), CancellationToken.None);
        var row = Assert.Single(presentation.AllTextRows);
        Assert.NotNull(row.InlineComparison);
        Assert.False(row.InlineComparison.IsValueCreated);
        Assert.NotEmpty(row.OldSegments);
        Assert.True(row.InlineComparison.IsValueCreated);
        Assert.Contains("预算", row.InlineDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void W08WordInlineHonorsWhitespaceAndCaseOptions()
    {
        var node = new DiffNode
        {
            StableId = "word:0",
            Path = "paragraph",
            Kind = DiffKind.Modified,
            OldValue = "任务 A 可刷新 3 次",
            NewValue = "任务a可刷新5次",
            OldIndex = 1,
            NewIndex = 1
        };
        var presentation = ComparisonPresentationBuilder.Build(Document([node]), CancellationToken.None,
            new DiffOptions { IgnoreWhitespace = true, IgnoreCase = true });
        var row = Assert.Single(presentation.AllTextRows);
        Assert.Equal("3", string.Concat(row.OldSegments.Where(part => part.IsChanged).Select(part => part.Text)));
        Assert.Equal("5", string.Concat(row.NewSegments.Where(part => part.IsChanged).Select(part => part.Text)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void W06FirstLineAndHangingIndentAreIndependentOfLeftMargin(bool hanging)
    {
        var row = new TextDiffRowViewModel("paragraph", DiffKind.Unchanged, 1, 1, "内容", "内容", [], [])
        {
            Metadata = new Dictionary<string, string>
            {
                ["old.word.leftTwips"] = "720",
                [hanging ? "old.word.hangingTwips" : "old.word.firstLineTwips"] = "360"
            }
        };
        Assert.Equal(48, row.BaseLeftInset);
        Assert.Equal(hanging ? -24 : 24, row.BaseFirstLineInset);
        Assert.Equal(hanging ? 24 : 48, row.BaseTextLeftInset);
        ThemeTestSupport.RunSta(() =>
        {
            var presenter = new WordParagraphPresenter
            {
                Width = 220,
                FontSize = 13.5,
                FirstLineIndent = row.BaseParagraphFirstLine,
                Segments = [new InlineDiffSegment("这是用于检查首行与后续行位置的中文长段落，正文应该在窄窗口中自然换行并保留正确的缩进。", false)]
            };
            var window = new Window
            {
                Width = 320,
                Height = 250,
                Content = presenter,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -20_000,
                Top = -20_000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            try
            {
                window.Show();
                Pump();
                window.UpdateLayout();
                var paragraph = Assert.IsType<Paragraph>(presenter.Document.Blocks.FirstBlock);
                Assert.Equal(hanging ? -24 : 24, paragraph.TextIndent);
                var first = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                var next = paragraph.ContentStart.GetLineStartPosition(1)!.GetCharacterRect(LogicalDirection.Forward);
                Assert.True(first.Left >= 0, $"First character is clipped: {first}");
                Assert.True(hanging ? first.Left < next.Left : first.Left > next.Left,
                    $"First={first}, next={next}, hanging={hanging}");
                Assert.True(first.Top < next.Top);
                Assert.Equal("这是用于检查首行与后续行位置的中文长段落，正文应该在窄窗口中自然换行并保留正确的缩进。", new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text);
                Assert.All(paragraph.Inlines.OfType<Run>(), run => Assert.Null(run.Background));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void W06ListMarkerAndBodyHaveSeparatePositions()
    {
        var row = new TextDiffRowViewModel("paragraph", DiffKind.Unchanged, 1, 1, "内容", "内容", [], [])
        { Metadata = new Dictionary<string, string> { ["old.word.leftTwips"] = "720", ["old.word.hangingTwips"] = "360", ["old.word.listMarker"] = "1." } };
        Assert.Equal(24, row.BaseListMarkerInset);
        Assert.Equal(48, row.BaseTextLeftInset);
        Assert.Equal(0, row.BaseParagraphFirstLine);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static DiffDocument GroupDocument(int baseCount, int mineCount)
    {
        var nodes = Enumerable.Range(1, baseCount).Select(index => new DiffNode
        {
            StableId = $"old:{index}",
            Path = $"正文/段落 {index}",
            Kind = DiffKind.Deleted,
            OldValue = $"旧段落{index}",
            OldIndex = index,
            Metadata = new Dictionary<string, string> { ["word.changeGroup"] = "group", ["word.groupCorrespondence"] = "unresolved" }
        }).Concat(Enumerable.Range(1, mineCount).Select(index => new DiffNode
        {
            StableId = $"new:{index}",
            Path = $"正文/段落 {index}",
            Kind = DiffKind.Added,
            NewValue = $"新段落{index}",
            NewIndex = index,
            Metadata = new Dictionary<string, string> { ["word.changeGroup"] = "group", ["word.groupCorrespondence"] = "unresolved" }
        })).ToArray();
        return Document(nodes);
    }

    private static DiffDocument Document(IReadOnlyList<DiffNode> nodes) => new()
    { RelativePath = "controlled.docx", FileKind = FileKind.Word, Nodes = nodes, Summary = DiffSummary.FromNodes(nodes) };
}
