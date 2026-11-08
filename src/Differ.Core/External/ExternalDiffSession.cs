using System.Text.Json.Serialization;

namespace Differ.Core.External;

public sealed record ExternalDiffSession
{
    public required Guid SessionId { get; init; }

    public required ExternalDiffRequest Request { get; init; }

    public required string BaseSnapshotPath { get; init; }

    public required string MineSnapshotPath { get; init; }

    public string? SessionDirectoryPath { get; init; }

    public long BaseLength { get; init; }

    public long MineLength { get; init; }

    public string BaseSha256 { get; init; } = string.Empty;

    public string MineSha256 { get; init; } = string.Empty;

    public DateTimeOffset BaseLastWriteUtc { get; init; }

    public DateTimeOffset MineLastWriteUtc { get; init; }

    public string? PreparationError { get; init; }

    [JsonIgnore]
    public string ComparisonKey => string.IsNullOrEmpty(BaseSha256)
        || string.IsNullOrEmpty(MineSha256)
        ? SessionId.ToString("N")
        : string.Join(
        "\u001f",
        NormalizePath(Request.MineFilePath),
        Request.BaseRevision ?? string.Empty,
        BaseSha256,
        MineSha256);

    public static ExternalDiffSession CreateFailed(
        ExternalDiffRequest request,
        string error)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        return new ExternalDiffSession
        {
            SessionId = Guid.NewGuid(),
            Request = request,
            BaseSnapshotPath = request.BaseFilePath,
            MineSnapshotPath = request.MineFilePath,
            PreparationError = error
        };
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).ToUpperInvariant();
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return path.ToUpperInvariant();
        }
    }
}
