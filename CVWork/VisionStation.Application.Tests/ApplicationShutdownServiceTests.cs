using Xunit;

namespace VisionStation.Application.Tests;

public sealed class ApplicationShutdownServiceTests
{
    [Fact]
    public async Task ShutdownRunsProductionThenMatchingThenCommunication()
    {
        var steps = new List<string>();
        var service = new ApplicationShutdownService(
            () =>
            {
                steps.Add("production");
                return Task.CompletedTask;
            },
            new RecordingAsyncDisposable(() => steps.Add("matching")),
            new RecordingDisposable(() => steps.Add("communication")));

        await service.ShutdownAsync();

        Assert.Equal(["production", "matching", "communication"], steps);
    }

    [Fact]
    public async Task ConcurrentShutdownCallsShareOneExecution()
    {
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var productionCalls = 0;
        var matching = new RecordingAsyncDisposable();
        var communication = new RecordingDisposable();
        var service = new ApplicationShutdownService(
            async () =>
            {
                productionCalls++;
                stopEntered.TrySetResult();
                await allowStop.Task;
            },
            matching,
            communication);

        var first = service.ShutdownAsync();
        await stopEntered.Task;
        var second = service.ShutdownAsync();

        Assert.Same(first, second);
        allowStop.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, productionCalls);
        Assert.Equal(1, matching.DisposeCount);
        Assert.Equal(1, communication.DisposeCount);
    }

    [Fact]
    public async Task ShutdownAttemptsEveryStepAndAggregatesFailures()
    {
        var steps = new List<string>();
        var service = new ApplicationShutdownService(
            () =>
            {
                steps.Add("production");
                throw new InvalidOperationException("production failed");
            },
            new RecordingAsyncDisposable(
                () =>
                {
                    steps.Add("matching");
                    throw new InvalidOperationException("matching failed");
                }),
            new RecordingDisposable(
                () =>
                {
                    steps.Add("communication");
                    throw new InvalidOperationException("communication failed");
                }));

        var exception = await Assert.ThrowsAsync<AggregateException>(() => service.ShutdownAsync());

        Assert.Equal(["production", "matching", "communication"], steps);
        Assert.Equal(
            ["production failed", "matching failed", "communication failed"],
            exception.InnerExceptions.Select(error => error.Message));
    }

    [Fact]
    public async Task StartupFallbackWithoutProductionStillDisposesInOrderOnce()
    {
        var steps = new List<string>();
        var matching = new RecordingAsyncDisposable(() => steps.Add("matching"));
        var communication = new RecordingDisposable(() => steps.Add("communication"));
        var service = new ApplicationShutdownService(
            (Func<Task>?)null,
            matching,
            communication);

        await Task.WhenAll(service.ShutdownAsync(), service.ShutdownAsync());

        Assert.Equal(["matching", "communication"], steps);
        Assert.Equal(1, matching.DisposeCount);
        Assert.Equal(1, communication.DisposeCount);
    }

    private sealed class RecordingAsyncDisposable(Action? dispose = null) : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            dispose?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDisposable(Action? dispose = null) : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            dispose?.Invoke();
        }
    }
}
