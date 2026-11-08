using System.Collections.Concurrent;
using Differ.Core.External;
using Microsoft.Extensions.Logging;

namespace Differ.App.Services;

/// <summary>
/// Reclaims only session-store-owned snapshots. Office readers may retain a
/// handle briefly after cancellation, so failed deletion has bounded retries.
/// </summary>
internal sealed class SnapshotCleanupService : IDisposable
{
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1)
    ];
    private static readonly Action<ILogger, Exception?> LogDeferredDeletion = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2102, nameof(LogDeferredDeletion)),
        "Snapshot cleanup will be retried on exit or by expiry cleanup");
    private readonly ExternalDiffSessionStore _store;
    private readonly ILogger<SnapshotCleanupService> _logger;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly ConcurrentDictionary<Guid, PendingCleanup> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private bool _disposed;

    public SnapshotCleanupService(
        ExternalDiffSessionStore store,
        ILogger<SnapshotCleanupService> logger)
        : this(store, logger, DefaultRetryDelays)
    {
    }

    internal SnapshotCleanupService(
        ExternalDiffSessionStore store,
        ILogger<SnapshotCleanupService> logger,
        IReadOnlyList<TimeSpan> retryDelays)
    {
        _store = store;
        _logger = logger;
        _retryDelays = retryDelays;
    }

    public void DeleteOrQueue(ExternalDiffSession session)
    {
        if (_store.TryDelete(session))
        {
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var pending = new PendingCleanup(session);
            if (_pending.TryAdd(session.SessionId, pending))
            {
                var token = _shutdown.Token;
                pending.Work = Task.Run(() => RetryAsync(session, token));
            }
        }
    }

    internal Task WaitForRetriesAsync()
    {
        return Task.WhenAll(_pending.Values.Select(pending => pending.Work));
    }

    public void Dispose()
    {
        Task[] work;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown.Cancel();
            work = _pending.Values.Select(pending => pending.Work).ToArray();
        }

        // Retry workers never need the dispatcher. A short, bounded final
        // drain lets canceled Office readers release their handles on exit.
        Task.Run(async () =>
        {
            await Task.WhenAll(work).ConfigureAwait(false);
            for (var attempt = 0; attempt < 4 && !_pending.IsEmpty; attempt++)
            {
                foreach (var pair in _pending)
                {
                    if (_store.TryDelete(pair.Value.Session))
                    {
                        _pending.TryRemove(pair.Key, out _);
                    }
                }

                if (!_pending.IsEmpty && attempt < 3)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
        }).Wait(TimeSpan.FromSeconds(1));
        _shutdown.Dispose();
    }

    private async Task RetryAsync(ExternalDiffSession session, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var delay in _retryDelays)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                if (_store.TryDelete(session))
                {
                    _pending.TryRemove(session.SessionId, out _);
                    return;
                }
            }

            LogDeferredDeletion(_logger, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Dispose performs the final retry after these workers finish.
        }
    }

    private sealed class PendingCleanup(ExternalDiffSession session)
    {
        public ExternalDiffSession Session { get; } = session;

        public Task Work { get; set; } = Task.CompletedTask;
    }
}
