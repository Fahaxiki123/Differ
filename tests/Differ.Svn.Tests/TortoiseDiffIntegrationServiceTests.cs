using Differ.Svn.Abstractions;
using Differ.Svn.Models;

namespace Differ.Svn.Tests;

public sealed class TortoiseDiffIntegrationServiceTests
{
    [Fact]
    public void InstallBacksUpGlobalAndClearsSupportedAdvancedOverrides()
    {
        var configuration = new InMemoryConfigurationStore
        {
            GlobalCommand = "BeyondCompare %base %mine"
        };
        configuration.Write(".xlsx", "SpreadsheetCompare %base %mine");
        configuration.Write(".docx", "DocumentCompare %base %mine");
        configuration.Write(".pdf", "PdfCompare %base %mine");
        var backups = new InMemoryBackupStore();
        var service = new TortoiseDiffIntegrationService(
            configuration,
            backups);

        var result = service.Install(@"E:\Differ\Differ.App.exe");

        Assert.Equal(1, result.WrittenCount);
        Assert.Equal(3, result.BackedUpExistingCount);
        Assert.Equal(2, result.ClearedExtensionOverrideCount);
        Assert.Equal(result.Command, configuration.ReadGlobal());
        Assert.Null(configuration.Read(".xlsx"));
        Assert.Null(configuration.Read(".docx"));
        Assert.Equal(
            "PdfCompare %base %mine",
            configuration.Read(".pdf"));
        Assert.Equal(
            "BeyondCompare %base %mine",
            backups.Backup?.OriginalGlobalCommand);
        Assert.Equal(
            "SpreadsheetCompare %base %mine",
            backups.Backup?.OriginalValues[".xlsx"]);
        Assert.Equal(
            "DocumentCompare %base %mine",
            backups.Backup?.OriginalValues[".docx"]);
        Assert.Equal(2, backups.Backup?.SchemaVersion);
        Assert.Equal(
            TortoiseDiffToolRegistration.SupportedExtensions.Count,
            backups.Backup?.OriginalValues.Count);
    }

    [Fact]
    public void ReinstallKeepsOriginalValuesWhileDifferStillOwnsConfiguration()
    {
        var configuration = new InMemoryConfigurationStore
        {
            GlobalCommand = "BeyondCompare %base %mine"
        };
        configuration.Write(".xlsx", "SpreadsheetCompare %base %mine");
        var backups = new InMemoryBackupStore();
        var service = new TortoiseDiffIntegrationService(
            configuration,
            backups);

        service.Install(@"E:\First\Differ.App.exe");
        service.Install(@"E:\Moved\Differ.App.exe");

        Assert.Equal(
            "BeyondCompare %base %mine",
            backups.Backup?.OriginalGlobalCommand);
        Assert.Equal(
            "SpreadsheetCompare %base %mine",
            backups.Backup?.OriginalValues[".xlsx"]);
        Assert.Contains(
            "\"E:\\Moved\\Differ.App.exe\"",
            configuration.ReadGlobal(),
            StringComparison.Ordinal);
        Assert.Null(configuration.Read(".xlsx"));
    }

