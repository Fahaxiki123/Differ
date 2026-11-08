using System.IO;
using System.Net;
using Differ.App.Resources;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.Abstractions;
using Differ.Core.External;
using Differ.Core.Models;
using Differ.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Differ.App.Tests;

public sealed class SessionActionsTests
{
    [Fact]
    public async Task HtmlReportDescribesAppliedRulesRatherThanPendingOptionsWithoutChangingTheDocumentAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.ViewModel.LoadAsync();
        var document = fixture.ViewModel.CurrentDocument;
        var output = Path.Combine(Path.GetDirectoryName(fixture.MinePath)!, "report.html");
        fixture.ViewModel.Options.IgnoreCase = true;
        Assert.True(fixture.ViewModel.HasPendingOptions);

        await fixture.ViewModel.ExportHtmlAsync(output);

        var pendingReport = WebUtility.HtmlDecode(await File.ReadAllTextAsync(output));
        Assert.Contains(Strings.RulesDefault, pendingReport, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.IgnoreCase, pendingReport, StringComparison.Ordinal);
        Assert.Same(document, fixture.ViewModel.CurrentDocument);
        Assert.Empty(document!.Warnings);

        await fixture.ViewModel.ReloadAsync();
        await fixture.ViewModel.ExportHtmlAsync(output);

