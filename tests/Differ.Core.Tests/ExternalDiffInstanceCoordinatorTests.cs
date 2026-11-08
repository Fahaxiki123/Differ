using Differ.Core.External;

namespace Differ.Core.Tests;

public sealed class ExternalDiffInstanceCoordinatorTests
{
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task ForwardTimeoutIncludesWaitingForHandlerAcknowledgmentAsync()
    {
        var instanceKey = Guid.NewGuid().ToString("N");
        using var primary = new ExternalDiffInstanceCoordinator(instanceKey);
        using var secondary = new ExternalDiffInstanceCoordinator(instanceKey);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });

        var forwarding = secondary.ForwardAsync(
            CreateSession(),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(await forwarding.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task ForwardPropagatesCallerCancellationWhileWaitingForAcknowledgmentAsync()
    {
        var instanceKey = Guid.NewGuid().ToString("N");
        using var primary = new ExternalDiffInstanceCoordinator(instanceKey);
        using var secondary = new ExternalDiffInstanceCoordinator(instanceKey);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });

        var forwarding = secondary.ForwardAsync(
            CreateSession(),
            TimeSpan.FromSeconds(30),
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => forwarding.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task ForwardTimeoutAlsoBoundsConnectingToAnUnavailableListenerAsync()
    {
        using var coordinator = new ExternalDiffInstanceCoordinator(Guid.NewGuid().ToString("N"));
        Assert.False(await coordinator.ForwardAsync(
            CreateSession(),
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task ListenerKeepsAcceptingAfterHandlerThrowsUnexpectedExceptionAsync()
    {
        var instanceKey = Guid.NewGuid().ToString("N");
        using var primary = new ExternalDiffInstanceCoordinator(instanceKey);
        Assert.True(primary.IsPrimary);

        var received = new List<Guid>();
        var secondAccepted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening((session, _) =>
        {
            received.Add(session.SessionId);

            // The first forwarded session simulates an unexpected failure while
            // the primary window is attaching the tab, for example a WPF
            // binding or template error raised on the dispatcher thread.
            if (received.Count == 1)
            {
                throw new InvalidCastException("Unexpected handler failure.");
            }

            secondAccepted.TrySetResult();
            return Task.CompletedTask;
        });

        using var firstSecondary = new ExternalDiffInstanceCoordinator(instanceKey);
        Assert.False(firstSecondary.IsPrimary);
        await firstSecondary.ForwardAsync(
            CreateSession(),
            ForwardTimeout,
            CancellationToken.None);

        using var secondSecondary = new ExternalDiffInstanceCoordinator(instanceKey);
        var secondForwarded = await secondSecondary.ForwardAsync(
            CreateSession(),
            ForwardTimeout,
            CancellationToken.None);

        Assert.True(
            secondForwarded,
            "The primary instance stopped accepting forwarded comparisons after "
            + "the handler threw an exception that no catch filter matched.");
        await secondAccepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, received.Count);
    }

    private static ExternalDiffSession CreateSession()
    {
        return ExternalDiffSession.CreateFailed(
            new ExternalDiffRequest
            {
                BaseFilePath = @"C:\temp\base.txt",
                MineFilePath = @"C:\work\mine.txt"
            },
            "snapshot unavailable");
    }
}
