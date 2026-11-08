using System.Windows;
using System.Windows.Documents;
using Differ.App.Controls;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.Tests;

public sealed class SpreadsheetCellPresenterTests
{
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void ReusedPresenterPreservesDiffSemanticsAndClearsOldValues(AppTheme initialTheme)
    {
        ThemeTestSupport.RunSta(() =>
        {
            var presenter = new SpreadsheetCellPresenter();
            using var themes = ThemeTestSupport.CreateService(presenter.Resources, new FakeSystemThemeMonitor(initialTheme));
            themes.InitializeFromSystem();
            var text = Assert.IsType<InlineDiffTextBlock>(presenter.Child);
            Assert.Equal(string.Empty, text.Text);
            Assert.Null(presenter.ToolTip);
            Assert.Same(presenter.FindResource("PanelBrush"), presenter.Background);

            foreach (var kind in new[] { DiffKind.Added, DiffKind.Deleted, DiffKind.Modified, DiffKind.Moved, DiffKind.Conflict, DiffKind.Unsupported })
            {
                var value = Cell("row " + kind, kind, changed: true);
                presenter.Cell = value;
                Assert.Same(text, presenter.Child);
                Assert.Same(value.Segments, text.Segments);
                Assert.Equal(kind, text.DiffKind);
                Assert.Equal(value.ToolTipText, presenter.ToolTip);
                Assert.Same(presenter.FindResource("Diff.Cell." + kind), presenter.Background);
                var changed = Assert.Single(text.Inlines.OfType<Run>(), run => run.FontWeight == FontWeights.SemiBold);
                Assert.Null(changed.Background);
                themes.Toggle();
                Assert.Same(presenter.FindResource("Diff.Cell." + kind), presenter.Background);
            }

            var unchanged = Cell("recycled plain text", DiffKind.Unchanged, changed: false);
            presenter.Cell = unchanged;
            Assert.Same(text, presenter.Child);
            Assert.Equal(unchanged.Text, text.Text);
            Assert.Equal(DiffKind.Unchanged, text.DiffKind);
            Assert.Same(presenter.FindResource("PanelBrush"), presenter.Background);
            Assert.Equal(unchanged.ToolTipText, presenter.ToolTip);

            presenter.Cell = Cell(string.Empty, DiffKind.Unchanged, changed: false);
            Assert.Equal(string.Empty, text.Text);
            presenter.Cell = null;
            Assert.Same(text, presenter.Child);
            Assert.Equal(string.Empty, text.Text);
            Assert.Null(text.Segments);
            Assert.Null(presenter.ToolTip);
            Assert.Same(presenter.FindResource("PanelBrush"), presenter.Background);
        });
    }

    private static SpreadsheetCellViewModel Cell(string text, DiffKind kind, bool changed)
    {
        InlineDiffSegment[] segments = string.IsNullOrEmpty(text) ? [] : [new InlineDiffSegment(text, changed)];
        return new SpreadsheetCellViewModel("B2", text, text, "B2\n" + text, kind, segments, segments);
    }
}
