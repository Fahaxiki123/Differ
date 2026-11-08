using System;
using System.Collections.Generic;
using System.IO;
using Differ.App.Services;
using Differ.Svn;
using Differ.Svn.Abstractions;
using Differ.Svn.Models;

namespace Differ.App.Tests;

public sealed class IntegrationMaintenanceTests
{
    private const string ExecutablePath = @"C:\公司工具\Differ release\Differ.App.exe";
    private const string OtherExecutablePath = @"C:\Other install\Differ.App.exe";

    [Theory]
    [InlineData("--install-integration", IntegrationMaintenanceOperation.Install)]
    [InlineData("--restore-integration", IntegrationMaintenanceOperation.Restore)]
    [InlineData("--integration-status", IntegrationMaintenanceOperation.Status)]
    [InlineData("--INSTALL-INTEGRATION", IntegrationMaintenanceOperation.Invalid)]
    [InlineData("--install-integration=true", IntegrationMaintenanceOperation.Invalid)]
    [InlineData("--restore-integration-extra", IntegrationMaintenanceOperation.Invalid)]
    public void MaintenanceParserAcceptsOnlyExactStandaloneCommands(
        string argument,
        IntegrationMaintenanceOperation expected)
    {
        Assert.Equal(expected, IntegrationMaintenanceArguments.Parse([argument]));
    }

    [Theory]
    [InlineData("--install-integration", "--restore-integration")]
    [InlineData("--integration-status", "--integration-status")]
    [InlineData("--restore-integration", "C:\\文件.xlsx")]
    [InlineData("--base", "--install-integration")]
    public void MaintenanceParserRejectsMixedOrRepeatedCommands(string first, string second)
    {
        Assert.Equal(IntegrationMaintenanceOperation.Invalid,
            IntegrationMaintenanceArguments.Parse([first, second]));
    }

    [Fact]
    public void ComparisonArgumentsAndNoArgumentsDoNotEnterMaintenance()
    {
        Assert.Equal(IntegrationMaintenanceOperation.None, IntegrationMaintenanceArguments.Parse([]));
        Assert.Equal(IntegrationMaintenanceOperation.None,
            IntegrationMaintenanceArguments.Parse(["--base", "原件.xlsx", "--mine", "新件.xlsx"]));
    }

    [Fact]
    public void InstallWithoutTortoiseSvnReturnsTwoAndDoesNotWriteSettings()
    {
        var settings = new MemoryConfiguration { Global = "ExistingTool" };
        var backups = new MemoryBackups();
        var maintenance = CreateService(settings, backups, tortoiseFound: false);

        Assert.Equal(2, maintenance.Execute(IntegrationMaintenanceOperation.Install, ExecutablePath));
        Assert.Equal("ExistingTool", settings.Global);
        Assert.Null(backups.Backup);
    }

    [Fact]
    public void InstallStatusAndRestorePreserveTargetComputerToolsAndQuotedUnicodePath()
    {
        var settings = new MemoryConfiguration { Global = "CompanyTool %base %mine" };
        settings.Write(".xlsx", "CompanySpreadsheetTool");
        settings.Write(".pdf", "PdfTool");
        var backups = new MemoryBackups();
        var maintenance = CreateService(settings, backups);

        Assert.Equal(4, maintenance.Execute(IntegrationMaintenanceOperation.Status, ExecutablePath));
        Assert.Equal(0, maintenance.Execute(IntegrationMaintenanceOperation.Install, ExecutablePath));
        Assert.StartsWith($"\"{ExecutablePath}\" --base %base --mine %mine", settings.Global, StringComparison.Ordinal);
        Assert.Null(settings.Read(".xlsx"));
        Assert.Equal("PdfTool", settings.Read(".pdf"));
        Assert.Equal(0, maintenance.Execute(IntegrationMaintenanceOperation.Status, ExecutablePath));
        Assert.Equal(0, maintenance.Execute(IntegrationMaintenanceOperation.Restore, ExecutablePath));
        Assert.Equal("CompanyTool %base %mine", settings.Global);
        Assert.Equal("CompanySpreadsheetTool", settings.Read(".xlsx"));
        Assert.Null(backups.Backup);
    }

    [Fact]
    public void StatusRejectsAnAdvancedOverrideEvenWhenGlobalMatches()
    {
        var settings = new MemoryConfiguration();
        var maintenance = CreateService(settings, new MemoryBackups());
        Assert.Equal(0, maintenance.Execute(IntegrationMaintenanceOperation.Install, ExecutablePath));
        settings.Write(".xlsx", "LaterManualTool");

        Assert.Equal(4, maintenance.Execute(IntegrationMaintenanceOperation.Status, ExecutablePath));
    }

