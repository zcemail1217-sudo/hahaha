using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class HalconOperationSchedulerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DelegateRunsOnOneDedicatedWorkerThreadInsteadOfCallerOrThreadPool()
    {
        await using var scheduler = new HalconOperationScheduler(workerCount: 1, capacity: 64);
        int callerThreadId = Environment.CurrentManagedThreadId;

        var observations = new List<(int ThreadId, bool IsThreadPoolThread)>();
        for (var index = 0; index < 5; index++)
        {
            observations.Add(await scheduler.RunAsync(
                () => (Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread),
                default));
        }

        int workerThreadId = Assert.Single(observations.Select(item => item.ThreadId).Distinct());
        Assert.NotEqual(callerThreadId, workerThreadId);
        Assert.All(observations, item => Assert.False(item.IsThreadPoolThread));
    }

    [Fact]
    public async Task DefaultSchedulerRunsExactlyTwoDelegatesConcurrently()
    {
        await using var scheduler = new HalconOperationScheduler();
        using var release = new ManualResetEventSlim();
        using var firstTwoStarted = new CountdownEvent(2);
        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var running = 0;
        var maximumRunning = 0;

        Task<int>[] operations = Enumerable.Range(0, 4)
            .Select(value => scheduler.RunAsync(
                () =>
                {
                    int current = Interlocked.Increment(ref running);
                    UpdateMaximum(ref maximumRunning, current);
                    int started = Interlocked.Increment(ref startedCount);
                    if (started <= 2)
                    {
                        firstTwoStarted.Signal();
                    }
                    else if (started == 3)
                    {
                        thirdStarted.TrySetResult();
                    }

                    try
                    {
                        if (!release.Wait(TestTimeout))
                        {
                            throw new TimeoutException("Test release gate was not opened.");
                        }

                        return value;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref running);
                    }
                },
                default))
            .ToArray();

        try
        {
            Assert.True(firstTwoStarted.Wait(TestTimeout));
            await Assert.ThrowsAsync<TimeoutException>(
                () => thirdStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(200)));
            Assert.Equal(2, Volatile.Read(ref maximumRunning));
        }
        finally
        {
            release.Set();
        }

        int[] results = await Task.WhenAll(operations);
        Assert.Equal([0, 1, 2, 3], results);
    }

    [Fact]
    public async Task CallerCancellationRemovesQueuedItemBeforeDelegateStarts()
    {
        await using var scheduler = new HalconOperationScheduler(workerCount: 1, capacity: 64);
        using var release = new ManualResetEventSlim();
        var runningStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedExecutions = 0;
        Task<int> running = scheduler.RunAsync(
            () =>
            {
                runningStarted.TrySetResult();
                if (!release.Wait(TestTimeout))
                {
                    throw new TimeoutException("Test release gate was not opened.");
                }

                return 1;
            },
            default);
        await runningStarted.Task.WaitAsync(TestTimeout);
        using var cancellation = new CancellationTokenSource();
        Task<int> queued = scheduler.RunAsync(
            () =>
            {
                Interlocked.Increment(ref queuedExecutions);
                return 2;
            },
            cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TestTimeout));
        Assert.Equal(0, Volatile.Read(ref queuedExecutions));
        release.Set();
        Assert.Equal(1, await running.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task WorkerRechecksCanceledTokenBeforeAdmissionWhenItsCallbackIsBlocked()
    {
        await using var scheduler = new HalconOperationScheduler(workerCount: 1, capacity: 64);
        using var runningRelease = new ManualResetEventSlim();
        var runningStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> running = scheduler.RunAsync(
            () =>
            {
                runningStarted.TrySetResult();
                if (!runningRelease.Wait(TestTimeout))
                {
                    throw new TimeoutException("Test release gate was not opened.");
                }

                return 1;
            },
            default);
        await runningStarted.Task.WaitAsync(TestTimeout);
        using var cancellation = new CancellationTokenSource();
        var queuedExecutions = 0;
        Task<int> queued = scheduler.RunAsync(
            () => Interlocked.Increment(ref queuedExecutions),
            cancellation.Token);
        using var blockingCallbackEntered = new ManualResetEventSlim();
        using var blockingCallbackRelease = new ManualResetEventSlim();
        using CancellationTokenRegistration blockingCallback = cancellation.Token.Register(
            () =>
            {
                blockingCallbackEntered.Set();
                if (!blockingCallbackRelease.Wait(TestTimeout))
                {
                    throw new TimeoutException("Cancellation callback release gate was not opened.");
                }
            });
        Task cancellationRequest = Task.Run(cancellation.Cancel);

        Assert.True(blockingCallbackEntered.Wait(TestTimeout));
        runningRelease.Set();
        Assert.Equal(1, await running.WaitAsync(TestTimeout));
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => queued.WaitAsync(TestTimeout));
            Assert.Equal(0, Volatile.Read(ref queuedExecutions));
        }
        finally
        {
            blockingCallbackRelease.Set();
            await cancellationRequest.WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task CancellationAfterDelegateStartsWaitsForItsSafeReturn()
    {
        await using var scheduler = new HalconOperationScheduler(workerCount: 1, capacity: 64);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        Task<int> operation = scheduler.RunAsync(
            () =>
            {
                started.TrySetResult();
                if (!release.Wait(TestTimeout))
                {
                    throw new TimeoutException("Test release gate was not opened.");
                }

                return 42;
            },
            cancellation.Token);
        await started.Task.WaitAsync(TestTimeout);

        cancellation.Cancel();

        Assert.False(operation.IsCompleted);
        release.Set();
        Assert.Equal(42, await operation.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task DelegateExceptionFaultsOnlyItsItemAndWorkerContinuesOnSameThread()
    {
        await using var scheduler = new HalconOperationScheduler(workerCount: 1, capacity: 64);
        var failedThreadId = 0;

        await Assert.ThrowsAsync<InjectedSchedulerException>(
            () => scheduler.RunAsync<int>(
                () =>
                {
                    failedThreadId = Environment.CurrentManagedThreadId;
                    throw new InjectedSchedulerException();
                },
                default));
        int survivingThreadId = await scheduler.RunAsync(
            () => Environment.CurrentManagedThreadId,
            default);

        Assert.NotEqual(0, failedThreadId);
        Assert.Equal(failedThreadId, survivingThreadId);
    }

    [Fact]
    public async Task DisposeRejectsQueuedAndNewWorkWhileWaitingForRunningDelegate()
    {
        var scheduler = new HalconOperationScheduler(workerCount: 1, capacity: 64);
        using var release = new ManualResetEventSlim();
        var runningStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedExecutions = 0;
        Task<int> running = scheduler.RunAsync(
            () =>
            {
                runningStarted.TrySetResult();
                if (!release.Wait(TestTimeout))
                {
                    throw new TimeoutException("Test release gate was not opened.");
                }

                return 7;
            },
            default);
        await runningStarted.Task.WaitAsync(TestTimeout);
        Task<int> queued = scheduler.RunAsync(
            () =>
            {
                Interlocked.Increment(ref queuedExecutions);
                return 8;
            },
            default);

        Task firstDispose = scheduler.DisposeAsync().AsTask();
        Task secondDispose = scheduler.DisposeAsync().AsTask();

        Assert.Same(firstDispose, secondDispose);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued.WaitAsync(TestTimeout));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => scheduler.RunAsync(() => 9, default));
        Assert.Equal(0, Volatile.Read(ref queuedExecutions));
        Assert.False(firstDispose.IsCompleted);

        release.Set();
        Assert.Equal(7, await running.WaitAsync(TestTimeout));
        await firstDispose.WaitAsync(TestTimeout);
        await secondDispose.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task DisposeRejectsBufferedAndBlockedWriterBeforeWaitingForRunningDelegate()
    {
        var scheduler = new HalconOperationScheduler(workerCount: 1, capacity: 1);
        using var release = new ManualResetEventSlim();
        var runningStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingExecutions = 0;
        Task<int> running = scheduler.RunAsync(
            () =>
            {
                runningStarted.TrySetResult();
                if (!release.Wait(TestTimeout))
                {
                    throw new TimeoutException("Test release gate was not opened.");
                }

                return 1;
            },
            default);
        await runningStarted.Task.WaitAsync(TestTimeout);
        Task<int> buffered = scheduler.RunAsync(
            () => Interlocked.Increment(ref waitingExecutions),
            default);
        Task<int> blockedWriter = scheduler.RunAsync(
            () => Interlocked.Increment(ref waitingExecutions),
            default);

        Task dispose = scheduler.DisposeAsync().AsTask();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => buffered.WaitAsync(TestTimeout));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => blockedWriter.WaitAsync(TestTimeout));
        Assert.Equal(0, Volatile.Read(ref waitingExecutions));
        Assert.False(dispose.IsCompleted);

        release.Set();
        Assert.Equal(1, await running.WaitAsync(TestTimeout));
        await dispose.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task CallerCanCancelWriterBlockedByFullChannel()
    {
        await using var scheduler = new HalconOperationScheduler(workerCount: 1, capacity: 1);
        using var release = new ManualResetEventSlim();
        var runningStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedWriterExecutions = 0;
        Task<int> running = scheduler.RunAsync(
            () =>
            {
                runningStarted.TrySetResult();
                if (!release.Wait(TestTimeout))
                {
                    throw new TimeoutException("Test release gate was not opened.");
                }

                return 1;
            },
            default);
        await runningStarted.Task.WaitAsync(TestTimeout);
        Task<int> buffered = scheduler.RunAsync(() => 2, default);
        using var cancellation = new CancellationTokenSource();
        Task<int> blockedWriter = scheduler.RunAsync(
            () => Interlocked.Increment(ref blockedWriterExecutions),
            cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => blockedWriter.WaitAsync(TestTimeout));
        Assert.Equal(0, Volatile.Read(ref blockedWriterExecutions));
        release.Set();
        Assert.Equal(1, await running.WaitAsync(TestTimeout));
        Assert.Equal(2, await buffered.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task AlreadyCanceledTokenNeverQueuesDelegate()
    {
        await using var scheduler = new HalconOperationScheduler();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var executions = 0;

        Task<int> operation = scheduler.RunAsync(
            () => Interlocked.Increment(ref executions),
            cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(0, Volatile.Read(ref executions));
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref maximum);
            if (candidate <= observed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref maximum, candidate, observed) != observed);
    }

    private sealed class InjectedSchedulerException : Exception;
}
