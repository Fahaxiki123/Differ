namespace Differ.Svn.Abstractions;

public interface ISvnClientLocator
{
    string? FindWorkingCopyRoot(string startPath);

    string? FindSvnExecutable(string? configuredPath = null);

    string? FindTortoiseProc(string? configuredPath = null);
}
