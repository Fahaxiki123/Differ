using Differ.Svn.Abstractions;

namespace Differ.Svn;

public sealed class SvnClientLocator : ISvnClientLocator
{
    private readonly ITortoiseSvnInstallationPaths _installationPaths;
    private readonly string? _pathValue;
    private readonly IReadOnlyList<string> _programFilesRoots;

    private static readonly string[] KnownRelativeCandidates =
    [
        @"TortoiseSVN\bin\svn.exe",
        @"VisualSVN Server\bin\svn.exe",
        @"SlikSvn\bin\svn.exe",
        @"CollabNet\Subversion Client\svn.exe"
    ];

    public SvnClientLocator()
        : this(
            new RegistryTortoiseSvnInstallationPaths(),
            Environment.GetEnvironmentVariable("PATH"),
            GetProgramFilesRoots())
    {
    }

    public SvnClientLocator(
        ITortoiseSvnInstallationPaths installationPaths,
        string? pathValue,
        IEnumerable<string> programFilesRoots)
    {
        ArgumentNullException.ThrowIfNull(installationPaths);
        ArgumentNullException.ThrowIfNull(programFilesRoots);
        _installationPaths = installationPaths;
        _pathValue = pathValue;
        _programFilesRoots = programFilesRoots.ToArray();
    }

    public string? FindWorkingCopyRoot(string startPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startPath);

        var fullPath = Path.GetFullPath(startPath);
        var directory = File.Exists(fullPath)
            ? new FileInfo(fullPath).Directory
            : new DirectoryInfo(fullPath);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".svn"))
                || File.Exists(Path.Combine(directory.FullName, ".svn")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public string? FindSvnExecutable(string? configuredPath = null)
    {
        if (IsExecutable(configuredPath))
        {
            return Path.GetFullPath(configuredPath!);
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "svn", "svn.exe");
        if (IsExecutable(bundled))
        {
            return bundled;
        }

        var fromPath = FindOnPath("svn.exe");
        if (fromPath is not null)
        {
            return fromPath;
        }

        foreach (var programFiles in _programFilesRoots)
        {
            foreach (var relativeCandidate in KnownRelativeCandidates)
            {
                var candidate = Path.Combine(programFiles, relativeCandidate);
                if (IsExecutable(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public string? FindTortoiseProc(string? configuredPath = null)
    {
        if (IsExecutable(configuredPath))
        {
            return Path.GetFullPath(configuredPath!);
        }

        var fromPath = FindOnPath("TortoiseProc.exe");
        if (fromPath is not null)
        {
            return fromPath;
        }

        foreach (var candidate in _installationPaths.GetExecutableCandidates())
        {
            if (Path.IsPathFullyQualified(candidate)
                && IsExecutable(candidate)
                && string.Equals(Path.GetFileName(candidate), "TortoiseProc.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(candidate);
            }
        }

        foreach (var programFiles in _programFilesRoots)
        {
            var candidate = Path.Combine(
                programFiles,
                "TortoiseSVN",
                "bin",
                "TortoiseProc.exe");
            if (IsExecutable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetProgramFilesRoots()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        return roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private string? FindOnPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(_pathValue))
        {
            return null;
        }

        foreach (var directory in _pathValue.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (IsExecutable(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static bool IsExecutable(string? path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && File.Exists(path)
            && string.Equals(
                Path.GetExtension(path),
                ".exe",
                StringComparison.OrdinalIgnoreCase);
    }
}
