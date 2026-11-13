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
    [Fact]
    public void RecycledRowContextReplacesTheValueOnTheCorrectSide()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var oldCell = Cell("before", DiffKind.Unchanged, changed: false);
            var newCell = Cell("after", DiffKind.Unchanged, changed: false);
            var presenter = new SpreadsheetCellPresenter(isBaseSide: false, columnIndex: 0);
            var row = new SpreadsheetRowViewModel("row", 1, 1, DiffKind.Modified, [oldCell], [newCell]);
            presenter.UpdateRow(row);
            Assert.Same(newCell, presenter.Cell);
            presenter.DataContext = row with { MineCells = [oldCell] };
            Assert.Same(oldCell, presenter.Cell);
            presenter.DataContext = null;
            Assert.Null(presenter.Cell);
            Assert.Null(presenter.ToolTip);
        });
    }

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void ReusedPresenterPreservesDiffSemanticsAndClearsOldValues(AppTheme initialTheme)
    {
        ThemeTestSupport.RunSta(() =>
        {
            var presenter = new SpreadsheetCellPresenter(isBaseSide: true);
            using var themes = ThemeTestSupport.CreateService(presenter.Resources, new FakeSystemThemeMonitor(initialTheme));
            themes.InitializeFromSystem();
            var text = Assert.IsType<InlineDiffTextBlock>(presenter.Child);
            Assert.Equal(string.Empty, text.Text);
            Assert.Null(presenter.ToolTip);
            Assert.Same(presenter.FindResource("PanelBrush"), presenter.Background);

            foreach (var kind in new[] { DiffKind.Deleted, DiffKind.Modified, DiffKind.Moved, DiffKind.Conflict, DiffKind.Unsupported })
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

            var replacement = Cell("value replacement", DiffKind.Modified, changed: true) with { IsContentReplacement = true };
            presenter.Cell = replacement;
            Assert.Same(presenter.FindResource("Diff.Cell.Deleted"), presenter.Background);
            Assert.Equal(DiffKind.Deleted, text.DiffKind);

            var opposite = Cell(string.Empty, DiffKind.Added, changed: false);
            presenter.Cell = opposite;
            Assert.Same(presenter.FindResource("PanelBrush"), presenter.Background);
            Assert.Equal(string.Empty, text.Text);

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
