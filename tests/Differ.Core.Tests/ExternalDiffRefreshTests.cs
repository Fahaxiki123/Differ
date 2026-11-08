using Differ.Core.External;

namespace Differ.Core.Tests;

public sealed class ExternalDiffRefreshTests
{
    [Fact]
    public async Task RefreshUsesFrozenBaseEvenWhenOriginalBaseHasBeenDeletedAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "Differ.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var basePath = Path.Combine(root, "base.txt");
            var minePath = Path.Combine(root, "mine.txt");
            await File.WriteAllTextAsync(basePath, "baseline");
            await File.WriteAllTextAsync(minePath, "first");
            var store = new ExternalDiffSessionStore(Path.Combine(root, "sessions"));
            var session = await store.CreateAsync(new ExternalDiffRequest
            {
                BaseFilePath = basePath,
                MineFilePath = minePath,
                BaseRevision = "123"
            }, CancellationToken.None);
            File.Delete(basePath);
            await File.WriteAllTextAsync(minePath, "second");

            var refreshed = await store.RefreshMineAsync(session, CancellationToken.None);

            Assert.Equal("baseline", await File.ReadAllTextAsync(refreshed.BaseSnapshotPath));
            Assert.Equal("first", await File.ReadAllTextAsync(session.MineSnapshotPath));
            Assert.Equal("second", await File.ReadAllTextAsync(refreshed.MineSnapshotPath));
            Assert.Same(session.Request, refreshed.Request);
            Assert.Equal(session.BaseSha256, refreshed.BaseSha256);
            Assert.Equal(session.BaseLastWriteUtc, refreshed.BaseLastWriteUtc);
            Assert.NotEqual(session.MineSha256, refreshed.MineSha256);
            Assert.NotEqual(session.SessionDirectoryPath, refreshed.SessionDirectoryPath);
            Assert.True(store.TryDelete(session));
            Assert.True(File.Exists(refreshed.BaseSnapshotPath));
            Assert.Equal("second", await File.ReadAllTextAsync(minePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedOrCanceledRefreshPreservesCurrentSnapshotsAndLeavesNoCandidateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "Differ.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.txt");
            await File.WriteAllTextAsync(source, "original");
            var store = new ExternalDiffSessionStore(Path.Combine(root, "sessions"));
            var session = await store.CreateAsync(new ExternalDiffRequest
            {
                BaseFilePath = source,
                MineFilePath = source
            }, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => store.RefreshMineAsync(session, cancellation.Token));
            File.Delete(source);
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => store.RefreshMineAsync(session, CancellationToken.None));

            Assert.Single(Directory.GetDirectories(store.RootPath));
            Assert.Equal("original", await File.ReadAllTextAsync(session.BaseSnapshotPath));
            Assert.Equal("original", await File.ReadAllTextAsync(session.MineSnapshotPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
