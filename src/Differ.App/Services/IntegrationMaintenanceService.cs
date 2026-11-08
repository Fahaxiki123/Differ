using System.IO;
using System.Security;
using System.Text.Json;
using Differ.Svn;
using Differ.Svn.Abstractions;

namespace Differ.App.Services;

public sealed class IntegrationMaintenanceService(
    ISvnClientLocator locator,
    ITortoiseDiffToolConfigurationStore configurationStore,
    ITortoiseDiffToolBackupStore backupStore)
{
    public const int Success = 0;
    public const int Failed = 1;
    public const int TortoiseSvnNotFound = 2;
    public const int NotFullyRegistered = 4;

    public int Execute(IntegrationMaintenanceOperation operation, string executablePath)
    {
        try
        {
            var service = new TortoiseDiffIntegrationService(configurationStore, backupStore);
            switch (operation)
            {
                case IntegrationMaintenanceOperation.Install:
                    if (locator.FindTortoiseProc() is null)
                    {
                        return TortoiseSvnNotFound;
                    }

                    service.Install(executablePath);
                    return service.GetStatus(executablePath).IsFullyRegistered ? Success : Failed;
                case IntegrationMaintenanceOperation.Restore:
                    return RestoreOwnedIntegration(service, executablePath);
                case IntegrationMaintenanceOperation.Status:
                    return service.GetStatus(executablePath).IsFullyRegistered
                        ? Success
                        : NotFullyRegistered;
                default:
                    return Failed;
            }
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or SecurityException
            or JsonException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException)
        {
            // The installer reports a controlled error for this exit code. Do not
            // log raw registry commands or exception messages containing paths.
            return Failed;
        }
    }

    private int RestoreOwnedIntegration(
        TortoiseDiffIntegrationService service,
        string executablePath)
    {
        var command = TortoiseDiffToolRegistration.BuildCommand(executablePath);
        var backup = backupStore.Load();
        if (backup is not null
            && string.Equals(backup.InstalledCommand, command, StringComparison.Ordinal))
        {
            service.Restore();
        }

        // Missing backups and backups owned by another installation are never
        // grounds to overwrite settings. Still block removal if this executable
        // remains referenced, including manually edited argument lists.
        return IsExecutableReferenced(executablePath) ? Failed : Success;
    }

    private bool IsExecutableReferenced(string executablePath)
    {
        if (ReferencesExecutable(configurationStore.ReadGlobal(), executablePath))
        {
            return true;
        }

        return TortoiseDiffToolRegistration.SupportedExtensions.Any(extension =>
            ReferencesExecutable(configurationStore.Read(extension), executablePath));
    }

    private static bool ReferencesExecutable(string? command, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var text = command.AsSpan().TrimStart().TrimStart('#').TrimStart();
        ReadOnlySpan<char> candidate;
        if (text.Length > 0 && text[0] == '"')
        {
            var end = text[1..].IndexOf('"');
            if (end < 0)
            {
                return false;
            }

            candidate = text.Slice(1, end);
        }
        else
        {
            var end = text.IndexOfAny(' ', '\t');
            candidate = end < 0 ? text : text[..end];
        }

        return Path.IsPathFullyQualified(candidate)
            && string.Equals(
                Path.GetFullPath(candidate.ToString()),
                Path.GetFullPath(executablePath),
                StringComparison.OrdinalIgnoreCase);
    }
}
