using Differ.Svn.Abstractions;
using Differ.Svn.Models;

namespace Differ.Svn;

public sealed class TortoiseDiffIntegrationService(
    ITortoiseDiffToolConfigurationStore configurationStore,
    ITortoiseDiffToolBackupStore backupStore)
{
    private const int GlobalRegistrationSchemaVersion = 2;

    public TortoiseDiffIntegrationStatus GetStatus(string executablePath)
    {
        var command = TortoiseDiffToolRegistration.BuildCommand(executablePath);
        var currentGlobal = NormalizeValue(configurationStore.ReadGlobal());
        var globalMatches = string.Equals(
            currentGlobal,
            command,
            StringComparison.Ordinal);
        var extensionOverrideCount =
            TortoiseDiffToolRegistration.SupportedExtensions.Count(
                extension => NormalizeValue(
                    configurationStore.Read(extension)) is not null);
        var isFullyRegistered = globalMatches
            && extensionOverrideCount == 0;

        return new TortoiseDiffIntegrationStatus(
            isFullyRegistered ? 1 : 0,
            SupportedCount: 1,
            ExistingOtherToolCount:
                (!globalMatches && currentGlobal is not null ? 1 : 0)
                + extensionOverrideCount,
            backupStore.Load() is not null);
    }

    public TortoiseDiffInstallResult Install(string executablePath)
    {
        var command = TortoiseDiffToolRegistration.BuildCommand(executablePath);
        var previousBackup = backupStore.Load();
        var currentGlobal = NormalizeValue(configurationStore.ReadGlobal());
        var currentExtensions = ReadCurrentExtensionValues();
        var originalGlobal = GetOriginalGlobalValue(
            previousBackup,
            currentGlobal);
        var originalExtensionValues = BuildOriginalExtensionValues(
            previousBackup,
            currentExtensions);
        var backup = new TortoiseDiffToolBackup
        {
            SchemaVersion = GlobalRegistrationSchemaVersion,
            CreatedUtc = previousBackup?.CreatedUtc ?? DateTimeOffset.UtcNow,
            InstalledCommand = command,
            OriginalGlobalCommand = originalGlobal,
            OriginalValues = originalExtensionValues
        };

        backupStore.Save(backup);

        var clearedExtensions = new List<string>();
        var globalChanged = !string.Equals(
            currentGlobal,
            command,
            StringComparison.Ordinal);
        try
        {
            foreach (var extension in TortoiseDiffToolRegistration
                .SupportedExtensions)
            {
                if (currentExtensions[extension] is null)
                {
                    continue;
                }

                clearedExtensions.Add(extension);
                configurationStore.Delete(extension);
            }

            if (globalChanged)
            {
                configurationStore.WriteGlobal(command);
            }
        }
        catch
        {
            RollBackExtensions(clearedExtensions, currentExtensions);
            if (globalChanged)
            {
                RestoreGlobalValue(currentGlobal);
            }

            RestorePreviousBackup(previousBackup);
            throw;
        }

        return new TortoiseDiffInstallResult(
            WrittenCount: globalChanged ? 1 : 0,
            BackedUpExistingCount: CountNonEmptyOriginalValues(
                originalGlobal,
                originalExtensionValues),
            command,
            ClearedExtensionOverrideCount: clearedExtensions.Count);
    }

    public TortoiseDiffRestoreResult Restore()
    {
        var backup = backupStore.Load();
        if (backup is null)
        {
            return new TortoiseDiffRestoreResult(0, 0, BackupRemoved: false);
        }

        return backup.SchemaVersion >= GlobalRegistrationSchemaVersion
            ? RestoreGlobalRegistration(backup)
            : RestoreLegacyExtensionRegistration(backup);
    }

    private TortoiseDiffRestoreResult RestoreGlobalRegistration(
        TortoiseDiffToolBackup backup)
    {
        var restoredCount = 0;
        var skippedChangedCount = 0;
        RestoreGlobalRegistrationValue(
            backup,
            ref restoredCount,
            ref skippedChangedCount);

        foreach (var extension in TortoiseDiffToolRegistration
            .SupportedExtensions)
        {
            if (!backup.OriginalValues.TryGetValue(
                extension,
                out var savedOriginal))
            {
                continue;
            }

            var current = NormalizeValue(configurationStore.Read(extension));
            var original = NormalizeValue(savedOriginal);

            if (string.Equals(
                current,
                backup.InstalledCommand,
                StringComparison.Ordinal))
            {
                RestoreExtensionValue(extension, original);
                restoredCount++;
                continue;
            }

            if (current is null)
            {
                if (original is not null)
                {
                    RestoreExtensionValue(extension, original);
                    restoredCount++;
                }

                continue;
            }

            if (!string.Equals(
                current,
                original,
                StringComparison.Ordinal))
            {
                skippedChangedCount++;
            }
        }

        return CompleteRestore(restoredCount, skippedChangedCount);
    }

    private void RestoreGlobalRegistrationValue(
        TortoiseDiffToolBackup backup,
        ref int restoredCount,
        ref int skippedChangedCount)
    {
        var current = NormalizeValue(configurationStore.ReadGlobal());
        var original = NormalizeValue(backup.OriginalGlobalCommand);

        if (string.Equals(
            current,
            backup.InstalledCommand,
            StringComparison.Ordinal))
        {
            RestoreGlobalValue(original);
            restoredCount++;
            return;
        }

        if (!string.Equals(
            current,
            original,
            StringComparison.Ordinal))
        {
            skippedChangedCount++;
        }
    }

    private TortoiseDiffRestoreResult RestoreLegacyExtensionRegistration(
        TortoiseDiffToolBackup backup)
    {
        var restoredCount = 0;
        var skippedChangedCount = 0;
        foreach (var pair in backup.OriginalValues)
        {
            var current = NormalizeValue(configurationStore.Read(pair.Key));
            var original = NormalizeValue(pair.Value);
            if (!string.Equals(
                current,
                backup.InstalledCommand,
                StringComparison.Ordinal))
            {
                if (!string.Equals(
                    current,
                    original,
                    StringComparison.Ordinal))
                {
                    skippedChangedCount++;
                }

                continue;
            }

            RestoreExtensionValue(pair.Key, original);
            restoredCount++;
        }

        return CompleteRestore(restoredCount, skippedChangedCount);
    }

    private TortoiseDiffRestoreResult CompleteRestore(
        int restoredCount,
        int skippedChangedCount)
    {
        var backupRemoved = skippedChangedCount == 0;
        if (backupRemoved)
        {
            backupStore.Delete();
        }

        return new TortoiseDiffRestoreResult(
            restoredCount,
            skippedChangedCount,
            backupRemoved);
    }

    private static string? GetOriginalGlobalValue(
        TortoiseDiffToolBackup? previousBackup,
        string? currentGlobal)
    {
        if (previousBackup is
            { SchemaVersion: >= GlobalRegistrationSchemaVersion }
            && string.Equals(
                currentGlobal,
                previousBackup.InstalledCommand,
                StringComparison.Ordinal))
        {
            return NormalizeValue(previousBackup.OriginalGlobalCommand);
        }

        return currentGlobal;
    }

    private static Dictionary<string, string?> BuildOriginalExtensionValues(
        TortoiseDiffToolBackup? previousBackup,
        Dictionary<string, string?> currentExtensions)
    {
        var originals = new Dictionary<string, string?>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var extension in TortoiseDiffToolRegistration
            .SupportedExtensions)
        {
            var current = currentExtensions[extension];
            string? previousOriginal = null;
            var hasPreviousOriginal = previousBackup is not null
                && previousBackup.OriginalValues.TryGetValue(
                    extension,
                    out previousOriginal);
            var stillOwnedByPreviousDiffer = previousBackup is not null
                && string.Equals(
                    current,
                    previousBackup.InstalledCommand,
                    StringComparison.Ordinal);

            originals[extension] = stillOwnedByPreviousDiffer
                && hasPreviousOriginal
                    ? NormalizeValue(previousOriginal)
                    : current ?? (
                        previousBackup is
                        { SchemaVersion: >= GlobalRegistrationSchemaVersion }
                        && hasPreviousOriginal
                            ? NormalizeValue(previousOriginal)
                            : null);
        }

        return originals;
    }

    private Dictionary<string, string?> ReadCurrentExtensionValues()
    {
        return TortoiseDiffToolRegistration.SupportedExtensions.ToDictionary(
            extension => extension,
            extension => NormalizeValue(configurationStore.Read(extension)),
            StringComparer.OrdinalIgnoreCase);
    }

    private void RollBackExtensions(
        IEnumerable<string> clearedExtensions,
        Dictionary<string, string?> currentExtensions)
    {
        foreach (var extension in clearedExtensions.Reverse())
        {
            RestoreExtensionValue(extension, currentExtensions[extension]);
        }
    }

    private void RestorePreviousBackup(TortoiseDiffToolBackup? previousBackup)
    {
        if (previousBackup is null)
        {
            backupStore.Delete();
        }
        else
        {
            backupStore.Save(previousBackup);
        }
    }

    private void RestoreGlobalValue(string? value)
    {
        if (value is null)
        {
            configurationStore.DeleteGlobal();
        }
        else
        {
            configurationStore.WriteGlobal(value);
        }
    }

    private void RestoreExtensionValue(string extension, string? value)
    {
        if (value is null)
        {
            configurationStore.Delete(extension);
        }
        else
        {
            configurationStore.Write(extension, value);
        }
    }

    private static int CountNonEmptyOriginalValues(
        string? originalGlobal,
        IReadOnlyDictionary<string, string?> originalExtensionValues)
    {
        return (originalGlobal is null ? 0 : 1)
            + originalExtensionValues.Values.Count(value => value is not null);
    }

    private static string? NormalizeValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
