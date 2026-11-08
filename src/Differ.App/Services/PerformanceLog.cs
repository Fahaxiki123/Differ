using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Differ.App.Services;

internal static class PerformanceLog
{
    private const long MaximumLogBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly ManualResetEventSlim Idle = new(
        initialState: true);
    private static int _pendingWrites;
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Differ",
        "Logs");
    private static readonly string LogPath = Path.Combine(
        LogDirectory,
        "performance.log");

    public static void Write(
        string operation,
        string fileKind,
        TimeSpan elapsed,
        bool? cacheHit = null,
        long? allocatedBytes = null,
        long? units = null,
        string? counters = null)
    {
        var safeOperation = Sanitize(operation);
        var safeFileKind = Sanitize(fileKind);
        if (Interlocked.Increment(ref _pendingWrites) > 256)
        {
            Interlocked.Decrement(ref _pendingWrites);
            return;
        }
        Idle.Reset();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(LogDirectory);
                    if (File.Exists(LogPath)
                        && new FileInfo(LogPath).Length > MaximumLogBytes)
                    {
                        File.Move(LogPath, $"{LogPath}.previous", overwrite: true);
                    }

                    var cacheText = cacheHit is null
                        ? string.Empty
                        : cacheHit.Value ? " cache=hit" : " cache=miss";
                    var metrics = string.Create(CultureInfo.InvariantCulture,
                        $" allocated_bytes={allocatedBytes ?? 0} units={units ?? 0} {counters}");
                    File.AppendAllText(
                        LogPath,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{DateTime.UtcNow:O} operation={safeOperation} kind={safeFileKind} elapsed_ms={elapsed.TotalMilliseconds:F1}{cacheText}{metrics}{Environment.NewLine}"));
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine(exception);
            }
            finally
            {
                if (Interlocked.Decrement(ref _pendingWrites) == 0)
                {
                    Idle.Set();
                }
            }
        });
    }

    public static void Flush(TimeSpan timeout)
    {
        Idle.Wait(timeout);
    }

    private static string Sanitize(string value)
    {
        return new string(value
            .Where(character => char.IsLetterOrDigit(character)
                || character is '-' or '_' or '.')
            .Take(40)
            .ToArray());
    }
}
