using Differ.App.ViewModels;
using Differ.Core.Models;

namespace Differ.App.Tests;

public sealed class PresentationAllocationTests
{
    [Fact]
    public void UnchangedContextSharesImmutableCellsAndRetainsTheCompleteGrid()
    {
        const int rowCount = 200;
        const int columnCount = 8;
        var rows = Enumerable.Range(1, rowCount).Select(row => new TabularDiffRow
        {
            StableId = $"row:{row}",
            OldIndex = row,
            NewIndex = row,
            Kind = DiffKind.Unchanged,
            Cells = Enumerable.Range(0, columnCount).Select(column =>
            {
                var key = ((char)('A' + column)).ToString();
                var text = $"条目 {row}，列 {key}";
                return Cell(key, $"{key}{row}", $"{key}{row}", text, text);
            }).ToArray()
        }).ToArray();
        var document = Document(rows);

        var presentation = ComparisonPresentationBuilder.Build(document, CancellationToken.None);
        var sheet = Assert.Single(presentation.WorksheetTabs);

        Assert.Equal(rowCount, sheet.Rows.Count);
        Assert.Equal(columnCount, sheet.Columns.Count);
        Assert.Equal(rowCount * columnCount, presentation.AllRows.Count);
        Assert.Equal(document.Summary, presentation.Summary);
        foreach (var row in sheet.Rows)
        {
            for (var column = 0; column < columnCount; column++)
            {
                var oldCell = row.BaseCells[column];
                var newCell = row.MineCells[column];
                Assert.Same(oldCell, newCell);
                Assert.Equal(oldCell.Text, string.Concat(oldCell.Segments.Select(segment => segment.Text)));
                Assert.Equal(oldCell.DetailsText, string.Concat(newCell.DetailsSegments.Select(segment => segment.Text)));
                Assert.Contains(oldCell.Address, newCell.ToolTipText, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void AlignedRowsKeepTheirDifferentAddressesAndSelectionDetails()
    {
        var row = new TabularDiffRow
        {
            StableId = "shifted-row",
            OldIndex = 12,
            NewIndex = 13,
            Kind = DiffKind.Unchanged,
            Cells = [Cell("A", "A12", "A13", "相同内容", "相同内容")]
        };
        var presentation = ComparisonPresentationBuilder.Build(Document([row]), CancellationToken.None);
        var sheet = Assert.Single(presentation.WorksheetTabs);
        var displayed = Assert.Single(sheet.Rows);

        Assert.NotSame(displayed.BaseCells[0], displayed.MineCells[0]);
        Assert.Equal("A12", displayed.BaseCells[0].Address);
        Assert.Equal("A13", displayed.MineCells[0].Address);
        Assert.Equal($"A12{Environment.NewLine}相同内容", displayed.BaseCells[0].ToolTipText);
        Assert.Equal($"A13{Environment.NewLine}相同内容", displayed.MineCells[0].ToolTipText);
        sheet.SelectCell(displayed, 0);
        Assert.Equal("A12", sheet.SelectedCell?.BaseAddress);
        Assert.Equal("A13", sheet.SelectedCell?.MineAddress);
        Assert.Equal("相同内容", sheet.SelectedCell?.BaseText);
        Assert.Equal("相同内容", sheet.SelectedCell?.MineText);
    }

    [Theory]
    [InlineData(DiffKind.Unchanged)]
    [InlineData(DiffKind.Modified)]
    public void EqualFormulaTextKeepsDistinctCachedValuesAndToolTips(DiffKind kind)
    {
        var oldDetails = "=SUM(A1:A9)\n缓存值：10";
        var newDetails = "=SUM(A1:A9)\n缓存值：20";
        var cell = Cell("A", "A10", "A10", "=SUM(A1:A9)", "=SUM(A1:A9)") with
        {
            OldDetails = oldDetails,
            NewDetails = newDetails,
            Kind = kind
        };
        var presentation = ComparisonPresentationBuilder.Build(Document([Row(cell, 10)]), CancellationToken.None);
        var sheet = Assert.Single(presentation.WorksheetTabs);
        var displayed = Assert.Single(sheet.Rows);

        Assert.NotSame(displayed.BaseCells[0], displayed.MineCells[0]);
        Assert.Equal(oldDetails, displayed.BaseCells[0].DetailsText);
        Assert.Equal(newDetails, displayed.MineCells[0].DetailsText);
        Assert.Equal($"A10{Environment.NewLine}{oldDetails}", displayed.BaseCells[0].ToolTipText);
        Assert.Equal($"A10{Environment.NewLine}{newDetails}", displayed.MineCells[0].ToolTipText);
        sheet.SelectCell(displayed, 0);
        Assert.Equal(oldDetails, sheet.SelectedCell?.BaseText);
        Assert.Equal(newDetails, sheet.SelectedCell?.MineText);
        Assert.Equal(oldDetails, string.Concat(sheet.SelectedCell!.BaseSegments.Select(segment => segment.Text)));
        Assert.Equal(newDetails, string.Concat(sheet.SelectedCell.MineSegments.Select(segment => segment.Text)));
        if (kind == DiffKind.Modified)
        {
            Assert.Contains(sheet.SelectedCell.BaseSegments, segment => segment.IsChanged && segment.Text.Contains('1'));
            Assert.Contains(sheet.SelectedCell.MineSegments, segment => segment.IsChanged && segment.Text.Contains('2'));
        }
    }

    [Theory]
    [InlineData("123456789", 90.8)]
    [InlineData("e\u0301😀中ABC", 90.8)]
    [InlineData("👩‍👩‍👧‍👦👩‍👩‍👧‍👦👩‍👩‍👧‍👦👩‍👩‍👧‍👦", 83.6)]
    public void WidthMeasurementRetainsUnicodeTextElementSemantics(string text, double expectedWidth)
    {
        var presentation = ComparisonPresentationBuilder.Build(
            Document([Row(Cell("A", "A1", "A1", text, text))]), CancellationToken.None);
        var sheet = Assert.Single(presentation.WorksheetTabs);

        Assert.Equal(expectedWidth, Assert.Single(sheet.Columns).Width, precision: 5);
        Assert.Equal(text, Assert.Single(sheet.Rows).BaseCells[0].Text);
    }

    [Fact]
    public void SaturatedColumnWidthStillPreservesLaterLongChangedCellContent()
    {
        var first = new string('a', 48);
        var oldText = new string('中', 300) + "原值";
        var newText = new string('中', 300) + "新值";
        var rows = new[]
        {
            Row(Cell("A", "A1", "A1", first, first)),
            Row(Cell("A", "A2", "A2", oldText, newText) with { Kind = DiffKind.Modified }, 2)
        };
        var presentation = ComparisonPresentationBuilder.Build(Document(rows), CancellationToken.None);
        var sheet = Assert.Single(presentation.WorksheetTabs);

        Assert.Equal(312, Assert.Single(sheet.Columns).Width);
        Assert.Equal(1, presentation.Summary.Changed);
        Assert.NotSame(sheet.Rows[1].BaseCells[0], sheet.Rows[1].MineCells[0]);
        Assert.Equal(oldText, sheet.Rows[1].BaseCells[0].Text);
        Assert.Equal(newText, sheet.Rows[1].MineCells[0].Text);
        sheet.SelectCell(sheet.Rows[1], 0);
        Assert.Equal(oldText, sheet.SelectedCell?.BaseText);
        Assert.Equal(newText, sheet.SelectedCell?.MineText);
    }

    private static TabularDiffCell Cell(string key, string oldAddress, string newAddress,
        string oldText, string newText) => new()
        {
            ColumnKey = key,
            OldAddress = oldAddress,
            NewAddress = newAddress,
            OldValue = oldText,
            NewValue = newText,
            OldDetails = oldText,
            NewDetails = newText,
            Kind = DiffKind.Unchanged
        };

    private static TabularDiffRow Row(TabularDiffCell cell, int index = 1) => new()
    {
        StableId = $"row:{index}",
        OldIndex = index,
        NewIndex = index,
        Kind = cell.Kind,
        Cells = [cell]
    };

    private static DiffDocument Document(TabularDiffRow[] rows)
    {
        var nodes = rows.SelectMany(row => row.Cells.Select(cell => new DiffNode
        {
            StableId = $"cell:{row.StableId}:{cell.ColumnKey}",
            Path = cell.NewAddress ?? string.Empty,
            Kind = cell.Kind,
            OldValue = cell.OldValue,
            NewValue = cell.NewValue,
            OldIndex = row.OldIndex,
            NewIndex = row.NewIndex,
            SectionKey = "Sheet1",
            Metadata = new Dictionary<string, string> { ["column"] = cell.ColumnKey }
        })).ToArray();
        return new DiffDocument
        {
            RelativePath = "presentation.xlsx",
            FileKind = FileKind.Excel,
            Nodes = nodes,
            Summary = DiffSummary.FromNodes(nodes),
            Sections = ["Sheet1"],
            TabularSections = [new TabularDiffSection
            {
                Key = "Sheet1",
                Columns = rows[0].Cells.Select(cell => cell.ColumnKey).ToArray(),
                Rows = rows
            }]
        };
    }
}
