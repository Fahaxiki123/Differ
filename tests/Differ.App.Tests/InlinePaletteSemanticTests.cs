using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Differ.App.Controls;
using Differ.App.Services;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.Tests;

public sealed class InlinePaletteSemanticTests
{
    [Fact]
    public void DeletedAndModifiedRunsKeepDistinctSemanticsAcrossThemeSwitches()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var resources = new ResourceDictionary();
            var host = new StackPanel { Resources = resources };
            var deleted = new InlineDiffTextBlock
            {
                DiffKind = DiffKind.Deleted,
                Segments = new[] { new InlineDiffSegment("removed", true) }
            };
            var modified = new InlineDiffTextBlock
            {
                DiffKind = DiffKind.Modified,
                Segments = new[] { new InlineDiffSegment("changed", true) }
            };
            host.Children.Add(deleted);
            host.Children.Add(modified);
            using var service = ThemeTestSupport.CreateService(
                resources,
                new FakeSystemThemeMonitor(AppTheme.Light));

            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                service.Apply(theme);
                var deletedRun = Assert.IsType<Run>(deleted.Inlines.FirstInline);
                var modifiedRun = Assert.IsType<Run>(modified.Inlines.FirstInline);
                var deletedColour = Assert.IsType<SolidColorBrush>(deletedRun.Foreground).Color;
                var modifiedColour = Assert.IsType<SolidColorBrush>(modifiedRun.Foreground).Color;

                Assert.Equal(
                    ThemeTestSupport.ResolveColour(resources, "Diff.Inline.Deleted.Brush"),
                    deletedColour);
                Assert.Equal(
                    ThemeTestSupport.ResolveColour(resources, "Diff.Inline.Modified.Brush"),
                    modifiedColour);
                Assert.NotEqual(deletedColour, modifiedColour);
                Assert.Null(deletedRun.Background);
                Assert.Null(modifiedRun.Background);
            }
        });
    }
}
