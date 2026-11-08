using System.Diagnostics;
using System.Text;
using Differ.Svn.Abstractions;

namespace Differ.Svn.Process;

public sealed class SafeProcessRunner : IProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public async Task<ProcessResult> RunTextAsync(
        string executablePath,
        IEnumerable<string> arguments,
        string? workingDirectory,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var process = CreateProcess(
            executablePath,
            arguments,
            workingDirectory,
            redirectOutput: true);
        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动外部程序：{executablePath}");
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        linkedCancellation.CancelAfter(timeout ?? DefaultTimeout);

        var outputTask = process.StandardOutput.ReadToEndAsync(linkedCancellation.Token);
        var errorTask = process.StandardError.ReadToEndAsync(linkedCancellation.Token);

        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, output, error);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    public async Task<ProcessResult> RunBinaryToFileAsync(
        string executablePath,
        IEnumerable<string> arguments,
        string? workingDirectory,
        string outputPath,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var process = CreateProcess(
            executablePath,
            arguments,
            workingDirectory,
            redirectOutput: true);
        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动外部程序：{executablePath}");
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        linkedCancellation.CancelAfter(timeout ?? DefaultTimeout);

        await using var destination = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(
            destination,
            linkedCancellation.Token);
        var errorTask = process.StandardError.ReadToEndAsync(linkedCancellation.Token);

        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
            await copyTask.ConfigureAwait(false);
            await destination.FlushAsync(linkedCancellation.Token).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, string.Empty, error);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    public void StartDetached(
        string executablePath,
        IEnumerable<string> arguments,
        string? workingDirectory)
    {
        using var process = CreateProcess(
            executablePath,
            arguments,
            workingDirectory,
            redirectOutput: false);
        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动外部程序：{executablePath}");
        }
    }

    private static System.Diagnostics.Process CreateProcess(
        string executablePath,
        IEnumerable<string> arguments,
        string? workingDirectory,
        bool redirectOutput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = redirectOutput,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            StandardOutputEncoding = redirectOutput ? Encoding.UTF8 : null,
            StandardErrorEncoding = redirectOutput ? Encoding.UTF8 : null,
            WorkingDirectory = workingDirectory ?? string.Empty
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new System.Diagnostics.Process { StartInfo = startInfo };
    }

    private static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and Kill.
        }
    }
}
