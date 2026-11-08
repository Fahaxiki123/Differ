using System.IO;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.Abstractions;
using Differ.Core.External;
using Differ.Core.Models;
using Differ.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Differ.App.Tests;

public sealed class ReviewInteractionTests
{
    [Fact]
    public async Task LargeSearchRunsInBackgroundAndFindsUnchangedContentAsync()
    {
        var nodes = Enumerable.Range(1, 4000).Select(i => new DiffNode
        {
            StableId = $"text:{i}",
            Path = $"text:{i}",
            OldIndex = i,
            NewIndex = i,
            OldValue = i == 3999 ? "needle" : "context",
            NewValue = i == 3999 ? "needle" : "context",
            Kind = DiffKind.Unchanged
        }).ToArray();
        using var fixture = new Fixture(new DiffDocument
        { RelativePath = "large.txt", FileKind = FileKind.Text, Nodes = nodes, Summary = DiffSummary.FromNodes(nodes) });
        await fixture.Model.LoadAsync();
        fixture.Model.FindText = "needle";
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)fixture.Model.FindNextCommand).ExecuteAsync(null);
        Assert.Equal(1, fixture.Model.FindMatchCount);
        Assert.Equal(3999, fixture.Model.SelectedTextRow?.NewIndex);
    }

    [Fact]
    public async Task ChangingQueryCancelsQueuedSearchWithoutPublishingStaleHitsAsync()
    {
        var nodes = Enumerable.Range(1, 4000).Select(i => new DiffNode
        { StableId = $"line:{i}", Path = "line", OldValue = "old query", NewValue = "old query", Kind = DiffKind.Unchanged }).ToArray();
        using var fixture = new Fixture(new DiffDocument
        { RelativePath = "large.txt", FileKind = FileKind.Text, Nodes = nodes, Summary = DiffSummary.FromNodes(nodes) });
        await fixture.Model.LoadAsync();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = fixture.Scheduler.RunAsync(() => true, _ => release.Task, CancellationToken.None);
        var second = fixture.Scheduler.RunAsync(() => true, _ => release.Task, CancellationToken.None);
        fixture.Model.FindText = "old query";
        var pending = ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)fixture.Model.FindNextCommand).ExecuteAsync(null);
        fixture.Model.FindText = "new query";
        release.SetResult(true);
        await Task.WhenAll(first, second, pending);
        Assert.Equal(0, fixture.Model.FindMatchCount);
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)fixture.Model.FindNextCommand).ExecuteAsync(null);
        Assert.Equal(0, fixture.Model.FindMatchCount);
    }
    private static readonly int[] ExpectedHiddenCounts = [11, 12];
    private static readonly string[] SpreadsheetColumns = ["A", "B"];

    [Theory]
    [InlineData(DiffDisplayMode.All)]
    [InlineData(DiffDisplayMode.DifferencesOnly)]
    [InlineData(DiffDisplayMode.Context)]
    public async Task StructureOnlyDifferenceRemainsInOverviewAndOpensDetailsAsync(DiffDisplayMode mode)
    {
        var document = SpreadsheetDocument();
        var structure = new DiffNode
        {
            StableId = "hidden-columns:First",
            Path = "First/隐藏列",
            SectionKey = "First",
            Kind = DiffKind.Modified,
            OldValue = "A",
            NewValue = "B",
            Metadata = new Dictionary<string, string> { ["custom.detail"] = "retained" }
        };
        var nodes = document.Nodes.Select(node => node with { Kind = DiffKind.Unchanged }).Append(structure).ToArray();
        var sections = document.TabularSections.Select(section => section with
        {
            Rows = section.Rows.Select(row => row with
            {
                Kind = DiffKind.Unchanged,
                Cells = row.Cells.Select(cell => cell with { Kind = DiffKind.Unchanged }).ToArray()
            }).ToArray()
        }).ToArray();
        using var fixture = new Fixture(document with
        { Nodes = nodes, Summary = DiffSummary.FromNodes(nodes), TabularSections = sections });
        await fixture.Model.LoadAsync();
        var model = fixture.Model;
        model.DisplayMode = mode;
        var marker = Assert.Single(model.DiffRows, node => node.Metadata.ContainsKey("overview.structure"));

        Assert.Equal("true", marker.Metadata["overview.structure"]);
        Assert.Equal("retained", marker.Metadata["custom.detail"]);
        Assert.False(structure.Metadata.ContainsKey("overview.structure"));
        Assert.Equal(model.SpreadsheetRows.Count + 1, model.DiffRows.Count);
        Assert.Equal(DiffKind.Modified, marker.Kind);
        model.SelectOverviewRow(marker);
        Assert.Equal(-1, model.SelectedSpreadsheetCell?.ColumnIndex);
        Assert.Equal("hidden-columns:First", model.SelectedSpreadsheetCell?.RowStableId);
        Assert.Equal(0, model.SelectedWorksheet?.SelectedDifferenceIndex);
        Assert.Equal(1, model.SelectedWorksheet?.NavigableDifferenceCount);
    }

    [Fact]
    public async Task WordDisplayLookupResolvesOnlyRowsInCurrentProjectionAsync()
    {
        using var fixture = new Fixture(WordDocument());
        await fixture.Model.LoadAsync();
        var model = fixture.Model;
        var first = model.TextRows[0];
        var changed = model.TextRows.First(row => row.HasDiffMarker);
        model.DisplayMode = DiffDisplayMode.Context;
        Assert.Null(model.ResolveWordDisplayRow(first));
        Assert.Contains(model.ResolveWordDisplayRow(changed), model.WordDisplayRows);
        var fold = model.WordDisplayRows.First(row => row.IsCollapsed);
        Assert.Same(fold, model.ResolveWordDisplayRow(fold.AnchorRow));

        Assert.True(model.GoTo("1"));
        Assert.Contains(model.ResolveWordDisplayRow(first), model.WordDisplayRows);
        model.DisplayMode = DiffDisplayMode.DifferencesOnly;
        Assert.Null(model.ResolveWordDisplayRow(first));
        Assert.Null(model.ResolveWordDisplayRow(fold.AnchorRow));
        Assert.Contains(model.ResolveWordDisplayRow(changed), model.WordDisplayRows);
    }

    [Theory]
    [InlineData(FileKind.Text)]
    [InlineData(FileKind.Word)]
    [InlineData(FileKind.Excel)]
    public async Task SelectingUnchangedOverviewRowKeepsDifferenceCountAndNavigationAsync(FileKind kind)
    {
        var document = kind switch
        {
            FileKind.Word => WordDocument(),
            FileKind.Excel => SpreadsheetDocument(),
            _ => TextDocument()
        };
        using var fixture = new Fixture(document);
        await fixture.Model.LoadAsync();
        var model = fixture.Model;
        var count = model.SelectedWorksheet?.NavigableDifferenceCount ?? document.Summary.Changed;
        model.SelectOverviewRow(model.DiffRows.First(row => row.Kind == DiffKind.Unchanged));

        Assert.Equal($"差异 — / {count}", model.DifferencePositionText);
        Assert.True(model.NextDifferenceCommand.CanExecute(null));
        model.NextDifferenceCommand.Execute(null);
        Assert.Equal($"差异 1 / {count}", model.DifferencePositionText);
    }

    [Fact]
    public async Task ComparisonWithoutDifferencesKeepsNoNavigableDifferenceMessageAsync()
    {
        var document = TextDocument();
        var nodes = document.Nodes.Select(node => node with { Kind = DiffKind.Unchanged }).ToArray();
        using var fixture = new Fixture(document with { Nodes = nodes, Summary = DiffSummary.FromNodes(nodes) });
        await fixture.Model.LoadAsync();
        fixture.Model.SelectOverviewRow(fixture.Model.DiffRows[0]);

        Assert.Equal("没有可导航的差异", fixture.Model.DifferencePositionText);
        Assert.False(fixture.Model.NextDifferenceCommand.CanExecute(null));
    }
    [Fact]
    public async Task ContextShowsThreeRowsAndExpandableExactHiddenCountsAsync()
    {
        using var fixture = new Fixture(TextDocument());
        await fixture.Model.LoadAsync();
        var model = fixture.Model;
        model.DisplayMode = DiffDisplayMode.Context;

        Assert.Equal(9, model.TextRows.Count);
        Assert.Equal(ExpectedHiddenCounts, model.TextRows.Where(row => row.IsCollapsed).Select(row => row.HiddenCount));
        Assert.Equal(Enumerable.Range(12, 7), model.TextRows.Where(row => !row.IsCollapsed).Select(row => row.NewIndex!.Value));
        Assert.Equal(model.TextRows.Count, model.DiffRows.Count);
        var folded = model.DiffRows[0];
        model.SelectOverviewRow(folded);
        Assert.Equal(1, model.SelectedTextRow?.NewIndex);
        Assert.Equal(19, model.TextRows.Count);
        Assert.Single(model.TextRows, row => row.IsCollapsed);
        Assert.Equal(30, model.TextRows.Where(row => !row.IsCollapsed).Count() + model.TextRows.Sum(row => row.HiddenCount));
    }

    [Fact]
    public async Task SearchHonorsFlagsAndRevealsCollapsedTargetsAsync()
    {
        using var fixture = new Fixture(TextDocument());
        await fixture.Model.LoadAsync();
        var model = fixture.Model;
        model.DisplayMode = DiffDisplayMode.Context;
        model.FindText = "needle";
        model.FindNext();
        Assert.Equal(2, model.FindMatchCount);
        Assert.Equal(1, model.FindMatchIndex);
        Assert.Equal(2, model.SelectedTextRow?.NewIndex);
        Assert.Contains(model.TextRows, row => row.NewIndex == 2);
        model.FindNext();
        Assert.Equal(15, model.SelectedTextRow?.NewIndex);
        model.FindPrevious();
        Assert.Equal(2, model.SelectedTextRow?.NewIndex);
        model.FindMatchCase = true;
        model.FindNext();
        Assert.Equal(1, model.FindMatchCount);
        Assert.Equal(2, model.SelectedTextRow?.NewIndex);
        model.FindMatchCase = false;
        model.FindDifferencesOnly = true;
        model.FindNext();
        Assert.Equal(1, model.FindMatchCount);
        Assert.Equal(15, model.SelectedTextRow?.NewIndex);
        model.FindText = "absent";
        model.FindNext();
        Assert.Equal(0, model.FindMatchCount);
        Assert.Equal(0, model.FindMatchIndex);
    }

    [Fact]
    public async Task GoToUsesOriginalCoordinatesAndMakesHiddenContentVisibleAsync()
    {
        using var fixture = new Fixture(TextDocument());
        await fixture.Model.LoadAsync();
        var model = fixture.Model;
        model.DisplayMode = DiffDisplayMode.Context;
        Assert.True(model.GoTo("29"));
        Assert.Equal(29, model.SelectedTextRow?.NewIndex);
        Assert.Equal(DiffDisplayMode.Context, model.DisplayMode);
        Assert.Single(model.TextRows, row => row.IsCollapsed);
        model.DisplayMode = DiffDisplayMode.DifferencesOnly;
        Assert.Single(model.TextRows);
        Assert.True(model.GoTo("3"));
        Assert.Equal(DiffDisplayMode.All, model.DisplayMode);
        Assert.Equal(3, model.SelectedTextRow?.NewIndex);
        Assert.False(model.GoTo("A3"));
        Assert.False(model.GoTo("300"));
        Assert.Equal(3, model.SelectedTextRow?.NewIndex);
    }

    [Fact]
    public async Task SpreadsheetSearchAndGoToCrossSheetsAndSelectExactCellAsync()
    {
        using var fixture = new Fixture(SpreadsheetDocument());
        await fixture.Model.LoadAsync();
        var model = fixture.Model;
        model.SelectedWorksheet = model.WorksheetTabs[0];
        model.DisplayMode = DiffDisplayMode.Context;
        Assert.True(model.GoTo("B123"));
        Assert.Equal("Other", model.SelectedWorksheet?.Name);
        Assert.Equal("B123", model.SelectedSpreadsheetCell?.MineAddress);
        Assert.True(model.GoTo("'First'!A2"));
        Assert.Equal("First", model.SelectedWorksheet?.Name);
        Assert.Equal("A2", model.SelectedSpreadsheetCell?.MineAddress);
        model.FindText = "target cell";
        model.FindNext();
        Assert.Equal("Other", model.SelectedWorksheet?.Name);
        Assert.Equal("B130", model.SelectedSpreadsheetCell?.MineAddress);
        Assert.Contains(model.SpreadsheetRows, row => row.MineRowNumber == 130);
        Assert.Equal(1, model.FindMatchCount);
        Assert.Equal(model.SpreadsheetRows.Count, model.DiffRows.Count);
        Assert.False(model.GoTo("Missing!A1"));
    }

    [Fact]
    public async Task SpreadsheetOverviewHasOneItemPerRowAndKeepsCellNavigationAsync()
    {
        using var fixture = new Fixture(SpreadsheetDocument());
        await fixture.Model.LoadAsync();
        var model = fixture.Model;
        model.SelectedWorksheet = model.WorksheetTabs[0];
        Assert.Equal(30, model.DiffRows.Count);
        Assert.Equal(2, model.SelectedWorksheet!.NavigableDifferenceCount);
        model.NextDifferenceCommand.Execute(null);
        Assert.Equal("B15", model.SelectedSpreadsheetCell?.MineAddress);
        model.DisplayMode = DiffDisplayMode.Context;
        Assert.Equal(9, model.SpreadsheetRows.Count);
        var folded = model.SpreadsheetRows.First(row => row.IsCollapsed);
        Assert.Equal(11, folded.HiddenCount);
        folded.ExpandCommand!.Execute(null);
        Assert.Equal(1, model.SelectedSpreadsheetRow?.MineRowNumber);
        Assert.Equal(model.SpreadsheetRows.Count, model.DiffRows.Count);
    }

    [Fact]
    public async Task WordContextCountsWholeTableRowsAndNeverSplitsChangedRowAsync()
    {
        using var fixture = new Fixture(WordDocument());
        await fixture.Model.LoadAsync();
        var model = fixture.Model;
        model.DisplayMode = DiffDisplayMode.Context;
        Assert.Equal(9, model.WordDisplayRows.Count);
        Assert.Equal(13, model.WordDisplayRows.Sum(row => row.HiddenCount));
        Assert.All(model.WordDisplayRows.Where(row => row.IsTable), row => Assert.Equal(2, row.ContentRows.Count));
        Assert.Equal(model.WordDisplayRows.Count, model.DiffRows.Count);
        model.DisplayMode = DiffDisplayMode.DifferencesOnly;
        Assert.Single(model.WordDisplayRows);
        Assert.Equal(2, model.TextRows.Count);
        Assert.Contains(model.TextRows, row => row.Kind == DiffKind.Unchanged);
    }

    [Fact]
    public async Task RecomparisonInvalidatesOldSearchResultsAsync()
    {
        using var fixture = new Fixture(TextDocument());
        await fixture.Model.LoadAsync();
        fixture.Model.FindText = "needle";
        fixture.Model.FindNext();
        Assert.Equal(2, fixture.Model.FindMatchCount);
        fixture.Adapter.Document = TextDocument() with
        {
            Nodes = [new DiffNode { StableId = "replacement", Path = "replacement", Kind = DiffKind.Modified,
                OldValue = "nothing", NewValue = "nothing", OldIndex = 1, NewIndex = 1 }]
        };
        await fixture.Model.ReloadAsync();
        fixture.Model.FindNext();
        Assert.Equal(0, fixture.Model.FindMatchCount);
        Assert.DoesNotContain(fixture.Model.TextRows, row => row.IsCollapsed);
    }

    private static DiffDocument TextDocument()
    {
        var nodes = Enumerable.Range(1, 30).Select(index => new DiffNode
        {
            StableId = $"text:{index}",
            Path = $"line {index}",
            OldIndex = index,
            NewIndex = index,
            Kind = index == 15 ? DiffKind.Modified : DiffKind.Unchanged,
            OldValue = index == 2 ? "needle" : index == 15 ? "Needle old" : $"line {index}",
            NewValue = index == 2 ? "needle" : index == 15 ? "Needle new" : $"line {index}"
        }).ToArray();
        return new DiffDocument
        {
            RelativePath = "test.txt",
            FileKind = FileKind.Text,
            Nodes = nodes,
            Summary = DiffSummary.FromNodes(nodes)
        };
    }

    private static DiffDocument WordDocument()
    {
        var nodes = Enumerable.Range(1, 20).SelectMany(row => Enumerable.Range(1, 2).Select(column => new DiffNode
        {
            StableId = $"word:{row}:{column}",
            Path = "table",
            OldIndex = (row - 1) * 2 + column,
            NewIndex = (row - 1) * 2 + column,
            Kind = row == 10 && column == 2 ? DiffKind.Modified : DiffKind.Unchanged,
            OldValue = $"cell {row},{column}",
            NewValue = $"cell {row},{column}",
            Metadata = new Dictionary<string, string>
            {
                ["word.alignedTableKey"] = "table",
                ["word.alignedTableRow"] = row.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["old.word.tableColumn"] = column.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["old.word.tableColumnCount"] = "2"
            }
        })).ToArray();
        return new DiffDocument
        {
            RelativePath = "test.docx",
            FileKind = FileKind.Word,
            Nodes = nodes,
            Summary = DiffSummary.FromNodes(nodes)
        };
    }

    private static DiffDocument SpreadsheetDocument()
    {
        var sections = new[] { (Name: "First", Start: 1, Changed: 15), (Name: "Other", Start: 123, Changed: 140) }
            .Select(sheet => new TabularDiffSection
            {
                Key = sheet.Name,
                Columns = ["A", "B"],
                Rows = Enumerable.Range(sheet.Start, 30).Select(row => new TabularDiffRow
                {
                    StableId = $"{sheet.Name}:row:{row}",
                    OldIndex = row,
                    NewIndex = row,
                    Kind = row == sheet.Changed ? DiffKind.Modified : DiffKind.Unchanged,
                    Cells = SpreadsheetColumns.Select(column => new TabularDiffCell
                    {
                        ColumnKey = column,
                        OldAddress = $"{column}{row}",
                        NewAddress = $"{column}{row}",
                        OldValue = sheet.Name == "Other" && row == 130 && column == "B" ? "target cell" : $"{column}{row}",
                        NewValue = sheet.Name == "Other" && row == 130 && column == "B" ? "target cell" : $"{column}{row}",
                        Kind = row == sheet.Changed ? DiffKind.Modified : DiffKind.Unchanged
                    }).ToArray()
                }).ToArray()
            }).ToArray();
        var nodes = sections.SelectMany(sheet => sheet.Rows.SelectMany(row => row.Cells.Select(cell => new DiffNode
        {
            StableId = $"{sheet.Key}:{cell.ColumnKey}:{row.NewIndex}",
            Path = "cell",
            OldIndex = row.OldIndex,
            NewIndex = row.NewIndex,
            OldValue = cell.OldValue,
            NewValue = cell.NewValue,
            Kind = cell.Kind,
            SectionKey = sheet.Key,
            Metadata = new Dictionary<string, string> { ["column"] = cell.ColumnKey }
        }))).ToArray();
        return new DiffDocument
        {
            RelativePath = "test.xlsx",
            FileKind = FileKind.Excel,
            Nodes = nodes,
            Summary = DiffSummary.FromNodes(nodes),
            Sections = sections.Select(section => section.Key).ToArray(),
            TabularSections = sections
        };
    }

    private sealed class StubAdapter(DiffDocument document) : IFileDiffAdapter
    {
        public DiffDocument Document { get; set; } = document;
        public FileKind FileKind => Document.FileKind;
        public bool CanHandle(string path) => true;
        public Task<DiffDocument> CompareAsync(DiffRequest request, CancellationToken cancellationToken) => Task.FromResult(Document);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Differ.Review.Tests", Guid.NewGuid().ToString("N"));
        private readonly ComparisonLoadScheduler _scheduler = new();
        public ComparisonLoadScheduler Scheduler => _scheduler;
        public Fixture(DiffDocument document)
        {
            Directory.CreateDirectory(_root);
            var before = Path.Combine(_root, "base");
            var after = Path.Combine(_root, "mine");
            File.WriteAllText(before, "base");
            File.WriteAllText(after, "mine");
            Adapter = new StubAdapter(document);
            Model = new DiffWindowViewModel(new DiffCoordinator([Adapter]), _scheduler,
                new ExternalDiffSession
                {
                    SessionId = Guid.NewGuid(),
                    BaseSnapshotPath = before,
                    MineSnapshotPath = after,
                    Request = new ExternalDiffRequest { BaseFilePath = before, MineFilePath = after, OriginalFileName = document.RelativePath }
                }, NullLogger<DiffWindowViewModel>.Instance, _ => { });
        }

        public StubAdapter Adapter { get; }
        public DiffWindowViewModel Model { get; }
        public void Dispose()
        {
            Model.Dispose();
            _scheduler.Dispose();
            Directory.Delete(_root, true);
        }
    }
}
