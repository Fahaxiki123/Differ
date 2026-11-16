using System.IO;
using System.Text.Json;
using Differ.App.Resources;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.External;
using Differ.Core.Services;
using Differ.Formats.Binary;
using Differ.Formats.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace Differ.App.Tests;

public sealed class EditorModeStateTests
{
    [Fact]
    public async Task ClosingModeAcceptsOnlyTheFinalOrderedBatchAndPreservesSaveAuthorityAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        var owner = fixture.Owner;
        await owner.SetEditModeAsync(true);
        var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.ApplyEditorModeAsync = (_, _) => acknowledgement.Task;
        var closing = owner.SetEditModeAsync(false);
        Assert.True(owner.IsEditModeChanging);
        Assert.True(owner.IsEditMode);
        var editor = owner.EditorSession!;
        owner.InvalidateEditorResults(editor.Id.ToString("N"));
        owner.UpdateEditorText(editor.Id.ToString("N"), 2, "last typed character 中文");
        acknowledgement.SetResult();
        await closing;
        Assert.False(owner.IsEditMode);
        Assert.True(owner.IsEditorDirty);
        Assert.True(owner.CanEditorSave);
        Assert.True(owner.CanEditorSaveAs);
        Assert.Null(editor.SaveTarget);
        Assert.Equal(TextInputOrigin.Unknown, editor.Origin);
        owner.SetEditorSaving(true);
        Assert.Equal(CodeReviewStrings.Saving, owner.EditorSaveStatus);
        Assert.False(owner.CanEditorSave);
        owner.SetEditorSaving(false);
        Assert.Equal(CodeReviewStrings.UnsavedNeedsSaveAs, owner.EditorSaveStatus);
        owner.UpdateEditorText(editor.Id.ToString("N"), 3, "late unauthorized edit");
        Assert.Equal("last typed character 中文", editor.Text);
        Assert.Equal(2, editor.Version);
    }

    [Fact]
    public async Task UnacknowledgedClosePreservesModeDirtyAndFailureStatusAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        var owner = fixture.Owner;
        await owner.SetEditModeAsync(true);
        var editor = owner.EditorSession!;
        owner.UpdateEditorText(editor.Id.ToString("N"), 2, "unsaved");
        owner.ApplyEditorModeAsync = (_, _) => Task.FromException(new TimeoutException());
        await owner.SetEditModeAsync(false);
        Assert.True(owner.IsEditMode);
        Assert.False(owner.IsEditModeChanging);
        Assert.True(owner.IsEditorDirty);
        Assert.Equal("unsaved", editor.Text);
        Assert.Equal(CodeReviewStrings.ModeChangeFailed, owner.StatusText);
        Assert.Equal(CodeReviewStrings.ModeChangeFailed, owner.EditorLocationText);
        Assert.Equal(CodeReviewStrings.ModeUnconfirmed, owner.EditorPermissionText);
        await owner.ReloadAsync();
        Assert.True(owner.EditorResultsCurrent);
        Assert.Equal(CodeReviewStrings.ModeChangeFailed, owner.StatusText);
        owner.ApplyEditorModeAsync = (_, _) => Task.CompletedTask;
        await owner.SetEditModeAsync(false);
        Assert.False(owner.IsEditMode);
        Assert.Equal(CodeReviewStrings.ReadOnly, owner.EditorPermissionText);
        Assert.NotEqual(CodeReviewStrings.ModeChangeFailed, owner.EditorLocationText);
    }

    [Fact]
    public async Task OldModeFailureCannotOverwriteARefreshedSessionAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        var owner = fixture.Owner;
        await owner.SetEditModeAsync(true);
        var delayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.ApplyEditorModeAsync = (_, _) => delayed.Task;
        var oldModeChange = owner.SetEditModeAsync(false);
        var originalSession = owner.EditorSession;
        await File.WriteAllTextAsync(fixture.MinePath, "new session");
        await owner.RefreshMineAsync();
        Assert.NotSame(originalSession, owner.EditorSession);
        Assert.False(owner.IsEditModeChanging);
        owner.ApplyEditorModeAsync = (_, _) => Task.CompletedTask;
        await owner.SetEditModeAsync(true);
        delayed.SetException(new TimeoutException());
        await oldModeChange;
        Assert.True(owner.IsEditMode);
        Assert.Equal(CodeReviewStrings.Editable, owner.EditorPermissionText);
        Assert.NotEqual(CodeReviewStrings.ModeChangeFailed, owner.EditorLocationText);
        Assert.Equal("new session", owner.EditorSession!.Text);
    }

    [Fact]
    public async Task RejectedEncodingChangeAppearsInTheSingleStatusUntilSuccessfulRecomputeAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        var owner = fixture.Owner;
        await owner.SetEditModeAsync(true);
        var editor = owner.EditorSession!;
        owner.UpdateEditorText(editor.Id.ToString("N"), 2, "edited 中文");
        await owner.ReloadAsync();
        owner.Options.TextEncodingName = "utf-16";
        await owner.ReloadAsync();
        Assert.Equal(Strings.EditorEncodingAfterEdit, owner.EditorLocationText);
        owner.UpdateEditorViewportState(editor.Id.ToString("N"), 2, "BASE 1 ↔ 结果 1", string.Empty, "plaintext", true, false);
        Assert.Equal(Strings.EditorEncodingAfterEdit, owner.EditorLocationText);
        Assert.True(owner.IsEditorDirty);
        owner.Options.TextEncodingName = null;
        await owner.ReloadAsync();
        Assert.NotEqual(Strings.EditorEncodingAfterEdit, owner.EditorLocationText);
        Assert.True(owner.EditorResultsCurrent);
        Assert.Equal("edited 中文", editor.Text);
    }

    [Fact]
    public async Task RecomputeFailureRemainsVisibleWhenTheViewportReportsLocationAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        var owner = fixture.Owner;
        await owner.SetEditModeAsync(true);
        var editor = owner.EditorSession!;
        owner.UpdateEditorText(editor.Id.ToString("N"), 2, "unsaved after infrastructure failure");
        await owner.ReloadAsync();
        fixture.Scheduler.Dispose();
        await owner.ReloadAsync();
        var expected = Strings.EditorComparisonFailed + nameof(ObjectDisposedException);
        Assert.Equal(expected, owner.EditorLocationText);
        owner.UpdateEditorViewportState(editor.Id.ToString("N"), 2, "BASE 1 ↔ 结果 1", string.Empty, "plaintext", true, false);
        Assert.Equal(expected, owner.EditorLocationText);
        Assert.True(owner.IsEditorDirty);
        Assert.False(owner.EditorResultsCurrent);
        Assert.True(owner.CanEditorSave);
    }

    [Fact]
    public async Task RefreshCancelsQueuedInlineFromPreviousSessionEvenWhenBothHaveVersionOneAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        var owner = fixture.Owner;
        var releaseWorkers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workersStarted = Enumerable.Range(0, 2).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var blockers = workersStarted.Select(started => fixture.Scheduler.RunAsync(() => false, async token =>
        {
            started.SetResult();
            await releaseWorkers.Task.WaitAsync(token);
            return true;
        }, CancellationToken.None)).ToArray();
        await Task.WhenAll(workersStarted.Select(started => started.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        var messages = new List<JsonElement>();
        owner.EditorCommandRequested += (_, command) => messages.Add(JsonSerializer.SerializeToElement(command));
        var previous = owner.EditorSession!;
        var previousInline = owner.RequestEditorInlineAsync(previous.Version, 1, 1, 1, 1);
        var newSessionReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(owner.EditorSession) && !ReferenceEquals(previous, owner.EditorSession)) newSessionReady.TrySetResult();
        };
        try
        {
            await File.WriteAllTextAsync(fixture.MinePath, "completely new content");
            var refresh = owner.RefreshMineAsync();
            await newSessionReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, previous.Version);
            Assert.Equal(previous.Version, owner.EditorSession!.Version);
            // The old viewport request must finish as cancelled before either worker is released.
            await previousInline.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(messages, item => item.GetProperty("type").GetString() == "inline");
            releaseWorkers.TrySetResult();
            await Task.WhenAll(blockers);
            await refresh;
            await owner.RequestEditorInlineAsync(owner.EditorSession.Version, 1, 1, 1, 1);
            Assert.Single(messages, item => item.GetProperty("type").GetString() == "inline");
            Assert.Equal("completely new content", owner.EditorSession.Text);
        }
        finally { releaseWorkers.TrySetResult(); await Task.WhenAll(blockers); }
    }

    [Fact]
    public async Task WhitespaceShortcutUsesEditedTextAndLeavesOtherAdvancedRulesPendingAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        using var otherTab = await Fixture.CreateAsync();
        var owner = fixture.Owner;
        var editor = owner.EditorSession!;
        await owner.SetEditModeAsync(true);
        owner.Options.IgnoreCase = true;
        owner.UpdateEditorText(editor.Id.ToString("N"), editor.Version + 1, "be fore");

        await owner.ToggleIgnoreWhitespaceCommand.ExecuteAsync(null);

        Assert.Same(editor, owner.EditorSession);
        Assert.True(owner.IsIgnoringWhitespace);
        Assert.True(owner.Options.IgnoreWhitespace);
        Assert.True(owner.HasPendingOptions);
        Assert.True(owner.EditorResultsCurrent);
        Assert.Empty(editor.Hunks);
        Assert.Equal("be fore", editor.Text);
        Assert.Equal("after", await File.ReadAllTextAsync(fixture.MinePath));
        Assert.False(otherTab.Owner.IsIgnoringWhitespace);

        owner.UpdateEditorText(editor.Id.ToString("N"), editor.Version + 1, "B E F O R E");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!owner.EditorResultsCurrent) await Task.Delay(10, timeout.Token);

        Assert.Single(editor.Hunks);
        Assert.True(owner.HasPendingOptions);
        await owner.ReloadAsync();
        Assert.Empty(editor.Hunks);
        Assert.False(owner.HasPendingOptions);
    }

    [Fact]
    public async Task RefreshToBinaryRemovesOldEditorPermissionsAndReopeningTextStartsReadOnlyAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        var owner = fixture.Owner;
        await owner.SetEditModeAsync(true);
        await File.WriteAllBytesAsync(fixture.MinePath, [0, 0, 1, 0, 2, 0, 3, 0]);
        await owner.RefreshMineAsync();
        Assert.Null(owner.EditorSession);
        Assert.False(owner.IsEditableText);
        Assert.False(owner.IsEditMode);
        Assert.False(owner.ToggleEditModeCommand.CanExecute(null));
        Assert.False(owner.CanEditorSaveAs);
        await File.WriteAllTextAsync(fixture.MinePath, "text again");
        await owner.RefreshMineAsync();
        Assert.NotNull(owner.EditorSession);
        Assert.True(owner.IsEditableText);
        Assert.False(owner.IsEditMode);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly ComparisonLoadScheduler _scheduler;
        private readonly UserPreferencesService _preferences;
        private Fixture(string root, ComparisonLoadScheduler scheduler, UserPreferencesService preferences, DiffWindowViewModel owner)
        { _root = root; _scheduler = scheduler; _preferences = preferences; Owner = owner; }
        public DiffWindowViewModel Owner { get; }
        public ComparisonLoadScheduler Scheduler => _scheduler;
        public string MinePath => Path.Combine(_root, "mine.txt");

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "Differ.Editor.Mode", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var basePath = Path.Combine(root, "base.txt");
            var minePath = Path.Combine(root, "mine.txt");
            await File.WriteAllTextAsync(basePath, "before");
            await File.WriteAllTextAsync(minePath, "after");
            var store = new ExternalDiffSessionStore(Path.Combine(root, "sessions"));
            var session = await store.CreateAsync(new ExternalDiffRequest { BaseFilePath = basePath, MineFilePath = minePath }, CancellationToken.None);
            var scheduler = new ComparisonLoadScheduler();
            var preferences = new UserPreferencesService(Path.Combine(root, "preferences.json"));
            var owner = new DiffWindowViewModel(new DiffCoordinator([new TextDiffAdapter(new TextFileReader()), new BinaryDiffAdapter()]),
                scheduler, session, NullLogger<DiffWindowViewModel>.Instance, _ => { });
            owner.ConfigureActions(store, item => store.TryDelete(item), preferences, () => [basePath, minePath]);
            owner.EnableDocumentEditor();
            await owner.LoadAsync();
            return new Fixture(root, scheduler, preferences, owner);
        }

        public void Dispose()
        {
            Owner.Dispose();
            _scheduler.Dispose();
            _preferences.Dispose();
            Directory.Delete(_root, true);
        }
    }
}
