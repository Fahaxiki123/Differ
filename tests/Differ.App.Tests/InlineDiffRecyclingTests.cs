using System.Windows.Documents;
using Differ.App.Controls;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.Tests;

public sealed class InlineDiffRecyclingTests
{
    [Fact]
    public void RecycledTextDoesNotRetainOldRunsOrChangedFormatting()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var text = new InlineDiffTextBlock
            {
                Segments = new[] { new InlineDiffSegment("原始普通值", false) }
            };
            Assert.Equal("原始普通值", text.Text);

            text.DiffKind = DiffKind.Modified;
            text.Segments = new[]
            {
                new InlineDiffSegment("新值：", false),
                new InlineDiffSegment("变化", true)
            };
            Assert.Equal("新值：变化", new TextRange(text.ContentStart, text.ContentEnd).Text);
            Assert.Equal(2, text.Inlines.OfType<Run>().Count());

            text.DiffKind = DiffKind.Unchanged;
            text.Segments = new[] { new InlineDiffSegment("后续普通值", false) };
            Assert.Equal("后续普通值", text.Text);
            Assert.DoesNotContain(text.Inlines.OfType<Run>(), run => run.Text.Contains("变化", StringComparison.Ordinal));

            text.Segments = null;
            Assert.Equal(string.Empty, text.Text);
            text.Segments = new[]
            {
                new InlineDiffSegment("多段", false),
                new InlineDiffSegment("普通值", false)
            };
            Assert.Equal("多段普通值", text.Text);
        });
    }
}
