using System.Security.Cryptography;

namespace Differ.Core.External;

public sealed class ExternalDiffSessionStore
{
    private const int CopyBufferSize = 128 * 1024;

    private readonly string _rootPath;
    private readonly string _rootPathWithSeparator;
    private readonly TimeProvider _timeProvider;

    public ExternalDiffSessionStore(
        string rootPath,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        _rootPath = Path.GetFullPath(rootPath);
        _rootPathWithSeparator = _rootPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string RootPath => _rootPath;

    public static string PreviewProfilesPath => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Differ.UIPhase1", "Profiles"));

    public static bool IsWithinPreviewProfile(string fullPath)
    {
        var root = PreviewProfilesPath;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(fullPath, root, comparison)
            || fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    public async Task<ExternalDiffSession> CreateAsync(
        ExternalDiffRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var baseInfo = ValidateSourceFile(request.BaseFilePath);
        var mineInfo = ValidateSourceFile(request.MineFilePath);
        using var metrics = Differ.Core.Diagnostics.ComparisonMetrics.Measure("file-prepare", "Snapshot", baseInfo.Length + mineInfo.Length);
        var sessionId = Guid.NewGuid();
        var sessionDirectory = Path.Combine(_rootPath, sessionId.ToString("N"));
        var extension = Path.GetExtension(request.FileTypePath);
        var baseSnapshotPath = Path.Combine(sessionDirectory, $"base{extension}");
        var mineSnapshotPath = Path.Combine(sessionDirectory, $"mine{extension}");

        Directory.CreateDirectory(sessionDirectory);
        try
        {
            var baseCopy = await CopyReadOnlyAsync(
                    request.BaseFilePath,
                    baseSnapshotPath,
                    cancellationToken)
                .ConfigureAwait(false);
            var mineCopy = await CopyReadOnlyAsync(
                    request.MineFilePath,
                    mineSnapshotPath,
                    cancellationToken)
                .ConfigureAwait(false);
            Directory.SetLastWriteTimeUtc(
                sessionDirectory,
                _timeProvider.GetUtcNow().UtcDateTime);

            return new ExternalDiffSession
            {
                SessionId = sessionId,
                Request = request,
                BaseSnapshotPath = baseSnapshotPath,
                MineSnapshotPath = mineSnapshotPath,
                SessionDirectoryPath = sessionDirectory,
                BaseLength = baseCopy.Length,
                MineLength = mineCopy.Length,
                BaseSha256 = baseCopy.Sha256,
                MineSha256 = mineCopy.Sha256,
                BaseLastWriteUtc = baseInfo.LastWriteTimeUtc,
                MineLastWriteUtc = mineInfo.LastWriteTimeUtc
            };
        }
        catch
        {
            TryDeleteDirectory(sessionDirectory);
            throw;
        }
    }

    public bool TryDelete(ExternalDiffSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return string.IsNullOrWhiteSpace(session.SessionDirectoryPath)
            || TryDeleteDirectory(session.SessionDirectoryPath);
    }

    public async Task<ExternalDiffSession> RefreshMineAsync(
        ExternalDiffSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();

        // BASE may no longer exist at its original TortoiseSVN path. Copy the
        // already-owned immutable baseline into a new, independently owned
        // session so either the old or new result can be retired safely.
        var refreshed = await CreateAsync(
                session.Request with { BaseFilePath = session.BaseSnapshotPath },
                cancellationToken)
            .ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            TryDelete(refreshed);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return refreshed with
        {
            Request = session.Request,
            BaseLastWriteUtc = session.BaseLastWriteUtc
        };
    }

    /// <summary>
    /// Refreshes the snapshot directory timestamp so long-running windows
    /// are not collected by <see cref="CleanupExpired"/> while still in use.
    /// </summary>
    public void Touch(ExternalDiffSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(session.SessionDirectoryPath))
        {
            return;
        }

        try
        {
            if (Directory.Exists(session.SessionDirectoryPath))
            {
                Directory.SetLastWriteTimeUtc(
                    session.SessionDirectoryPath,
                    _timeProvider.GetUtcNow().UtcDateTime);
            }
        }
        catch (IOException)
        {
            // Touching is best effort; expiry cleanup stays correct either way.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public int CleanupExpired(TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumAge),
                maximumAge,
                "Maximum age must be positive.");
        }

        if (!Directory.Exists(_rootPath))
        {
            return 0;
        }

        var cutoffUtc = _timeProvider.GetUtcNow().UtcDateTime - maximumAge;
        var deleted = 0;
        foreach (var directory in Directory.EnumerateDirectories(_rootPath))
        {
            DateTime lastWriteUtc;
            try
            {
                lastWriteUtc = Directory.GetLastWriteTimeUtc(directory);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (lastWriteUtc < cutoffUtc && TryDeleteDirectory(directory))
            {
                deleted++;
            }
        }

        return deleted;
    }

    private static FileInfo ValidateSourceFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (Directory.Exists(path))
        {
            throw new IOException($"The comparison input is a directory: {path}");
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException(
                "The comparison input does not exist.",
                path);
        }

        return info;
    }

    private static async Task<(long Length, string Sha256)> CopyReadOnlyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferSize];
        long copiedLength = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)
                   .ConfigureAwait(false)) != 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            copiedLength += read;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (copiedLength, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private bool TryDeleteDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (!fullPath.StartsWith(
                _rootPathWithSeparator,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Directory.Exists(fullPath))
        {
            return true;
        }

        try
        {
            Directory.Delete(fullPath, recursive: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
