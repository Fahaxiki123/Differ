using System.Security.Cryptography;
using System.Text;
using Differ.Core.External;

namespace Differ.Core.Tests;

public sealed class ExternalDiffSessionStoreTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedSnapshotContentWithSameLengthAndTimestampGetsDistinctKeyAsync(bool changeBase)
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var basePath = Path.Combine(testRoot, "base.txt");
            var minePath = Path.Combine(testRoot, "mine.txt");
            await File.WriteAllTextAsync(basePath, "AAAA");
            await File.WriteAllTextAsync(minePath, "AAAA");
            var changedPath = changeBase ? basePath : minePath;
            var originalTimestamp = File.GetLastWriteTimeUtc(changedPath);
            var store = new ExternalDiffSessionStore(Path.Combine(testRoot, "sessions"));
            var request = CreateRequest(basePath, minePath);
            var original = await store.CreateAsync(request, CancellationToken.None);

            await File.WriteAllTextAsync(changedPath, "BBBB");
            File.SetLastWriteTimeUtc(changedPath, originalTimestamp);
            var changed = await store.CreateAsync(request, CancellationToken.None);

            Assert.Equal(original.BaseLength, changed.BaseLength);
            Assert.Equal(original.MineLength, changed.MineLength);
            Assert.Equal(original.BaseLastWriteUtc, changed.BaseLastWriteUtc);
            Assert.Equal(original.MineLastWriteUtc, changed.MineLastWriteUtc);
            Assert.NotEqual(original.ComparisonKey, changed.ComparisonKey);
            Assert.Equal("AAAA", await File.ReadAllTextAsync(
                changeBase ? original.BaseSnapshotPath : original.MineSnapshotPath));
            Assert.Equal("BBBB", await File.ReadAllTextAsync(
                changeBase ? changed.BaseSnapshotPath : changed.MineSnapshotPath));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HashRepresentsAllCopiedBytesAndIdenticalContentCanReuseItsKeyAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var basePath = Path.Combine(testRoot, "base.txt");
            var minePath = Path.Combine(testRoot, "mine.txt");
            var contents = Encoding.UTF8.GetBytes(new string('界', 100_000));
            await File.WriteAllBytesAsync(basePath, contents);
            await File.WriteAllBytesAsync(minePath, contents);
            var store = new ExternalDiffSessionStore(Path.Combine(testRoot, "sessions"));
            var request = CreateRequest(basePath, minePath);
            var original = await store.CreateAsync(request, CancellationToken.None);
            File.SetLastWriteTimeUtc(minePath, DateTime.UtcNow.AddMinutes(1));
            var repeated = await store.CreateAsync(request, CancellationToken.None);

            Assert.Equal(Convert.ToHexString(SHA256.HashData(contents)), original.BaseSha256);
            Assert.Equal(original.BaseSha256, original.MineSha256);
            Assert.Equal(contents.LongLength, original.BaseLength);
            Assert.Equal(original.ComparisonKey, repeated.ComparisonKey);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitLocalSaveTargetDoesNotReuseAnUnverifiedComparisonKeyAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var basePath = Path.Combine(testRoot, "base.txt");
            var minePath = Path.Combine(testRoot, "mine.txt");
            await File.WriteAllTextAsync(basePath, "BASE");
            await File.WriteAllTextAsync(minePath, "MINE");
            var store = new ExternalDiffSessionStore(Path.Combine(testRoot, "sessions"));
            var request = CreateRequest(basePath, minePath);
            var unverified = await store.CreateAsync(request, CancellationToken.None);
            var local = await store.CreateAsync(
                request with { InvocationSource = ExternalDiffRequest.LocalFileSource }, CancellationToken.None);

            Assert.Equal(unverified.BaseSha256, local.BaseSha256);
            Assert.Equal(unverified.MineSha256, local.MineSha256);
            Assert.NotEqual(unverified.ComparisonKey, local.ComparisonKey);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CanceledCopyRemovesItsIncompleteSessionDirectoryAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var source = Path.Combine(testRoot, "source.txt");
            await File.WriteAllTextAsync(source, "unchanged source");
            var store = new ExternalDiffSessionStore(Path.Combine(testRoot, "sessions"));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateAsync(
                CreateRequest(source, source), cancellation.Token));

            Assert.Empty(Directory.EnumerateDirectories(store.RootPath));
            Assert.Equal("unchanged source", await File.ReadAllTextAsync(source));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void SessionsWithoutVerifiedHashesCannotAccidentallyReuseMetadataKeys()
    {
        var request = CreateRequest("base.txt", "mine.txt");
        var first = ExternalDiffSession.CreateFailed(request, "first failure");
        var second = ExternalDiffSession.CreateFailed(request, "second failure");
        Assert.NotEqual(first.ComparisonKey, second.ComparisonKey);
    }

    [Fact]
    public async Task CreateAsyncCopiesBothInputsAndDeletesOnlyOwnedDirectoryAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var sourceDirectory = Path.Combine(testRoot, "source");
            var sessionRoot = Path.Combine(testRoot, "sessions");
            Directory.CreateDirectory(sourceDirectory);
            var basePath = Path.Combine(sourceDirectory, "base.xlsx");
            var minePath = Path.Combine(sourceDirectory, "mine.xlsx");
            await File.WriteAllTextAsync(basePath, "base");
            await File.WriteAllTextAsync(minePath, "mine");

            var store = new ExternalDiffSessionStore(sessionRoot);
            var session = await store.CreateAsync(
                CreateRequest(basePath, minePath),
                CancellationToken.None);

            Assert.NotEqual(basePath, session.BaseSnapshotPath);
            Assert.NotEqual(minePath, session.MineSnapshotPath);
            Assert.Equal("base", await File.ReadAllTextAsync(session.BaseSnapshotPath));
            Assert.Equal("mine", await File.ReadAllTextAsync(session.MineSnapshotPath));
            Assert.Equal(".xlsx", Path.GetExtension(session.BaseSnapshotPath));
            Assert.True(store.TryDelete(session));
            Assert.False(Directory.Exists(session.SessionDirectoryPath));
            Assert.True(File.Exists(basePath));
            Assert.True(File.Exists(minePath));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void CleanupExpiredDeletesOnlyDirectoriesOlderThanMaximumAge()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var sessionRoot = Path.Combine(testRoot, "sessions");
            var expired = Directory.CreateDirectory(
                Path.Combine(sessionRoot, "expired"));
            var current = Directory.CreateDirectory(
                Path.Combine(sessionRoot, "current"));
            Directory.SetLastWriteTimeUtc(
                expired.FullName,
                DateTime.UtcNow - TimeSpan.FromDays(3));
            Directory.SetLastWriteTimeUtc(
                current.FullName,
                DateTime.UtcNow);

            var store = new ExternalDiffSessionStore(sessionRoot);
            var deleted = store.CleanupExpired(TimeSpan.FromDays(1));

            Assert.Equal(1, deleted);
            Assert.False(Directory.Exists(expired.FullName));
            Assert.True(Directory.Exists(current.FullName));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TouchRefreshesSnapshotDirectorySoCleanupKeepsItAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var sourceDirectory = Path.Combine(testRoot, "source");
            var sessionRoot = Path.Combine(testRoot, "sessions");
            Directory.CreateDirectory(sourceDirectory);
            var basePath = Path.Combine(sourceDirectory, "base.txt");
            var minePath = Path.Combine(sourceDirectory, "mine.txt");
            await File.WriteAllTextAsync(basePath, "base");
            await File.WriteAllTextAsync(minePath, "mine");

            var store = new ExternalDiffSessionStore(sessionRoot);
            var session = await store.CreateAsync(
                CreateRequest(basePath, minePath),
                CancellationToken.None);
            Directory.SetLastWriteTimeUtc(
                session.SessionDirectoryPath!,
                DateTime.UtcNow - TimeSpan.FromDays(3));

            store.Touch(session);
            var deleted = store.CleanupExpired(TimeSpan.FromDays(1));

            Assert.Equal(0, deleted);
            Assert.True(Directory.Exists(session.SessionDirectoryPath));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PrimaryCoordinatorReceivesForwardedSessionAsync()
    {
        var key = $"test-{Guid.NewGuid():N}";
        using var primary = new ExternalDiffInstanceCoordinator(key);
        using var secondary = new ExternalDiffInstanceCoordinator(key);
        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);

        var request = CreateRequest(
            Path.Combine(Path.GetTempPath(), "base.cs"),
            Path.Combine(Path.GetTempPath(), "mine.cs"));
        var sent = ExternalDiffSession.CreateFailed(request, "test error");
        var received = new TaskCompletionSource<ExternalDiffSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(
            (session, _) =>
            {
                received.TrySetResult(session);
                return Task.CompletedTask;
            });

        var forwarded = await secondary.ForwardAsync(
            sent,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(forwarded);
        Assert.Equal(sent.SessionId, actual.SessionId);
        Assert.Equal(sent.Request.MineFilePath, actual.Request.MineFilePath);
        Assert.Equal(sent.PreparationError, actual.PreparationError);
    }

    [Fact]
    public async Task ForwardedSnapshotsRemainAvailableUntilPrimaryDeletesThemAsync()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var basePath = Path.Combine(testRoot, "base.txt");
            var minePath = Path.Combine(testRoot, "mine.txt");
            await File.WriteAllTextAsync(basePath, "base");
            await File.WriteAllTextAsync(minePath, "mine");
            var store = new ExternalDiffSessionStore(
                Path.Combine(testRoot, "sessions"));
            var session = await store.CreateAsync(
                CreateRequest(basePath, minePath),
                CancellationToken.None);

            var key = $"test-{Guid.NewGuid():N}";
            using var primary = new ExternalDiffInstanceCoordinator(key);
            using var secondary = new ExternalDiffInstanceCoordinator(key);
            var received = new TaskCompletionSource<ExternalDiffSession>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            primary.StartListening(
                (forwardedSession, _) =>
                {
                    received.TrySetResult(forwardedSession);
                    return Task.CompletedTask;
                });

            Assert.True(
                await secondary.ForwardAsync(
                    session,
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None));
            var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(actual.BaseSnapshotPath));
            Assert.True(File.Exists(actual.MineSnapshotPath));
            Assert.Equal("base", await File.ReadAllTextAsync(actual.BaseSnapshotPath));
            Assert.Equal("mine", await File.ReadAllTextAsync(actual.MineSnapshotPath));

            Assert.True(store.TryDelete(actual));
            Assert.False(Directory.Exists(actual.SessionDirectoryPath));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static ExternalDiffRequest CreateRequest(
        string basePath,
        string minePath)
    {
        return new ExternalDiffRequest
        {
            BaseFilePath = basePath,
            MineFilePath = minePath,
            OriginalFileName = "sample.xlsx",
            BaseRevision = "BASE"
        };
    }

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "Differ.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
