using System.IO;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.External;
using Differ.Core.Services;
using Differ.Formats.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace Differ.App.Tests;

public sealed class EditModeViewModelTests
{
    [Theory]
    [InlineData(null, TextInputOrigin.Unknown)]
    [InlineData("WC", TextInputOrigin.Unknown)]
    [InlineData("47", TextInputOrigin.HistoricalFile)]
    public async Task PermissionDoesNotPromoteOriginAndOffStillAllowsSavingDirtyText(string? revision, TextInputOrigin expectedOrigin)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Differ.EditMode.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var basePath = Path.Combine(directory, "base.txt");
        var minePath = Path.Combine(directory, "mine.txt");
        await File.WriteAllTextAsync(basePath, "BASE 原文");
        await File.WriteAllTextAsync(minePath, "当前原文");
        using var scheduler = new ComparisonLoadScheduler();
        var store = new ExternalDiffSessionStore(Path.Combine(directory, "sessions"));
        var session = await store.CreateAsync(new ExternalDiffRequest
        {
            BaseFilePath = basePath,
            MineFilePath = minePath,
            MineRevision = revision,
            MineDisplayName = "工作副本 - 可写名称不能提供保存权限"
        }, CancellationToken.None);
        using var owner = new DiffWindowViewModel(new DiffCoordinator([new TextDiffAdapter(new TextFileReader())]), scheduler,
            session, NullLogger<DiffWindowViewModel>.Instance, _ => { });
        try
        {
            owner.EnableDocumentEditor();
            await owner.LoadAsync();
            var editor = Assert.IsType<EditableTextSession>(owner.EditorSession);
            var id = editor.Id.ToString("N");
            Assert.False(owner.IsEditMode);
            Assert.Equal(expectedOrigin, editor.Origin);
            Assert.Null(editor.SaveTarget);
            owner.UpdateEditorText(id, editor.Version + 1, "readonly message must be rejected");
            owner.InvalidateEditorResults(id);
            Assert.Equal("当前原文", editor.Text);
            Assert.True(owner.EditorResultsCurrent);
            Assert.False(owner.IsEditorDirty);

            await owner.SetEditModeAsync(true);
            Assert.True(owner.IsEditMode);
            owner.UpdateEditorText(id, editor.Version + 1, "已编辑中文");
            await WaitForResults(owner);
            owner.UpdateEditorViewportState(id, editor.Version, "当前有效位置", string.Empty, "plaintext", true, false);
            Assert.True(owner.CanEditorUndo);
            owner.UpdateEditorViewportState(id, editor.Version - 1, "旧版本位置", string.Empty, "stale", false, true);
            owner.UpdateEditorViewportState(Guid.NewGuid().ToString("N"), editor.Version, "其他标签位置", string.Empty, "stale", false, true);
            Assert.Equal("当前有效位置", owner.EditorLocationText);
            Assert.True(owner.CanEditorUndo);
            Assert.False(owner.CanEditorRedo);

            await owner.SetEditModeAsync(false);
            Assert.Equal(expectedOrigin, editor.Origin);
            Assert.Null(editor.SaveTarget);
            Assert.True(owner.IsEditorDirty);
            Assert.True(owner.CanEditorSave);
            Assert.False(owner.CanEditorUndo);
            Assert.False(owner.CanEditorAdopt);
            owner.UpdateEditorText(id, editor.Version + 1, "late readonly mutation");
            Assert.Equal("已编辑中文", editor.Text);
            await Assert.ThrowsAsync<IOException>(() => owner.SaveEditorAsync(basePath));
            await Assert.ThrowsAsync<IOException>(() => owner.SaveEditorAsync(session.MineSnapshotPath));
            Assert.True(owner.IsEditorDirty);
            Assert.Equal(expectedOrigin, editor.Origin);
            var target = Path.Combine(directory, "explicit-result.txt");
            await owner.SaveEditorAsync(target);
            Assert.False(owner.IsEditMode);
            Assert.False(owner.IsEditorDirty);
            Assert.Equal("已编辑中文", await File.ReadAllTextAsync(target));
            Assert.Equal("BASE 原文", await File.ReadAllTextAsync(basePath));
            Assert.Equal("当前原文", await File.ReadAllTextAsync(minePath));
            Assert.Equal(TextInputOrigin.LocalFile, editor.Origin);
        }
        finally
        {
            owner.Dispose();
            store.TryDelete(session);
            Directory.Delete(directory, true);
        }
    }

    private static async Task WaitForResults(DiffWindowViewModel owner)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!owner.EditorResultsCurrent) await Task.Delay(10, timeout.Token);
    }
}
