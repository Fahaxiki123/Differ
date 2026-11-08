using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Differ.App.Controls;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.External;
using Differ.Core.Models;
using Differ.Core.Services;
using Differ.Formats.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace Differ.App.Tests;

public sealed class EditorOverviewContinuityTests
{
    [Fact]
    public async Task PendingInputKeepsOnlyThePaintSnapshotAndBlocksOldOverviewInteractionsAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        var owner = fixture.Owner;
        await owner.SetEditModeAsync(true);
        var editor = owner.EditorSession!;
        var stableRows = owner.OverviewRows;
        Assert.NotEmpty(stableRows);
        Assert.Same(owner.DiffRows, stableRows);
        Assert.True(owner.CanInteractWithOverview);
        var selected = owner.SelectedDiffRow;
        var commands = new List<object>();
        var overviewRequests = 0;
        owner.EditorCommandRequested += (_, command) => commands.Add(command);
        owner.OverviewScrollRequested += (_, _) => overviewRequests++;

        owner.InvalidateEditorResults(editor.Id.ToString("N"));
        Assert.False(owner.EditorResultsCurrent);
        Assert.Null(owner.CurrentDocument);
        Assert.Empty(owner.DiffRows);
        Assert.Same(stableRows, owner.OverviewRows);
        Assert.False(owner.CanInteractWithOverview);
        Assert.False(owner.CanExport);
        owner.SelectOverviewRow(stableRows[^1]);
        owner.ScrollFromOverview(120);
        owner.CenterFromOverview(.5);
        Assert.Same(selected, owner.SelectedDiffRow);
        Assert.Empty(commands);
        Assert.Equal(0, overviewRequests);
        Assert.False(owner.GoTo("3"));

