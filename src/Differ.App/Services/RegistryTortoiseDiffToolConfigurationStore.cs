using System.Runtime.Versioning;
using Differ.Svn.Abstractions;
using Microsoft.Win32;

namespace Differ.App.Services;

[SupportedOSPlatform("windows")]
public sealed class RegistryTortoiseDiffToolConfigurationStore :
    ITortoiseDiffToolConfigurationStore
{
    private const string RegistryPath = @"Software\TortoiseSVN";
    private const string DiffToolsRegistryPath =
        $@"{RegistryPath}\DiffTools";
    private const string GlobalDiffValueName = "Diff";

    public string? ReadGlobal()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            RegistryPath,
            writable: false);
        return key?.GetValue(GlobalDiffValueName) as string;
    }

    public void WriteGlobal(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        using var key = Registry.CurrentUser.CreateSubKey(
            RegistryPath,
            writable: true);
        key.SetValue(
            GlobalDiffValueName,
            command,
            RegistryValueKind.String);
    }

    public void DeleteGlobal()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            RegistryPath,
            writable: true);
        key?.DeleteValue(
            GlobalDiffValueName,
            throwOnMissingValue: false);
    }

    public string? Read(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        using var key = Registry.CurrentUser.OpenSubKey(
            DiffToolsRegistryPath,
            writable: false);
        return key?.GetValue(extension) as string;
    }

    public void Write(string extension, string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        using var key = Registry.CurrentUser.CreateSubKey(
            DiffToolsRegistryPath,
            writable: true);
        key.SetValue(extension, command, RegistryValueKind.String);
    }

    public void Delete(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        using var key = Registry.CurrentUser.OpenSubKey(
            DiffToolsRegistryPath,
            writable: true);
        key?.DeleteValue(extension, throwOnMissingValue: false);
    }
}
