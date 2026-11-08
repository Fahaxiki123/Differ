using System.IO;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.Abstractions;
using Differ.Core.External;
using Differ.Core.Models;
using Differ.Core.Services;
using Differ.Formats.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Logging.Abstractions;

namespace Differ.App.Tests;

public sealed class PresentationRegressionTests
{
    [Fact]
    public void IgnoredDifferenceKeepsBothOriginalStrings()
    {
        var node = new DiffNode { StableId = "text:0", Path = "line", Kind = DiffKind.Unchanged, OldValue = "Alpha  value", NewValue = "alpha value" };
        var presentation = ComparisonPresentationBuilder.Build(Document([node]), CancellationToken.None);
        var row = Assert.Single(presentation.AllTextRows);
        Assert.Equal("Alpha  value", string.Concat(row.OldSegments.Select(segment => segment.Text)));
        Assert.Equal("alpha value", string.Concat(row.NewSegments.Select(segment => segment.Text)));
        Assert.All(row.NewSegments, segment => Assert.False(segment.IsChanged));
    }

    [Fact]
    public async Task HiddenColumnChangeIsCountedNavigableAndReadableAsync()
    {
        using var files = new TemporaryComparison();
        MakeWorkbook(files.BasePath, false, "one", "two");
        MakeWorkbook(files.MinePath, true, "one", "two");
        using var scheduler = new ComparisonLoadScheduler();
        using var viewModel = files.CreateViewModel(new ExcelDiffAdapter(), scheduler);
        await viewModel.LoadAsync();
        Assert.False(viewModel.HasError, viewModel.ErrorText);
        var sheet = Assert.Single(viewModel.WorksheetTabs);
        Assert.True(viewModel.DifferenceCount > 0);
        Assert.Equal(viewModel.DifferenceCount, sheet.DifferenceCount);
        Assert.Equal(sheet.DifferenceCount, sheet.NavigableDifferenceCount);
        Assert.True(viewModel.NextDifferenceCommand.CanExecute(null));
        Assert.NotNull(sheet.SelectedCell);
        Assert.Equal(-1, sheet.SelectedCell.ColumnIndex);
        Assert.NotEqual(sheet.SelectedCell.BaseText, sheet.SelectedCell.MineText);
        Assert.All(sheet.Rows, row => Assert.Equal(DiffKind.Unchanged, row.Kind));
        viewModel.ShowUnchanged = false;
        Assert.NotNull(sheet.SelectedCell);
        Assert.True(viewModel.NextDifferenceCommand.CanExecute(null));
    }

    [Fact]
    public void ChangedLineHighlightsOnlyImportantChangesWhenOptionsAreApplied()
    {
        var node = new DiffNode { StableId = "line", Path = "line", Kind = DiffKind.Modified, OldValue = "Name  = 10", NewValue = "name = 20" };
        var presentation = ComparisonPresentationBuilder.Build(Document([node]), CancellationToken.None,
            new DiffOptions { IgnoreCase = true, IgnoreWhitespace = true });
        var row = Assert.Single(presentation.AllTextRows);
        Assert.Equal("Name  = 10", string.Concat(row.OldSegments.Select(segment => segment.Text)));
        Assert.Equal("name = 20", string.Concat(row.NewSegments.Select(segment => segment.Text)));
        Assert.Equal("1", string.Concat(row.OldSegments.Where(segment => segment.IsChanged).Select(segment => segment.Text)));
        Assert.Equal("2", string.Concat(row.NewSegments.Where(segment => segment.IsChanged).Select(segment => segment.Text)));
    }

    [Fact]
    public async Task EachChangedCellInSameRowHasItsOwnNavigationTargetAsync()
    {
        using var files = new TemporaryComparison();
        MakeWorkbook(files.BasePath, false, "before A", "before B");
        MakeWorkbook(files.MinePath, false, "after A", "after B");
        using var scheduler = new ComparisonLoadScheduler();
        using var viewModel = files.CreateViewModel(new ExcelDiffAdapter(), scheduler);
        await viewModel.LoadAsync();
        Assert.False(viewModel.HasError, viewModel.ErrorText);
        var sheet = Assert.Single(viewModel.WorksheetTabs);
        Assert.Equal(2, sheet.DifferenceCount);
        Assert.Equal("A1", sheet.SelectedCell?.MineAddress);
        viewModel.NextDifferenceCommand.Execute(null);
        Assert.Equal("B1", sheet.SelectedCell?.MineAddress);
        Assert.Equal(1, sheet.SelectedDifferenceIndex);
        viewModel.PreviousDifferenceCommand.Execute(null);
        Assert.Equal("A1", sheet.SelectedCell?.MineAddress);
    }

