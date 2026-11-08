using System.Runtime.Versioning;
using System.Security;
using Differ.Svn.Abstractions;
using Microsoft.Win32;

namespace Differ.Svn;

public sealed class RegistryTortoiseSvnInstallationPaths : ITortoiseSvnInstallationPaths
{
    public IEnumerable<string> GetExecutableCandidates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        return ReadWindowsCandidates();
    }

    [SupportedOSPlatform("windows")]
    private static List<string> ReadWindowsCandidates()
    {
        var candidates = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                ReadCandidate(hive, view, @"Software\TortoiseSVN", "Directory", true, candidates);
                ReadCandidate(hive, view, @"Software\TortoiseSVN", "ProcPath", false, candidates);
                ReadCandidate(hive, view,
                    @"Software\Microsoft\Windows\CurrentVersion\App Paths\TortoiseProc.exe",
                    null, false, candidates);
            }
        }

        return candidates;
    }

    [SupportedOSPlatform("windows")]
    private static void ReadCandidate(
        RegistryHive hive,
        RegistryView view,
        string keyPath,
        string? valueName,
        bool isDirectory,
        List<string> candidates)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(keyPath, writable: false);
            if (key?.GetValue(valueName) is not string value
                || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            // These registry values contain paths, never command lines. Do not
            // split out arguments or execute a registry string through a shell.
            var path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(path) || path.Contains('"'))
            {
                return;
            }

            if (isDirectory)
            {
                candidates.Add(Path.Combine(path, "bin", "TortoiseProc.exe"));
                candidates.Add(Path.Combine(path, "TortoiseProc.exe"));
            }
            else
            {
                candidates.Add(path);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or ArgumentException)
        {
            // A restricted or stale registry entry should not disable the other
            // registry views, PATH or the standard installation directories.
        }
    }
}