        var appliedReport = WebUtility.HtmlDecode(await File.ReadAllTextAsync(output));
        Assert.Contains(Strings.IgnoreCase, appliedReport, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.PendingComparisonOptions, appliedReport, StringComparison.Ordinal);
        Assert.Empty(fixture.ViewModel.CurrentDocument!.Warnings);
    }

    [Fact]
    public async Task SuccessfulRefreshKeepsFrozenBaseAndOnlyThenRetiresOldSessionAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.ViewModel.LoadAsync();
        var oldSession = fixture.ViewModel.Session;
        var oldDocument = fixture.ViewModel.CurrentDocument;
        File.Delete(fixture.BasePath);
        await File.WriteAllTextAsync(fixture.MinePath, "second");

        await fixture.ViewModel.RefreshMineAsync();

        Assert.NotSame(oldDocument, fixture.ViewModel.CurrentDocument);
        Assert.Equal("BASE", fixture.ViewModel.CurrentDocument!.Nodes[0].OldValue);
        Assert.Equal("second", fixture.ViewModel.CurrentDocument.Nodes[0].NewValue);
        Assert.Equal("123", fixture.ViewModel.Session.Request.BaseRevision);
        Assert.False(Directory.Exists(oldSession.SessionDirectoryPath));
        Assert.Equal("second", await File.ReadAllTextAsync(fixture.MinePath));
        Assert.Single(Directory.GetDirectories(fixture.Store.RootPath));
    }

    [Fact]
    public async Task FailedComparisonPreservesOldDocumentAndSnapshotForRetryAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.ViewModel.LoadAsync();
        var session = fixture.ViewModel.Session;
        var document = fixture.ViewModel.CurrentDocument;
        await File.WriteAllTextAsync(fixture.MinePath, "broken");

        await fixture.ViewModel.RefreshMineAsync();

        Assert.Same(session, fixture.ViewModel.Session);
        Assert.Same(document, fixture.ViewModel.CurrentDocument);
        Assert.False(fixture.ViewModel.HasError);
        Assert.Contains("刷新失败", fixture.ViewModel.StatusText, StringComparison.Ordinal);
        Assert.Single(Directory.GetDirectories(fixture.Store.RootPath));
        await File.WriteAllTextAsync(fixture.MinePath, "fixed");
        await fixture.ViewModel.RefreshMineAsync();
        Assert.Equal("fixed", fixture.ViewModel.CurrentDocument!.Nodes[0].NewValue);
    }

    [Fact]
    public async Task EditingOptionsCancelsRefreshWithoutPublishingItsCandidateAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.ViewModel.LoadAsync();
        var session = fixture.ViewModel.Session;
        var document = fixture.ViewModel.CurrentDocument;
        await File.WriteAllTextAsync(fixture.MinePath, "waiting");
        var refresh = fixture.ViewModel.RefreshMineAsync();
        await fixture.Adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        fixture.ViewModel.Options.IgnoreCase = true;
        await refresh.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Same(session, fixture.ViewModel.Session);
        Assert.Same(document, fixture.ViewModel.CurrentDocument);
        Assert.True(fixture.ViewModel.HasPendingOptions);
        Assert.Contains(Strings.PendingComparisonOptions, fixture.ViewModel.Options.ActiveRulesSummary, StringComparison.Ordinal);
        Assert.Single(Directory.GetDirectories(fixture.Store.RootPath));
        await fixture.ViewModel.ReloadAsync();
        Assert.False(fixture.ViewModel.HasPendingOptions);
        Assert.Equal("first", fixture.ViewModel.CurrentDocument!.Nodes[0].NewValue);
    }

    [Fact]
    public async Task ClosingCancelsRefreshAndReclaimsItsCandidateAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.ViewModel.LoadAsync();
        await File.WriteAllTextAsync(fixture.MinePath, "waiting");
        var original = fixture.ViewModel.Session;
        var refresh = fixture.ViewModel.RefreshMineAsync();
        await fixture.Adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        fixture.ViewModel.Dispose();
        await refresh.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Single(Directory.GetDirectories(fixture.Store.RootPath));
        Assert.True(Directory.Exists(original.SessionDirectoryPath));
        Assert.Equal("first", await File.ReadAllTextAsync(original.MineSnapshotPath));
    }

    [Fact]
    public async Task ExplicitRulesSaveAndRestoreArePerFormatAndDoNotPersistTemporaryEditsAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.ViewModel.LoadAsync();
        fixture.ViewModel.Options.IgnoreCase = true;
        Assert.Null(await fixture.Preferences.LoadRulesAsync("text"));
        await fixture.ViewModel.SaveRulesCommand.ExecuteAsync(null);
        Assert.True((await fixture.Preferences.LoadRulesAsync("text"))!.IgnoreCase);
        Assert.Null(await fixture.Preferences.LoadRulesAsync("excel"));
        await fixture.ViewModel.RestoreRulesCommand.ExecuteAsync(null);
        Assert.False(fixture.ViewModel.Options.IgnoreCase);
        Assert.False(fixture.ViewModel.HasPendingOptions);
        Assert.Null(await fixture.Preferences.LoadRulesAsync("text"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly ComparisonLoadScheduler _scheduler;
        private Fixture(string root, ExternalDiffSessionStore store, ControlledAdapter adapter,
            ComparisonLoadScheduler scheduler, DiffWindowViewModel viewModel, UserPreferencesService preferences)
        {
            _root = root; Store = store; Adapter = adapter; _scheduler = scheduler;
            ViewModel = viewModel; Preferences = preferences;
        }
        public string BasePath => Path.Combine(_root, "base.txt");
        public string MinePath => Path.Combine(_root, "mine.txt");
        public ExternalDiffSessionStore Store { get; }
        public ControlledAdapter Adapter { get; }
        public DiffWindowViewModel ViewModel { get; }
        public UserPreferencesService Preferences { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "Differ.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var basePath = Path.Combine(root, "base.txt");
            var minePath = Path.Combine(root, "mine.txt");
            await File.WriteAllTextAsync(basePath, "BASE");
            await File.WriteAllTextAsync(minePath, "first");
            var store = new ExternalDiffSessionStore(Path.Combine(root, "sessions"));
            var session = await store.CreateAsync(new ExternalDiffRequest
            {
                BaseFilePath = basePath,
                MineFilePath = minePath,
                BaseRevision = "123"
            }, CancellationToken.None);
            var scheduler = new ComparisonLoadScheduler();
            var adapter = new ControlledAdapter();
            var viewModel = new DiffWindowViewModel(new DiffCoordinator([adapter]), scheduler, session,
                NullLogger<DiffWindowViewModel>.Instance, _ => { });
            var preferences = new UserPreferencesService(Path.Combine(root, "preferences.json"));
            viewModel.ConfigureActions(store, item => store.TryDelete(item), preferences,
                () => [basePath, minePath, viewModel.Session.BaseSnapshotPath, viewModel.Session.MineSnapshotPath]);
            return new Fixture(root, store, adapter, scheduler, viewModel, preferences);
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            _scheduler.Dispose();
            Preferences.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    internal sealed class ControlledAdapter : IFileDiffAdapter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FileKind FileKind => FileKind.Text;
        public bool CanHandle(string path) => true;
        public async Task<DiffDocument> CompareAsync(DiffRequest request, CancellationToken cancellationToken)
        {
            var oldValue = await File.ReadAllTextAsync(request.BaseFilePath!, cancellationToken);
            var newValue = await File.ReadAllTextAsync(request.CurrentFilePath!, cancellationToken);
            if (newValue == "broken") throw new InvalidOperationException("Simulated parse failure");
            if (newValue == "waiting")
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            var nodes = new[] { new DiffNode { StableId = "row:1", Path = "1", Kind = DiffKind.Modified,
                OldIndex = 1, NewIndex = 1, OldValue = oldValue, NewValue = newValue } };
            return new DiffDocument
            {
                RelativePath = request.RelativePath,
                FileKind = FileKind.Text,
                Nodes = nodes,
                Summary = DiffSummary.FromNodes(nodes)
            };
        }
    }
}
