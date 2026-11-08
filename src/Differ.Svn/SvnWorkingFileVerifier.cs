using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Differ.Core.External;
using Differ.Core.Files;
using SharpSvn;

namespace Differ.Svn;

public interface IWorkingCopyFileVerifier
{
    bool IsVersionedFile(string fullPath);
}

/// <summary>Uses Subversion's public local info API; never queries a URL or changes the working copy.</summary>
public sealed class NativeWorkingCopyFileVerifier : IWorkingCopyFileVerifier
{
    public bool IsVersionedFile(string fullPath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return ReadLocalInfo(fullPath); }
        catch (Exception exception) when (exception is DllNotFoundException or FileNotFoundException
            or BadImageFormatException or TypeInitializationException or FileLoadException)
        {
            // Native deployment or WC format failures must downgrade to Save As.
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ReadLocalInfo(string fullPath)
    {
        try
        {
            using var client = new SvnClient();
            var args = new SvnInfoArgs { ThrowOnError = false, Depth = SvnDepth.Empty };
            return client.GetInfo(new SvnPathTarget(fullPath), args, out Collection<SvnInfoEventArgs> items)
                && items.Count == 1 && items[0].NodeKind == SvnNodeKind.File
                && !string.IsNullOrEmpty(items[0].WorkingCopyRoot)
                && string.Equals(Path.GetFullPath(items[0].FullPath), fullPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (SvnException)
        {
            return false;
        }
    }
}

public sealed class SvnWorkingFileVerifier(IWorkingCopyFileVerifier? verifier = null)
{
    private readonly IWorkingCopyFileVerifier _verifier = verifier ?? new NativeWorkingCopyFileVerifier();

    public async Task<string?> VerifyAsync(ExternalDiffSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var request = session.Request;
        if (!string.Equals(request.InvocationSource, "tortoisesvn", StringComparison.Ordinal)
            || request.MineRevision is not ("WC" or "WORKING") || string.IsNullOrEmpty(session.MineSha256)) return null;
        var path = Path.GetFullPath(request.MineFilePath);
        if (Same(path, request.BaseFilePath) || Same(path, session.BaseSnapshotPath) || Same(path, session.MineSnapshotPath)
            || IsWithin(path, session.SessionDirectoryPath)
            || IsWithin(path, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Differ", "Sessions"))) return null;
        cancellationToken.ThrowIfCancellationRequested();
        if (!await Task.Run(() => _verifier.IsVersionedFile(path), cancellationToken).ConfigureAwait(false)) return null;
        var fingerprint = await FileSnapshot.CreateAsync(path, cancellationToken).ConfigureAwait(false);
        return fingerprint.Exists && fingerprint.Sha256 == session.MineSha256 ? path : null;
    }

    private static bool Same(string path, string other) => string.Equals(path, Path.GetFullPath(other), StringComparison.OrdinalIgnoreCase);
    private static bool IsWithin(string path, string? directory) => directory is not null
        && path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
