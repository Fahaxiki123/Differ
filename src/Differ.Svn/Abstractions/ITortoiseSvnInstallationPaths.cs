namespace Differ.Svn.Abstractions;

public interface ITortoiseSvnInstallationPaths
{
    IEnumerable<string> GetExecutableCandidates();
}
