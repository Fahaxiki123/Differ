namespace Differ.App.Services;

public sealed class ComparisonLoadScheduler : IDisposable
{
    private readonly object _gate = new();
    private readonly List<IWorkItem> _queue = [];
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private long _sequence;
    private bool _isDisposed;

    public ComparisonLoadScheduler()
    {
        _workers = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(WorkerAsync))
            .ToArray();
    }

    public Task<T> RunAsync<T>(
        Func<bool> isSelected,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentNullException.ThrowIfNull(isSelected);
        ArgumentNullException.ThrowIfNull(operation);

        var item = new WorkItem<T>(
            Interlocked.Increment(ref _sequence),
            isSelected,
            operation,
            cancellationToken,
            _shutdown.Token);
        lock (_gate)
        {
            _queue.Add(item);
        }

        _available.Release();
        return item.Task;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _shutdown.Cancel();
        IWorkItem[] pending;
        lock (_gate)
        {
            pending = _queue.ToArray();
            _queue.Clear();
        }

        foreach (var item in pending)
        {
            item.Cancel();
        }

        // The two workers observe the shutdown token asynchronously. The
        // process-level scheduler lives for the process lifetime, so leave its
        // synchronization primitives intact until those workers have exited.
    }

    private async Task WorkerAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _available.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }

            var item = DequeueNext();
            if (item is null)
            {
                continue;
            }

            await item.ExecuteAsync().ConfigureAwait(false);
        }
    }

    private IWorkItem? DequeueNext()
    {
        lock (_gate)
        {
            foreach (var canceledItem in _queue.Where(item => item.IsCanceled))
            {
                canceledItem.Cancel();
            }

            _queue.RemoveAll(item => item.IsCanceled);
            if (_queue.Count == 0)
            {
                return null;
            }

            var selectedIndex = -1;
            var selectedSequence = long.MinValue;
            var oldestIndex = 0;
            var oldestSequence = long.MaxValue;
            for (var index = 0; index < _queue.Count; index++)
            {
                var item = _queue[index];
                if (item.Sequence < oldestSequence)
                {
                    oldestIndex = index;
                    oldestSequence = item.Sequence;
                }

                if (item.IsSelected() && item.Sequence > selectedSequence)
                {
                    selectedIndex = index;
                    selectedSequence = item.Sequence;
                }
            }

            var nextIndex = selectedIndex >= 0 ? selectedIndex : oldestIndex;
            var next = _queue[nextIndex];
            _queue.RemoveAt(nextIndex);
            return next;
        }
    }

    private interface IWorkItem
    {
        long Sequence { get; }

        bool IsCanceled { get; }

        bool IsSelected();

        Task ExecuteAsync();

        void Cancel();
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<bool> _isSelected;
        private readonly Func<CancellationToken, Task<T>> _operation;
        private readonly CancellationTokenSource _linkedCancellation;
        private readonly CancellationToken _operationToken;
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkItem(
            long sequence,
            Func<bool> isSelected,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken,
            CancellationToken shutdownToken)
        {
            Sequence = sequence;
            _isSelected = isSelected;
            _operation = operation;
            _linkedCancellation = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken, shutdownToken);
            _operationToken = _linkedCancellation.Token;
            _cancellationRegistration = _operationToken.Register(
                () => _completion.TrySetCanceled(_operationToken));
        }

        public long Sequence { get; }

        public bool IsCanceled => _operationToken.IsCancellationRequested;

        public Task<T> Task => _completion.Task;

        public bool IsSelected()
        {
            return !IsCanceled && _isSelected();
        }

        public async Task ExecuteAsync()
        {
            if (IsCanceled)
            {
                Cancel();
                return;
            }

            try
            {
                var result = await _operation(_operationToken)
                    .ConfigureAwait(false);
                _completion.TrySetResult(result);
            }
            catch (OperationCanceledException)
            {
                Cancel();
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                _cancellationRegistration.Dispose();
                _linkedCancellation.Dispose();
            }
        }

        public void Cancel()
        {
            _completion.TrySetCanceled(_operationToken);
        }
    }
}
