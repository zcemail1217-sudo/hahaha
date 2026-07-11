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
    public async Task RunSingleAsync_when_busy_clear_fails_faults_after_remaining_cleanup()
    {
        var harness = CoordinatorHarness.Create();
        harness.Plc.SetBusyHandler = (busy, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return busy
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("busy-clear-failure"));
        };

        var exception = await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            await harness.Coordinator.RunSingleAsync().WaitAsync(Watchdog);
        });

        Assert.Contains(
            exception.Flatten().InnerExceptions,
            error => error.Message == "busy-clear-failure");
        Assert.Equal([true, false], harness.Plc.BusyWrites.ToArray());
        Assert.Equal(1, harness.CommunicationChannels.DisconnectCount);
        Assert.Null(harness.Execution.Current);
        Assert.Equal(ProductionState.Faulted, harness.Coordinator.Snapshot.State);
        Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);
        var alarm = Assert.Single(
            harness.Alarms.Raised,
            item => item.Id == "production:cleanup");
        Assert.Equal(AlarmSeverity.Critical, alarm.Severity);
    }

    [Fact]
    public async Task RunSingleAsync_when_disconnect_fails_releases_session_and_reports_cleanup()
    {
        var harness = CoordinatorHarness.Create();
        harness.CommunicationChannels.DisconnectHandler = cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException(new InvalidOperationException("disconnect-failure"));
        };

        var exception = await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            await harness.Coordinator.RunSingleAsync().WaitAsync(Watchdog);
        });

        Assert.Contains(
            exception.Flatten().InnerExceptions,
            error => error.Message == "disconnect-failure");
        Assert.Equal(1, harness.CommunicationChannels.DisconnectCount);
        Assert.Null(harness.Execution.Current);
        Assert.Equal(ProductionState.Faulted, harness.Coordinator.Snapshot.State);
        Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);
        var alarm = Assert.Single(
            harness.Alarms.Raised,
            item => item.Id == "production:cleanup");
        Assert.Equal(AlarmSeverity.Critical, alarm.Severity);
    }

    [Fact]
    public async Task RunSingleAsync_when_session_dispose_fails_after_release_keeps_module_truth()
    {
        var innerExecution = new InspectionExecution(
            new TestInspectionExecutor(),
            new FakeAppLogService());
        var execution = new DisposeAfterReleaseFailingInspectionExecution(innerExecution);
        var harness = CoordinatorHarness.Create(inspectionExecution: execution);

        var exception = await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            await harness.Coordinator.RunSingleAsync().WaitAsync(Watchdog);
        });

        Assert.Contains(
            exception.Flatten().InnerExceptions,
            error => error.Message == "session-dispose-failure");
        Assert.Equal(1, harness.CommunicationChannels.DisconnectCount);
        Assert.Null(harness.Execution.Current);
        Assert.Equal(ProductionState.Faulted, harness.Coordinator.Snapshot.State);
        Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);
        Assert.Single(
            harness.Alarms.Raised,
            item => item.Id == "production:cleanup" &&
                    item.Severity == AlarmSeverity.Critical);
    }

    [Fact]
    public async Task RunSingleAsync_never_projects_reacquired_external_session_as_production_owner()
    {
        var innerExecution = new InspectionExecution(
            new TestInspectionExecutor(),
            new FakeAppLogService());
        IInspectionSession? externalSession = null;
        var execution = new DisposeAfterReleaseFailingInspectionExecution(
            innerExecution,
            () =>
            {
                var admission = innerExecution.TryBegin(new InspectionRunIntent(
                    InspectionRunModes.RecipeTest,
                    "RecipeManagementViewModel"));
                externalSession = admission is RunAdmission.Acquired acquired
                    ? acquired.Session
                    : throw new InvalidOperationException("External session was not acquired.");
            });
        var harness = CoordinatorHarness.Create(inspectionExecution: execution);

        try
        {
            await Assert.ThrowsAsync<AggregateException>(async () =>
            {
                await harness.Coordinator.RunSingleAsync().WaitAsync(Watchdog);
            });

            Assert.NotNull(externalSession);
            Assert.Same(externalSession.Run, harness.Execution.Current);
            Assert.Equal(ProductionState.Faulted, harness.Coordinator.Snapshot.State);
            Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);
        }
        finally
        {
            if (externalSession is not null)
            {
                await externalSession.DisposeAsync().AsTask().WaitAsync(Watchdog);
            }
        }
    }

    [Fact]
    public async Task RunSingleAsync_when_multiple_cleanup_stages_fail_preserves_all_failures()
    {
        var harness = CoordinatorHarness.Create();
        harness.Plc.SetBusyHandler = (busy, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return busy
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("busy-clear-failure"));
        };
        harness.CommunicationChannels.DisconnectHandler = cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException(new InvalidOperationException("disconnect-failure"));
        };

        var exception = await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            await harness.Coordinator.RunSingleAsync().WaitAsync(Watchdog);
        });

        Assert.Equal(
            ["busy-clear-failure", "disconnect-failure"],
            exception.Flatten().InnerExceptions.Select(error => error.Message).ToArray());
        Assert.Equal([true, false], harness.Plc.BusyWrites.ToArray());
        Assert.Equal(1, harness.CommunicationChannels.DisconnectCount);
        Assert.Null(harness.Execution.Current);
        Assert.Equal(ProductionState.Faulted, harness.Coordinator.Snapshot.State);
        Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);
        Assert.Single(
            harness.Alarms.Raised,
            item => item.Id == "production:cleanup" &&
                    item.Severity == AlarmSeverity.Critical);
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
    public async Task Concurrent_stop_calls_do_not_block_behind_synchronous_cancellation_callback()
    {
        var harness = CoordinatorHarness.Create();
        var executeEntered = CoordinatorHarness.NewSignal();
        var callbackEntered = CoordinatorHarness.NewSignal();
        var allowCallbackExit = CoordinatorHarness.NewSignal();
        var allowExecutorExit = CoordinatorHarness.NewSignal();
        harness.Executor.Handler = async (unusedRequest, cancellationToken) =>
        {
            _ = cancellationToken.Register(() =>
            {
                callbackEntered.TrySetResult();
                allowCallbackExit.Task.GetAwaiter().GetResult();
            });
            executeEntered.TrySetResult();
            await allowExecutorExit.Task;
            throw new OperationCanceledException(cancellationToken);
        };
        var singleTask = harness.Coordinator.RunSingleAsync();
        await executeEntered.Task.WaitAsync(Watchdog);

        var firstStopTask = Task.Run(async () => await harness.Coordinator.StopAsync());
        await callbackEntered.Task.WaitAsync(Watchdog);
        var secondStopReachedAwait = CoordinatorHarness.NewSignal();
        var secondStopTask = Task.Run(async () =>
        {
            var pendingStop = harness.Coordinator.StopAsync();
            secondStopReachedAwait.TrySetResult();
            return await pendingStop;
        });
        var reachedAwaitBeforeRelease = false;
        try
        {
            await secondStopReachedAwait.Task.WaitAsync(Watchdog);
            reachedAwaitBeforeRelease = true;
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            allowCallbackExit.TrySetResult();
        }

        try
        {
            await secondStopReachedAwait.Task.WaitAsync(Watchdog);
        }
        finally
        {
            allowExecutorExit.TrySetResult();
        }

        var stops = await Task.WhenAll(firstStopTask, secondStopTask).WaitAsync(Watchdog);
        var single = await singleTask.WaitAsync(Watchdog);

        Assert.True(
            reachedAwaitBeforeRelease,
            "The second StopAsync call did not return its task while cancellation callbacks were running.");
        Assert.All(
            stops,
            stop => Assert.Equal(ProductionCommandDisposition.Completed, stop.Disposition));
        Assert.Equal(ProductionCommandDisposition.Canceled, single.Disposition);
        Assert.Null(harness.Execution.Current);
    }

    [Fact]
    public async Task Caller_cancellation_queues_operation_cancel_and_defers_dispose_until_callbacks_finish()
    {
        var harness = CoordinatorHarness.Create();
        var executeEntered = CoordinatorHarness.NewSignal();
        var callbackEntered = CoordinatorHarness.NewSignal();
        var releaseExecutor = CoordinatorHarness.NewSignal();
        var probeDisposal = CoordinatorHarness.NewSignal();
        var probeCompleted = CoordinatorHarness.NewSignal();
        var allowCallbackExit = CoordinatorHarness.NewSignal();
        var disposedDuringCallback = 0;
        harness.Executor.Handler = async (unusedRequest, cancellationToken) =>
        {
            _ = cancellationToken.Register(() =>
            {
                callbackEntered.TrySetResult();
                probeDisposal.Task.GetAwaiter().GetResult();
                try
                {
                    _ = cancellationToken.WaitHandle;
                }
                catch (ObjectDisposedException)
                {
                    Interlocked.Exchange(ref disposedDuringCallback, 1);
                }

                probeCompleted.TrySetResult();
                allowCallbackExit.Task.GetAwaiter().GetResult();
            });
            executeEntered.TrySetResult();
            await releaseExecutor.Task;
            throw new OperationCanceledException(cancellationToken);
        };
        using var callerCancellation = new CancellationTokenSource();
        var singleTask = harness.Coordinator.RunSingleAsync(callerCancellation.Token);
        await executeEntered.Task.WaitAsync(Watchdog);

        var callerCancelReturned = CoordinatorHarness.NewSignal();
        var cancelInvocation = Task.Run(() =>
        {
            callerCancellation.Cancel();
            callerCancelReturned.TrySetResult();
        });
        await callbackEntered.Task.WaitAsync(Watchdog);
        var cancelReturnedBeforeCallbackExit = false;
        ProductionCommandResult<InspectionRunResult>? single = null;
        var completedWhileCallbackBlocked = false;
        try
        {
            try
            {
                await callerCancelReturned.Task.WaitAsync(Watchdog);
                cancelReturnedBeforeCallbackExit = true;
            }
            catch (TimeoutException)
            {
            }

            releaseExecutor.TrySetResult();
            try
            {
                single = await singleTask.WaitAsync(Watchdog);
                completedWhileCallbackBlocked = true;
            }
            catch (TimeoutException)
            {
            }

            probeDisposal.TrySetResult();
            await probeCompleted.Task.WaitAsync(Watchdog);
        }
        finally
        {
            releaseExecutor.TrySetResult();
            probeDisposal.TrySetResult();
            allowCallbackExit.TrySetResult();
            await cancelInvocation.WaitAsync(Watchdog);
            single ??= await singleTask.WaitAsync(Watchdog);
        }

        Assert.True(
            cancelReturnedBeforeCallbackExit,
            "Caller cancellation synchronously executed operation callbacks.");
        Assert.True(
            completedWhileCallbackBlocked,
            "Production cleanup did not complete while the cancellation callback was blocked.");
        Assert.Equal(0, Volatile.Read(ref disposedDuringCallback));
        Assert.Equal(ProductionCommandDisposition.Canceled, single.Disposition);
        Assert.Equal(ProductionState.Stopped, harness.Coordinator.Snapshot.State);
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

    [Fact]
    public async Task Start_and_waiting_stops_observe_same_startup_cleanup_failure()
    {
        var harness = CoordinatorHarness.Create();
        var allowCameraConnect = CoordinatorHarness.NewSignal();
        harness.Camera.ConnectHandler = _ => allowCameraConnect.Task;
        harness.CommunicationChannels.DisconnectHandler = cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException(new InvalidOperationException("disconnect-failure"));
        };
        var startTask = harness.Coordinator.StartAsync();
        await harness.Camera.ConnectEntered.Task.WaitAsync(Watchdog);
        var firstStopTask = harness.Coordinator.StopAsync();
        var secondStopTask = harness.Coordinator.StopAsync();

        allowCameraConnect.TrySetResult();
        var startException = await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            await startTask.WaitAsync(Watchdog);
        });
        var firstStopException = await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            await firstStopTask.WaitAsync(Watchdog);
        });
        var secondStopException = await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            await secondStopTask.WaitAsync(Watchdog);
        });

        Assert.All(
            [startException, firstStopException, secondStopException],
            exception => Assert.Contains(
                exception.Flatten().InnerExceptions,
                error => error.Message == "disconnect-failure"));
        Assert.Equal(1, harness.CommunicationChannels.DisconnectCount);
        Assert.Null(harness.Execution.Current);
        Assert.Equal(ProductionState.Faulted, harness.Coordinator.Snapshot.State);
        Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);
        Assert.Single(
            harness.Alarms.Raised,
            item => item.Id == "production:cleanup" &&
                    item.Severity == AlarmSeverity.Critical);
    }

    [Fact]
    public async Task Stop_during_initialization_cancels_registered_operation()
    {
        var harness = CoordinatorHarness.Create();
        harness.Camera.ConnectHandler = cancellationToken =>
            Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
        using var recorder = new DistinctStateRecorder(harness.Coordinator);
        var startTask = harness.Coordinator.StartAsync();
        await harness.Camera.ConnectEntered.Task.WaitAsync(Watchdog);

        var stop = await harness.Coordinator.StopAsync().WaitAsync(Watchdog);
        var start = await startTask.WaitAsync(Watchdog);

        Assert.Equal(ProductionCommandDisposition.Completed, stop.Disposition);
        Assert.Equal(ProductionCommandDisposition.Canceled, start.Disposition);
        Assert.Equal(
            [ProductionState.Starting, ProductionState.Stopping, ProductionState.Stopped],
            recorder.States);
        Assert.Equal(1, harness.CommunicationChannels.DisconnectCount);
        Assert.Null(harness.Execution.Current);
    }

    [Fact]
    public async Task Stop_during_last_initialization_return_never_transitions_back_to_running()
    {
        var harness = CoordinatorHarness.Create();
        var allowConnectReturn = CoordinatorHarness.NewSignal();
        harness.CommunicationChannels.ConnectHandler = _ => allowConnectReturn.Task;
        using var recorder = new DistinctStateRecorder(harness.Coordinator);
        var startTask = harness.Coordinator.StartAsync();
        await harness.CommunicationChannels.ConnectEntered.Task.WaitAsync(Watchdog);
        var stopTask = harness.Coordinator.StopAsync();

        try
        {
            allowConnectReturn.TrySetResult();
            var stop = await stopTask.WaitAsync(Watchdog);
            var start = await startTask.WaitAsync(Watchdog);

            Assert.Equal(ProductionCommandDisposition.Completed, stop.Disposition);
            Assert.Equal(ProductionCommandDisposition.Canceled, start.Disposition);
            Assert.Equal(
                [ProductionState.Starting, ProductionState.Stopping, ProductionState.Stopped],
                recorder.States);
            Assert.DoesNotContain(ProductionState.Running, recorder.States);
        }
        finally
        {
            allowConnectReturn.TrySetResult();
        }
    }

    [Fact]
    public async Task Reentrant_stop_publishes_snapshots_in_revision_order()
    {
        var harness = CoordinatorHarness.Create();
        Task<ProductionCommandResult>? stopTask = null;
        var stopped = 0;
        EventHandler<ProductionSnapshot> stopOnStarting = (_, snapshot) =>
        {
            if (snapshot.State == ProductionState.Starting &&
                Interlocked.Exchange(ref stopped, 1) == 0)
            {
                stopTask = harness.Coordinator.StopAsync();
            }
        };
        harness.Coordinator.SnapshotChanged += stopOnStarting;
        using var recorder = new DistinctStateRecorder(harness.Coordinator);

        ProductionCommandResult start;
        try
        {
            start = await harness.Coordinator.StartAsync().WaitAsync(Watchdog);
        }
        finally
        {
            harness.Coordinator.SnapshotChanged -= stopOnStarting;
        }

        Assert.NotNull(stopTask);
        var stop = await stopTask.WaitAsync(Watchdog);

        Assert.Equal(ProductionCommandDisposition.Canceled, start.Disposition);
        Assert.Equal(ProductionCommandDisposition.Completed, stop.Disposition);
        Assert.Equal(
            [ProductionState.Starting, ProductionState.Stopping, ProductionState.Stopped],
            recorder.States);
    }

    [Fact]
    public async Task Concurrent_stop_calls_wait_same_completion_and_clean_once()
    {
        var harness = CoordinatorHarness.Create();
        var executeEntered = CoordinatorHarness.NewSignal();
        var cancellationObserved = CoordinatorHarness.NewSignal();
        var allowExecutorExit = CoordinatorHarness.NewSignal();
        harness.Executor.Handler = async (_, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(
                () => cancellationObserved.TrySetResult());
            executeEntered.TrySetResult();
            await allowExecutorExit.Task;
            throw new OperationCanceledException(cancellationToken);
        };
        var start = await harness.Coordinator.StartAsync().WaitAsync(Watchdog);
        await executeEntered.Task.WaitAsync(Watchdog);

        var stops = Enumerable.Range(0, 8)
            .Select(_ => harness.Coordinator.StopAsync())
            .ToArray();
        await cancellationObserved.Task.WaitAsync(Watchdog);
        allowExecutorExit.TrySetResult();
        var results = await Task.WhenAll(stops).WaitAsync(Watchdog);

        Assert.Equal(ProductionCommandDisposition.Completed, start.Disposition);
        Assert.All(
            results,
            result => Assert.Equal(ProductionCommandDisposition.Completed, result.Disposition));
        Assert.Equal(1, harness.CommunicationChannels.DisconnectCount);
        Assert.Equal([true, false], harness.Plc.BusyWrites.ToArray());
        Assert.Null(harness.Execution.Current);
        Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);
    }

    [Fact]
    public async Task Stop_timeout_faults_and_holds_session_until_late_cleanup()
    {
        var harness = CoordinatorHarness.Create(stopWaitTimeoutMs: 200);
        var executeEntered = CoordinatorHarness.NewSignal();
        var allowExecutorExit = CoordinatorHarness.NewSignal();
        harness.Executor.Handler = async (_, cancellationToken) =>
        {
            executeEntered.TrySetResult();
            await allowExecutorExit.Task;
            throw new OperationCanceledException(cancellationToken);
        };
        var start = await harness.Coordinator.StartAsync().WaitAsync(Watchdog);
        await executeEntered.Task.WaitAsync(Watchdog);
        var session = harness.Execution.Current;
        Assert.NotNull(session);
        Task<ProductionCommandResult>? firstStopTask = null;
        Task<ProductionCommandResult>? repeatedStopTask = null;
        Task<ProductionCommandResult>? finalStopTask = null;

        try
        {
            firstStopTask = harness.Coordinator.StopAsync();
            await AssertInternalStopTimeoutAsync(firstStopTask);

            Assert.Equal(ProductionCommandDisposition.Completed, start.Disposition);
            Assert.Equal(ProductionState.Faulted, harness.Coordinator.Snapshot.State);
            Assert.Equal(session.SessionId, harness.Coordinator.Snapshot.ActiveSessionId);
            Assert.Same(session, harness.Execution.Current);
            var conflict = Assert.IsType<RunAdmission.Rejected>(
                harness.Execution.TryBegin(new InspectionRunIntent(
                    InspectionRunModes.RecipeTest,
                    "RecipeManagementViewModel")));
            Assert.Equal(RunRejectionReason.Busy, conflict.Rejection.Reason);

            repeatedStopTask = harness.Coordinator.StopAsync();
            await AssertInternalStopTimeoutAsync(repeatedStopTask);
            Assert.Equal(ProductionState.Faulted, harness.Coordinator.Snapshot.State);
            Assert.Equal(session.SessionId, harness.Coordinator.Snapshot.ActiveSessionId);
            var timeoutAlarm = Assert.Single(
                harness.Alarms.Raised,
                alarm => alarm.Id == "production:stop-timeout");
            Assert.Equal(AlarmSeverity.Critical, timeoutAlarm.Severity);
            Assert.Equal(
                "Production stop timed out; the production operation has not completed.",
                timeoutAlarm.Message);

            finalStopTask = harness.Coordinator.StopAsync();
            allowExecutorExit.TrySetResult();
            var finalStop = await finalStopTask.WaitAsync(Watchdog);

            Assert.Equal(ProductionCommandDisposition.Completed, finalStop.Disposition);
            Assert.Null(harness.Execution.Current);
            Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);
            Assert.Equal(1, harness.CommunicationChannels.DisconnectCount);
            Assert.Equal(ProductionState.Faulted, harness.Coordinator.Snapshot.State);
            Assert.Single(
                harness.Alarms.Raised,
                alarm => alarm.Id == "production:stop-timeout");
        }
        finally
        {
            allowExecutorExit.TrySetResult();
            foreach (var pending in new[] { firstStopTask, repeatedStopTask, finalStopTask })
            {
                if (pending is null)
                {
                    continue;
                }

                try
                {
                    await pending.WaitAsync(Watchdog);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    [Fact]
    public async Task Timeout_before_session_attachment_never_regresses_faulted_to_starting()
    {
        var harness = CoordinatorHarness.Create(stopWaitTimeoutMs: 200);
        var changedEntered = CoordinatorHarness.NewSignal();
        var allowChangedReturn = CoordinatorHarness.NewSignal();
        EventHandler<InspectionExecutionChangedEventArgs> blockAdmission = (_, args) =>
        {
            if (args.Current is not null)
            {
                changedEntered.TrySetResult();
                allowChangedReturn.Task.GetAwaiter().GetResult();
            }
        };
        harness.Execution.Changed += blockAdmission;
        using var recorder = new DistinctStateRecorder(harness.Coordinator);
        var startTask = Task.Run(() => harness.Coordinator.StartAsync());
        await changedEntered.Task.WaitAsync(Watchdog);
        Task<ProductionCommandResult>? stopTask = null;

        try
        {
            stopTask = harness.Coordinator.StopAsync();
            await AssertInternalStopTimeoutAsync(stopTask);
            Assert.Equal(ProductionState.Faulted, harness.Coordinator.Snapshot.State);
            Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);

            allowChangedReturn.TrySetResult();
            var start = await startTask.WaitAsync(Watchdog);

            Assert.Equal(ProductionCommandDisposition.Canceled, start.Disposition);
            Assert.DoesNotContain(ProductionState.Starting, recorder.States);
            Assert.Equal(ProductionState.Faulted, harness.Coordinator.Snapshot.State);
            Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);
        }
        finally
        {
            allowChangedReturn.TrySetResult();
            harness.Execution.Changed -= blockAdmission;
            try
            {
                await startTask.WaitAsync(Watchdog);
                if (stopTask is not null)
                {
                    await stopTask.WaitAsync(Watchdog);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    [Fact]
    public async Task Pending_reservation_timeout_never_claims_external_session_ownership()
    {
        var innerExecution = new InspectionExecution(
            new TestInspectionExecutor(),
            new FakeAppLogService());
        var externalAdmission = Assert.IsType<RunAdmission.Acquired>(
            innerExecution.TryBegin(new InspectionRunIntent(
                InspectionRunModes.RecipeTest,
                "RecipeManagementViewModel")));
        var external = externalAdmission.Session.Run;
        var blockingExecution = new BlockingRejectedInspectionExecution(external);
        var harness = CoordinatorHarness.Create(200, blockingExecution);
        var startTask = Task.Run(() => harness.Coordinator.StartAsync());
        await blockingExecution.TryBeginEntered.Task.WaitAsync(Watchdog);
        Task<ProductionCommandResult>? stopTask = null;

        try
        {
            stopTask = harness.Coordinator.StopAsync();
            await AssertInternalStopTimeoutAsync(stopTask);
            Assert.Equal(ProductionState.Faulted, harness.Coordinator.Snapshot.State);
            Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);
            Assert.Same(external, harness.Execution.Current);

            blockingExecution.AllowTryBeginReturn.TrySetResult();
            var start = await startTask.WaitAsync(Watchdog);

            Assert.Equal(ProductionCommandDisposition.Rejected, start.Disposition);
            Assert.Equal(RunRejectionReason.Busy, start.Rejection?.Reason);
            Assert.Equal(ProductionState.Faulted, harness.Coordinator.Snapshot.State);
            Assert.Null(harness.Coordinator.Snapshot.ActiveSessionId);
            Assert.Same(external, harness.Execution.Current);
        }
        finally
        {
            blockingExecution.AllowTryBeginReturn.TrySetResult();
            try
            {
                await startTask.WaitAsync(Watchdog);
                if (stopTask is not null)
                {
                    await stopTask.WaitAsync(Watchdog);
                }
            }
            catch (Exception)
            {
            }

            await externalAdmission.Session.DisposeAsync().AsTask().WaitAsync(Watchdog);
        }
    }

    [Fact]
    public async Task Stop_when_idle_is_noop_and_external_session_is_not_owner()
    {
        var harness = CoordinatorHarness.Create();

        var idle = await harness.Coordinator.StopAsync().WaitAsync(Watchdog);
        Assert.Equal(ProductionCommandDisposition.NoOp, idle.Disposition);

        var externalAdmission = Assert.IsType<RunAdmission.Acquired>(
            harness.Execution.TryBegin(new InspectionRunIntent(
                InspectionRunModes.RecipeTest,
                "RecipeManagementViewModel")));
        try
        {
            var external = await harness.Coordinator.StopAsync().WaitAsync(Watchdog);

            Assert.Equal(ProductionCommandDisposition.Rejected, external.Disposition);
            Assert.Equal(RunRejectionReason.NotOwner, external.Rejection?.Reason);
            Assert.Same(externalAdmission.Session.Run, harness.Execution.Current);
            Assert.Equal(0, harness.CommunicationChannels.DisconnectCount);
        }
        finally
        {
            await externalAdmission.Session.DisposeAsync().AsTask().WaitAsync(Watchdog);
        }
    }

    [Fact]
    public async Task RunSingle_fault_publishes_faulted_sequence_and_releases_session()
    {
        var harness = CoordinatorHarness.Create();
        harness.Executor.Handler = (_, _) =>
            Task.FromException<InspectionRunResult>(
                new InvalidOperationException("inspection-failure"));
        using var recorder = new DistinctStateRecorder(harness.Coordinator);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await harness.Coordinator.RunSingleAsync().WaitAsync(Watchdog);
        });

        Assert.Equal("inspection-failure", exception.Message);
        Assert.Equal(
            [
                ProductionState.Starting,
                ProductionState.Running,
                ProductionState.Stopping,
                ProductionState.Faulted
            ],
            recorder.States);
        Assert.Null(harness.Execution.Current);
        var alarm = Assert.Single(
            harness.Alarms.Raised,
            item => item.Severity == AlarmSeverity.Error);
        Assert.Equal("production:single-run", alarm.Id);
        Assert.Equal(AlarmSeverity.Error, alarm.Severity);
    }

    private static async Task AssertInternalStopTimeoutAsync(
        Task<ProductionCommandResult> stopTask)
    {
        var completed = await Task.WhenAny(stopTask, Task.Delay(Watchdog));
        Assert.Same(stopTask, completed);
        await Assert.ThrowsAsync<TimeoutException>(async () => await stopTask);
    }
}
