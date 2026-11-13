namespace Differ.Core.External;

public sealed record ExternalDiffRequest
{
    public const string LocalFileSource = "local-edit";

    public required string BaseFilePath { get; init; }

    public required string MineFilePath { get; init; }

    public string? BaseDisplayName { get; init; }

    public string? MineDisplayName { get; init; }

    public string? BaseRevision { get; init; }

    public string? MineRevision { get; init; }

    public string? OriginalFileName { get; init; }

    public string? InvocationSource { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasExplicitLocalSaveTarget =>
        string.Equals(InvocationSource, LocalFileSource, StringComparison.Ordinal)
        && string.IsNullOrEmpty(MineRevision);

    public string FileTypePath => string.IsNullOrWhiteSpace(OriginalFileName)
        ? MineFilePath
        : OriginalFileName;
}