        owner.UpdateEditorText(editor.Id.ToString("N"), editor.Version + 1, "before\nunchanged\nnew result");
        await owner.ReloadAsync();
        Assert.True(owner.EditorResultsCurrent);
        Assert.True(owner.CanInteractWithOverview);
        Assert.Same(owner.DiffRows, owner.OverviewRows);
        Assert.NotSame(stableRows, owner.OverviewRows);
        Assert.Contains(owner.OverviewRows, row => row.NewValue == "new result");
        owner.ScrollFromOverview(120);
        Assert.Equal(1, overviewRequests);
    }

    [Fact]
    public async Task FailedCurrentComparisonClearsRetainedOverviewAndPublishesVersionedFailureAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        var owner = fixture.Owner;
        Assert.NotEmpty(owner.OverviewRows);
        var editor = owner.EditorSession!;
        var messages = new List<JsonElement>();
        owner.EditorCommandRequested += (_, command) => messages.Add(JsonSerializer.SerializeToElement(command));
        fixture.Scheduler.Dispose();

        await owner.ReloadAsync();

        Assert.False(owner.EditorResultsCurrent);
        Assert.False(owner.CanInteractWithOverview);
        Assert.Empty(owner.OverviewRows);
        var failure = Assert.Single(messages, item => item.GetProperty("type").GetString() == "projectionFailed");
        Assert.Equal(editor.Id.ToString("N"), failure.GetProperty("session").GetString());
        Assert.Equal(editor.Version, failure.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task SameVersionOptionsChangeCancelsQueuedInlineBeforePublishingNewHighlightsAsync()
    {
        using var fixture = await Fixture.CreateAsync("ABC xxx", "abc yyy");
        var owner = fixture.Owner;
        await owner.SetEditModeAsync(true);
        owner.MoveEditorDifference(1);
        Assert.True(owner.CanEditorAdopt);
        var editor = owner.EditorSession!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Enumerable.Range(0, 2).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var blockers = started.Select(signal => fixture.Scheduler.RunAsync(() => false, async token =>
        {
            signal.SetResult();
            await release.Task.WaitAsync(token);
            return true;
        }, CancellationToken.None)).ToArray();
        await Task.WhenAll(started.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        var messages = new List<JsonElement>();
        owner.EditorCommandRequested += (_, command) => messages.Add(JsonSerializer.SerializeToElement(command));
        try
        {
            var oldInline = owner.RequestEditorInlineAsync(editor.Version, 1, 1, 1, 1);
            owner.Options.IgnoreCase = true;
            var reload = owner.ReloadAsync();
            await oldInline.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(-1, owner.CurrentHunk);
            Assert.DoesNotContain(messages, item => item.GetProperty("type").GetString() == "inline");
            Assert.Equal(1, editor.Version);
            release.SetResult();
            await Task.WhenAll(blockers);
            await reload;
            Assert.False(owner.CanEditorAdopt);
            owner.AdoptEditorHunk();
            Assert.DoesNotContain(messages, item => item.GetProperty("type").GetString() == "replace");
            await owner.RequestEditorInlineAsync(editor.Version, 1, 1, 1, 1);
            var current = Assert.Single(messages, item => item.GetProperty("type").GetString() == "inline");
            var row = Assert.Single(current.GetProperty("rows").EnumerateArray());
            foreach (var property in new[] { "oldSegments", "newSegments" })
            {
                var segments = row.GetProperty(property).EnumerateArray().ToArray();
                Assert.False(segments[0].GetProperty("changed").GetBoolean());
                Assert.True(segments[0].GetProperty("length").GetInt32() >= 3);
            }
        }
        finally { release.TrySetResult(); await Task.WhenAll(blockers); }
    }

    [Fact]
    public void DisablingOverviewPreservesItsPixelsAndDismissesThePreviousPreview()
    {
        ThemeTestSupport.RunSta(() =>
        {
            var canvas = new DiffOverviewCanvas
            {
                Width = 44,
                Height = 200,
                AddedBrush = Brushes.Green,
                HoverBrush = Brushes.LightGray,
                HoverPenBrush = Brushes.Gray,
                ViewportBrush = Brushes.Transparent,
                ViewportBorderBrush = Brushes.Transparent,
                Rows = [new DiffRowViewModel("added", "line:1", DiffKind.Added, null, "", 1, "added text", null, 0)]
            };
            canvas.Measure(new Size(44, 200));
            canvas.Arrange(new Rect(0, 0, 44, 200));
            var before = Rasterize(canvas);
            OverviewPreviewEventArgs? preview = null;
            canvas.PreviewChanged += (_, args) => preview = args;
            canvas.UpdateHover(20);
            Assert.NotNull(preview?.Row);
            Assert.True(canvas.SelectBucketAt(20));

            canvas.IsEnabled = false;

            Assert.NotNull(preview);
            Assert.Null(preview.Row);
            Assert.Equal(-1, canvas.HoverBucketIndex);
            Assert.False(canvas.SelectBucketAt(20));
            canvas.UpdateHover(20);
            Assert.Null(preview.Row);
            Assert.Equal(1, canvas.Opacity);
            Assert.Equal(before, Rasterize(canvas));
            Assert.NotEmpty(canvas.Rows!);
        });
    }

    private static byte[] Rasterize(DiffOverviewCanvas canvas)
    {
        canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(44, 200, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var pixels = new byte[44 * 200 * 4];
        bitmap.CopyPixels(pixels, 44 * 4, 0);
        return pixels;
    }

    private sealed class Fixture(string directory, ComparisonLoadScheduler scheduler, UserPreferencesService preferences,
        DiffWindowViewModel owner) : IDisposable
    {
        public DiffWindowViewModel Owner { get; } = owner;
        public ComparisonLoadScheduler Scheduler { get; } = scheduler;

        public static async Task<Fixture> CreateAsync(string baseText = "before\nunchanged\nend", string mineText = "after\nunchanged\nchanged")
        {
            var directory = Path.Combine(Path.GetTempPath(), "Differ.Editor.Overview", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var basePath = Path.Combine(directory, "base.txt");
            var minePath = Path.Combine(directory, "mine.txt");
            await File.WriteAllTextAsync(basePath, baseText);
            await File.WriteAllTextAsync(minePath, mineText);
            var store = new ExternalDiffSessionStore(Path.Combine(directory, "sessions"));
            var session = await store.CreateAsync(new ExternalDiffRequest { BaseFilePath = basePath, MineFilePath = minePath }, CancellationToken.None);
            var scheduler = new ComparisonLoadScheduler();
            var preferences = new UserPreferencesService(Path.Combine(directory, "preferences.json"));
            var owner = new DiffWindowViewModel(new DiffCoordinator([new TextDiffAdapter(new TextFileReader())]), scheduler,
                session, NullLogger<DiffWindowViewModel>.Instance, _ => { });
            owner.ConfigureActions(store, item => store.TryDelete(item), preferences, () => [basePath, minePath]);
            owner.EnableDocumentEditor();
            await owner.LoadAsync();
            return new Fixture(directory, scheduler, preferences, owner);
        }

        public void Dispose()
        {
            Owner.Dispose();
            Scheduler.Dispose();
            preferences.Dispose();
            Directory.Delete(directory, true);
        }
    }
}
