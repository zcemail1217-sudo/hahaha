using VisionStation.Application;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class ProductionCoordinatorTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task RunSingleAsync_when_successful_publishes_exact_state_sequence()
    {
        var harness = CoordinatorHarness.Create();
        using var recorder = new DistinctStateRecorder(harness.Coordinator);

        var result = await harness.Coordinator.RunSingleAsync().WaitAsync(Watchdog);

        Assert.Equal(ProductionCommandDisposition.Completed, result.Disposition);
        Assert.NotNull(result.Value);
        Assert.Equal(
            [
                ProductionState.Starting,
                ProductionState.Running,
                ProductionState.Stopping,
                ProductionState.Stopped
            ],
            recorder.States);
        Assert.Null(harness.Execution.Current);
    }

    [Fact]
    public async Task RunSingleAsync_while_continuous_returns_busy_immediately()
    {
        var harness = CoordinatorHarness.Create();
        var executeEntered = CoordinatorHarness.NewSignal();
        harness.Executor.Handler = async (_, cancellationToken) =>
        {
            executeEntered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            return TestRunResults.Ok();
        };

        var start = await harness.Coordinator.StartAsync().WaitAsync(Watchdog);
        Assert.Equal(ProductionCommandDisposition.Completed, start.Disposition);
        await executeEntered.Task.WaitAsync(Watchdog);

        try
        {
            var conflictTask = harness.Coordinator.RunSingleAsync();

            Assert.True(conflictTask.IsCompleted);
            var conflict = await conflictTask;
            Assert.Equal(ProductionCommandDisposition.Rejected, conflict.Disposition);
            Assert.Equal(RunRejectionReason.Busy, conflict.Rejection?.Reason);
        }
        finally
        {
            var stop = await harness.Coordinator.StopAsync().WaitAsync(Watchdog);
            Assert.Equal(ProductionCommandDisposition.Completed, stop.Disposition);
        }
    }

    [Fact]
    public async Task StartAsync_while_single_is_running_returns_busy_immediately()
    {
        var harness = CoordinatorHarness.Create();
        var executeEntered = CoordinatorHarness.NewSignal();
        harness.Executor.Handler = async (_, cancellationToken) =>
        {
            executeEntered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            return TestRunResults.Ok();
        };
        var singleTask = harness.Coordinator.RunSingleAsync();
        await executeEntered.Task.WaitAsync(Watchdog);

        ProductionCommandResult<InspectionRunResult> single;
        try
        {
            var conflictTask = harness.Coordinator.StartAsync();

            Assert.True(conflictTask.IsCompleted);
            var conflict = await conflictTask;
            Assert.Equal(ProductionCommandDisposition.Rejected, conflict.Disposition);
            Assert.Equal(RunRejectionReason.Busy, conflict.Rejection?.Reason);

            var stop = await harness.Coordinator.StopAsync().WaitAsync(Watchdog);
            Assert.Equal(ProductionCommandDisposition.Completed, stop.Disposition);
            single = await singleTask.WaitAsync(Watchdog);
        }
        finally
        {
            if (!singleTask.IsCompleted)
            {
                await harness.Coordinator.StopAsync().WaitAsync(Watchdog);
            }
        }

        Assert.Equal(ProductionCommandDisposition.Canceled, single.Disposition);
    }

    [Fact]
    public async Task RunSingleAsync_when_caller_cancels_returns_canceled_without_fault_alarm()
    {
        var harness = CoordinatorHarness.Create();
        var executeEntered = CoordinatorHarness.NewSignal();
        harness.Executor.Handler = async (_, cancellationToken) =>
        {
            executeEntered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            return TestRunResults.Ok();
        };
        using var cancellation = new CancellationTokenSource();
        using var recorder = new DistinctStateRecorder(harness.Coordinator);
        var singleTask = harness.Coordinator.RunSingleAsync(cancellation.Token);
        await executeEntered.Task.WaitAsync(Watchdog);

        cancellation.Cancel();
        var result = await singleTask.WaitAsync(Watchdog);

        Assert.Equal(ProductionCommandDisposition.Canceled, result.Disposition);
        Assert.DoesNotContain(
            harness.Alarms.Raised,
            alarm => alarm.Severity is AlarmSeverity.Error or AlarmSeverity.Critical);
        Assert.Equal(ProductionState.Stopped, harness.Coordinator.Snapshot.State);
        Assert.Contains(ProductionState.Stopping, recorder.States);
        Assert.Null(harness.Execution.Current);
    }

    [Fact]
    public async Task Stop_from_admission_changed_cancels_pending_production_owner()
    {
        var harness = CoordinatorHarness.Create();
        Task<ProductionCommandResult>? duplicateTask = null;
        Task<ProductionCommandResult>? stopTask = null;
        var handled = 0;
        EventHandler<InspectionExecutionChangedEventArgs> handler = (_, args) =>
        {
            if (args.Current is not null && Interlocked.Exchange(ref handled, 1) == 0)
            {
                duplicateTask = harness.Coordinator.StartAsync();
                stopTask = harness.Coordinator.StopAsync();
            }
        };
        harness.Execution.Changed += handler;

        ProductionCommandResult original;
        try
        {
            original = await harness.Coordinator.StartAsync().WaitAsync(Watchdog);
        }
        finally
        {
            harness.Execution.Changed -= handler;
        }

        Assert.NotNull(duplicateTask);
        Assert.NotNull(stopTask);
        var duplicate = await duplicateTask.WaitAsync(Watchdog);
        var stop = await stopTask.WaitAsync(Watchdog);

        Assert.Equal(ProductionCommandDisposition.Canceled, original.Disposition);
        Assert.Equal(ProductionCommandDisposition.Rejected, duplicate.Disposition);
        Assert.Equal(RunRejectionReason.AlreadyRunning, duplicate.Rejection?.Reason);
        Assert.Equal(ProductionCommandDisposition.Completed, stop.Disposition);
        Assert.Null(harness.Execution.Current);
        Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);
    }

    [Fact]
    public async Task Concurrent_start_calls_initialize_only_one_operation()
    {
        var harness = CoordinatorHarness.Create();
        var allowConnect = CoordinatorHarness.NewSignal();
        harness.Camera.ConnectHandler = _ => allowConnect.Task;
        var firstTask = harness.Coordinator.StartAsync();
        await harness.Camera.ConnectEntered.Task.WaitAsync(Watchdog);

        try
        {
            var duplicateTask = harness.Coordinator.StartAsync();

            Assert.True(duplicateTask.IsCompleted);
            var duplicate = await duplicateTask;
            Assert.Equal(ProductionCommandDisposition.Rejected, duplicate.Disposition);
            Assert.Equal(RunRejectionReason.AlreadyRunning, duplicate.Rejection?.Reason);
            Assert.Equal(1, harness.Camera.ConnectCount);

            allowConnect.TrySetResult();
            var first = await firstTask.WaitAsync(Watchdog);
            Assert.Equal(ProductionCommandDisposition.Completed, first.Disposition);
        }
        finally
        {
            allowConnect.TrySetResult();
            if (!firstTask.IsCompleted)
            {
                await firstTask.WaitAsync(Watchdog);
            }

            var stop = await harness.Coordinator.StopAsync().WaitAsync(Watchdog);
            Assert.Equal(ProductionCommandDisposition.Completed, stop.Disposition);
        }
    }
}
