using Xunit;

namespace VisionStation.Application.Tests;

public sealed class ApplicationShutdownServiceTests
{
    [Fact]
    public async Task ShutdownClosesAdmissionThenStopsProductionDrainsAndReleasesResources()
    {
        var steps = new List<string>();
        var service = new ApplicationShutdownService(
            new RecordingRunLifetime(steps),
            () =>
            {
                steps.Add("production");
                return Task.CompletedTask;
            },
            new RecordingAsyncDisposable(() => steps.Add("matching")),
            new RecordingDisposable(() => steps.Add("communication")));

        await service.ShutdownAsync();

        Assert.Equal(["gate", "production", "drain", "matching", "communication"], steps);
    }

    [Fact]
    public async Task ShutdownClosesAdmissionSynchronouslyBeforeReturningItsTask()
    {
        var allowProductionStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new RecordingRunLifetime();
        var service = new ApplicationShutdownService(
            lifetime,
            () => allowProductionStop.Task,
            new RecordingAsyncDisposable(),
            new RecordingDisposable());

        var shutdown = service.ShutdownAsync();

        Assert.True(lifetime.IsShutdownRequested);
        Assert.Equal(1, lifetime.BeginShutdownCount);
        Assert.False(shutdown.IsCompleted);

        allowProductionStop.TrySetResult();
        await shutdown;
    }

    [Fact]
    public async Task DrainFailureBlocksMatchingAndCommunicationRelease()
    {
        var steps = new List<string>();
        var lifetime = new RecordingRunLifetime(
            steps,
            drain: () => throw new InvalidOperationException("drain failed"));
        var matching = new RecordingAsyncDisposable(() => steps.Add("matching"));
        var communication = new RecordingDisposable(() => steps.Add("communication"));
        var service = new ApplicationShutdownService(
            lifetime,
            () =>
            {
                steps.Add("production");
                return Task.CompletedTask;
            },
            matching,
            communication);

        var exception = await Assert.ThrowsAsync<AggregateException>(() => service.ShutdownAsync());

        Assert.Equal(["gate", "production", "drain"], steps);
        Assert.Contains(exception.InnerExceptions, error => error.Message == "drain failed");
        Assert.Equal(0, matching.DisposeCount);
        Assert.Equal(0, communication.DisposeCount);
    }

    [Fact]
    public async Task CancellationCallbackFailureDoesNotSkipStopDrainOrResourceRelease()
    {
        var steps = new List<string>();
        var lifetime = new InspectionRunLifetime();
        var callbackRegistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = lifetime.RunTrackedAsync<int>(async cancellationToken =>
        {
            using var registration = cancellationToken.Register(
                () =>
                {
                    callbackEntered.TrySetResult();
                    throw new InvalidOperationException("cancellation callback failed");
                });
            callbackRegistered.TrySetResult();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellationToken.ThrowIfCancellationRequested();
            return 1;
        });
        await callbackRegistered.Task;

        var service = new ApplicationShutdownService(
            lifetime,
            () =>
            {
                steps.Add("production");
                return Task.CompletedTask;
            },
            new RecordingAsyncDisposable(() => steps.Add("matching")),
            new RecordingDisposable(() => steps.Add("communication")));

        var exception = await Assert.ThrowsAsync<AggregateException>(() => service.ShutdownAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(["production", "matching", "communication"], steps);
        Assert.Contains(
            exception.Flatten().InnerExceptions,
            error => error.Message == "cancellation callback failed");
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
            new RecordingRunLifetime(),
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
    public async Task ReentrantShutdownFromAdmissionCancellationSharesOneExecution()
    {
        ApplicationShutdownService? service = null;
        Task? reentrantShutdown = null;
        var lifetime = new RecordingRunLifetime(
            beginShutdown: () => reentrantShutdown = service!.ShutdownAsync());
        var matching = new RecordingAsyncDisposable();
        var communication = new RecordingDisposable();
        service = new ApplicationShutdownService(
            lifetime,
            () => Task.CompletedTask,
            matching,
            communication);

        var firstShutdown = service.ShutdownAsync();
        await firstShutdown;

        Assert.Same(firstShutdown, reentrantShutdown);
        Assert.Equal(1, lifetime.BeginShutdownCount);
        Assert.Equal(1, matching.DisposeCount);
        Assert.Equal(1, communication.DisposeCount);
    }

    [Fact]
    public async Task ShutdownAttemptsEveryStepAndAggregatesFailures()
    {
        var steps = new List<string>();
        var service = new ApplicationShutdownService(
            new RecordingRunLifetime(steps),
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

        Assert.Equal(["gate", "production", "drain", "matching", "communication"], steps);
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
            new RecordingRunLifetime(steps),
            (Func<Task>?)null,
            matching,
            communication);

        await Task.WhenAll(service.ShutdownAsync(), service.ShutdownAsync());

        Assert.Equal(["gate", "drain", "matching", "communication"], steps);
        Assert.Equal(1, matching.DisposeCount);
        Assert.Equal(1, communication.DisposeCount);
    }

    private sealed class RecordingRunLifetime(
        ICollection<string>? steps = null,
        Func<Task>? drain = null,
        Action? beginShutdown = null) : IInspectionRunLifetime
    {
        public bool IsShutdownRequested { get; private set; }

        public int BeginShutdownCount { get; private set; }

        public Task<T> RunTrackedAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            return operation(cancellationToken);
        }

        public void BeginShutdown()
        {
            BeginShutdownCount++;
            IsShutdownRequested = true;
            steps?.Add("gate");
            if (BeginShutdownCount == 1)
            {
                beginShutdown?.Invoke();
            }
        }

        public Task DrainAsync()
        {
            steps?.Add("drain");
            return drain?.Invoke() ?? Task.CompletedTask;
        }
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
