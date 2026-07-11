using VisionStation.Application;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class InspectionExecutionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task TryBegin_rejects_second_session_until_first_is_disposed()
    {
        var execution = new InspectionExecution(new RecordingExecutor());

        var first = Assert.IsType<RunAdmission.Acquired>(
            execution.TryBegin(NewIntent(nameof(TryBegin_rejects_second_session_until_first_is_disposed))));
        var second = Assert.IsType<RunAdmission.Rejected>(
            execution.TryBegin(NewIntent(nameof(TryBegin_rejects_second_session_until_first_is_disposed))));

        Assert.Equal(RunRejectionReason.Busy, second.Rejection.Reason);
        Assert.Same(first.Session.Run, second.Rejection.Active);
        Assert.Same(first.Session.Run, execution.Current);

        await DisposeWithTimeoutAsync(first.Session);

        Assert.Null(execution.Current);
        var third = Assert.IsType<RunAdmission.Acquired>(
            execution.TryBegin(NewIntent(nameof(TryBegin_rejects_second_session_until_first_is_disposed))));
        await DisposeWithTimeoutAsync(third.Session);
    }

    [Fact]
    public async Task TryBegin_with_100_concurrent_callers_acquires_exactly_one()
    {
        var execution = new InspectionExecution(new RecordingExecutor());
        var start = NewSignal();
        var intent = NewIntent(nameof(TryBegin_with_100_concurrent_callers_acquires_exactly_one));
        var attempts = Enumerable.Range(0, 100)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return execution.TryBegin(intent);
            }))
            .ToArray();

        start.SetResult();
        var admissions = await Task.WhenAll(attempts).WaitAsync(Timeout);

        var acquired = Assert.Single(admissions.OfType<RunAdmission.Acquired>());
        Assert.Equal(99, admissions.OfType<RunAdmission.Rejected>().Count());
        await DisposeWithTimeoutAsync(acquired.Session);
    }

    [Fact]
    public async Task Session_allows_sequential_execution_and_publishes_results()
    {
        var executor = new RecordingExecutor();
        var execution = new InspectionExecution(executor);
        var completed = new List<InspectionRunResult>();
        execution.RunCompleted += (_, result) => completed.Add(result);
        var session = Acquire(
            execution,
            nameof(Session_allows_sequential_execution_and_publishes_results));

        var first = await session.ExecuteAsync(new InspectionRequest()).WaitAsync(Timeout);
        var second = await session.ExecuteAsync(new InspectionRequest()).WaitAsync(Timeout);

        Assert.Equal(2, executor.CallCount);
        Assert.Collection(
            completed,
            result => Assert.Same(first, result),
            result => Assert.Same(second, result));
        await DisposeWithTimeoutAsync(session);
    }

    [Fact]
    public async Task Session_rejects_concurrent_execution_before_executor_side_effects()
    {
        var started = NewSignal();
        var release = NewSignal();
        var executor = new RecordingExecutor
        {
            Handler = async (_, _) =>
            {
                started.SetResult();
                await release.Task;
                return TestRunResults.Ok();
            }
        };
        var execution = new InspectionExecution(executor);
        var session = Acquire(
            execution,
            nameof(Session_rejects_concurrent_execution_before_executor_side_effects));
        var firstExecution = session.ExecuteAsync(new InspectionRequest());

        await started.Task.WaitAsync(Timeout);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.ExecuteAsync(new InspectionRequest()).WaitAsync(Timeout));
            Assert.Equal(1, executor.CallCount);
        }
        finally
        {
            release.TrySetResult();
            await firstExecution.WaitAsync(Timeout);
            await DisposeWithTimeoutAsync(session);
        }
    }

    [Fact]
    public async Task Dispose_during_execution_keeps_global_admission_until_execution_finishes()
    {
        var started = NewSignal();
        var release = NewSignal();
        var executor = new RecordingExecutor
        {
            Handler = async (_, _) =>
            {
                started.SetResult();
                await release.Task;
                return TestRunResults.Ok();
            }
        };
        var execution = new InspectionExecution(executor);
        var session = Acquire(
            execution,
            nameof(Dispose_during_execution_keeps_global_admission_until_execution_finishes));
        var runTask = session.ExecuteAsync(new InspectionRequest());
        Task? disposeTask = null;

        await started.Task.WaitAsync(Timeout);
        try
        {
            disposeTask = session.DisposeAsync().AsTask();

            Assert.False(disposeTask.IsCompleted);
            var rejected = Assert.IsType<RunAdmission.Rejected>(
                execution.TryBegin(NewIntent(
                    nameof(Dispose_during_execution_keeps_global_admission_until_execution_finishes))));
            Assert.Equal(RunRejectionReason.Busy, rejected.Rejection.Reason);
            Assert.Same(session.Run, rejected.Rejection.Active);

            release.SetResult();
            await runTask.WaitAsync(Timeout);
            await disposeTask.WaitAsync(Timeout);

            Assert.Null(execution.Current);
        }
        finally
        {
            release.TrySetResult();
            await runTask.WaitAsync(Timeout);
            if (disposeTask is null)
            {
                await DisposeWithTimeoutAsync(session);
            }
            else
            {
                await disposeTask.WaitAsync(Timeout);
            }
        }
    }

    [Fact]
    public async Task Disposed_session_fails_before_executor_is_called()
    {
        var executor = new RecordingExecutor();
        var execution = new InspectionExecution(executor);
        var session = Acquire(
            execution,
            nameof(Disposed_session_fails_before_executor_is_called));
        await DisposeWithTimeoutAsync(session);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.ExecuteAsync(new InspectionRequest()).WaitAsync(Timeout));

        Assert.Equal(0, executor.CallCount);
        Assert.Null(execution.Current);
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_stale_session_cannot_release_next_session()
    {
        var started = NewSignal();
        var release = NewSignal();
        var executor = new RecordingExecutor
        {
            Handler = async (_, _) =>
            {
                started.SetResult();
                await release.Task;
                return TestRunResults.Ok();
            }
        };
        var execution = new InspectionExecution(executor);
        var changes = new List<ActiveInspectionRun?>();
        execution.Changed += (_, args) => changes.Add(args.Current);
        var firstSession = Acquire(
            execution,
            nameof(Dispose_is_idempotent_and_stale_session_cannot_release_next_session));
        var runTask = firstSession.ExecuteAsync(new InspectionRequest());
        IInspectionSession? secondSession = null;

        await started.Task.WaitAsync(Timeout);
        try
        {
            var firstDispose = firstSession.DisposeAsync().AsTask();
            var secondDispose = firstSession.DisposeAsync().AsTask();

            Assert.Same(firstDispose, secondDispose);
            Assert.False(firstDispose.IsCompleted);

            release.SetResult();
            await runTask.WaitAsync(Timeout);
            await firstDispose.WaitAsync(Timeout);
            await secondDispose.WaitAsync(Timeout);

            secondSession = Acquire(
                execution,
                $"{nameof(Dispose_is_idempotent_and_stale_session_cannot_release_next_session)}.second");
            await firstSession.DisposeAsync().AsTask().WaitAsync(Timeout);

            Assert.Same(secondSession.Run, execution.Current);
            Assert.Same(secondSession.Run, Assert.IsType<ActiveInspectionRun>(changes.Last()));

            await DisposeWithTimeoutAsync(secondSession);
            Assert.Null(execution.Current);
        }
        finally
        {
            release.TrySetResult();
            await runTask.WaitAsync(Timeout);
            await firstSession.DisposeAsync().AsTask().WaitAsync(Timeout);
            if (secondSession is not null)
            {
                await DisposeWithTimeoutAsync(secondSession);
            }
        }
    }

    [Fact]
    public async Task Reentrant_reacquire_does_not_publish_stale_null_to_later_subscribers()
    {
        var execution = new InspectionExecution(new RecordingExecutor());
        RunAdmission? reentrantAdmission = null;
        var laterSubscriberValues = new List<ActiveInspectionRun?>();
        EventHandler<InspectionExecutionChangedEventArgs> reentrantSubscriber = (_, args) =>
        {
            if (args.Current is null)
            {
                reentrantAdmission = execution.TryBegin(NewIntent(
                    $"{nameof(Reentrant_reacquire_does_not_publish_stale_null_to_later_subscribers)}.reentrant"));
            }
        };
        execution.Changed += reentrantSubscriber;
        execution.Changed += (_, args) => laterSubscriberValues.Add(args.Current);
        var first = Acquire(
            execution,
            nameof(Reentrant_reacquire_does_not_publish_stale_null_to_later_subscribers));

        await DisposeWithTimeoutAsync(first);

        var reacquired = Assert.IsType<RunAdmission.Acquired>(reentrantAdmission).Session;
        try
        {
            Assert.Same(reacquired.Run, execution.Current);
            Assert.Collection(
                laterSubscriberValues,
                current => Assert.Same(first.Run, current),
                current => Assert.Same(reacquired.Run, current));
        }
        finally
        {
            execution.Changed -= reentrantSubscriber;
            await DisposeWithTimeoutAsync(reacquired);
        }
    }

    [Fact]
    public async Task Subscriber_failures_do_not_break_result_or_release()
    {
        var execution = new InspectionExecution(new RecordingExecutor());
        var changedCount = 0;
        var completedCount = 0;
        execution.Changed += static (_, _) => throw new InvalidOperationException("Changed subscriber failure.");
        execution.Changed += (_, _) => Interlocked.Increment(ref changedCount);
        execution.RunCompleted += static (_, _) => throw new InvalidOperationException("RunCompleted subscriber failure.");
        execution.RunCompleted += (_, _) => Interlocked.Increment(ref completedCount);
        var session = Acquire(
            execution,
            nameof(Subscriber_failures_do_not_break_result_or_release));

        var result = await session.ExecuteAsync(new InspectionRequest()).WaitAsync(Timeout);
        await DisposeWithTimeoutAsync(session);

        Assert.Equal(InspectionOutcome.Ok, result.Result.Outcome);
        Assert.Equal(2, Volatile.Read(ref changedCount));
        Assert.Equal(1, Volatile.Read(ref completedCount));
        Assert.Null(execution.Current);
    }

    [Fact]
    public void TryBegin_rejects_default_mode_before_creating_session()
    {
        var executor = new RecordingExecutor();
        var execution = new InspectionExecution(executor);

        Assert.Throws<ArgumentNullException>(() => execution.TryBegin(null!));
        Assert.Throws<ArgumentException>(
            () => execution.TryBegin(new InspectionRunIntent(
                new InspectionRunMode("custom.test", "Custom Test"),
                " ")));
        Assert.Throws<ArgumentException>(
            () => execution.TryBegin(new InspectionRunIntent(
                default,
                nameof(TryBegin_rejects_default_mode_before_creating_session))));

        Assert.Null(execution.Current);
        Assert.Equal(0, executor.CallCount);
    }

    private static IInspectionSession Acquire(InspectionExecution execution, string entryPoint)
    {
        return Assert.IsType<RunAdmission.Acquired>(
            execution.TryBegin(NewIntent(entryPoint))).Session;
    }

    private static InspectionRunIntent NewIntent(string entryPoint)
    {
        return new InspectionRunIntent(
            new InspectionRunMode("custom.test", "Custom Test"),
            entryPoint);
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static Task DisposeWithTimeoutAsync(IInspectionSession session)
    {
        return session.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    private sealed class RecordingExecutor : IInspectionExecutor
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Func<InspectionRequest, CancellationToken, Task<InspectionRunResult>> Handler { get; init; } =
            static (_, _) => Task.FromResult(TestRunResults.Ok());

        public Task<InspectionRunResult> ExecuteAsync(
            InspectionRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Handler(request, cancellationToken);
        }
    }
}

internal static class TestRunResults
{
    public static InspectionRunResult Ok()
    {
        var frame = new ImageFrame(
            "test-frame",
            1,
            1,
            1,
            PixelFormatKind.Gray8,
            [0],
            DateTimeOffset.UtcNow,
            "test");
        return new InspectionRunResult(
            new InspectionResult
            {
                Outcome = InspectionOutcome.Ok,
                CycleTime = TimeSpan.FromMilliseconds(5)
            },
            frame,
            frame,
            new Recipe());
    }
}
