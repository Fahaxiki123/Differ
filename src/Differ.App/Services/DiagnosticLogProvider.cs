using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Differ.App.Services;

/// <summary>
/// Persists bounded, controlled diagnostic metadata. Logging state, message
/// formatters, scopes, exception messages and stack traces are never read.
/// </summary>
internal sealed class DiagnosticLogProvider : ILoggerProvider
{
    private const long DefaultMaximumBytes = 1024 * 1024;
    private const int DefaultRetainedFiles = 3;
    private readonly string _directory;
    private readonly long _maximumBytes;
    private readonly int _retainedFiles;
    private readonly Channel<DiagnosticEvent> _events = Channel.CreateBounded<DiagnosticEvent>(
        new BoundedChannelOptions(512)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    private readonly Task _writer;
    private int _disposed;

    public DiagnosticLogProvider()
        : this(Path.Combine(
            AppRuntimeProfile.DataRoot, "Logs"))
    {
    }

    internal DiagnosticLogProvider(
        string directory,
        long maximumBytes = DefaultMaximumBytes,
        int retainedFiles = DefaultRetainedFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 512);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedFiles, 1);
        _directory = Path.GetFullPath(directory);
        _maximumBytes = maximumBytes;
        _retainedFiles = retainedFiles;
        _writer = Task.Run(WriteEventsAsync);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new DiagnosticLogger(this, NormalizeCategory(categoryName));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _events.Writer.TryComplete();
            _writer.Wait(TimeSpan.FromSeconds(1));
        }
    }

    internal Task CompleteAsync()
    {
        _events.Writer.TryComplete();
        return _writer;
    }

    private async Task WriteEventsAsync()
    {
        await foreach (var item in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, "diagnostics.log");
                var line = JsonSerializer.Serialize(item) + Environment.NewLine;
                if (File.Exists(path)
                    && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > _maximumBytes)
                {
                    Rotate(path);
                }

                await File.AppendAllTextAsync(path, line, Encoding.UTF8).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must not interrupt comparison when disk access
                // fails. Do not send the exception message to another sink.
                Debug.WriteLine($"Diagnostic log unavailable: {exception.GetType().Name}");
            }
        }
    }

    private void Rotate(string path)
    {
        if (_retainedFiles == 1)
        {
            File.Delete(path);
            return;
        }

        for (var index = _retainedFiles - 1; index > 0; index--)
        {
            var source = index == 1 ? path : $"{path}.{index - 1}";
            if (File.Exists(source))
            {
                File.Move(source, $"{path}.{index}", overwrite: true);
            }
        }
    }

    private static string NormalizeCategory(string category)
    {
        // Category strings are caller-controlled too. Map to known constants
        // rather than sanitizing a path into a still-identifiable filename.
        return category switch
        {
            "Differ.App.App" => "App",
            "Differ.App.ViewModels.DiffWindowViewModel" => "Comparison",
            "Differ.App.ViewModels.ComparisonWorkspaceViewModel" => "Workspace",
            "Differ.App.ViewModels.IntegrationWindowViewModel" => "Integration",
            "Differ.App.Services.SnapshotCleanupService" => "SnapshotCleanup",
            _ => "Other"
        };
    }

    private sealed class DiagnosticLogger(DiagnosticLogProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel is >= LogLevel.Information and <= LogLevel.Critical;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider._events.Writer.TryWrite(new DiagnosticEvent(
                    DateTimeOffset.UtcNow,
                    eventId.Id,
                    category,
                    logLevel.ToString(),
                    exception?.GetType().Name));
            }
        }
    }

    private sealed record DiagnosticEvent(
        DateTimeOffset TimestampUtc,
        int EventId,
        string Category,
        string Level,
        string? ExceptionType);
}
