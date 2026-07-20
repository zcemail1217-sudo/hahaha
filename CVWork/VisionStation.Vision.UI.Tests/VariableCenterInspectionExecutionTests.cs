using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Prism.Events;
using VisionStation.Application;
using VisionStation.Application.Presentation;
using VisionStation.Client.ViewModels;
using VisionStation.Devices;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class VariableCenterInspectionExecutionTests
{
    [Fact]
    public void VariableCenter_observes_the_execution_module_not_raw_runner()
    {
        var constructor = Assert.Single(typeof(VariableCenterViewModel).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.Contains(parameters, parameter =>
            parameter.ParameterType == typeof(IInspectionExecution));
        var legacyContractName = string.Concat("IInspection", "Runner");
        Assert.DoesNotContain(parameters, parameter =>
            parameter.ParameterType.Name == legacyContractName);
    }

    [Fact]
    public void VariableCenter_subscribes_and_unsubscribes_module_result_event()
    {
        var source = File.ReadAllText(GetViewModelPath());

        Assert.Contains(
            "_inspectionExecution.RunCompleted += OnInspectionCompleted;",
            source);
        Assert.Contains(
            "_inspectionExecution.RunCompleted -= OnInspectionCompleted;",
            source);
    }

    [Fact]
    public async Task VariableCenter_dispatches_execution_results_and_stops_observing_after_dispose()
    {
        var connectEntered = RecipeManagementTestHarness.NewSignal();
        var connectCancelled = RecipeManagementTestHarness.NewSignal();
        var channels = new RecordingCommunicationChannels
        {
            ConnectHandler = async (_, cancellationToken) =>
            {
                connectEntered.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    connectCancelled.TrySetResult(true);
                }
            }
        };
        var session = new RecordingInspectionSession(RecipeManagementTestHarness.Active(
            InspectionRunModes.RecipeTest,
            nameof(VariableCenterViewModel)));
        var execution = new FakeInspectionExecution(session);
        var dispatcher = new ImmediateUiDispatcher();
        var viewModel = CreateViewModel(execution, dispatcher, channels);
        var disposed = false;

        try
        {
            await connectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await RecipeManagementTestHarness.WaitUntilAsync(
                () => viewModel.SelectedRecipe is not null && !viewModel.IsBusy);

            var mutationCount = 0;
            var allMutationsWereDispatched = true;
            viewModel.RuntimeValues.CollectionChanged += (_, _) =>
            {
                mutationCount++;
                allMutationsWereDispatched &= dispatcher.IsInvoking;
            };

            await Task.Run(() => execution.PublishCompleted(CreateRunResult("A")));

            Assert.True(allMutationsWereDispatched);
            Assert.True(mutationCount > 0);
            Assert.Equal("A", Assert.Single(viewModel.RuntimeValues).Value);

            viewModel.Dispose();
            disposed = true;
            await connectCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await Task.Run(() => execution.PublishCompleted(CreateRunResult("B")));

            Assert.Equal("A", Assert.Single(viewModel.RuntimeValues).Value);
        }
        finally
        {
            if (!disposed)
            {
                viewModel.Dispose();
                await connectCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }

            await viewModel.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(ProjectionSource.Inspection)]
    [InlineData(ProjectionSource.Configuration)]
    [InlineData(ProjectionSource.CommunicationFrame)]
    public async Task In_flight_projection_does_not_commit_after_dispose(
        ProjectionSource source)
    {
        var connectEntered = RecipeManagementTestHarness.NewSignal();
        var connectExited = RecipeManagementTestHarness.NewSignal();
        var channels = new RecordingCommunicationChannels
        {
            ConnectHandler = async (_, cancellationToken) =>
            {
                connectEntered.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                finally
                {
                    connectExited.TrySetResult(true);
                }
            }
        };
        var dispatcher = new GateableUiDispatcher();
        var context = CreateContext(dispatcher, channels);
        var disposed = false;
        Task? publication = null;
        Task? synchronousDisposal = null;

        try
        {
            await connectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await RecipeManagementTestHarness.WaitUntilAsync(
                () => context.ViewModel.SelectedRecipe is not null &&
                      !context.ViewModel.IsBusy);

            RecipeVariableItem? communicationVariable = null;
            if (source == ProjectionSource.CommunicationFrame)
            {
                communicationVariable = new RecipeVariableItem
                {
                    Key = "channel-value",
                    Name = "Channel Value",
                    Source = "TCP:channel-1"
                };
                context.ViewModel.Variables.Add(communicationVariable);
            }

            var statusBeforePublication = context.ViewModel.StatusText;
            dispatcher.BlockNextInvocation();
            publication = Task.Run(() => PublishProjection(context, source));
            await dispatcher.InvocationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            synchronousDisposal = Task.Run(context.ViewModel.Dispose);
            await synchronousDisposal.WaitAsync(TimeSpan.FromSeconds(2));
            disposed = true;
            dispatcher.ReleaseInvocation();
            await publication.WaitAsync(TimeSpan.FromSeconds(2));

            switch (source)
            {
                case ProjectionSource.Inspection:
                    Assert.Empty(context.ViewModel.RuntimeValues);
                    break;
                case ProjectionSource.Configuration:
                    Assert.Equal(statusBeforePublication, context.ViewModel.StatusText);
                    break;
                case ProjectionSource.CommunicationFrame:
                    Assert.NotNull(communicationVariable);
                    Assert.Equal(string.Empty, communicationVariable.LiveValue);
                    Assert.Null(communicationVariable.LiveValueUpdatedAt);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(source), source, null);
            }
        }
        finally
        {
            dispatcher.ReleaseInvocation();
            if (publication is not null)
            {
                await publication.WaitAsync(TimeSpan.FromSeconds(2));
            }

            if (synchronousDisposal is not null)
            {
                await synchronousDisposal.WaitAsync(TimeSpan.FromSeconds(2));
                disposed = true;
            }

            if (!disposed)
            {
                context.ViewModel.Dispose();
            }

            await connectExited.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await context.ViewModel.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_waits_for_polling_exit_and_reuses_completion()
    {
        var connectEntered = RecipeManagementTestHarness.NewSignal();
        var cancellationObserved = RecipeManagementTestHarness.NewSignal();
        var releaseConnect = RecipeManagementTestHarness.NewSignal();
        var connectExited = RecipeManagementTestHarness.NewSignal();
        var channels = new RecordingCommunicationChannels
        {
            ConnectHandler = async (_, cancellationToken) =>
            {
                connectEntered.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.TrySetResult(true);
                    await releaseConnect.Task;
                }
                finally
                {
                    connectExited.TrySetResult(true);
                }
            }
        };
        var context = CreateContext(new ImmediateUiDispatcher(), channels);
        Task? disposal = null;
        Task? repeatedAsyncDisposal = null;
        var disposalStarted = false;

        try
        {
            await connectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            disposal = context.ViewModel.DisposeAsync().AsTask();
            disposalStarted = true;
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(disposal.IsCompleted);
            var repeatedSynchronousDisposal = Task.Run(context.ViewModel.Dispose);
            repeatedAsyncDisposal = Task.Run(async () =>
                await context.ViewModel.DisposeAsync());
            await repeatedSynchronousDisposal.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(disposal.IsCompleted);
            Assert.False(repeatedAsyncDisposal.IsCompleted);

            releaseConnect.TrySetResult(true);
            await Task.WhenAll(disposal, repeatedAsyncDisposal)
                .WaitAsync(TimeSpan.FromSeconds(2));
            await connectExited.Task.WaitAsync(TimeSpan.FromSeconds(2));

            context.ViewModel.Dispose();
            await context.ViewModel.DisposeAsync();
        }
        finally
        {
            releaseConnect.TrySetResult(true);
            if (!disposalStarted)
            {
                context.ViewModel.Dispose();
            }

            if (disposal is not null)
            {
                await disposal.WaitAsync(TimeSpan.FromSeconds(2));
            }

            if (repeatedAsyncDisposal is not null)
            {
                await repeatedAsyncDisposal.WaitAsync(TimeSpan.FromSeconds(2));
            }

            await connectExited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Reentrant_dispose_does_not_deadlock_with_concurrent_dispose()
    {
        var context = CreateContext(
            new ImmediateUiDispatcher(),
            new RecordingCommunicationChannels());
        await RecipeManagementTestHarness.WaitUntilAsync(
            () => context.ViewModel.SelectedRecipe is not null &&
                  !context.ViewModel.IsBusy);

        var projectionEntered = RecipeManagementTestHarness.NewSignal();
        var allowReentrantDispose = RecipeManagementTestHarness.NewSignal();
        var projectionFinished = RecipeManagementTestHarness.NewSignal();
        Exception? projectionException = null;
        Task? firstDisposal = null;
        NotifyCollectionChangedEventHandler? onRuntimeValuesChanged = null;
        onRuntimeValuesChanged = (_, _) =>
        {
            context.ViewModel.RuntimeValues.CollectionChanged -= onRuntimeValuesChanged;
            projectionEntered.TrySetResult(true);
            allowReentrantDispose.Task.GetAwaiter().GetResult();
            context.ViewModel.Dispose();
        };
        context.ViewModel.RuntimeValues.CollectionChanged += onRuntimeValuesChanged;

        var projectionThread = new Thread(() =>
        {
            try
            {
                context.Execution.PublishCompleted(CreateRunResult("A"));
            }
            catch (Exception exception)
            {
                projectionException = exception;
            }
            finally
            {
                projectionFinished.TrySetResult(true);
            }
        })
        {
            IsBackground = true,
            Name = "VariableCenter reentrant projection"
        };

        try
        {
            projectionThread.Start();
            await projectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            firstDisposal = Task.Run(context.ViewModel.Dispose);
            await RecipeManagementTestHarness.WaitUntilAsync(
                () => ReadDisposeStarted(context.ViewModel));
            allowReentrantDispose.TrySetResult(true);

            await Task.WhenAll(firstDisposal, projectionFinished.Task)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Null(projectionException);
        }
        finally
        {
            allowReentrantDispose.TrySetResult(true);
            if (!projectionFinished.Task.IsCompleted)
            {
                try
                {
                    projectionThread.Interrupt();
                }
                catch (ThreadStateException)
                {
                }
            }

            await projectionFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (firstDisposal is not null)
            {
                await firstDisposal.WaitAsync(TimeSpan.FromSeconds(2));
            }

            await context.ViewModel.DisposeAsync();
            Assert.True(projectionThread.Join(TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task Hostile_event_remove_does_not_block_other_unsubscriptions_or_dispatch()
    {
        var connectEntered = RecipeManagementTestHarness.NewSignal();
        var connectExited = RecipeManagementTestHarness.NewSignal();
        var channels = new RecordingCommunicationChannels
        {
            ConnectHandler = async (_, cancellationToken) =>
            {
                connectEntered.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                finally
                {
                    connectExited.TrySetResult(true);
                }
            }
        };
        var dispatcher = new CountingUiDispatcher();
        var context = CreateContext(dispatcher, channels);
        var disposalStarted = false;

        try
        {
            await connectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await RecipeManagementTestHarness.WaitUntilAsync(
                () => context.ViewModel.SelectedRecipe is not null &&
                      !context.ViewModel.IsBusy);
            Assert.Equal(1, context.Execution.RunCompletedSubscriberCount);
            Assert.Equal(
                1,
                context.ConfigurationRepository.ConfigurationSavedSubscriberCount);
            Assert.Equal(1, context.Channels.FrameReceivedSubscriberCount);

            context.Execution.ThrowOnRunCompletedRemove = true;
            await context.ViewModel.DisposeAsync();
            disposalStarted = true;
            await connectExited.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, context.Execution.RunCompletedSubscriberCount);
            Assert.Equal(
                0,
                context.ConfigurationRepository.ConfigurationSavedSubscriberCount);
            Assert.Equal(0, context.Channels.FrameReceivedSubscriberCount);

            var invocationCount = dispatcher.InvocationCount;
            context.Execution.PublishCompleted(CreateRunResult("A"));
            context.ConfigurationRepository.PublishSaved(new DeviceConfiguration());
            context.Channels.PublishFrame(new CommunicationChannelRuntimeFrame(
                "TCP",
                "channel-1",
                "Channel 1",
                [(byte)'A'],
                1,
                1));

            Assert.Equal(invocationCount, dispatcher.InvocationCount);
            Assert.Empty(context.ViewModel.RuntimeValues);
        }
        finally
        {
            context.Execution.ThrowOnRunCompletedRemove = false;
            if (!disposalStarted)
            {
                context.ViewModel.Dispose();
            }

            await context.ViewModel.DisposeAsync();
            await connectExited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static void PublishProjection(
        VariableCenterTestContext context,
        ProjectionSource source)
    {
        switch (source)
        {
            case ProjectionSource.Inspection:
                context.Execution.PublishCompleted(CreateRunResult("A"));
                break;
            case ProjectionSource.Configuration:
                context.ConfigurationRepository.PublishSaved(new DeviceConfiguration());
                break;
            case ProjectionSource.CommunicationFrame:
                context.Channels.PublishFrame(new CommunicationChannelRuntimeFrame(
                    "TCP",
                    "channel-1",
                    "Channel 1",
                    [(byte)'A'],
                    1,
                    1));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(source), source, null);
        }
    }

    private static VariableCenterTestContext CreateContext(
        IUiDispatcher dispatcher,
        RecordingCommunicationChannels channels)
    {
        var session = new RecordingInspectionSession(RecipeManagementTestHarness.Active(
            InspectionRunModes.RecipeTest,
            nameof(VariableCenterViewModel)));
        var execution = new FakeInspectionExecution(session);
        var configurationRepository = new FakeDeviceConfigurationRepository();
        var viewModel = CreateViewModel(
            execution,
            dispatcher,
            channels,
            configurationRepository);
        return new VariableCenterTestContext(
            viewModel,
            execution,
            configurationRepository,
            channels);
    }

    private static VariableCenterViewModel CreateViewModel(
        IInspectionExecution execution,
        IUiDispatcher dispatcher,
        RecordingCommunicationChannels channels,
        FakeDeviceConfigurationRepository? configurationRepository = null) =>
        new(
            new RecordingRecipeRepository(new Recipe
            {
                Id = "recipe-1",
                Name = "Recipe 1",
                ProductCode = "P-1"
            }),
            configurationRepository ?? new FakeDeviceConfigurationRepository(),
            execution,
            dispatcher,
            new DeviceConfiguration(),
            new EventAggregator(),
            channels,
            new DeviceRuntime(),
            new SimulatedPlcClient(),
            new SimulatedDigitalIoController(),
            new UnsavedChangesService());

    private static InspectionRunResult CreateRunResult(string value) =>
        RecipeRunResults.Ok() with
        {
            RuntimeValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Run.Value"] = value
            }
        };

    private static bool ReadDisposeStarted(VariableCenterViewModel viewModel)
    {
        var field = typeof(VariableCenterViewModel).GetField(
            "_disposeStarted",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        return Assert.IsType<int>(field.GetValue(viewModel)) != 0;
    }

    private static string GetViewModelPath(
        [CallerFilePath] string testFilePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!,
            "..",
            "VisionStation.Client",
            "ViewModels",
            "VariableCenterViewModel.cs"));

    public enum ProjectionSource
    {
        Inspection,
        Configuration,
        CommunicationFrame
    }

    private sealed record VariableCenterTestContext(
        VariableCenterViewModel ViewModel,
        FakeInspectionExecution Execution,
        FakeDeviceConfigurationRepository ConfigurationRepository,
        RecordingCommunicationChannels Channels);

    private sealed class GateableUiDispatcher : IUiDispatcher
    {
        private int _blockNextInvocation;

        public TaskCompletionSource<bool> InvocationEntered { get; } =
            RecipeManagementTestHarness.NewSignal();

        public TaskCompletionSource<bool> InvocationRelease { get; } =
            RecipeManagementTestHarness.NewSignal();

        public void BlockNextInvocation() =>
            Interlocked.Exchange(ref _blockNextInvocation, 1);

        public void ReleaseInvocation() =>
            InvocationRelease.TrySetResult(true);

        public void Invoke(Action action)
        {
            if (Interlocked.Exchange(ref _blockNextInvocation, 0) == 1)
            {
                InvocationEntered.TrySetResult(true);
                InvocationRelease.Task.GetAwaiter().GetResult();
            }

            action();
        }
    }

    private sealed class CountingUiDispatcher : IUiDispatcher
    {
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public void Invoke(Action action)
        {
            Interlocked.Increment(ref _invocationCount);
            action();
        }
    }
}
