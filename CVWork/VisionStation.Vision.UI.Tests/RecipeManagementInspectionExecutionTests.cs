using System.IO;
using VisionStation.Application;
using VisionStation.Client.ViewModels;
using VisionStation.Domain;
using VisionStation.Vision.UI.ViewModels;
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
    public async Task Deferred_refresh_retries_one_transient_read_and_applies_latest_recipe()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var runEntered = RecipeManagementTestHarness.NewSignal();
        var allowRun = new TaskCompletionSource<InspectionRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Session.Handler = (_, _) =>
        {
            runEntered.TrySetResult(true);
            return allowRun.Task;
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await runEntered.Task.WaitAsync(CommandTimeout);
        var readAttempt = 0;
        harness.Recipes.GetAsyncHandler = (_, current, _) =>
            Interlocked.Increment(ref readAttempt) == 1
                ? Task.FromException<Recipe?>(new IOException("transient-read-failure"))
                : Task.FromResult(current);
        harness.PublishRecipeChanged(RecipeWithVariable("DeferredRetry", "latest"));

        allowRun.TrySetResult(RecipeRunResults.Ok());
        await running.WaitAsync(CommandTimeout);
        await RecipeManagementTestHarness.WaitUntilAsync(() =>
            harness.ViewModel.RecipeVariables.Any(variable =>
                variable.Key == "DeferredRetry"));

        Assert.Equal(2, readAttempt);
    }

    [Fact]
    public async Task Navigation_cleanup_replay_retries_one_transient_read_after_return()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var runEntered = RecipeManagementTestHarness.NewSignal();
        harness.Session.Handler = async (_, token) =>
        {
            runEntered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return RecipeRunResults.Ok();
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await runEntered.Task.WaitAsync(CommandTimeout);
        var readAttempt = 0;
        harness.Recipes.GetAsyncHandler = (_, current, _) =>
            Interlocked.Increment(ref readAttempt) == 1
                ? Task.FromException<Recipe?>(new IOException("navigation-transient-read"))
                : Task.FromResult(current);
        harness.PublishRecipeChanged(RecipeWithVariable(
            "NavigationDeferredRetry",
            "latest"));

        harness.ViewModel.OnNavigatedFrom(null!);
        await running.WaitAsync(CommandTimeout);
        harness.ViewModel.OnNavigatedTo(null!);
        await RecipeManagementTestHarness.WaitUntilAsync(() =>
            harness.ViewModel.RecipeVariables.Any(variable =>
                variable.Key == "NavigationDeferredRetry"));

        Assert.Equal(2, readAttempt);
    }

    [Fact]
    public async Task Deferred_refresh_stops_after_one_retry_when_reads_keep_failing()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var runEntered = RecipeManagementTestHarness.NewSignal();
        var allowRun = new TaskCompletionSource<InspectionRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Session.Handler = (_, _) =>
        {
            runEntered.TrySetResult(true);
            return allowRun.Task;
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await runEntered.Task.WaitAsync(CommandTimeout);
        var secondReadFailed = RecipeManagementTestHarness.NewSignal();
        var thirdReadStarted = RecipeManagementTestHarness.NewSignal();
        var readAttempt = 0;
        harness.Recipes.GetAsyncHandler = (_, _, _) =>
        {
            var attempt = Interlocked.Increment(ref readAttempt);
            if (attempt == 2)
            {
                secondReadFailed.TrySetResult(true);
            }
            else if (attempt == 3)
            {
                thirdReadStarted.TrySetResult(true);
            }

            return Task.FromException<Recipe?>(new IOException($"read-failure-{attempt}"));
        };
        harness.PublishRecipeChanged(RecipeWithVariable("DeferredFailure", "latest"));

        allowRun.TrySetResult(RecipeRunResults.Ok());
        await running.WaitAsync(CommandTimeout);
        await secondReadFailed.Task.WaitAsync(CommandTimeout);

        var observed = await Task.WhenAny(
            thirdReadStarted.Task,
            Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(thirdReadStarted.Task, observed);
        Assert.Equal(2, readAttempt);
    }

    [Fact]
    public async Task Late_deferred_refresh_cannot_overwrite_newer_external_recipe()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var runEntered = RecipeManagementTestHarness.NewSignal();
        harness.Session.Handler = async (_, token) =>
        {
            runEntered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return RecipeRunResults.Ok();
        };

        Task running;
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(
                new ImmediateSynchronizationContext());
            running = harness.ViewModel.TestRunRecipeCommand.Execute();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        await runEntered.Task.WaitAsync(CommandTimeout);
        var staleRecipe = RecipeWithVariable("DeferredA", "A");
        harness.PublishRecipeChanged(staleRecipe);

        var staleReadStarted = RecipeManagementTestHarness.NewSignal();
        var staleRead = new TaskCompletionSource<Recipe?>();
        var readNumber = 0;
        harness.Recipes.GetAsyncHandler = (_, current, _) =>
        {
            if (Interlocked.Increment(ref readNumber) == 1)
            {
                staleReadStarted.TrySetResult(true);
                return staleRead.Task;
            }

            return Task.FromResult(current);
        };

        harness.ViewModel.OnNavigatedFrom(null!);
        await running.WaitAsync(CommandTimeout);
        await staleReadStarted.Task.WaitAsync(CommandTimeout);

        var latestRecipe = RecipeWithVariable("ExternalB", "B");
        harness.PublishRecipeChanged(latestRecipe);
        await RecipeManagementTestHarness.WaitUntilAsync(() =>
            harness.ViewModel.RecipeVariables.Any(variable => variable.Key == "ExternalB"));

        staleRead.SetResult(staleRecipe);

        Assert.Contains(
            harness.ViewModel.RecipeVariables,
            variable => variable.Key == "ExternalB");
        Assert.DoesNotContain(
            harness.ViewModel.RecipeVariables,
            variable => variable.Key == "DeferredA");
    }

    [Fact]
    public async Task Late_refresh_for_previous_recipe_cannot_overwrite_switched_page()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var staleRecipe = RecipeWithVariable("StaleRecipeOne", "stale");
        var staleReadStarted = RecipeManagementTestHarness.NewSignal();
        var staleRead = new TaskCompletionSource<Recipe?>();
        harness.Recipes.GetAsyncHandler = (recipeId, current, _) =>
        {
            if (recipeId == "recipe-1")
            {
                staleReadStarted.TrySetResult(true);
                return staleRead.Task;
            }

            return Task.FromResult(current);
        };

        harness.PublishRecipeChanged(staleRecipe);
        await staleReadStarted.Task.WaitAsync(CommandTimeout);

        var switchedRecipe = new Recipe
        {
            Id = "recipe-2",
            Name = "Recipe 2",
            ProductCode = "P-2",
            Variables =
            [
                new RecipeVariableDefinition
                {
                    Key = "CurrentRecipeTwo",
                    Name = "Current Recipe Two",
                    CurrentValue = "current"
                }
            ]
        };
        harness.Recipes.ReplaceCurrent(switchedRecipe);
        harness.ViewModel.SelectedRecipe = new RecipeListItem(
            switchedRecipe.Id,
            switchedRecipe.Name,
            switchedRecipe.ProductCode,
            1,
            0,
            0,
            string.Empty,
            false);
        await RecipeManagementTestHarness.WaitUntilAsync(() =>
            harness.ViewModel.RecipeId == switchedRecipe.Id);

        staleRead.SetResult(staleRecipe);

        Assert.Equal(switchedRecipe.Id, harness.ViewModel.RecipeId);
        Assert.Contains(
            harness.ViewModel.RecipeVariables,
            variable => variable.Key == "CurrentRecipeTwo");
        Assert.DoesNotContain(
            harness.ViewModel.RecipeVariables,
            variable => variable.Key == "StaleRecipeOne");
    }

    [Fact]
    public async Task Old_refresh_cannot_overwrite_newer_same_recipe_after_A_B_A_reload()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var staleRecipe = RecipeWithVariable("StaleA", "stale");
        var staleReadStarted = RecipeManagementTestHarness.NewSignal();
        var staleRead = new TaskCompletionSource<Recipe?>();
        var recipeARead = 0;
        harness.Recipes.GetAsyncHandler = (recipeId, current, _) =>
        {
            if (recipeId == "recipe-1" && Interlocked.Increment(ref recipeARead) == 1)
            {
                staleReadStarted.TrySetResult(true);
                return staleRead.Task;
            }

            return Task.FromResult(current?.Id == recipeId ? current : null);
        };

        harness.PublishRecipeChanged(staleRecipe);
        await staleReadStarted.Task.WaitAsync(CommandTimeout);

        var recipeB = new Recipe
        {
            Id = "recipe-2",
            Name = "Recipe B",
            ProductCode = "P-B",
            Variables =
            [
                new RecipeVariableDefinition
                {
                    Key = "RecipeB",
                    Name = "Recipe B",
                    CurrentValue = "B"
                }
            ]
        };
        harness.Recipes.ReplaceCurrent(recipeB);
        harness.ViewModel.SelectedRecipe = RecipeListItemFor(recipeB);
        await RecipeManagementTestHarness.WaitUntilAsync(() =>
            harness.ViewModel.RecipeId == recipeB.Id);

        var newerRecipeA = RecipeWithVariable("NewerA", "newer");
        harness.Recipes.ReplaceCurrent(newerRecipeA);
        harness.ViewModel.SelectedRecipe = RecipeListItemFor(newerRecipeA);
        await RecipeManagementTestHarness.WaitUntilAsync(() =>
            harness.ViewModel.RecipeVariables.Any(variable => variable.Key == "NewerA"));

        staleRead.SetResult(staleRecipe);

        Assert.Equal(newerRecipeA.Id, harness.ViewModel.RecipeId);
        Assert.Contains(
            harness.ViewModel.RecipeVariables,
            variable => variable.Key == "NewerA");
        Assert.DoesNotContain(
            harness.ViewModel.RecipeVariables,
            variable => variable.Key == "StaleA");
    }

    [Fact]
    public async Task EndRun_failure_does_not_fault_or_strand_test_run_state()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        harness.RunControl.EndRunHandler = () =>
            throw new InvalidOperationException("end-run-failure");

        var firstFailure = await Record.ExceptionAsync(() =>
            harness.ViewModel.TestRunRecipeCommand.Execute().WaitAsync(CommandTimeout));

        Assert.Null(firstFailure);
        Assert.False(harness.ViewModel.IsTestRunning);
        Assert.False(harness.ViewModel.IsTestRunPaused);
        Assert.Equal(1, harness.Session.DisposeCount);
        Assert.Equal(1, harness.Channels.DisconnectCount);
        Assert.True(harness.ViewModel.TestRunRecipeCommand.CanExecute());

        var secondFailure = await Record.ExceptionAsync(() =>
            harness.ViewModel.TestRunRecipeCommand.Execute().WaitAsync(CommandTimeout));

        Assert.Null(secondFailure);
        Assert.Equal(2, harness.Execution.TryBeginCount);
        Assert.Equal(2, harness.Session.DisposeCount);
    }

    [Fact]
    public async Task Disconnect_and_warning_sink_failures_preserve_truth_and_pending_replay()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var runEntered = RecipeManagementTestHarness.NewSignal();
        var allowRun = new TaskCompletionSource<InspectionRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Session.Handler = (_, _) =>
        {
            runEntered.TrySetResult(true);
            return allowRun.Task;
        };
        harness.Channels.DisconnectHandler = static (_, _) =>
            Task.FromException(new InvalidOperationException("disconnect-failure"));
        harness.Log.ThrowOnWarning = true;

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await runEntered.Task.WaitAsync(CommandTimeout);
        harness.PublishRecipeChanged(RecipeWithVariable("CleanupReplay", "latest"));
        allowRun.TrySetResult(RecipeRunResults.Ok());

        var failure = await Record.ExceptionAsync(() => running.WaitAsync(CommandTimeout));
        await RecipeManagementTestHarness.WaitUntilAsync(() =>
            harness.ViewModel.RecipeVariables.Any(variable => variable.Key == "CleanupReplay"));

        Assert.Null(failure);
        Assert.False(harness.ViewModel.IsTestRunning);
        Assert.False(harness.ViewModel.IsTestRunPaused);
        Assert.Equal(1, harness.Session.DisposeCount);
        Assert.True(harness.ViewModel.TestRunRecipeCommand.CanExecute());
    }

    [Fact]
    public async Task Flow_editor_failure_is_reported_without_faulting_async_command()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        harness.FlowEditor.Handler = static (_, _) =>
            Task.FromException(new InvalidOperationException("dialog-failure"));
        harness.Log.ThrowOnError = true;

        var failure = await Record.ExceptionAsync(() =>
            harness.ViewModel.OpenFlowEditorCommand.Execute().WaitAsync(CommandTimeout));

        Assert.Null(failure);
        Assert.Equal(
            "打开流程编辑器失败：dialog-failure",
            harness.ViewModel.StatusText);
    }

    [Fact]
    public async Task Running_recipe_is_frozen_across_reset_attempts()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var alternateRecipe = new RecipeListItem(
            "recipe-2",
            "Recipe 2",
            "P-2",
            1,
            0,
            0,
            string.Empty,
            false);
        var alternateFlow = new RecipeFlowSummaryItem(
            "alternate",
            "Alternate",
            0,
            0,
            string.Empty);
        harness.ViewModel.Recipes.Add(alternateRecipe);
        harness.ViewModel.FlowSummaries.Add(alternateFlow);
        var initialName = harness.ViewModel.RecipeName;
        var initialParameterCount = harness.ViewModel.ProductParameters.Count;
        var firstAttemptEntered = RecipeManagementTestHarness.NewSignal();
        var attempt = 0;
        harness.Session.Handler = async (_, token) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                firstAttemptEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return RecipeRunResults.Ok();
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await firstAttemptEntered.Task.WaitAsync(CommandTimeout);
        var runningRecipe = harness.ViewModel.SelectedRecipe;
        var runningFlow = harness.ViewModel.SelectedFlow;

        Assert.False(harness.ViewModel.SaveCommand.CanExecute());
        Assert.False(harness.ViewModel.SetCurrentRecipeCommand.CanExecute());
        Assert.False(harness.ViewModel.OpenFlowEditorCommand.CanExecute());
        Assert.False(harness.ViewModel.NewRecipeCommand.CanExecute());
        Assert.False(harness.ViewModel.DeleteRecipeCommand.CanExecute());
        Assert.False(harness.ViewModel.ReloadCommand.CanExecute());
        Assert.False(harness.ViewModel.AddProductParameterCommand.CanExecute());

        harness.ViewModel.RecipeName = "Mutated While Running";
        harness.ViewModel.SelectedFlow = alternateFlow;
        harness.ViewModel.SelectedRecipe = alternateRecipe;
        harness.ViewModel.AddProductParameterCommand.Execute();

        Assert.Equal(initialName, harness.ViewModel.RecipeName);
        Assert.Same(runningFlow, harness.ViewModel.SelectedFlow);
        Assert.Same(runningRecipe, harness.ViewModel.SelectedRecipe);
        Assert.Equal(initialParameterCount, harness.ViewModel.ProductParameters.Count);

        harness.ViewModel.ResetTestRunCommand.Execute();
        await running.WaitAsync(CommandTimeout);

        Assert.Equal(2, harness.Session.Requests.Count);
        Assert.All(
            harness.Session.Requests,
            request => Assert.Equal("recipe-1", request.RecipeId));
        var saved = Assert.Single(harness.Recipes.SavedRecipes);
        Assert.Equal(initialName, saved.Name);
        Assert.All(
            harness.Session.Requests,
            request =>
            {
                var snapshot = Assert.IsType<Recipe>(request.RecipeSnapshot);
                Assert.Equal(request.RecipeId, snapshot.Id, ignoreCase: true);
                Assert.Equal(initialName, snapshot.Name);
            });
        Assert.Equal(1, harness.Recipes.SaveCount);
        Assert.Equal(1, harness.Recipes.SetCurrentCount);
    }

    [Fact]
    public async Task Recipe_editability_notifies_when_test_run_starts_and_finishes()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var editability = typeof(RecipeManagementViewModel).GetProperty(
            "IsRecipeEditingEnabled");
        Assert.NotNull(editability);
        Assert.True(Assert.IsType<bool>(editability.GetValue(harness.ViewModel)));
        var notifications = 0;
        harness.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == "IsRecipeEditingEnabled")
            {
                Interlocked.Increment(ref notifications);
            }
        };
        var runEntered = RecipeManagementTestHarness.NewSignal();
        var allowRun = new TaskCompletionSource<InspectionRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Session.Handler = (_, _) =>
        {
            runEntered.TrySetResult(true);
            return allowRun.Task;
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await runEntered.Task.WaitAsync(CommandTimeout);
        Assert.False(Assert.IsType<bool>(editability.GetValue(harness.ViewModel)));

        allowRun.TrySetResult(RecipeRunResults.Ok());
        await running.WaitAsync(CommandTimeout);

        Assert.True(Assert.IsType<bool>(editability.GetValue(harness.ViewModel)));
        Assert.Equal(2, Volatile.Read(ref notifications));

        var beforeReload = Volatile.Read(ref notifications);
        harness.ViewModel.ReloadCommand.Execute();
        await RecipeManagementTestHarness.WaitUntilAsync(() =>
            !harness.ViewModel.IsBusy &&
            Volatile.Read(ref notifications) >= beforeReload + 2);
        Assert.True(Assert.IsType<bool>(editability.GetValue(harness.ViewModel)));
    }

    [Fact]
    public async Task Reset_attempt_uses_frozen_recipe_snapshot_with_defaults_without_resaving()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        harness.PublishRecipeChanged(RecipeWithVariable(
            "ResetVariable",
            "current-before-run",
            "default-after-reset"));
        await RecipeManagementTestHarness.WaitUntilAsync(() =>
            harness.ViewModel.RecipeVariables.Any(variable =>
                variable.Key == "ResetVariable" &&
                variable.CurrentValue == "current-before-run"));
        var firstAttemptEntered = RecipeManagementTestHarness.NewSignal();
        var attempt = 0;
        harness.Session.Handler = async (_, token) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                firstAttemptEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return RecipeRunResults.Ok();
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await firstAttemptEntered.Task.WaitAsync(CommandTimeout);
        harness.ViewModel.ResetTestRunCommand.Execute();
        await running.WaitAsync(CommandTimeout);

        var saved = Assert.Single(harness.Recipes.SavedRecipes);
        Assert.Equal(
            "current-before-run",
            Assert.Single(saved.Variables).CurrentValue);

        Assert.Equal(2, harness.Session.Requests.Count);
        var firstSnapshot = Assert.IsType<Recipe>(
            harness.Session.Requests[0].RecipeSnapshot);
        var secondSnapshot = Assert.IsType<Recipe>(
            harness.Session.Requests[1].RecipeSnapshot);
        Assert.Equal(
            "current-before-run",
            Assert.Single(firstSnapshot.Variables).CurrentValue);
        Assert.Equal(
            "default-after-reset",
            Assert.Single(secondSnapshot.Variables).CurrentValue);
        Assert.All(
            harness.Session.Requests,
            request => Assert.Equal(
                request.RecipeId,
                request.RecipeSnapshot!.Id,
                ignoreCase: true));
        Assert.Equal(1, harness.Recipes.SaveCount);
        Assert.Equal(1, harness.Recipes.SetCurrentCount);
        Assert.Equal(1, harness.Execution.TryBeginCount);
        Assert.Equal(1, harness.Session.DisposeCount);
    }

    [Fact]
    public async Task Reset_cancel_does_not_run_blocking_callbacks_on_caller()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var runEntered = RecipeManagementTestHarness.NewSignal();
        var callbackEntered = RecipeManagementTestHarness.NewSignal();
        var releaseCallback = RecipeManagementTestHarness.NewSignal();
        var attempt = 0;
        harness.RunControl.RequestResetHandler = () =>
            throw new InvalidOperationException("request-reset-failure");
        harness.Session.Handler = async (_, token) =>
        {
            if (Interlocked.Increment(ref attempt) != 1)
            {
                return RecipeRunResults.Ok();
            }

            using var registration = token.Register(() =>
            {
                callbackEntered.TrySetResult(true);
                releaseCallback.Task.GetAwaiter().GetResult();
            });
            runEntered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return RecipeRunResults.Ok();
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await runEntered.Task.WaitAsync(CommandTimeout);
        var resetCall = Task.Run(() => harness.ViewModel.ResetTestRunCommand.Execute());
        try
        {
            await callbackEntered.Task.WaitAsync(CommandTimeout);
            await resetCall.WaitAsync(CommandTimeout);
        }
        finally
        {
            releaseCallback.TrySetResult(true);
        }

        await resetCall.WaitAsync(CommandTimeout);
        await running.WaitAsync(CommandTimeout);
        Assert.Equal(2, harness.Session.Requests.Count);
    }

    [Fact]
    public async Task Navigation_cancel_unsubscribes_before_blocking_callbacks_finish()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var runEntered = RecipeManagementTestHarness.NewSignal();
        var callbackEntered = RecipeManagementTestHarness.NewSignal();
        var releaseCallback = RecipeManagementTestHarness.NewSignal();
        harness.Session.Handler = async (_, token) =>
        {
            using var registration = token.Register(() =>
            {
                callbackEntered.TrySetResult(true);
                releaseCallback.Task.GetAwaiter().GetResult();
            });
            runEntered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return RecipeRunResults.Ok();
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await runEntered.Task.WaitAsync(CommandTimeout);
        var navigationCall = Task.Run(() => harness.ViewModel.OnNavigatedFrom(null!));
        try
        {
            await callbackEntered.Task.WaitAsync(CommandTimeout);
            await navigationCall.WaitAsync(CommandTimeout);
            Assert.Equal(0, harness.Execution.ChangedSubscriberCount);
            Assert.False(running.IsCompleted);
        }
        finally
        {
            releaseCallback.TrySetResult(true);
        }

        await navigationCall.WaitAsync(CommandTimeout);
        await running.WaitAsync(CommandTimeout);
    }

    private static Recipe RecipeWithVariable(
        string key,
        string value,
        string defaultValue = "") =>
        new()
        {
            Id = "recipe-1",
            Name = $"Recipe {key}",
            ProductCode = "P-1",
            Variables =
            [
                new RecipeVariableDefinition
                {
                    Key = key,
                    Name = key,
                    DefaultValue = defaultValue,
                    CurrentValue = value
                }
            ]
        };

    private static RecipeListItem RecipeListItemFor(Recipe recipe) =>
        new(
            recipe.Id,
            recipe.Name,
            recipe.ProductCode,
            recipe.EffectiveFlows.Count,
            recipe.GetActiveFlow().Tools.Count,
            recipe.GetActiveFlow().Rois.Count,
            string.Empty,
            false);

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
    public async Task Pause_is_unavailable_during_initial_persist_and_enabled_for_active_attempt()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var saveEntered = RecipeManagementTestHarness.NewSignal();
        var allowSave = RecipeManagementTestHarness.NewSignal();
        harness.Recipes.SaveHandler = async (_, _) =>
        {
            saveEntered.TrySetResult(true);
            await allowSave.Task;
        };
        var attemptEntered = RecipeManagementTestHarness.NewSignal();
        var allowAttempt = new TaskCompletionSource<InspectionRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Session.Handler = (_, token) =>
        {
            attemptEntered.TrySetResult(true);
            return allowAttempt.Task.WaitAsync(token);
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await saveEntered.Task.WaitAsync(CommandTimeout);

        var canPauseDuringPersist =
            harness.ViewModel.PauseTestRunCommand.CanExecute();
        harness.ViewModel.PauseTestRunCommand.Execute();
        var pausedDuringPersist = harness.ViewModel.IsTestRunPaused;
        var pauseCountDuringPersist = harness.RunControl.PauseCount;

        allowSave.TrySetResult(true);
        await attemptEntered.Task.WaitAsync(CommandTimeout);
        var canPauseDuringAttempt =
            harness.ViewModel.PauseTestRunCommand.CanExecute();
        allowAttempt.TrySetResult(RecipeRunResults.Ok());
        await running.WaitAsync(CommandTimeout);

        Assert.False(canPauseDuringPersist);
        Assert.False(pausedDuringPersist);
        Assert.Equal(0, pauseCountDuringPersist);
        Assert.True(canPauseDuringAttempt);
        Assert.Equal(0, harness.RunControl.PauseCount);
    }

    [Fact]
    public async Task Reset_during_initial_persist_is_latched_without_cancelling_or_resaving()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        harness.PublishRecipeChanged(RecipeWithVariable(
            "LatchedReset",
            "current-before-reset",
            "default-after-reset"));
        await RecipeManagementTestHarness.WaitUntilAsync(() =>
            harness.ViewModel.RecipeVariables.Any(variable =>
                variable.Key == "LatchedReset"));

        var saveEntered = RecipeManagementTestHarness.NewSignal();
        var allowSave = RecipeManagementTestHarness.NewSignal();
        CancellationToken saveToken = default;
        harness.Recipes.SaveHandler = async (_, token) =>
        {
            saveToken = token;
            saveEntered.TrySetResult(true);
            await allowSave.Task;
            token.ThrowIfCancellationRequested();
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await saveEntered.Task.WaitAsync(CommandTimeout);
        harness.ViewModel.ResetTestRunCommand.Execute();
        var saveWasCancelled = saveToken.IsCancellationRequested;
        var beginCountDuringPersist = harness.RunControl.BeginCount;
        var resetCountDuringPersist = harness.RunControl.RequestResetCount;
        allowSave.TrySetResult(true);
        Assert.False(saveWasCancelled);
        await running.WaitAsync(CommandTimeout);

        Assert.Equal(0, beginCountDuringPersist);
        Assert.Equal(0, resetCountDuringPersist);
        Assert.Equal(1, harness.Execution.TryBeginCount);
        Assert.Equal(1, harness.Recipes.SaveCount);
        Assert.Equal(1, harness.Recipes.SetCurrentCount);
        var saved = Assert.Single(harness.Recipes.SavedRecipes);
        Assert.Equal(
            "current-before-reset",
            Assert.Single(saved.Variables).CurrentValue);
        Assert.Single(harness.Session.Requests);
        Assert.Equal(1, harness.RunControl.BeginCount);
        var snapshot = Assert.IsType<Recipe>(
            Assert.Single(harness.Session.Requests).RecipeSnapshot);
        var resetVariable = Assert.Single(snapshot.Variables);
        Assert.Equal(resetVariable.DefaultValue, resetVariable.CurrentValue);
        Assert.Equal(0, harness.RunControl.RequestResetCount);
        Assert.Equal(1, harness.Session.DisposeCount);
    }

    [Fact]
    public async Task Early_reset_then_navigation_cancels_persist_without_touching_run_control()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        var saveEntered = RecipeManagementTestHarness.NewSignal();
        var allowSave = RecipeManagementTestHarness.NewSignal();
        CancellationToken saveToken = default;
        harness.Recipes.SaveHandler = async (_, token) =>
        {
            saveToken = token;
            saveEntered.TrySetResult(true);
            await allowSave.Task;
            token.ThrowIfCancellationRequested();
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await saveEntered.Task.WaitAsync(CommandTimeout);
        harness.ViewModel.ResetTestRunCommand.Execute();
        var resetCountDuringPersist = harness.RunControl.RequestResetCount;

        harness.ViewModel.OnNavigatedFrom(null!);
        var lifetimeSaveWasCancelled = saveToken.IsCancellationRequested;
        allowSave.TrySetResult(true);
        await running.WaitAsync(CommandTimeout);

        Assert.True(lifetimeSaveWasCancelled);
        Assert.Equal(0, resetCountDuringPersist);
        Assert.Empty(harness.Session.Requests);
        Assert.Equal(0, harness.Channels.ConnectCount);
        Assert.Equal(0, harness.RunControl.BeginCount);
        Assert.Equal(0, harness.RunControl.EndCount);
        Assert.Equal(0, harness.RunControl.PauseCount);
        Assert.Equal(0, harness.RunControl.ResumeCount);
        Assert.Equal(0, harness.RunControl.RequestResetCount);
        Assert.False(harness.RunControl.IsPaused);
        Assert.False(harness.ViewModel.IsTestRunning);
        Assert.False(harness.ViewModel.IsTestRunPaused);
        Assert.Equal(1, harness.Session.DisposeCount);
    }

    [Fact]
    public async Task Reset_does_not_overwrite_external_recipe_saved_after_initial_persist()
    {
        await using var harness = await RecipeManagementTestHarness.CreateAsync();
        harness.PublishRecipeChanged(RecipeWithVariable(
            "InitialFrozen",
            "current-before-reset",
            "default-after-reset"));
        await RecipeManagementTestHarness.WaitUntilAsync(() =>
            harness.ViewModel.RecipeVariables.Any(variable =>
                variable.Key == "InitialFrozen"));
        var initialRecipeName = harness.ViewModel.RecipeName;
        var firstAttemptEntered = RecipeManagementTestHarness.NewSignal();
        var attempt = 0;
        harness.Session.Handler = async (_, token) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                firstAttemptEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return RecipeRunResults.Ok();
        };

        var running = harness.ViewModel.TestRunRecipeCommand.Execute();
        await firstAttemptEntered.Task.WaitAsync(CommandTimeout);

        var external = RecipeWithVariable("ExternalAfterSave", "external");
        harness.PublishRecipeChanged(external);
        harness.ViewModel.ResetTestRunCommand.Execute();
        await running.WaitAsync(CommandTimeout);

        var stored = await harness.Recipes.GetAsync("recipe-1");
        Assert.NotNull(stored);
        Assert.Contains(
            stored!.Variables,
            variable =>
                variable.Key == "ExternalAfterSave" &&
                variable.CurrentValue == "external");
        await RecipeManagementTestHarness.WaitUntilAsync(() =>
            harness.ViewModel.RecipeVariables.Any(variable =>
                variable.Key == "ExternalAfterSave" &&
                variable.CurrentValue == "external"));
        Assert.Equal(1, harness.Recipes.SaveCount);
        Assert.Equal(2, harness.Session.Requests.Count);
        var firstSnapshot = Assert.IsType<Recipe>(
            harness.Session.Requests[0].RecipeSnapshot);
        var secondSnapshot = Assert.IsType<Recipe>(
            harness.Session.Requests[1].RecipeSnapshot);
        Assert.All(
            new[] { firstSnapshot, secondSnapshot },
            snapshot =>
            {
                Assert.Equal("recipe-1", snapshot.Id);
                Assert.Equal(initialRecipeName, snapshot.Name);
                Assert.Equal("P-1", snapshot.ProductCode);
                Assert.DoesNotContain(
                    snapshot.Variables,
                    variable => variable.Key == "ExternalAfterSave");
                Assert.Equal(
                    firstSnapshot.EffectiveFlows.Select(flow => flow.Id),
                    snapshot.EffectiveFlows.Select(flow => flow.Id));
            });
        Assert.Equal(
            "current-before-reset",
            Assert.Single(firstSnapshot.Variables).CurrentValue);
        Assert.Equal(
            "default-after-reset",
            Assert.Single(secondSnapshot.Variables).CurrentValue);
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
