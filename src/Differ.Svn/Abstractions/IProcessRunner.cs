using Differ.Svn.Process;

namespace Differ.Svn.Abstractions;

public interface IProcessRunner
{
    Task<ProcessResult> RunTextAsync(
        string executablePath,
        IEnumerable<string> arguments,
        string? workingDirectory,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    Task<ProcessResult> RunBinaryToFileAsync(
        string executablePath,
        IEnumerable<string> arguments,
        string? workingDirectory,
        string outputPath,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    void StartDetached(
        string executablePath,
        IEnumerable<string> arguments,
        string? workingDirectory);
}
