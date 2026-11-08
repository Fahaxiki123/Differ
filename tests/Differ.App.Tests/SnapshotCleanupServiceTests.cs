using System.IO;
using Differ.App.Services;
using Differ.Core.External;
using Microsoft.Extensions.Logging.Abstractions;

namespace Differ.App.Tests;

public sealed class SnapshotCleanupServiceTests
{
    [Fact]
    public async Task SnapshotIsDeletedAfterCanceledReaderReleasesItsHandleAsync()
    {
        var root = CreateRoot();
        try
        {
            var (store, session) = await CreateSessionAsync(root);
            using var cleanup = new SnapshotCleanupService(
                store,
                NullLogger<SnapshotCleanupService>.Instance,
                [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100)]);
            using (var reader = File.Open(session.BaseSnapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                cleanup.DeleteOrQueue(session);
                Assert.True(Directory.Exists(session.SessionDirectoryPath));
            }

            await cleanup.WaitForRetriesAsync().WaitAsync(TimeSpan.FromSeconds(3));

            Assert.False(Directory.Exists(session.SessionDirectoryPath));
            Assert.Equal("source", await File.ReadAllTextAsync(session.Request.MineFilePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExitMakesAFinalCleanupAttemptAfterBackgroundRetriesExhaustAsync()
    {
        var root = CreateRoot();
        try
        {
            var (store, session) = await CreateSessionAsync(root);
            var cleanup = new SnapshotCleanupService(
                store,
                NullLogger<SnapshotCleanupService>.Instance,
                [TimeSpan.FromMilliseconds(1)]);
            using (var reader = File.Open(session.BaseSnapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                cleanup.DeleteOrQueue(session);
                await cleanup.WaitForRetriesAsync().WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(Directory.Exists(session.SessionDirectoryPath));
            }

            cleanup.Dispose();

            Assert.False(Directory.Exists(session.SessionDirectoryPath));
            Assert.Equal("source", await File.ReadAllTextAsync(session.Request.MineFilePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(ExternalDiffSessionStore Store, ExternalDiffSession Session)> CreateSessionAsync(string root)
    {
        var source = Path.Combine(root, "source.txt");
        await File.WriteAllTextAsync(source, "source");
        var store = new ExternalDiffSessionStore(Path.Combine(root, "sessions"));
        var session = await store.CreateAsync(new ExternalDiffRequest
        {
            BaseFilePath = source,
            MineFilePath = source
        }, CancellationToken.None);
        return (store, session);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "Differ.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
