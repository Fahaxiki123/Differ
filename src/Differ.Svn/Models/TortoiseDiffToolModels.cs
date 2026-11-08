namespace Differ.Svn.Models;

public sealed record TortoiseDiffToolBackup
{
    public int SchemaVersion { get; init; } = 2;

    public required DateTimeOffset CreatedUtc { get; init; }

    public required string InstalledCommand { get; init; }

    public string? OriginalGlobalCommand { get; init; }

    public required IReadOnlyDictionary<string, string?> OriginalValues { get; init; }
}

public sealed record TortoiseDiffIntegrationStatus(
    int RegisteredCount,
    int SupportedCount,
    int ExistingOtherToolCount,
    bool HasBackup)
{
    public bool IsFullyRegistered =>
        SupportedCount > 0 && RegisteredCount == SupportedCount;
}

public sealed record TortoiseDiffInstallResult(
    int WrittenCount,
    int BackedUpExistingCount,
    string Command,
    int ClearedExtensionOverrideCount);

public sealed record TortoiseDiffRestoreResult(
    int RestoredCount,
    int SkippedChangedCount,
    bool BackupRemoved);
