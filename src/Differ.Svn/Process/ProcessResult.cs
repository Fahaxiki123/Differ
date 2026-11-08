namespace Differ.Svn.Process;

public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
