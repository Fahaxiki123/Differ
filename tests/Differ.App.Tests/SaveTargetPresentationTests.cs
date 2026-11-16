using System.Collections.Concurrent;
using System.IO;
using Differ.App.Resources;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.External;
using Differ.Core.Services;
using Differ.Formats.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace Differ.App.Tests;

public sealed class SaveTargetPresentationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveAsDisplaysTheCommittedTargetAndRefreshReadsTheOriginalSource(bool explicitLocal)
    {
        using var fixture = await Fixture.CreateAsync(explicitLocal);
        var owner = fixture.Owner;
        var originalSession = owner.Session;
        var originalFileName = owner.FileName;
        var originalTitle = owner.MineTitle;
        Assert.False(owner.HasAlternateEditorSaveTarget);
        Assert.Empty(owner.EditorSaveTargetName);
        Assert.Contains(fixture.MinePath, owner.EditorMinePathHint, StringComparison.Ordinal);
        Assert.DoesNotContain(CodeReviewStrings.RefreshUsesSource, owner.EditorRefreshHint, StringComparison.Ordinal);
        if (!explicitLocal)
        {
            Assert.Contains(CodeReviewStrings.SaveTargetUnspecified, owner.EditorMinePathHint, StringComparison.Ordinal);
            Assert.Contains(CodeReviewStrings.SaveTargetUnspecified, owner.EditorSaveHint, StringComparison.Ordinal);
        }

        var notifications = new ConcurrentQueue<string?>();
        owner.PropertyChanged += (_, args) => notifications.Enqueue(args.PropertyName);
        await owner.SetEditModeAsync(true);
        var editor = Assert.IsType<EditableTextSession>(owner.EditorSession);
        owner.UpdateEditorText(editor.Id.ToString("N"), editor.Version + 1, "另存结果");
        await WaitForResults(owner);
        await owner.SaveEditorAsync(fixture.CopyPath);

        Assert.True(owner.HasAlternateEditorSaveTarget);
        Assert.Equal(Path.GetFileName(fixture.CopyPath), owner.EditorSaveTargetName);
        Assert.Equal(CodeReviewStrings.ResultCopy, owner.EditorMineIdentity);
        Assert.Contains(fixture.MinePath, owner.EditorMinePathHint, StringComparison.Ordinal);
        Assert.Contains(fixture.CopyPath, owner.EditorMinePathHint, StringComparison.Ordinal);
        Assert.Contains(CodeReviewStrings.RefreshUsesSource, owner.EditorMinePathHint, StringComparison.Ordinal);
        Assert.Contains(fixture.CopyPath, owner.EditorSaveHint, StringComparison.Ordinal);
        Assert.Contains(fixture.MinePath, owner.EditorRefreshHint, StringComparison.Ordinal);
        Assert.Contains(fixture.CopyPath, owner.EditorRefreshHint, StringComparison.Ordinal);
        Assert.Contains(CodeReviewStrings.RefreshUsesSource, owner.EditorRefreshHint, StringComparison.Ordinal);
        Assert.Equal(originalFileName, owner.FileName);
        Assert.Equal(originalTitle, owner.MineTitle);
        Assert.Equal(fixture.MinePath, owner.MinePath);
        foreach (var property in new[]
        {
            nameof(owner.HasAlternateEditorSaveTarget), nameof(owner.EditorSaveTargetName),
            nameof(owner.EditorMinePathHint), nameof(owner.EditorRefreshHint), nameof(owner.EditorSaveHint)
        }) Assert.Contains(property, notifications);

        owner.UpdateEditorText(editor.Id.ToString("N"), editor.Version + 1, "继续保存到副本");
        await WaitForResults(owner);
        await owner.SaveEditorAsync();
        Assert.Equal("继续保存到副本", await File.ReadAllTextAsync(fixture.CopyPath));
        Assert.Equal("原始当前文件", await File.ReadAllTextAsync(fixture.MinePath));

        await owner.RefreshMineAsync();

        Assert.NotSame(originalSession, owner.Session);
        Assert.Equal("原始当前文件", owner.EditorSession!.Text);
        Assert.Equal(explicitLocal ? fixture.MinePath : null, owner.EditorSession.SaveTarget);
        Assert.False(owner.HasAlternateEditorSaveTarget);
        Assert.Empty(owner.EditorSaveTargetName);
        Assert.DoesNotContain(fixture.CopyPath, owner.EditorMinePathHint, StringComparison.Ordinal);
        Assert.DoesNotContain(CodeReviewStrings.RefreshUsesSource, owner.EditorRefreshHint, StringComparison.Ordinal);
        Assert.Equal(explicitLocal ? CodeReviewStrings.Saved : CodeReviewStrings.NeedsSaveAs, owner.EditorSaveStatus);
        Assert.Equal("继续保存到副本", await File.ReadAllTextAsync(fixture.CopyPath));
        Assert.Equal("共同基础", await File.ReadAllTextAsync(fixture.BasePath));
    }

    [Fact]
    public async Task FailedOrCancelledSaveAsDoesNotAdvertiseAnUncommittedTarget()
    {
        using var fixture = await Fixture.CreateAsync(explicitLocal: true);
        var owner = fixture.Owner;
        await owner.SaveEditorAsync(fixture.CopyPath);
        var expectedHint = owner.EditorMinePathHint;
        var expectedRefreshHint = owner.EditorRefreshHint;
        var expectedSaveHint = owner.EditorSaveHint;
        var nextTarget = Path.Combine(fixture.Root, "未写入的另存目标.txt");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.SaveEditorAsync(nextTarget, new CancellationToken(true)));
        await Assert.ThrowsAsync<IOException>(() => owner.SaveEditorAsync(fixture.BasePath));

        Assert.Equal(fixture.CopyPath, owner.EditorSession!.SaveTarget);
        Assert.Equal(Path.GetFileName(fixture.CopyPath), owner.EditorSaveTargetName);
        Assert.Equal(expectedHint, owner.EditorMinePathHint);
        Assert.Equal(expectedRefreshHint, owner.EditorRefreshHint);
        Assert.Equal(expectedSaveHint, owner.EditorSaveHint);
        Assert.False(File.Exists(nextTarget));
        Assert.Equal("共同基础", await File.ReadAllTextAsync(fixture.BasePath));
    }

    [Fact]
    public async Task CancelledRefreshKeepsTheSavedTargetAndItsHint()
    {
        using var fixture = await Fixture.CreateAsync(explicitLocal: true);
        var owner = fixture.Owner;
        await owner.SaveEditorAsync(fixture.CopyPath);
        var editor = owner.EditorSession;
        var expectedHint = owner.EditorMinePathHint;
        owner.ConfirmDiscardEditorAsync = () => Task.FromResult(false);

        await owner.RefreshMineAsync();

        Assert.Same(editor, owner.EditorSession);
        Assert.Equal(fixture.CopyPath, owner.EditorSession!.SaveTarget);
        Assert.Equal(expectedHint, owner.EditorMinePathHint);
    }

    private static async Task WaitForResults(DiffWindowViewModel owner)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!owner.EditorResultsCurrent) await Task.Delay(10, timeout.Token);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ComparisonLoadScheduler _scheduler;
        private readonly ExternalDiffSessionStore _store;
        private Fixture(string root, ComparisonLoadScheduler scheduler, ExternalDiffSessionStore store, DiffWindowViewModel owner)
        {
            Root = root;
            _scheduler = scheduler;
            _store = store;
            Owner = owner;
        }

        public string Root { get; }
        public string BasePath => Path.Combine(Root, "base.txt");
        public string MinePath => Path.Combine(Root, "mine.txt");
        public string CopyPath => Path.Combine(Root, "另存目标-较长的中文文件名.txt");
        public DiffWindowViewModel Owner { get; }

        public static async Task<Fixture> CreateAsync(bool explicitLocal)
        {
            var root = Path.Combine(Path.GetTempPath(), "Differ.SaveTarget.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var basePath = Path.Combine(root, "base.txt");
            var minePath = Path.Combine(root, "mine.txt");
            await File.WriteAllTextAsync(basePath, "共同基础");
            await File.WriteAllTextAsync(minePath, "原始当前文件");
            var scheduler = new ComparisonLoadScheduler();
            var store = new ExternalDiffSessionStore(Path.Combine(root, "sessions"));
            var session = await store.CreateAsync(new ExternalDiffRequest
            {
                BaseFilePath = basePath,
                MineFilePath = minePath,
                MineDisplayName = "原始传入标题",
                InvocationSource = explicitLocal ? ExternalDiffRequest.LocalFileSource : null
            }, CancellationToken.None);
            var owner = new DiffWindowViewModel(new DiffCoordinator([new TextDiffAdapter(new TextFileReader())]), scheduler,
                session, NullLogger<DiffWindowViewModel>.Instance, _ => { });
            owner.ConfigureActions(store, item => store.TryDelete(item), new UserPreferencesService(Path.Combine(root, "preferences.json")), () => []);
            owner.EnableDocumentEditor();
            await owner.LoadAsync();
            return new Fixture(root, scheduler, store, owner);
        }

        public void Dispose()
        {
            Owner.Dispose();
            _store.TryDelete(Owner.Session);
            _scheduler.Dispose();
            Directory.Delete(Root, true);
        }
    }
}
