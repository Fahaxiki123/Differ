using System.Security.Cryptography;

namespace Differ.Core.Files;

public sealed record FileSnapshot(
    string FullPath,
    bool Exists,
    long Length,
    DateTimeOffset LastWriteUtc,
    string Sha256)
{
    public static async Task<FileSnapshot> CreateAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            return new FileSnapshot(
                fullPath,
                Exists: false,
                Length: 0,
                DateTimeOffset.MinValue,
                Sha256: string.Empty);
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        info.Refresh();
        return new FileSnapshot(
            fullPath,
            Exists: true,
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            Convert.ToHexString(hash));
    }
}