    [Fact]
    public void ExplicitReinstallBacksUpNewManualOverridesBeforeClearingThem()
    {
        var configuration = new InMemoryConfigurationStore
        {
            GlobalCommand = "BeyondCompare %base %mine"
        };
        configuration.Write(".xlsx", "SpreadsheetCompare %base %mine");
        var backups = new InMemoryBackupStore();
        var service = new TortoiseDiffIntegrationService(
            configuration,
            backups);
        service.Install(@"E:\First\Differ.App.exe");

        configuration.WriteGlobal("MyNewGlobalTool %base %mine");
        configuration.Write(".xlsx", "MyNewSpreadsheetTool %base %mine");

        var result = service.Install(@"E:\Moved\Differ.App.exe");

        Assert.Equal(1, result.ClearedExtensionOverrideCount);
        Assert.Equal(
            "MyNewGlobalTool %base %mine",
            backups.Backup?.OriginalGlobalCommand);
        Assert.Equal(
            "MyNewSpreadsheetTool %base %mine",
            backups.Backup?.OriginalValues[".xlsx"]);
        Assert.Null(configuration.Read(".xlsx"));
        Assert.Contains(
            "\"E:\\Moved\\Differ.App.exe\"",
            configuration.ReadGlobal(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void InstallUpgradesSchemaOneAndPreservesItsRestoreValues()
    {
        const string oldDifferCommand =
            "\"E:\\Old\\Differ.App.exe\" --base %base --mine %mine";
        var configuration = new InMemoryConfigurationStore
        {
            GlobalCommand = "BeyondCompare %base %mine"
        };
        configuration.Write(".xlsx", oldDifferCommand);
        configuration.Write(".docx", oldDifferCommand);
        configuration.Write(".csv", "CsvCompare %base %mine");
        var backups = new InMemoryBackupStore
        {
            Backup = CreateLegacyBackup(
                oldDifferCommand,
                new Dictionary<string, string?>
                {
                    [".xlsx"] = "SpreadsheetCompare %base %mine",
                    [".docx"] = null
                })
        };
        var service = new TortoiseDiffIntegrationService(
            configuration,
            backups);

        var result = service.Install(@"E:\New\Differ.App.exe");

        Assert.Equal(3, result.ClearedExtensionOverrideCount);
        Assert.Null(configuration.Read(".xlsx"));
        Assert.Null(configuration.Read(".docx"));
        Assert.Null(configuration.Read(".csv"));
        Assert.Equal(2, backups.Backup?.SchemaVersion);
        Assert.Equal(
            "BeyondCompare %base %mine",
            backups.Backup?.OriginalGlobalCommand);
        Assert.Equal(
            "SpreadsheetCompare %base %mine",
            backups.Backup?.OriginalValues[".xlsx"]);
        Assert.Null(backups.Backup?.OriginalValues[".docx"]);
        Assert.Equal(
            "CsvCompare %base %mine",
            backups.Backup?.OriginalValues[".csv"]);
    }

    [Fact]
    public void RestoreReturnsGlobalAndAdvancedOverrides()
    {
        var configuration = new InMemoryConfigurationStore
        {
            GlobalCommand = "BeyondCompare %base %mine"
        };
        configuration.Write(".xlsx", "SpreadsheetCompare %base %mine");
        configuration.Write(".csv", "CsvCompare %base %mine");
        var backups = new InMemoryBackupStore();
        var service = new TortoiseDiffIntegrationService(
            configuration,
            backups);
        service.Install(@"E:\Differ\Differ.App.exe");

        var result = service.Restore();

        Assert.Equal(3, result.RestoredCount);
        Assert.Equal(0, result.SkippedChangedCount);
        Assert.True(result.BackupRemoved);
        Assert.Equal(
            "BeyondCompare %base %mine",
            configuration.ReadGlobal());
        Assert.Equal(
            "SpreadsheetCompare %base %mine",
            configuration.Read(".xlsx"));
        Assert.Equal(
            "CsvCompare %base %mine",
            configuration.Read(".csv"));
        Assert.Null(backups.Backup);
    }

    [Fact]
    public void RestoreDoesNotOverwriteALaterManualGlobalChange()
    {
        var configuration = new InMemoryConfigurationStore();
        var backups = new InMemoryBackupStore();
        var service = new TortoiseDiffIntegrationService(
            configuration,
            backups);
        service.Install(@"E:\Differ\Differ.App.exe");
        configuration.WriteGlobal("MyNewTool %base %mine");

        var result = service.Restore();

        Assert.Equal(0, result.RestoredCount);
        Assert.Equal(1, result.SkippedChangedCount);
        Assert.False(result.BackupRemoved);
        Assert.Equal("MyNewTool %base %mine", configuration.ReadGlobal());
        Assert.NotNull(backups.Backup);
    }

    [Fact]
    public void RestoreDoesNotOverwriteALaterManualAdvancedOverride()
    {
        var configuration = new InMemoryConfigurationStore
        {
            GlobalCommand = "BeyondCompare %base %mine"
        };
        configuration.Write(".xlsx", "SpreadsheetCompare %base %mine");
        configuration.Write(".docx", "DocumentCompare %base %mine");
        var backups = new InMemoryBackupStore();
        var service = new TortoiseDiffIntegrationService(
            configuration,
            backups);
        service.Install(@"E:\Differ\Differ.App.exe");
        configuration.Write(".xlsx", "MyNewSpreadsheetTool %base %mine");

        var result = service.Restore();

        Assert.Equal(2, result.RestoredCount);
        Assert.Equal(1, result.SkippedChangedCount);
        Assert.False(result.BackupRemoved);
        Assert.Equal(
            "BeyondCompare %base %mine",
            configuration.ReadGlobal());
        Assert.Equal(
            "MyNewSpreadsheetTool %base %mine",
            configuration.Read(".xlsx"));
        Assert.Equal(
            "DocumentCompare %base %mine",
            configuration.Read(".docx"));
        Assert.NotNull(backups.Backup);
    }

    [Fact]
    public void RestoreStillSupportsSchemaOneExtensionBackups()
    {
        const string oldDifferCommand =
            "\"E:\\Old\\Differ.App.exe\" --base %base --mine %mine";
        var configuration = new InMemoryConfigurationStore();
        configuration.Write(".xlsx", oldDifferCommand);
        configuration.Write(".docx", oldDifferCommand);
        configuration.Write(".csv", "CsvCompare %base %mine");
        var backups = new InMemoryBackupStore
        {
            Backup = CreateLegacyBackup(
                oldDifferCommand,
                new Dictionary<string, string?>
                {
                    [".xlsx"] = "SpreadsheetCompare %base %mine",
                    [".docx"] = null
                })
        };
        var service = new TortoiseDiffIntegrationService(
            configuration,
            backups);

        var result = service.Restore();

        Assert.Equal(2, result.RestoredCount);
        Assert.Equal(0, result.SkippedChangedCount);
        Assert.True(result.BackupRemoved);
        Assert.Equal(
            "SpreadsheetCompare %base %mine",
            configuration.Read(".xlsx"));
        Assert.Null(configuration.Read(".docx"));
        Assert.Equal(
            "CsvCompare %base %mine",
            configuration.Read(".csv"));
        Assert.Null(backups.Backup);
    }

    [Fact]
    public void StatusRequiresGlobalCommandAndNoSupportedAdvancedOverrides()
    {
        var executablePath = @"E:\Differ\Differ.App.exe";
        var command = TortoiseDiffToolRegistration.BuildCommand(executablePath);
        var configuration = new InMemoryConfigurationStore
        {
            GlobalCommand = command
        };
        configuration.Write(".xlsx", "AnotherTool %base %mine");
        var service = new TortoiseDiffIntegrationService(
            configuration,
            new InMemoryBackupStore());

        var status = service.GetStatus(executablePath);

        Assert.False(status.IsFullyRegistered);
        Assert.Equal(0, status.RegisteredCount);
        Assert.Equal(1, status.SupportedCount);
        Assert.Equal(1, status.ExistingOtherToolCount);

        configuration.Delete(".xlsx");

        status = service.GetStatus(executablePath);

        Assert.True(status.IsFullyRegistered);
        Assert.Equal(1, status.RegisteredCount);
        Assert.Equal(0, status.ExistingOtherToolCount);
    }

    [Fact]
    public void StatusCountsMismatchedGlobalAndEachSupportedOverride()
    {
        var configuration = new InMemoryConfigurationStore
        {
            GlobalCommand = "BeyondCompare %base %mine"
        };
        configuration.Write(".xlsx", "SpreadsheetCompare %base %mine");
        configuration.Write(".docx", "DocumentCompare %base %mine");
        configuration.Write(".pdf", "PdfCompare %base %mine");
        var service = new TortoiseDiffIntegrationService(
            configuration,
            new InMemoryBackupStore());

        var status = service.GetStatus(@"E:\Differ\Differ.App.exe");

        Assert.False(status.IsFullyRegistered);
        Assert.Equal(3, status.ExistingOtherToolCount);
    }

    [Fact]
    public void InstallRollsBackOverridesAndBackupWhenGlobalWriteFails()
    {
        var configuration = new InMemoryConfigurationStore
        {
            GlobalCommand = "BeyondCompare %base %mine"
        };
        configuration.Write(".xlsx", "SpreadsheetCompare %base %mine");
        var backups = new InMemoryBackupStore();
        var service = new TortoiseDiffIntegrationService(
            configuration,
            backups);
        configuration.FailNextGlobalWrite = true;

        Assert.Throws<InvalidOperationException>(
            () => service.Install(@"E:\Differ\Differ.App.exe"));

        Assert.Equal(
            "BeyondCompare %base %mine",
            configuration.ReadGlobal());
        Assert.Equal(
            "SpreadsheetCompare %base %mine",
            configuration.Read(".xlsx"));
        Assert.Null(backups.Backup);
    }

    [Fact]
    public void SupportedExtensionsIncludeCsvForGlobalOverrideCleanup()
    {
        Assert.Contains(
            ".csv",
            TortoiseDiffToolRegistration.SupportedExtensions);
    }

    private static TortoiseDiffToolBackup CreateLegacyBackup(
        string installedCommand,
        IReadOnlyDictionary<string, string?> originalValues)
    {
        return new TortoiseDiffToolBackup
        {
            SchemaVersion = 1,
            CreatedUtc = DateTimeOffset.UtcNow,
            InstalledCommand = installedCommand,
            OriginalValues = originalValues
        };
    }

    private sealed class InMemoryConfigurationStore :
        ITortoiseDiffToolConfigurationStore
    {
        private readonly Dictionary<string, string> _values =
            new(StringComparer.OrdinalIgnoreCase);

        public string? GlobalCommand { get; set; }

        public bool FailNextGlobalWrite { get; set; }

        public string? ReadGlobal()
        {
            return GlobalCommand;
        }

        public void WriteGlobal(string command)
        {
            if (FailNextGlobalWrite)
            {
                FailNextGlobalWrite = false;
                throw new InvalidOperationException("Simulated write failure.");
            }

            GlobalCommand = command;
        }

        public void DeleteGlobal()
        {
            GlobalCommand = null;
        }

        public string? Read(string extension)
        {
            return _values.TryGetValue(extension, out var value)
                ? value
                : null;
        }

        public void Write(string extension, string command)
        {
            _values[extension] = command;
        }

        public void Delete(string extension)
        {
            _values.Remove(extension);
        }
    }

    private sealed class InMemoryBackupStore : ITortoiseDiffToolBackupStore
    {
        public TortoiseDiffToolBackup? Backup { get; set; }

        public TortoiseDiffToolBackup? Load()
        {
            return Backup;
        }

        public void Save(TortoiseDiffToolBackup backup)
        {
            Backup = backup;
        }

        public void Delete()
        {
            Backup = null;
        }
    }
}