    [Fact]
    public async Task ReloadCancelsOldOptionsAndNeverAppliesStaleResultAsync()
    {
        using var files = new TemporaryComparison();
        await File.WriteAllTextAsync(files.BasePath, "base");
        await File.WriteAllTextAsync(files.MinePath, "mine");
        var adapter = new DelayedAdapter();
        using var scheduler = new ComparisonLoadScheduler();
        using var viewModel = files.CreateViewModel(adapter, scheduler, "sample.txt");
        var first = viewModel.LoadAsync();
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        viewModel.Options.IgnoreCase = true;
        await viewModel.ReloadAsync();
        Assert.Equal("new options", Assert.Single(viewModel.TextRows).NewText);
        adapter.Release.TrySetResult();
        await first;
        await adapter.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("new options", Assert.Single(viewModel.TextRows).NewText);
        Assert.False(viewModel.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddedOrRemovedCellWithinExistingRowNavigatesToCellAsync(bool reverse)
    {
        using var files = new TemporaryComparison();
        MakeWorkbook(files.BasePath, false, "one", "two");
        MakeWorkbook(files.MinePath, false, "one", "two");
        var removePath = reverse ? files.MinePath : files.BasePath;
        using (var document = SpreadsheetDocument.Open(removePath, true))
        {
            var sheet = document.WorkbookPart!.WorksheetParts.Single();
            var worksheet = Assert.IsType<Worksheet>(sheet.Worksheet);
            worksheet.Descendants<Cell>().Single(cell => cell.CellReference == "B1").Remove();
            worksheet.Save();
        }

        using var scheduler = new ComparisonLoadScheduler();
        using var viewModel = files.CreateViewModel(new ExcelDiffAdapter(), scheduler);
        await viewModel.LoadAsync();
        Assert.False(viewModel.HasError, viewModel.ErrorText);
        var sheetModel = Assert.Single(viewModel.WorksheetTabs);
        Assert.Equal(1, sheetModel.DifferenceCount);
        Assert.Equal(1, sheetModel.SelectedCell?.ColumnIndex);
        Assert.Equal(0, sheetModel.SelectedDifferenceIndex);
        Assert.NotNull(sheetModel.SelectedRow);
    }

    [Fact]
    public void AlignedWordTableRowsDoNotMergeDeletedAndRemainingRows()
    {
        var nodes = Enumerable.Range(0, 2).Select(index => new DiffNode
        {
            StableId = $"cell:{index}",
            Path = "table",
            Kind = index == 0 ? DiffKind.Deleted : DiffKind.Unchanged,
            OldValue = $"old {index}",
            NewValue = index == 0 ? null : "remaining",
            Metadata = new Dictionary<string, string>
            {
                ["old.word.tableKey"] = "table",
                ["old.word.tableRow"] = "1",
                ["old.word.tableColumn"] = "1",
                ["old.word.tableColumnCount"] = "1",
                ["word.alignedTableKey"] = "aligned",
                ["word.alignedTableRow"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        }).ToArray();
        var presentation = ComparisonPresentationBuilder.Build(Document(nodes) with { FileKind = FileKind.Word }, CancellationToken.None);
        Assert.Equal(2, presentation.AllWordRows.Count);
        Assert.Equal("remaining", presentation.AllWordRows[1].Cells[0].NewText);
    }

    private static DiffDocument Document(IReadOnlyList<DiffNode> nodes) => new()
    {
        RelativePath = "test.txt",
        FileKind = FileKind.Text,
        Nodes = nodes,
        Summary = DiffSummary.FromNodes(nodes)
    };

    private static void MakeWorkbook(string path, bool hidden, string a, string b)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new Workbook();
        var sheet = workbook.AddNewPart<WorksheetPart>();
        sheet.Worksheet = new Worksheet(new Columns(new Column { Min = 1, Max = 1, Hidden = hidden }),
            new SheetData(new Row(
                new Cell { CellReference = "A1", DataType = CellValues.InlineString, InlineString = new InlineString(new Text(a)) },
                new Cell { CellReference = "B1", DataType = CellValues.InlineString, InlineString = new InlineString(new Text(b)) })
            { RowIndex = 1 }));
        workbook.Workbook.Append(new Sheets(new Sheet { Name = "Sheet1", SheetId = 1, Id = workbook.GetIdOfPart(sheet) }));
        workbook.Workbook.Save();
    }

    private sealed class TemporaryComparison : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Differ.App.Tests", Guid.NewGuid().ToString("N"));
        public TemporaryComparison() => Directory.CreateDirectory(_root);
        public string BasePath => Path.Combine(_root, "base.xlsx");
        public string MinePath => Path.Combine(_root, "mine.xlsx");

        public DiffWindowViewModel CreateViewModel(IFileDiffAdapter adapter, ComparisonLoadScheduler scheduler, string name = "test.xlsx") => new(
            new DiffCoordinator([adapter]), scheduler, new ExternalDiffSession
            {
                SessionId = Guid.NewGuid(),
                BaseSnapshotPath = BasePath,
                MineSnapshotPath = MinePath,
                Request = new ExternalDiffRequest { BaseFilePath = BasePath, MineFilePath = MinePath, OriginalFileName = name }
            }, NullLogger<DiffWindowViewModel>.Instance, _ => { });

        public void Dispose() => Directory.Delete(_root, true);
    }

    private sealed class DelayedAdapter : IFileDiffAdapter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FileKind FileKind => FileKind.Text;
        public bool CanHandle(string path) => true;

        public async Task<DiffDocument> CompareAsync(DiffRequest request, CancellationToken cancellationToken)
        {
            if (!request.Options.IgnoreCase)
            {
                Started.TrySetResult();
                await Release.Task;
                Finished.TrySetResult();
            }

            return Document([new DiffNode
            {
                StableId = "line", Path = "line", Kind = DiffKind.Modified,
                OldValue = "old", NewValue = request.Options.IgnoreCase ? "new options" : "stale"
            }]);
        }
    }
}