    [Fact]
    public void OldInstallationCannotRestoreOrDeleteNewInstallationBackup()
    {
        var settings = new MemoryConfiguration { Global = "OriginalTool" };
        settings.Write(".xlsx", "OriginalSpreadsheetTool");
        var backups = new MemoryBackups();
        var maintenance = CreateService(settings, backups);
        maintenance.Execute(IntegrationMaintenanceOperation.Install, ExecutablePath);
        maintenance.Execute(IntegrationMaintenanceOperation.Install, OtherExecutablePath);
        var newBackup = backups.Backup;

        Assert.Equal(0, maintenance.Execute(IntegrationMaintenanceOperation.Restore, ExecutablePath));
        Assert.Equal(TortoiseDiffToolRegistration.BuildCommand(OtherExecutablePath), settings.Global);
        Assert.Null(settings.Read(".xlsx"));
        Assert.Same(newBackup, backups.Backup);
        Assert.Equal(0, maintenance.Execute(IntegrationMaintenanceOperation.Restore, OtherExecutablePath));
        Assert.Equal("OriginalTool", settings.Global);
        Assert.Equal("OriginalSpreadsheetTool", settings.Read(".xlsx"));
    }

    [Fact]
    public void RestoreKeepsManualToolsAndTheirBackup()
    {
        var settings = new MemoryConfiguration { Global = "OriginalTool" };
        settings.Write(".xlsx", "OriginalSpreadsheetTool");
        var backups = new MemoryBackups();
        var maintenance = CreateService(settings, backups);
        maintenance.Execute(IntegrationMaintenanceOperation.Install, ExecutablePath);
        settings.Global = "LaterManualGlobal";
        settings.Write(".xlsx", "LaterManualSpreadsheet");

        Assert.Equal(0, maintenance.Execute(IntegrationMaintenanceOperation.Restore, ExecutablePath));
        Assert.Equal("LaterManualGlobal", settings.Global);
        Assert.Equal("LaterManualSpreadsheet", settings.Read(".xlsx"));
        Assert.NotNull(backups.Backup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingBackupBlocksUninstallWhenGlobalOrExtensionStillReferencesExecutable(bool extension)
    {
        var settings = new MemoryConfiguration();
        var command = TortoiseDiffToolRegistration.BuildCommand(ExecutablePath);
        if (extension)
        {
            settings.Write(".xlsx", command);
        }
        else
        {
            settings.Global = command;
        }

        var maintenance = CreateService(settings, new MemoryBackups());
        Assert.Equal(1, maintenance.Execute(IntegrationMaintenanceOperation.Restore, ExecutablePath));
        Assert.Equal(command, extension ? settings.Read(".xlsx") : settings.Global);
    }

    [Fact]
    public void MissingBackupAndNoReferenceIsSafeToUninstall()
    {
        var settings = new MemoryConfiguration { Global = "UnrelatedTool" };
        Assert.Equal(0, CreateService(settings, new MemoryBackups())
            .Execute(IntegrationMaintenanceOperation.Restore, ExecutablePath));
        Assert.Equal("UnrelatedTool", settings.Global);
    }

    [Fact]
    public void ModifiedArgumentsStillReferencingThisExecutableBlockUninstall()
    {
        var settings = new MemoryConfiguration();
        var backups = new MemoryBackups();
        var maintenance = CreateService(settings, backups);
        maintenance.Execute(IntegrationMaintenanceOperation.Install, ExecutablePath);
        settings.Global = $"\"{ExecutablePath}\" %base %mine";

        Assert.Equal(1, maintenance.Execute(IntegrationMaintenanceOperation.Restore, ExecutablePath));
        Assert.Equal($"\"{ExecutablePath}\" %base %mine", settings.Global);
        Assert.NotNull(backups.Backup);
    }

    [Fact]
    public void DifferentOwnerBackupCannotBeUsedToRemoveCurrentReference()
    {
        var settings = new MemoryConfiguration();
        var backups = new MemoryBackups();
        var maintenance = CreateService(settings, backups);
        maintenance.Execute(IntegrationMaintenanceOperation.Install, OtherExecutablePath);
        settings.Global = TortoiseDiffToolRegistration.BuildCommand(ExecutablePath);

        Assert.Equal(1, maintenance.Execute(IntegrationMaintenanceOperation.Restore, ExecutablePath));
        Assert.Equal(TortoiseDiffToolRegistration.BuildCommand(ExecutablePath), settings.Global);
        Assert.Equal(TortoiseDiffToolRegistration.BuildCommand(OtherExecutablePath), backups.Backup?.InstalledCommand);
    }

    [Theory]
    [InlineData(IntegrationMaintenanceOperation.Install)]
    [InlineData(IntegrationMaintenanceOperation.Restore)]
    [InlineData(IntegrationMaintenanceOperation.Status)]
    public void InvalidBackupReturnsFailureWithoutChangingSettings(IntegrationMaintenanceOperation operation)
    {
        var settings = new MemoryConfiguration { Global = "OriginalTool" };
        var backups = new MemoryBackups { ReadError = new InvalidDataException("Invalid backup") };

        Assert.Equal(1, CreateService(settings, backups).Execute(operation, ExecutablePath));
        Assert.Equal("OriginalTool", settings.Global);
    }

    [Fact]
    public void BackupWriteAccessFailureCannotMutateRegistry()
    {
        var settings = new MemoryConfiguration { Global = "OriginalTool" };
        settings.Write(".xlsx", "OriginalSpreadsheet");
        var backups = new MemoryBackups { WriteError = new UnauthorizedAccessException() };

        Assert.Equal(1, CreateService(settings, backups)
            .Execute(IntegrationMaintenanceOperation.Install, ExecutablePath));
        Assert.Equal("OriginalTool", settings.Global);
        Assert.Equal("OriginalSpreadsheet", settings.Read(".xlsx"));
    }

    [Fact]
    public void RegistryRestoreFailureIsNotReportedAsSuccess()
    {
        var settings = new MemoryConfiguration { Global = "OriginalTool" };
        var backups = new MemoryBackups();
        var maintenance = CreateService(settings, backups);
        maintenance.Execute(IntegrationMaintenanceOperation.Install, ExecutablePath);
        settings.WriteError = new UnauthorizedAccessException();

        Assert.Equal(1, maintenance.Execute(IntegrationMaintenanceOperation.Restore, ExecutablePath));
        Assert.Equal(TortoiseDiffToolRegistration.BuildCommand(ExecutablePath), settings.Global);
        Assert.NotNull(backups.Backup);
    }

    private static IntegrationMaintenanceService CreateService(
        MemoryConfiguration settings,
        MemoryBackups backups,
        bool tortoiseFound = true)
    {
        return new IntegrationMaintenanceService(new StubLocator(tortoiseFound), settings, backups);
    }

    private sealed class StubLocator(bool tortoiseFound) : ISvnClientLocator
    {
        public string? FindTortoiseProc(string? configuredPath = null) =>
            tortoiseFound ? @"C:\Company Tools\TortoiseSVN\bin\TortoiseProc.exe" : null;

        public string? FindSvnExecutable(string? configuredPath = null) =>
            throw new InvalidOperationException("Maintenance must not require svn.exe.");

        public string? FindWorkingCopyRoot(string startPath) =>
            throw new InvalidOperationException("Maintenance must not inspect working copies.");
    }

    private sealed class MemoryConfiguration : ITortoiseDiffToolConfigurationStore
    {
        private readonly Dictionary<string, string> _extensions = new(StringComparer.OrdinalIgnoreCase);

        public string? Global { get; set; }

        public Exception? WriteError { get; set; }

        public string? ReadGlobal() => Global;

        public void WriteGlobal(string command)
        {
            if (WriteError is not null)
            {
                throw WriteError;
            }

            Global = command;
        }

        public void DeleteGlobal() => Global = null;

        public string? Read(string extension) => _extensions.GetValueOrDefault(extension);

        public void Write(string extension, string command) => _extensions[extension] = command;

        public void Delete(string extension) => _extensions.Remove(extension);
    }

    private sealed class MemoryBackups : ITortoiseDiffToolBackupStore
    {
        public TortoiseDiffToolBackup? Backup { get; set; }

        public Exception? ReadError { get; set; }

        public Exception? WriteError { get; set; }

        public TortoiseDiffToolBackup? Load()
        {
            if (ReadError is not null)
            {
                throw ReadError;
            }

            return Backup;
        }

        public void Save(TortoiseDiffToolBackup backup)
        {
            if (WriteError is not null)
            {
                throw WriteError;
            }

            Backup = backup;
        }

        public void Delete() => Backup = null;
    }
}
