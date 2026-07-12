using System.IO;
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
        }
    }

    private static VariableCenterViewModel CreateViewModel(
        IInspectionExecution execution,
        IUiDispatcher dispatcher,
        RecordingCommunicationChannels channels) =>
        new(
            new RecordingRecipeRepository(new Recipe
            {
                Id = "recipe-1",
                Name = "Recipe 1",
                ProductCode = "P-1"
            }),
            new FakeDeviceConfigurationRepository(),
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

    private static string GetViewModelPath(
        [CallerFilePath] string testFilePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!,
            "..",
            "VisionStation.Client",
            "ViewModels",
            "VariableCenterViewModel.cs"));
}
