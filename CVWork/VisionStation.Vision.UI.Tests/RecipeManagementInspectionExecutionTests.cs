using VisionStation.Application;
using VisionStation.Client.ViewModels;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class RecipeManagementInspectionExecutionTests
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Known_external_session_disables_test_run_until_released()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var active = RecipeManagementTestHarness.Active(
            InspectionRunModes.Continuous,
            nameof(ProductionCoordinator));

        harness.Execution.PublishCurrent(active);
        Assert.False(harness.ViewModel.TestRunRecipeCommand.CanExecute());

        harness.Execution.PublishCurrent(null);
        Assert.True(harness.ViewModel.TestRunRecipeCommand.CanExecute());
    }

    [Fact]
    public async Task Navigation_return_refreshes_occupancy_that_changed_while_away()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        harness.ViewModel.OnNavigatedFrom(null!);
        Assert.Equal(0, harness.Execution.ChangedSubscriberCount);

        harness.Execution.PublishCurrent(RecipeManagementTestHarness.Active(
            InspectionRunModes.Continuous,
            nameof(ProductionCoordinator)));

        harness.ViewModel.OnNavigatedTo(null!);

        Assert.Equal(1, harness.Execution.ChangedSubscriberCount);
        Assert.False(harness.ViewModel.TestRunRecipeCommand.CanExecute());
        harness.Execution.PublishCurrent(null);
    }

    [Fact]
    public async Task Rejected_run_does_not_save_switch_connect_or_begin_run_control()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var active = RecipeManagementTestHarness.Active(
            InspectionRunModes.Continuous,
            nameof(ProductionCoordinator));
        harness.Execution.TryBeginHandler = _ => new RunAdmission.Rejected(
            new RunRejection(RunRejectionReason.Busy, active));

        await harness.ViewModel.TestRunRecipeCommand.Execute().WaitAsync(CommandTimeout);

        Assert.Equal(1, harness.Execution.TryBeginCount);
        Assert.Equal(0, harness.Recipes.SaveCount);
        Assert.Equal(0, harness.Recipes.SetCurrentCount);
        Assert.Equal(0, harness.Channels.ConnectCount);
        Assert.Equal(0, harness.Channels.DisconnectCount);
        Assert.Equal(0, harness.RunControl.BeginCount);
        Assert.Equal(0, harness.Session.DisposeCount);
        Assert.Contains("连续生产", harness.ViewModel.StatusText);
        Assert.Equal(InspectionRunModes.RecipeTest, harness.Execution.LastIntent?.Mode);
        Assert.Equal(
            nameof(RecipeManagementViewModel),
            harness.Execution.LastIntent?.EntryPoint);
    }

    [Fact]
    public async Task Recipe_changes_during_run_are_coalesced_and_replayed_after_release()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var entered = RecipeManagementTestHarness.NewSignal();
        harness.Session.Handler = async (_, token) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return RecipeRunResults.Ok();
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await entered.Task.WaitAsync(CommandTimeout);
        var getAsyncBaseline = harness.Recipes.GetAsyncCount;
        const string externalVariableKey = "ExternalDeferredVariable";
        var externalRecipe = new Recipe
        {
            Id = "recipe-1",
            Name = "Externally Updated Recipe",
            ProductCode = "P-EXT",
            Variables =
            [
                new RecipeVariableDefinition
                {
                    Key = externalVariableKey,
                    Name = "External Deferred Variable",
                    CurrentValue = "external"
                }
            ]
        };

        harness.PublishRecipeChanged(externalRecipe);
        harness.PublishRecipeChanged(externalRecipe);

        Assert.DoesNotContain(
            harness.ViewModel.RecipeVariables,
            variable => variable.Key == externalVariableKey);
        Assert.Equal(getAsyncBaseline, harness.Recipes.GetAsyncCount);

        harness.ViewModel.OnNavigatedFrom(null!);
        await running.WaitAsync(CommandTimeout);
        await RecipeManagementTestHarness.WaitUntilAsync(() =>
            harness.ViewModel.RecipeVariables.Any(variable =>
                variable.Key == externalVariableKey));

        Assert.Equal(getAsyncBaseline + 1, harness.Recipes.GetAsyncCount);
    }

    [Fact]
    public async Task Paused_run_can_reenter_async_command_only_to_resume()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var entered = RecipeManagementTestHarness.NewSignal();
        harness.Session.Handler = async (_, token) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return RecipeRunResults.Ok();
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await entered.Task.WaitAsync(CommandTimeout);
        harness.ViewModel.PauseTestRunCommand.Execute();

        Assert.True(harness.ViewModel.IsTestRunPaused);
        Assert.True(harness.ViewModel.TestRunRecipeCommand.CanExecute());
        await harness.ViewModel.TestRunRecipeCommand.Execute().WaitAsync(CommandTimeout);
        Assert.False(harness.ViewModel.IsTestRunPaused);
        Assert.False(harness.RunControl.IsPaused);

        harness.ViewModel.OnNavigatedFrom(null!);
        await running.WaitAsync(CommandTimeout);
    }

    [Fact]
    public async Task Reset_during_persist_reuses_session_and_restarts_attempt()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var saveEntered = RecipeManagementTestHarness.NewSignal();
        var saveAttempt = 0;
        harness.Recipes.SaveHandler = async (_, token) =>
        {
            if (Interlocked.Increment(ref saveAttempt) == 1)
            {
                saveEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await saveEntered.Task.WaitAsync(CommandTimeout);
        harness.ViewModel.ResetTestRunCommand.Execute();
        await running.WaitAsync(CommandTimeout);

        Assert.Equal(1, harness.Execution.TryBeginCount);
        Assert.Equal(2, harness.Recipes.SaveCount);
        Assert.True(
            harness.Session.Requests.Count == 1,
            $"Requests={harness.Session.Requests.Count}; " +
            $"Status={harness.ViewModel.StatusText}; " +
            $"Running={harness.ViewModel.IsTestRunning}; " +
            $"Disposed={harness.Session.DisposeCount}; " +
            $"Disconnects={harness.Channels.DisconnectCount}");
        Assert.Equal(1, harness.Session.DisposeCount);
    }

    [Fact]
    public async Task Reset_reuses_one_session_for_two_attempts_and_disposes_once()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var firstEntered = RecipeManagementTestHarness.NewSignal();
        var attempt = 0;
        harness.Session.Handler = async (_, token) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                firstEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return RecipeRunResults.Ok();
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await firstEntered.Task.WaitAsync(CommandTimeout);
        harness.ViewModel.ResetTestRunCommand.Execute();
        await running.WaitAsync(CommandTimeout);

        Assert.Equal(1, harness.Execution.TryBeginCount);
        Assert.True(
            harness.Session.Requests.Count == 2,
            $"Requests={harness.Session.Requests.Count}; " +
            $"Status={harness.ViewModel.StatusText}; " +
            $"Running={harness.ViewModel.IsTestRunning}; " +
            $"Disposed={harness.Session.DisposeCount}; " +
            $"Disconnects={harness.Channels.DisconnectCount}");
        Assert.Equal(1, harness.Session.DisposeCount);
        Assert.Equal(1, harness.Channels.DisconnectCount);
    }

    [Fact]
    public async Task Navigation_away_cancels_lifetime_and_releases_session_once()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var entered = RecipeManagementTestHarness.NewSignal();
        harness.Session.Handler = async (_, token) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return RecipeRunResults.Ok();
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await entered.Task.WaitAsync(CommandTimeout);
        harness.ViewModel.OnNavigatedFrom(null!);
        await running.WaitAsync(CommandTimeout);

        Assert.Equal(1, harness.Session.DisposeCount);
        Assert.Equal(1, harness.Channels.DisconnectCount);
        Assert.Contains("已取消", harness.ViewModel.StatusText);
    }

    [Fact]
    public async Task Navigation_cancel_wins_over_late_reset_exception()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var entered = RecipeManagementTestHarness.NewSignal();
        var cancellationObserved = RecipeManagementTestHarness.NewSignal();
        var allowResetException = RecipeManagementTestHarness.NewSignal();
        harness.Session.Handler = async (_, token) =>
        {
            entered.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult(true);
                await allowResetException.Task;
                throw new InspectionRunResetException();
            }

            return RecipeRunResults.Ok();
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await entered.Task.WaitAsync(CommandTimeout);
        harness.ViewModel.ResetTestRunCommand.Execute();
        await cancellationObserved.Task.WaitAsync(CommandTimeout);
        harness.ViewModel.OnNavigatedFrom(null!);
        allowResetException.TrySetResult(true);
        await running.WaitAsync(CommandTimeout);

        Assert.Single(harness.Session.Requests);
        Assert.Equal(1, harness.Session.DisposeCount);
        Assert.Contains("已取消", harness.ViewModel.StatusText);
    }
}
