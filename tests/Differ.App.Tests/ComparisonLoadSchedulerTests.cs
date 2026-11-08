using System.Collections.Concurrent;
using Differ.App.Services;

namespace Differ.App.Tests;

public sealed class ComparisonLoadSchedulerTests
{
    [Fact]
    public async Task RunAsyncUsesAtMostTwoWorkersAndPrioritizesSelectedTab()
    {
        using var scheduler = new ComparisonLoadScheduler();
        var firstStarted = NewSignal();
        var secondStarted = NewSignal();
        var releaseFirst = NewSignal();
        var releaseSecond = NewSignal();
        var startOrder = new ConcurrentQueue<string>();

        var first = scheduler.RunAsync(
            () => false,
            async cancellationToken =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return 1;
            },
            CancellationToken.None);
        var second = scheduler.RunAsync(
            () => false,
            async cancellationToken =>
            {
                secondStarted.TrySetResult();
                await releaseSecond.Task.WaitAsync(cancellationToken);
                return 2;
            },
            CancellationToken.None);
        await Task.WhenAll(firstStarted.Task, secondStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(2));

        var background = scheduler.RunAsync(
            () => false,
            _ =>
            {
                startOrder.Enqueue("background");
                return Task.FromResult(3);
            },
            CancellationToken.None);
        var selected = scheduler.RunAsync(
            () => true,
            _ =>
            {
                startOrder.Enqueue("selected");
                return Task.FromResult(4);
            },
            CancellationToken.None);

        Assert.False(background.IsCompleted);
        Assert.False(selected.IsCompleted);
        releaseFirst.TrySetResult();
        await selected.WaitAsync(TimeSpan.FromSeconds(2));
        await background.WaitAsync(TimeSpan.FromSeconds(2));
        releaseSecond.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal("selected", startOrder.TryDequeue(out var item) ? item : null);
    }

    [Fact]
    public async Task CancelingQueuedItemCompletesWithoutRunningOperation()
    {
        using var scheduler = new ComparisonLoadScheduler();
        var blockersStarted = 0;
        var bothStarted = NewSignal();
        var release = NewSignal();

        Task<int> StartBlocker()
        {
            return scheduler.RunAsync(
                () => false,
                async cancellationToken =>
                {
                    if (Interlocked.Increment(ref blockersStarted) == 2)
                    {
                        bothStarted.TrySetResult();
                    }

                    await release.Task.WaitAsync(cancellationToken);
                    return 1;
                },
                CancellationToken.None);
        }

        var first = StartBlocker();
        var second = StartBlocker();
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var operationStarted = false;
        var queued = scheduler.RunAsync(
            () => true,
            _ =>
            {
                operationStarted = true;
                return Task.FromResult(2);
            },
            cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await queued.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(operationStarted);

        release.TrySetResult();
        await Task.WhenAll(first, second);
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
