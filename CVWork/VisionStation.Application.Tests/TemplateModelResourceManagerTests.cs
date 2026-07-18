using System.Reflection;
using System.Text.Json;
using VisionStation.Application.Recipes;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class TemplateModelResourceManagerTests
{
    [Fact]
    public void CopySessionBaseOwnsNonVirtualCommitAndDisposeStateMachine()
    {
        var commit = typeof(TemplateRecipeCopySession).GetMethod(
            nameof(TemplateRecipeCopySession.CommitAsync))!;
        var dispose = typeof(TemplateRecipeCopySession).GetMethod(
            nameof(TemplateRecipeCopySession.DisposeAsync))!;

        Assert.False(commit.IsVirtual);
        Assert.True(!dispose.IsVirtual || dispose.IsFinal);
    }

    [Fact]
    public void ResourceManagerSupportsLegacyApplicationLogAndNeutralDiagnosticSink()
    {
        Type[][] signatures = typeof(TemplateModelResourceManager)
            .GetConstructors(System.Reflection.BindingFlags.Instance |
                             System.Reflection.BindingFlags.NonPublic)
            .Where(constructor => constructor.IsAssembly)
            .Select(constructor => constructor
                .GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray())
            .ToArray();

        Assert.Contains(
            [typeof(ITemplateModelStore), typeof(ITemplateModelRetirementSink), typeof(IAppLogService)],
            signatures);
        Assert.Contains(
            [typeof(ITemplateModelStore), typeof(ITemplateModelRetirementSink), typeof(ITemplateMatchingDiagnosticSink)],
            signatures);
    }

    [Fact]
    public async Task CommitDefersUnexpectedReleaseHookThrowAndDisposeRetriesIt()
    {
        var session = new ThrowingReleaseCopySession(Task7Fixtures.CreateSingleModelRecipe());

        await session.CommitAsync(CancellationToken.None);
        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(2, session.ReleaseAttempts);
        Assert.False(session.RolledBack);
    }

    [Fact]
    public async Task PrepareCopiesEveryCompleteReferenceIncludingInactiveEngineWithoutMutatingSource()
    {
        var source = Task7Fixtures.CreateSourceRecipe();
        var originalActive = Task7Fixtures.ReadState(source, "flow-a", "active-halcon");
        var originalInactive = Task7Fixtures.ReadState(source, "flow-b", "inactive-halcon");
        var store = new RecordingTemplateModelStore();
        var manager = new TemplateModelResourceManager(store, new RecordingRetirementSink(store.Events), new RecordingLog());

        await using var session = await manager.PrepareRecipeCopyAsync(source, "recipe-copy", default);

        Assert.Equal("recipe-copy", session.Recipe.Id);
        Assert.Equal(2, store.CopyCalls.Count);
        Assert.Contains(store.CopyCalls, call =>
            call.SourceOwner == new TemplateModelOwner("recipe-source", "flow-a", "active-halcon") &&
            call.TargetOwner == new TemplateModelOwner("recipe-copy", "flow-a", "active-halcon"));
        Assert.Contains(store.CopyCalls, call =>
            call.SourceOwner == new TemplateModelOwner("recipe-source", "flow-b", "inactive-halcon") &&
            call.TargetOwner == new TemplateModelOwner("recipe-copy", "flow-b", "inactive-halcon"));

        var copiedActive = Task7Fixtures.ReadState(session.Recipe, "flow-a", "active-halcon");
        var copiedInactive = Task7Fixtures.ReadState(session.Recipe, "flow-b", "inactive-halcon");
        Assert.NotEqual(originalActive.Reference.ModelPath, copiedActive.Reference.ModelPath);
        Assert.NotEqual(originalInactive.Reference.ModelPath, copiedInactive.Reference.ModelPath);
        Assert.Equal(originalActive.Geometry, copiedActive.Geometry);
        Assert.Equal(originalInactive.Geometry, copiedInactive.Geometry);
        Assert.Equal(new string('1', 64), copiedActive.Reference.ModelChecksum);
        Assert.Equal(new string('2', 64), copiedActive.Reference.MetadataChecksum);
        Assert.Equal("copy-1", copiedActive.Reference.Generation);
        Assert.EndsWith("model-1.shm", copiedActive.Reference.ModelPath, StringComparison.Ordinal);
        Assert.EndsWith("model-1.json", copiedActive.Reference.MetadataPath, StringComparison.Ordinal);
        Assert.Equal("OpenCv", Task7Fixtures.GetTool(session.Recipe, "flow-b", "inactive-halcon").Parameters["engine"]);
        Assert.Equal("preserve", Task7Fixtures.GetTool(session.Recipe, "flow-b", "inactive-halcon").Parameters["halcon.futureExtension"]);
        Assert.DoesNotContain(store.CopyCalls, call => call.SourceOwner.ToolId == "plain-tool");
        Assert.NotSame(
            Task7Fixtures.GetTool(source, "flow-a", "active-halcon").Parameters,
            Task7Fixtures.GetTool(session.Recipe, "flow-a", "active-halcon").Parameters);
        Assert.Equal(originalActive, Task7Fixtures.ReadState(source, "flow-a", "active-halcon"));
        Assert.Equal(originalInactive, Task7Fixtures.ReadState(source, "flow-b", "inactive-halcon"));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await session.CommitAsync(cancelled.Token);
    }

    [Fact]
    public async Task PrepareClearsSourceStorageRevisionForNewRecipe()
    {
        var source = Task7Fixtures.CreateSourceRecipe() with
        {
            StorageRevision = "v1-source-revision"
        };
        var store = new RecordingTemplateModelStore();
        var manager = new TemplateModelResourceManager(
            store,
            new RecordingRetirementSink(store.Events),
            new RecordingLog());

        await using var session = await manager.PrepareRecipeCopyAsync(
            source,
            "recipe-copy",
            default);

        Assert.Empty(session.Recipe.StorageRevision);
        Assert.Equal("v1-source-revision", source.StorageRevision);
    }

    [Fact]
    public async Task PrepareCreatesIndependentCloneOfCompleteRecipeGraph()
    {
        var source = Task7Fixtures.CreateMutableGraphRecipe();
        var store = new RecordingTemplateModelStore();
        var manager = new TemplateModelResourceManager(
            store,
            new RecordingRetirementSink(store.Events),
            new RecordingLog());

        await using var session = await manager.PrepareRecipeCopyAsync(
            source,
            "recipe-copy",
            default);
        var copy = session.Recipe;
        var sourceFlow = source.EffectiveFlows.Single();
        var copiedFlow = copy.EffectiveFlows.Single();

        Assert.NotSame(source.Camera, copy.Camera);
        Assert.NotSame(source.TracePolicy, copy.TracePolicy);
        Assert.NotSame(source.Camera.CameraCalibration, copy.Camera.CameraCalibration);
        Assert.NotSame(
            source.Camera.CameraCalibration!.CameraMatrix,
            copy.Camera.CameraCalibration!.CameraMatrix);
        Assert.NotSame(
            source.Camera.CameraCalibration.DistortionCoefficients,
            copy.Camera.CameraCalibration.DistortionCoefficients);
        Assert.NotSame(source.Camera.CameraCalibration.Pattern, copy.Camera.CameraCalibration.Pattern);
        Assert.NotSame(source.Camera.CameraCalibration.Views, copy.Camera.CameraCalibration.Views);
        Assert.NotSame(
            source.Camera.CameraCalibration.Views[0],
            copy.Camera.CameraCalibration.Views[0]);
        Assert.NotSame(
            source.Camera.CameraCalibration.Views[0].RotationVector,
            copy.Camera.CameraCalibration.Views[0].RotationVector);
        Assert.NotSame(
            source.Camera.CameraCalibration.Views[0].TranslationVector,
            copy.Camera.CameraCalibration.Views[0].TranslationVector);
        Assert.NotSame(source.Camera.PlaneCalibration, copy.Camera.PlaneCalibration);
        Assert.NotSame(
            source.Camera.PlaneCalibration!.ImageToWorldMatrix,
            copy.Camera.PlaneCalibration!.ImageToWorldMatrix);
        Assert.NotSame(
            source.Camera.PlaneCalibration.WorldToImageMatrix,
            copy.Camera.PlaneCalibration.WorldToImageMatrix);
        Assert.NotSame(
            source.Camera.PlaneCalibration.PointErrors,
            copy.Camera.PlaneCalibration.PointErrors);
        Assert.NotSame(
            source.Camera.PlaneCalibration.PointErrors[0],
            copy.Camera.PlaneCalibration.PointErrors[0]);
        Assert.NotSame(
            source.Camera.PlaneCalibration.PointErrors[0].ImagePoint,
            copy.Camera.PlaneCalibration.PointErrors[0].ImagePoint);

        AssertIndependentList(source.ProductParameters, copy.ProductParameters);
        AssertIndependentList(source.Variables, copy.Variables);
        AssertIndependentList(source.SignalMappings, copy.SignalMappings);
        AssertIndependentList(source.Flows, copy.Flows);
        AssertIndependentList(sourceFlow.Rois, copiedFlow.Rois);
        AssertIndependentList(sourceFlow.Rois[0].Points, copiedFlow.Rois[0].Points);
        AssertIndependentList(sourceFlow.Tools, copiedFlow.Tools);
        Assert.NotSame(sourceFlow.Tools[0].Parameters, copiedFlow.Tools[0].Parameters);
        AssertIndependentList(source.MotionSequences, copy.MotionSequences);
        AssertIndependentList(source.MotionSequences[0].Steps, copy.MotionSequences[0].Steps);
        Assert.NotSame(
            source.MotionSequences[0].Steps[0].Parameters,
            copy.MotionSequences[0].Steps[0].Parameters);
        AssertIndependentList(source.ProcessSteps, copy.ProcessSteps);
        AssertIndependentList(
            source.ProcessSteps[0].AxisTargets,
            copy.ProcessSteps[0].AxisTargets);
        Assert.NotSame(source.ProcessSteps[0].Parameters, copy.ProcessSteps[0].Parameters);
        AssertIndependentList(source.VisionResults, copy.VisionResults);
        AssertIndependentList(source.PlcSignals, copy.PlcSignals);
        Assert.Same(copiedFlow.Rois, copy.Rois);
        Assert.Same(copiedFlow.Tools, copy.Tools);

        copy.Camera.CameraCalibration.CameraMatrix[0] = 101;
        copy.Camera.CameraCalibration.Views[0].RotationVector[0] = 102;
        copy.Camera.PlaneCalibration.ImageToWorldMatrix[0] = 103;
        copiedFlow.Tools[0].Parameters["plain"] = "copy";
        copy.MotionSequences[0].Steps[0].Parameters["motion"] = "copy";
        copy.ProcessSteps[0].Parameters["process"] = "copy";

        Assert.Equal(1, source.Camera.CameraCalibration.CameraMatrix[0]);
        Assert.Equal(4, source.Camera.CameraCalibration.Views[0].RotationVector[0]);
        Assert.Equal(6, source.Camera.PlaneCalibration.ImageToWorldMatrix[0]);
        Assert.Equal("source", sourceFlow.Tools[0].Parameters["plain"]);
        Assert.Equal("source", source.MotionSequences[0].Steps[0].Parameters["motion"]);
        Assert.Equal("source", source.ProcessSteps[0].Parameters["process"]);
    }

    private static void AssertIndependentList<T>(
        IReadOnlyList<T> source,
        IReadOnlyList<T> copy)
        where T : class
    {
        Assert.NotSame(source, copy);
        Assert.Equal(source.Count, copy.Count);
        Assert.All(source.Zip(copy), pair => Assert.NotSame(pair.First, pair.Second));
    }

    [Fact]
    public async Task PrepareFailureOnSecondCopyRollsBackOnlyCompletedGeneration()
    {
        var store = new RecordingTemplateModelStore { FailCopyNumber = 2 };
        var manager = new TemplateModelResourceManager(store, new RecordingRetirementSink(store.Events), new RecordingLog());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.PrepareRecipeCopyAsync(Task7Fixtures.CreateSourceRecipe(), "recipe-copy", default));

        Assert.Equal(2, store.CopyCalls.Count);
        var deleted = Assert.Single(store.DeletedGenerations);
        Assert.Equal(
            new TemplateModelOwner("recipe-copy", "flow-a", "active-halcon"),
            deleted.Owner);
        Assert.Equal("copy-1", deleted.Reference.Generation);
        Assert.Empty(store.DeletedOwners);
    }

    [Fact]
    public async Task GenerationCleanupCancellationIsAggregatedWithPrimaryCopyFailure()
    {
        var store = new RecordingTemplateModelStore
        {
            FailCopyNumber = 2,
            CancelDeleteGeneration = true
        };
        var manager = new TemplateModelResourceManager(
            store,
            new RecordingRetirementSink(store.Events),
            new RecordingLog());

        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            manager.PrepareRecipeCopyAsync(
                Task7Fixtures.CreateSourceRecipe(),
                "recipe-copy",
                default));

        Assert.Collection(
            failure.InnerExceptions,
            primary => Assert.IsType<InvalidOperationException>(primary),
            cleanup => Assert.IsType<OperationCanceledException>(cleanup));
    }

    [Fact]
    public async Task CopyCancellationRemainsUnwrappedWhenRollbackAlsoFails()
    {
        var store = new RecordingTemplateModelStore
        {
            CancelCopyNumber = 2,
            FailDeleteGeneration = true
        };
        var log = new RecordingLog();
        var manager = new TemplateModelResourceManager(
            store,
            new RecordingRetirementSink(store.Events),
            log);

        var cancellation = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            manager.PrepareRecipeCopyAsync(
                Task7Fixtures.CreateSourceRecipe(),
                "recipe-copy",
                default));

        Assert.Same(store.LastCopyCancellation, cancellation);
        var warning = Assert.Single(log.Warnings);
        Assert.Contains("recipe-copy", warning, StringComparison.Ordinal);
        Assert.Contains("flow-a", warning, StringComparison.Ordinal);
        Assert.Contains("active-halcon", warning, StringComparison.Ordinal);
        Assert.Contains("copy-1", warning, StringComparison.Ordinal);
        Assert.Contains("Injected generation cleanup failure.", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoreCopyCancellationMergesCurrentAndEarlierOrphansBeforeRethrowingSameInstance()
    {
        var store = new RecordingTemplateModelStore
        {
            CancelCopyWithCleanupNumber = 2,
            FailDeleteGeneration = true
        };
        var log = new RecordingLog();
        var manager = new TemplateModelResourceManager(
            store,
            new RecordingRetirementSink(store.Events),
            log);

        var cancellation = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            manager.PrepareRecipeCopyAsync(
                Task7Fixtures.CreateSourceRecipe(),
                "recipe-copy",
                default));

        Assert.Same(store.LastCopyCancellation, cancellation);
        Assert.Equal(2, log.Warnings.Count);
        Assert.Contains(
            log.Warnings,
            warning =>
                warning.Contains("recipe-copy/flow-b/inactive-halcon", StringComparison.Ordinal) &&
                warning.Contains("partial-copy-2", StringComparison.Ordinal) &&
                warning.Contains("Injected partial copy cleanup failure.", StringComparison.Ordinal));
        Assert.Contains(
            log.Warnings,
            warning =>
                warning.Contains("recipe-copy/flow-a/active-halcon", StringComparison.Ordinal) &&
                warning.Contains("copy-1", StringComparison.Ordinal) &&
                warning.Contains("Injected generation cleanup failure.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StoreCopyFailureMergesExactOrphansWithoutLosingPrimaryException()
    {
        var store = new RecordingTemplateModelStore
        {
            FailCopyWithCleanupNumber = 2,
            FailDeleteGeneration = true
        };
        var manager = new TemplateModelResourceManager(
            store,
            new RecordingRetirementSink(store.Events),
            new RecordingLog());

        var cleanupFailure = await Assert.ThrowsAsync<TemplateModelGenerationCleanupException>(() =>
            manager.PrepareRecipeCopyAsync(
                Task7Fixtures.CreateSourceRecipe(),
                "recipe-copy",
                default));

        Assert.Same(store.LastCopyFailure, cleanupFailure.PrimaryException);
        Assert.Collection(
            cleanupFailure.Failures,
            currentFailure =>
            {
                Assert.Equal(
                    new TemplateModelOwner("recipe-copy", "flow-b", "inactive-halcon"),
                    currentFailure.Owner);
                Assert.Equal("partial-copy-2", currentFailure.Generation);
                Assert.Equal(
                    "Injected partial copy cleanup failure.",
                    currentFailure.CleanupException.Message);
            },
            earlierFailure =>
            {
                Assert.Equal(
                    new TemplateModelOwner("recipe-copy", "flow-a", "active-halcon"),
                    earlierFailure.Owner);
                Assert.Equal("copy-1", earlierFailure.Generation);
                Assert.Equal(
                    "Injected generation cleanup failure.",
                    earlierFailure.CleanupException.Message);
            });
    }

    [Fact]
    public async Task PartialStateAfterEarlierCopyFailsClosedAndRollsBackCopiedOwner()
    {
        var source = Task7Fixtures.CreateSourceRecipe();
        Task7Fixtures.GetTool(source, "flow-b", "inactive-halcon")
            .Parameters.Remove("halcon.modelChecksum");
        var store = new RecordingTemplateModelStore();
        var manager = new TemplateModelResourceManager(store, new RecordingRetirementSink(store.Events), new RecordingLog());

        var error = await Assert.ThrowsAsync<TemplateMatchingConfigurationException>(() =>
            manager.PrepareRecipeCopyAsync(source, "recipe-copy", default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, error.Code);
        Assert.Single(store.CopyCalls);
        var deleted = Assert.Single(store.DeletedGenerations);
        Assert.Equal(
            new TemplateModelOwner("recipe-copy", "flow-a", "active-halcon"),
            deleted.Owner);
        Assert.Empty(store.DeletedOwners);
    }

    [Fact]
    public async Task UncommittedDisposeCleansResourcesButCommittedDisposeKeepsThem()
    {
        var store = new RecordingTemplateModelStore();
        var manager = new TemplateModelResourceManager(store, new RecordingRetirementSink(store.Events), new RecordingLog());
        var source = Task7Fixtures.CreateSingleModelRecipe();

        var abandoned = await manager.PrepareRecipeCopyAsync(source, "abandoned-copy", default);
        await abandoned.DisposeAsync();
        Assert.Equal(
            new TemplateModelOwner("abandoned-copy", "flow-a", "active-halcon"),
            Assert.Single(store.DeletedGenerations).Owner);
        Assert.Empty(store.DeletedOwners);

        var retained = await manager.PrepareRecipeCopyAsync(source, "retained-copy", default);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await retained.CommitAsync(cancelled.Token);
        await retained.DisposeAsync();

        Assert.Single(store.DeletedGenerations);
    }

    [Fact]
    public async Task FailedDisposeRetriesEveryExactGenerationAfterPartialCleanupAndRejectsCommit()
    {
        var store = new RecordingTemplateModelStore
        {
            DeleteGenerationFailuresRemaining = 1
        };
        var manager = new TemplateModelResourceManager(
            store,
            new RecordingRetirementSink(store.Events),
            new RecordingLog());
        var source = Task7Fixtures.CreateSourceRecipe();
        var session = await manager.PrepareRecipeCopyAsync(source, "retry-copy", default);

        var cleanupFailure = await Assert.ThrowsAsync<TemplateModelGenerationCleanupException>(
            () => session.DisposeAsync().AsTask());
        Assert.Null(cleanupFailure.PrimaryException);
        var exactFailure = Assert.Single(cleanupFailure.Failures);
        Assert.Equal(
            new TemplateModelOwner("retry-copy", "flow-b", "inactive-halcon"),
            exactFailure.Owner);
        Assert.Equal("copy-2", exactFailure.Generation);
        Assert.Equal(
            "Injected generation cleanup failure.",
            exactFailure.CleanupException.Message);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            session.CommitAsync(CancellationToken.None));

        var replacement = await manager.PrepareRecipeCopyAsync(source, "retry-copy", default);
        await replacement.CommitAsync(CancellationToken.None);
        await replacement.DisposeAsync();

        await session.DisposeAsync();

        Assert.Equal(
            ["copy-2", "copy-1", "copy-2", "copy-1"],
            store.DeleteGenerationCalls
                .Select(call => call.Reference.Generation)
                .ToArray());
        Assert.All(
            store.DeleteGenerationCalls,
            call => Assert.Equal("retry-copy", call.Owner.RecipeId));
        Assert.DoesNotContain(
            store.DeleteGenerationCalls,
            call => call.Reference.Generation is "copy-3" or "copy-4");
    }

    [Fact]
    public async Task SameTargetRecipeCannotBePreparedUntilFirstSessionIsDisposed()
    {
        var store = new RecordingTemplateModelStore();
        var manager = new TemplateModelResourceManager(store, new RecordingRetirementSink(store.Events), new RecordingLog());
        var source = Task7Fixtures.CreateSingleModelRecipe();
        await using var first = await manager.PrepareRecipeCopyAsync(source, "recipe-copy", default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.PrepareRecipeCopyAsync(source, "RECIPE-COPY", default));

        await using var otherTarget = await manager.PrepareRecipeCopyAsync(source, "other-copy", default);
        Assert.Equal("other-copy", otherTarget.Recipe.Id);

        await first.DisposeAsync();
        await using var retry = await manager.PrepareRecipeCopyAsync(source, "recipe-copy", default);
        Assert.Equal("recipe-copy", retry.Recipe.Id);
    }

    [Fact]
    public async Task ManagersSharingStoreAlsoShareTargetRecipeReservation()
    {
        var store = new RecordingTemplateModelStore();
        var firstManager = new TemplateModelResourceManager(
            store,
            new RecordingRetirementSink(store.Events),
            new RecordingLog());
        var secondManager = new TemplateModelResourceManager(
            store,
            new RecordingRetirementSink(store.Events),
            new RecordingLog());
        var source = Task7Fixtures.CreateSingleModelRecipe();
        await using var first = await firstManager.PrepareRecipeCopyAsync(
            source,
            "recipe-copy",
            default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            secondManager.PrepareRecipeCopyAsync(source, "RECIPE-COPY", default));
    }

    [Fact]
    public async Task AmbiguousCaseVariantHalconKeysFailAsConfigurationErrorBeforeClone()
    {
        var source = Task7Fixtures.CreateSingleModelRecipe();
        var tool = Task7Fixtures.GetTool(source, "flow-a", "active-halcon");
        var ambiguous = new Dictionary<string, string>(tool.Parameters, StringComparer.Ordinal)
        {
            ["HALCON.MODELPATH"] = tool.Parameters["halcon.modelPath"]
        };
        var flow = source.EffectiveFlows[0];
        source = (source with
        {
            Flows = [flow with { Tools = [tool with { Parameters = ambiguous }] }]
        }).WithNormalizedFlows();
        var store = new RecordingTemplateModelStore();
        var manager = new TemplateModelResourceManager(
            store,
            new RecordingRetirementSink(store.Events),
            new RecordingLog());

        var error = await Assert.ThrowsAsync<TemplateMatchingConfigurationException>(() =>
            manager.PrepareRecipeCopyAsync(source, "recipe-copy", default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, error.Code);
        Assert.Empty(store.CopyCalls);
    }

    [Fact]
    public async Task DeleteRetiresEveryOwnerBeforeDeletingAnyFileAndIncludesInactiveState()
    {
        var store = new RecordingTemplateModelStore();
        var retirement = new RecordingRetirementSink(store.Events);
        var manager = new TemplateModelResourceManager(store, retirement, new RecordingLog());

        await manager.DeleteRecipeResourcesAsync(Task7Fixtures.CreateSourceRecipe(), default);

        Assert.Equal(
            new[]
            {
                "retire:flow-a/active-halcon",
                "retire:flow-b/inactive-halcon",
                "delete:flow-a/active-halcon",
                "delete:flow-b/inactive-halcon"
            },
            store.Events);
        Assert.DoesNotContain(store.DeletedOwners, owner => owner.ToolId == "plain-tool");
    }

    [Fact]
    public async Task RetireFailurePreventsEveryFileDeletion()
    {
        var store = new RecordingTemplateModelStore();
        var retirement = new RecordingRetirementSink(store.Events)
        {
            FailOwner = new TemplateModelOwner("recipe-source", "flow-b", "inactive-halcon")
        };
        var manager = new TemplateModelResourceManager(store, retirement, new RecordingLog());

        await Assert.ThrowsAnyAsync<Exception>(() =>
            manager.DeleteRecipeResourcesAsync(Task7Fixtures.CreateSourceRecipe(), default));

        Assert.Empty(store.DeletedOwners);
        Assert.Equal(2, retirement.Owners.Count);
    }

    [Fact]
    public async Task CancellationRaisedByRetirementPropagatesWithoutAggregateWrapping()
    {
        var store = new RecordingTemplateModelStore();
        var retirement = new RecordingRetirementSink(store.Events)
        {
            CancelOwner = new TemplateModelOwner(
                "recipe-source",
                "flow-a",
                "active-halcon")
        };
        var manager = new TemplateModelResourceManager(store, retirement, new RecordingLog());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            manager.DeleteRecipeResourcesAsync(Task7Fixtures.CreateSourceRecipe(), default));

        Assert.Empty(store.DeletedOwners);
    }

    [Fact]
    public async Task CancellationAfterAllOwnersRetireStopsBeforePersistentCleanupStarts()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new RecordingTemplateModelStore();
        var retirement = new RecordingRetirementSink(store.Events)
        {
            OnRetired = owner =>
            {
                if (string.Equals(owner.ToolId, "inactive-halcon", StringComparison.Ordinal))
                {
                    cancellation.Cancel();
                }
            }
        };
        var manager = new TemplateModelResourceManager(store, retirement, new RecordingLog());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.DeleteRecipeResourcesAsync(
                Task7Fixtures.CreateSourceRecipe(),
                cancellation.Token));

        Assert.Empty(store.DeletedOwners);
    }

    [Fact]
    public async Task CallerCancellationAtRetirementBoundaryIsNotWrappedWithRetirementFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var failedOwner = new TemplateModelOwner(
            "recipe-source",
            "flow-b",
            "inactive-halcon");
        var store = new RecordingTemplateModelStore();
        var retirement = new RecordingRetirementSink(store.Events)
        {
            FailOwner = failedOwner,
            OnRetired = owner =>
            {
                if (owner == failedOwner)
                {
                    cancellation.Cancel();
                }
            }
        };
        var manager = new TemplateModelResourceManager(store, retirement, new RecordingLog());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.DeleteRecipeResourcesAsync(
                Task7Fixtures.CreateSourceRecipe(),
                cancellation.Token));

        Assert.Empty(store.DeletedOwners);
    }

    [Fact]
    public async Task CancellationDuringFirstPersistentDeleteDoesNotStopRemainingOwners()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new RecordingTemplateModelStore
        {
            OnDeleted = owner =>
            {
                if (string.Equals(owner.ToolId, "active-halcon", StringComparison.Ordinal))
                {
                    cancellation.Cancel();
                }
            }
        };
        var manager = new TemplateModelResourceManager(
            store,
            new RecordingRetirementSink(store.Events),
            new RecordingLog());

        await manager.DeleteRecipeResourcesAsync(
            Task7Fixtures.CreateSourceRecipe(),
            cancellation.Token);

        Assert.Equal(2, store.DeletedOwners.Count);
    }

    [Fact]
    public async Task UnexpectedPersistentDeleteCancellationIsAggregatedAfterRemainingOwnersAreTried()
    {
        var cancelledOwner = new TemplateModelOwner(
            "recipe-source",
            "flow-a",
            "active-halcon");
        var store = new RecordingTemplateModelStore
        {
            CancelDeleteOwner = cancelledOwner
        };
        var manager = new TemplateModelResourceManager(
            store,
            new RecordingRetirementSink(store.Events),
            new RecordingLog());

        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            manager.DeleteRecipeResourcesAsync(Task7Fixtures.CreateSourceRecipe(), default));

        Assert.IsType<OperationCanceledException>(Assert.Single(failure.InnerExceptions));
        Assert.Equal(2, store.DeletedOwners.Count);
        Assert.Contains(store.DeletedOwners, owner => owner.ToolId == "inactive-halcon");
    }

    [Fact]
    public async Task FileDeleteFailureDoesNotPreventOtherRetiredOwnersFromBeingCleaned()
    {
        var store = new RecordingTemplateModelStore
        {
            FailDeleteOwner = new TemplateModelOwner("recipe-source", "flow-a", "active-halcon")
        };
        var manager = new TemplateModelResourceManager(store, new RecordingRetirementSink(store.Events), new RecordingLog());

        await Assert.ThrowsAsync<AggregateException>(() =>
            manager.DeleteRecipeResourcesAsync(Task7Fixtures.CreateSourceRecipe(), default));

        Assert.Equal(2, store.DeletedOwners.Count);
        Assert.Contains(store.DeletedOwners, owner => owner.ToolId == "inactive-halcon");
    }

    [Fact]
    public async Task RetireToolDoesNotDeletePersistentFiles()
    {
        var store = new RecordingTemplateModelStore();
        var retirement = new RecordingRetirementSink(store.Events);
        var manager = new TemplateModelResourceManager(store, retirement, new RecordingLog());
        var owner = new TemplateModelOwner("recipe", "flow", "tool");

        await manager.RetireToolAsync(owner, default);

        Assert.Equal(owner, Assert.Single(retirement.Owners));
        Assert.Empty(store.DeletedOwners);
    }
}

public sealed class RecipeTemplateLifecycleServiceTests
{
    [Fact]
    public async Task DuplicateHoldsMutationThroughPrepareCreateCommitAndDispose()
    {
        var events = new List<string>();
        var repository = new RecordingRecipeRepository(events);
        var resources = new RecordingResourceManager(events);
        var service = new RecipeTemplateLifecycleService(repository, resources, new RecordingLog());
        var source = Task7Fixtures.CreateSingleModelRecipe() with
        {
            Name = "产品 A",
            ProductCode = "PRODUCT-A"
        };

        var copy = await service.DuplicateAsync(source, "recipe-copy", default);

        Assert.Equal("recipe-copy", copy.Id);
        Assert.Equal("产品 A-副本", copy.Name);
        Assert.Equal("PRODUCT-A-COPY", copy.ProductCode);
        Assert.Equal(
            new[]
            {
                "mutation-begin",
                "target-check",
                "prepare",
                "create",
                "commit",
                "mutation-dispose"
            },
            events);
        Assert.True(resources.LastSession!.Committed);
        Assert.Equal(1, resources.LastSession.ReleaseAttempts);
    }

    [Fact]
    public async Task PrepareFailureNeverCallsRepositorySave()
    {
        var events = new List<string>();
        var repository = new RecordingRecipeRepository(events);
        var resources = new RecordingResourceManager(events) { FailPrepare = true };
        var service = new RecipeTemplateLifecycleService(repository, resources, new RecordingLog());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DuplicateAsync(Task7Fixtures.CreateSingleModelRecipe(), "recipe-copy", default));

        Assert.Equal(
            new[] { "mutation-begin", "target-check", "prepare", "mutation-dispose" },
            events);
        Assert.Empty(repository.SavedRecipes);
    }

    [Fact]
    public async Task CreateFailureNeverDeletesTargetAndDisposesUncommittedResources()
    {
        var events = new List<string>();
        var repository = new RecordingRecipeRepository(events) { FailCreate = true };
        var resources = new RecordingResourceManager(events);
        var service = new RecipeTemplateLifecycleService(repository, resources, new RecordingLog());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DuplicateAsync(Task7Fixtures.CreateSingleModelRecipe(), "recipe-copy", default));

        Assert.Equal(
            new[]
            {
                "mutation-begin",
                "target-check",
                "prepare",
                "create",
                "dispose",
                "mutation-dispose"
            },
            events);
        Assert.False(repository.StoredRecipes.ContainsKey("recipe-copy"));
        Assert.False(resources.LastSession!.Committed);
        Assert.True(resources.LastSession.Disposed);
    }

    [Fact]
    public async Task CreateCancellationWithRollbackFailureLogsExactOrphansAndRethrowsSameCancellation()
    {
        var events = new List<string>();
        var repository = new RecordingRecipeRepository(events) { CancelCreate = true };
        var cleanupException = new IOException("Injected exact generation cleanup failure.");
        var resources = new RecordingResourceManager(events)
        {
            RollbackFailures =
            [
                new TemplateModelGenerationCleanupFailure(
                    new TemplateModelOwner("recipe-copy", "flow-a", "active-halcon"),
                    "generation-copy-a",
                    cleanupException),
                new TemplateModelGenerationCleanupFailure(
                    new TemplateModelOwner("recipe-copy", "flow-b", "inactive-halcon"),
                    "generation-copy-b",
                    new UnauthorizedAccessException("Injected inactive cleanup failure."))
            ]
        };
        var log = new RecordingLog();
        var service = new RecipeTemplateLifecycleService(repository, resources, log);

        var cancellation = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.DuplicateAsync(Task7Fixtures.CreateSourceRecipe(), "recipe-copy", default));

        Assert.Same(repository.LastCreateCancellation, cancellation);
        Assert.False(repository.StoredRecipes.ContainsKey("recipe-copy"));
        Assert.Equal(2, log.Warnings.Count);
        Assert.Contains(
            log.Warnings,
            warning =>
                warning.Contains("recipe-copy/flow-a/active-halcon", StringComparison.Ordinal) &&
                warning.Contains("generation-copy-a", StringComparison.Ordinal) &&
                warning.Contains(cleanupException.Message, StringComparison.Ordinal));
        Assert.Contains(
            log.Warnings,
            warning =>
                warning.Contains("recipe-copy/flow-b/inactive-halcon", StringComparison.Ordinal) &&
                warning.Contains("generation-copy-b", StringComparison.Ordinal) &&
                warning.Contains("Injected inactive cleanup failure.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TransientReservationReleaseFailureIsRetriedWithoutRollingBackPublishedCopy()
    {
        var events = new List<string>();
        var repository = new RecordingRecipeRepository(events);
        var resources = new RecordingResourceManager(events) { ReservationReleaseFailures = 1 };
        var service = new RecipeTemplateLifecycleService(repository, resources, new RecordingLog());

        var copy = await service.DuplicateAsync(
            Task7Fixtures.CreateSingleModelRecipe(),
            "recipe-copy",
            default);

        Assert.Equal("recipe-copy", copy.Id);
        Assert.True(repository.StoredRecipes.ContainsKey("recipe-copy"));
        Assert.Equal(
            new[]
            {
                "mutation-begin",
                "target-check",
                "prepare",
                "create",
                "commit",
                "commit",
                "mutation-dispose"
            },
            events);
        Assert.True(resources.LastSession!.Committed);
        Assert.Equal(2, resources.LastSession.ReleaseAttempts);
    }

    [Fact]
    public async Task PersistentReservationReleaseFailureKeepsPublishedCopyAndDisposeCanRetry()
    {
        var events = new List<string>();
        var repository = new RecordingRecipeRepository(events);
        var resources = new RecordingResourceManager(events) { ReservationReleaseFailures = 3 };
        var service = new RecipeTemplateLifecycleService(repository, resources, new RecordingLog());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DuplicateAsync(Task7Fixtures.CreateSingleModelRecipe(), "recipe-copy", default));

        Assert.True(repository.StoredRecipes.ContainsKey("recipe-copy"));
        Assert.Equal(2, resources.LastSession!.ReleaseAttempts);
        Assert.False(resources.LastSession.Disposed);
        Assert.DoesNotContain("repo-delete", events);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resources.LastSession.DisposeAsync().AsTask());
        await resources.LastSession.DisposeAsync();
        await resources.LastSession.DisposeAsync();

        Assert.Equal(4, resources.LastSession.ReleaseAttempts);
        Assert.True(resources.LastSession.Committed);
        Assert.False(resources.LastSession.Disposed);
        Assert.True(repository.StoredRecipes.ContainsKey("recipe-copy"));
    }

    [Fact]
    public async Task DuplicateRejectsExistingTargetBeforePreparingResources()
    {
        var events = new List<string>();
        var repository = new RecordingRecipeRepository(events);
        repository.StoredRecipes["recipe-copy"] = new Recipe { Id = "recipe-copy" };
        var resources = new RecordingResourceManager(events);
        var service = new RecipeTemplateLifecycleService(repository, resources, new RecordingLog());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DuplicateAsync(Task7Fixtures.CreateSingleModelRecipe(), "RECIPE-COPY", default));

        Assert.Equal(
            new[] { "mutation-begin", "target-check", "mutation-dispose" },
            events);
        Assert.Null(resources.LastSession);
    }

    [Fact]
    public async Task DeleteRemovesJsonBeforeRetiringAndDeletingResources()
    {
        var events = new List<string>();
        var recipe = Task7Fixtures.CreateSingleModelRecipe();
        recipe = recipe with { StorageRevision = recipe.Name };
        var repository = new RecordingRecipeRepository(events);
        repository.StoredRecipes[recipe.Id] = recipe;
        var resources = new RecordingResourceManager(events);
        var service = new RecipeTemplateLifecycleService(repository, resources, new RecordingLog());

        await service.DeleteAsync(recipe, default);

        Assert.Equal(
            new[]
            {
                "mutation-begin",
                "target-check",
                "repo-delete",
                "resources-delete",
                "mutation-dispose"
            },
            events);
        Assert.False(repository.StoredRecipes.ContainsKey(recipe.Id));
    }

    [Fact]
    public async Task DeleteRejectsCallerRevisionThatDoesNotMatchAuthoritativeSnapshot()
    {
        var events = new List<string>();
        var callerSnapshot = new Recipe
        {
            Id = "recipe-source",
            Name = "stale",
            StorageRevision = "stale"
        };
        var authoritative = Task7Fixtures.CreateSingleModelRecipe() with
        {
            Name = "authoritative",
            StorageRevision = "authoritative"
        };
        var repository = new RecordingRecipeRepository(events);
        repository.StoredRecipes[authoritative.Id] = authoritative;
        var resources = new RecordingResourceManager(events);
        var service = new RecipeTemplateLifecycleService(repository, resources, new RecordingLog());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(callerSnapshot, default));

        Assert.True(repository.StoredRecipes.ContainsKey(authoritative.Id));
        Assert.Null(resources.LastDeletedRecipe);
        Assert.Equal("mutation-dispose", events[^1]);
    }

    [Fact]
    public async Task DeleteWithMatchingCallerRevisionUsesAuthoritativeSnapshotForCleanup()
    {
        var events = new List<string>();
        var authoritative = Task7Fixtures.CreateSingleModelRecipe() with
        {
            Name = "authoritative",
            StorageRevision = "authoritative"
        };
        var callerSnapshot = new Recipe
        {
            Id = authoritative.Id,
            Name = "stale-fields",
            StorageRevision = authoritative.Name
        };
        var repository = new RecordingRecipeRepository(events);
        repository.StoredRecipes[authoritative.Id] = authoritative;
        var resources = new RecordingResourceManager(events);
        var service = new RecipeTemplateLifecycleService(repository, resources, new RecordingLog());

        await service.DeleteAsync(callerSnapshot, default);

        Assert.Equal(authoritative.Name, resources.LastDeletedRecipe!.Name);
        Assert.Equal(authoritative.Name, resources.LastDeletedRecipe.StorageRevision);
    }

    [Fact]
    public async Task MissingAuthoritativeRecipeStillUsesCallerSnapshotForOrphanCleanup()
    {
        var events = new List<string>();
        var callerSnapshot = Task7Fixtures.CreateSingleModelRecipe();
        var repository = new RecordingRecipeRepository(events);
        var resources = new RecordingResourceManager(events);
        var service = new RecipeTemplateLifecycleService(repository, resources, new RecordingLog());

        await service.DeleteAsync(callerSnapshot, default);

        Assert.Equal(callerSnapshot.Id, resources.LastDeletedRecipe!.Id);
        Assert.Contains("resources-delete", events);
    }

    [Fact]
    public async Task RepositoryDeleteFailureDoesNotTouchModelResources()
    {
        var events = new List<string>();
        var recipe = Task7Fixtures.CreateSingleModelRecipe();
        recipe = recipe with { StorageRevision = recipe.Name };
        var repository = new RecordingRecipeRepository(events) { FailDelete = true };
        repository.StoredRecipes[recipe.Id] = recipe;
        var resources = new RecordingResourceManager(events);
        var service = new RecipeTemplateLifecycleService(repository, resources, new RecordingLog());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(recipe, default));

        Assert.Equal(
            new[]
            {
                "mutation-begin",
                "target-check",
                "repo-delete",
                "mutation-dispose"
            },
            events);
    }

    [Fact]
    public async Task ResourceCleanupFailureOnlyLogsOrphanWarningAfterJsonDeletion()
    {
        var events = new List<string>();
        var recipe = Task7Fixtures.CreateSingleModelRecipe();
        recipe = recipe with { StorageRevision = recipe.Name };
        var repository = new RecordingRecipeRepository(events);
        repository.StoredRecipes[recipe.Id] = recipe;
        var resources = new RecordingResourceManager(events) { FailDelete = true };
        var log = new RecordingLog();
        var service = new RecipeTemplateLifecycleService(repository, resources, log);

        await service.DeleteAsync(recipe, default);

        Assert.Equal(
            new[]
            {
                "mutation-begin",
                "target-check",
                "repo-delete",
                "resources-delete",
                "mutation-dispose"
            },
            events);
        Assert.False(repository.StoredRecipes.ContainsKey(recipe.Id));
        Assert.Contains(log.Warnings, warning => warning.Contains("orphan", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class JsonRecipeRepositoryAtomicityTests : IDisposable
{
    private readonly string _baseDirectory = Path.Combine(
        Path.GetTempPath(),
        $"visionstation-recipe-atomic-{Guid.NewGuid():N}");

    [Fact]
    public async Task ConcurrentCreateAcrossRepositoryInstancesAllowsExactlyOneWinner()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var firstRepository = new JsonRecipeRepository(paths);
        var secondRepository = new JsonRecipeRepository(paths);
        var recipe = new Recipe { Id = "same-id", Name = "candidate" };

        var attempts = await Task.WhenAll(
            TryCreateAsync(firstRepository, recipe),
            TryCreateAsync(secondRepository, recipe));

        Assert.Single(attempts, succeeded => succeeded);
        Assert.Single(Directory.EnumerateFiles(paths.RecipeDirectory, "*.recipe.json"));
    }

    [Fact]
    public async Task MutationDeleteRejectsRevisionFromDeletedAndRecreatedRecipe()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        await using var mutation = await repository.BeginMutationAsync("aba", default);
        var staleRecipe = await mutation.CreateAsync(
            new Recipe { Id = "aba", Name = "old" },
            default);
        await mutation.DeleteAsync(staleRecipe, default);
        _ = await mutation.CreateAsync(
            new Recipe { Id = "aba", Name = "new" },
            default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mutation.DeleteAsync(staleRecipe, default));

        var stored = await mutation.GetAsync(default);
        Assert.Equal("new", stored!.Name);
    }

    [Fact]
    public async Task StaleRecipeLoadedBeforeDeleteAndRecreateCannotSaveOverNewRecipe()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        _ = await repository.CreateAsync(new Recipe { Id = "aba-save", Name = "old" });
        var stale = await repository.GetAsync("aba-save");
        await using (var mutation = await repository.BeginMutationAsync("aba-save", default))
        {
            var oldRecipe = await mutation.GetAsync(default);
            await mutation.DeleteAsync(oldRecipe!, default);
            _ = await mutation.CreateAsync(
                new Recipe { Id = "aba-save", Name = "recreated" },
                default);
        }

        var newBytes = await File.ReadAllBytesAsync(
            Path.Combine(paths.RecipeDirectory, "aba-save.recipe.json"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveAsync(stale! with { Name = "stale-overwrite" }));

        Assert.Equal(
            newBytes,
            await File.ReadAllBytesAsync(Path.Combine(paths.RecipeDirectory, "aba-save.recipe.json")));
    }

    [Fact]
    public async Task IdenticalPayloadDeleteAndRecreateGetsNewTokenAndRejectsStaleSaveAndDelete()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        var input = new Recipe { Id = "identical-aba", Name = "same-payload" };
        var first = await repository.CreateAsync(input);
        Recipe recreated;
        await using (var mutation = await repository.BeginMutationAsync(input.Id, default))
        {
            await mutation.DeleteAsync(first, default);
            recreated = await mutation.CreateAsync(input, default);
        }

        Assert.StartsWith("v1-", first.StorageRevision, StringComparison.Ordinal);
        Assert.StartsWith("v1-", recreated.StorageRevision, StringComparison.Ordinal);
        Assert.NotEqual(first.StorageRevision, recreated.StorageRevision);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveAsync(first));
        await using (var mutation = await repository.BeginMutationAsync(input.Id, default))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                mutation.DeleteAsync(first, default));
        }

        Assert.Equal(recreated.StorageRevision, (await repository.GetAsync(input.Id))!.StorageRevision);
    }

    [Fact]
    public async Task LifecycleDeleteWithStaleRecipeCannotDeleteRecreatedJsonOrResources()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        var stale = await repository.CreateAsync(
            new Recipe { Id = "lifecycle-aba", Name = "old" });
        await using (var mutation = await repository.BeginMutationAsync(stale.Id, default))
        {
            await mutation.DeleteAsync(stale, default);
            _ = await mutation.CreateAsync(
                new Recipe { Id = stale.Id, Name = "recreated" },
                default);
        }

        var recipePath = Path.Combine(paths.RecipeDirectory, "lifecycle-aba.recipe.json");
        var recreatedBytes = await File.ReadAllBytesAsync(recipePath);
        var events = new List<string>();
        var resources = new RecordingResourceManager(events);
        var lifecycle = new RecipeTemplateLifecycleService(
            repository,
            resources,
            new RecordingLog());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifecycle.DeleteAsync(stale));

        Assert.Equal(recreatedBytes, await File.ReadAllBytesAsync(recipePath));
        Assert.Null(resources.LastDeletedRecipe);
    }

    [Fact]
    public async Task MutationOfDifferentRecipeWhileSaveWaitsDoesNotCauseFalseConflict()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var firstRepository = new JsonRecipeRepository(paths);
        var secondRepository = new JsonRecipeRepository(paths);
        _ = await firstRepository.CreateAsync(new Recipe { Id = "target", Name = "target-old" });
        _ = await firstRepository.CreateAsync(new Recipe { Id = "other", Name = "other-old" });
        var target = await firstRepository.GetAsync("target");
        var mutation = await firstRepository.BeginMutationAsync("other", default);
        var queuedSave = secondRepository.SaveAsync(target! with { Name = "target-new" });
        await Task.Delay(100);
        var other = await mutation.GetAsync(default);
        await mutation.DeleteAsync(other!, default);
        _ = await mutation.CreateAsync(new Recipe { Id = "other", Name = "other-new" }, default);
        await mutation.DisposeAsync();

        var saved = await queuedSave;

        Assert.Equal("target-new", saved.Name);
    }

    [Fact]
    public async Task CurrentPointerChangeDoesNotPreventTargetRecipeUpdate()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        _ = await repository.CreateAsync(new Recipe { Id = "target", Name = "old" });
        _ = await repository.CreateAsync(new Recipe { Id = "other", Name = "other" });
        var target = await repository.GetAsync("target");
        await repository.SetCurrentRecipeAsync("other");

        var saved = await repository.SaveAsync(target! with { Name = "new" });

        Assert.Equal("new", saved.Name);
    }

    [Fact]
    public async Task SanitizedPathAliasCannotReadUpdateOrDeleteOriginalRecipe()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        var original = await repository.CreateAsync(
            new Recipe { Id = "a_b", Name = "original" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.GetAsync("a/b"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveAsync(original with { Id = "a/b", Name = "alias-update" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteAsync("a/b"));

        Assert.Equal("original", (await repository.GetAsync("a_b"))!.Name);
    }

    [Fact]
    public async Task CurrentPointerAliasIsTreatedAsStaleAndRepairedToStoredRecipeId()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        _ = await repository.CreateAsync(new Recipe { Id = "default", Name = "default" });
        _ = await repository.CreateAsync(new Recipe { Id = "a_b", Name = "stored" });
        await File.WriteAllTextAsync(paths.CurrentRecipePath, "a/b");

        var current = await repository.GetCurrentAsync();

        Assert.Equal("a_b", current.Id);
        Assert.Equal("a_b", await File.ReadAllTextAsync(paths.CurrentRecipePath));
    }

    [Fact]
    public async Task GetCurrentRecipeIdValidatesAndRepairsMissingDerivedPointer()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        _ = await repository.CreateAsync(new Recipe { Id = "stored", Name = "stored" });
        await File.WriteAllTextAsync(paths.CurrentRecipePath, "missing");

        var currentRecipeId = await repository.GetCurrentRecipeIdAsync();

        Assert.Equal("stored", currentRecipeId);
        Assert.Equal("stored", await File.ReadAllTextAsync(paths.CurrentRecipePath));
    }

    [Fact]
    public async Task CreateReturnsIncarnationRevisionWithoutPersistingCallerToken()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);

        var created = await repository.CreateAsync(new Recipe { Id = "snapshot", Name = "snapshot" });
        var reloaded = await repository.GetAsync("snapshot");
        var json = await File.ReadAllTextAsync(
            Path.Combine(paths.RecipeDirectory, "snapshot.recipe.json"));
        using var document = JsonDocument.Parse(json);

        Assert.Equal(created.StorageRevision, reloaded!.StorageRevision);
        Assert.StartsWith("v1-", created.StorageRevision, StringComparison.Ordinal);
        Assert.True(document.RootElement.TryGetProperty("_storageIncarnation", out _));
        Assert.False(document.RootElement.TryGetProperty("storageRevision", out _));
    }

    [Fact]
    public async Task CreateIgnoresCallerSuppliedStorageRevisionAndUpdateRotatesToken()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        const string forgedRevision = "caller-forged-revision";

        var created = await repository.CreateAsync(
            new Recipe
            {
                Id = "forged-token",
                Name = "forged-token",
                StorageRevision = forgedRevision
            });
        var updated = await repository.SaveAsync(created);

        Assert.NotEqual(forgedRevision, created.StorageRevision);
        Assert.StartsWith("v1-", created.StorageRevision, StringComparison.Ordinal);
        Assert.StartsWith("v1-", updated.StorageRevision, StringComparison.Ordinal);
        Assert.NotEqual(created.StorageRevision, updated.StorageRevision);
        Assert.Equal(
            updated.StorageRevision,
            (await new JsonRecipeRepository(paths).GetAsync(created.Id))!.StorageRevision);
    }

    [Fact]
    public async Task LegacyJsonGetsStableHashTokenUntilFirstSuccessfulUpdate()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var recipePath = Path.Combine(paths.RecipeDirectory, "legacy.recipe.json");
        var legacyBytes = System.Text.Encoding.UTF8.GetBytes(
            "{\"id\":\"legacy\",\"name\":\"legacy\"}");
        await File.WriteAllBytesAsync(recipePath, legacyBytes);
        var repository = new JsonRecipeRepository(paths);

        var firstRead = await repository.GetAsync("legacy");
        var secondRead = await new JsonRecipeRepository(paths).GetAsync("legacy");

        Assert.StartsWith("legacy-", firstRead!.StorageRevision, StringComparison.Ordinal);
        Assert.Equal(firstRead.StorageRevision, secondRead!.StorageRevision);

        var updated = await repository.SaveAsync(firstRead with { Name = "updated" });
        var persistedJson = await File.ReadAllTextAsync(recipePath);

        Assert.False(updated.StorageRevision.StartsWith("legacy-", StringComparison.Ordinal));
        Assert.Contains("_storageIncarnation", persistedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("storageRevision", persistedJson, StringComparison.Ordinal);
        Assert.Equal(
            updated.StorageRevision,
            (await new JsonRecipeRepository(paths).GetAsync("legacy"))!.StorageRevision);
    }

    [Fact]
    public async Task MalformedCurrentPointerFallsBackAndRepairsToLegalRecipe()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        _ = await repository.CreateAsync(new Recipe { Id = "legal", Name = "legal" });
        await File.WriteAllTextAsync(paths.CurrentRecipePath, ":");

        var current = await repository.GetCurrentAsync();

        Assert.Equal("legal", current.Id);
        Assert.Equal("legal", await File.ReadAllTextAsync(paths.CurrentRecipePath));
    }

    [Fact]
    public async Task CurrentPointerDirectoryFailsClosedWithoutDeletingIt()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        _ = await repository.CreateAsync(new Recipe { Id = "legal", Name = "legal" });
        File.Delete(paths.CurrentRecipePath);
        Directory.CreateDirectory(paths.CurrentRecipePath);

        await Assert.ThrowsAnyAsync<Exception>(() => repository.GetCurrentAsync());
        Assert.True(Directory.Exists(paths.CurrentRecipePath));
    }

    [Fact]
    public async Task LockedCurrentRecipeJsonIsNotMistakenForAStaleDerivedPointer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        _ = await repository.CreateAsync(new Recipe { Id = "target", Name = "target" });
        _ = await repository.CreateAsync(new Recipe { Id = "fallback", Name = "fallback" });
        var targetPath = Path.Combine(paths.RecipeDirectory, "target.recipe.json");

        await using (var blocker = new FileStream(
                         targetPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => repository.GetCurrentAsync());
        }

        Assert.Equal("target", await File.ReadAllTextAsync(paths.CurrentRecipePath));
    }

    [Fact]
    public async Task CreateKeepsCommittedJsonWhenDerivedCurrentPointerCannotBeInitialized()
    {
        var paths = new RuntimePaths(_baseDirectory);
        Directory.CreateDirectory(paths.CurrentRecipePath);
        var repository = new JsonRecipeRepository(paths);

        var created = await repository.CreateAsync(
            new Recipe { Id = "committed", Name = "committed" });

        Assert.Equal("committed", created.Id);
        Assert.Equal(created.StorageRevision, (await repository.GetAsync("committed"))!.StorageRevision);
    }

    [Fact]
    public async Task DeleteFailureBeforeJsonCommitLeavesCurrentAndResourcesUntouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        var target = await repository.CreateAsync(new Recipe { Id = "target", Name = "target" });
        _ = await repository.CreateAsync(new Recipe { Id = "fallback", Name = "fallback" });
        await repository.SetCurrentRecipeAsync(target.Id);
        var events = new List<string>();
        var resources = new RecordingResourceManager(events);
        var lifecycle = new RecipeTemplateLifecycleService(repository, resources, new RecordingLog());
        var recipePath = Path.Combine(paths.RecipeDirectory, "target.recipe.json");

        await using (var blocker = new FileStream(
                         recipePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => lifecycle.DeleteAsync(target));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }

        Assert.Equal("target", await File.ReadAllTextAsync(paths.CurrentRecipePath));
        Assert.NotNull(await repository.GetAsync("target"));
        Assert.Null(resources.LastDeletedRecipe);
    }

    [Fact]
    public async Task PermanentCatalogPathErrorFailsImmediatelyInsteadOfRetryingAsLockContention()
    {
        var paths = new RuntimePaths(_baseDirectory);
        Directory.Delete(paths.RecipeDirectory);
        await File.WriteAllTextAsync(paths.RecipeDirectory, "not-a-directory");
        var repository = new JsonRecipeRepository(paths);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<IOException>(() =>
            repository.CreateAsync(new Recipe { Id = "never-created" }, timeout.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public async Task CatalogScopeReleasesProcessGateWhenFileLockDisposeThrows()
    {
        var scopeType = typeof(JsonRecipeRepository).GetNestedType(
            "CatalogScope",
            BindingFlags.NonPublic)!;
        var gate = new SemaphoreSlim(0, 1);
        var lockPath = Path.Combine(_baseDirectory, "throwing-dispose.lock");
        Directory.CreateDirectory(_baseDirectory);
        var fileLock = new ThrowingDisposeFileStream(lockPath);
        var scope = (IAsyncDisposable)Activator.CreateInstance(
            scopeType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [gate, fileLock],
            culture: null)!;

        await Assert.ThrowsAsync<IOException>(() => scope.DisposeAsync().AsTask());

        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task SaveIsUpdateOnlyAndDoesNotCreateMissingRecipe()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveAsync(new Recipe { Id = "missing", Name = "must-not-create" }));

        Assert.False(File.Exists(Path.Combine(paths.RecipeDirectory, "missing.recipe.json")));
    }

    [Fact]
    public async Task MoveFailureKeepsOldRecipeBytesAndCleansStagingFile()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        var original = await repository.CreateAsync(
            new Recipe { Id = "locked", Name = "original" });
        var recipePath = Path.Combine(paths.RecipeDirectory, "locked.recipe.json");
        var originalBytes = await File.ReadAllBytesAsync(recipePath);

        await using (var blocker = new FileStream(
                         recipePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() =>
                repository.SaveAsync(original with { Name = "changed" }));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(recipePath));
        AssertNoStagingFiles(paths);
    }

    [Fact]
    public async Task CurrentPointerMoveFailureKeepsOldBytesAndCleansStagingFile()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        _ = await repository.CreateAsync(new Recipe { Id = "first" });
        _ = await repository.CreateAsync(new Recipe { Id = "second" });
        await repository.SetCurrentRecipeAsync("first");
        var originalBytes = await File.ReadAllBytesAsync(paths.CurrentRecipePath);

        await using (var blocker = new FileStream(
                         paths.CurrentRecipePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() =>
                repository.SetCurrentRecipeAsync("second"));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(paths.CurrentRecipePath));
        AssertNoStagingFiles(paths);
    }

    [Fact]
    public async Task QueuedSaveCannotOverwriteRecipeDeletedAndRecreatedByLockOwner()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        var original = await repository.CreateAsync(
            new Recipe { Id = "queued", Name = "old" });
        var recipePath = Path.Combine(paths.RecipeDirectory, "queued.recipe.json");
        var lockPath = Path.Combine(paths.RecipeDirectory, ".recipe-catalog.lock");
        var externalLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        var queuedSave = repository.SaveAsync(original with { Name = "stale-save" });
        await WaitForCatalogGateAsync(repository);

        File.Delete(recipePath);
        var recreated = new Recipe { Id = "queued", Name = "recreated" }.WithNormalizedFlows();
        await File.WriteAllBytesAsync(
            recipePath,
            JsonSerializer.SerializeToUtf8Bytes(recreated));
        externalLock.Dispose();

        await Assert.ThrowsAsync<InvalidOperationException>(() => queuedSave);
        Assert.Equal("recreated", (await repository.GetAsync("queued"))!.Name);
    }

    [Fact]
    public async Task CatalogFileLockWaitHonorsCancellationAndReleasesProcessGate()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        var lockPath = Path.Combine(paths.RecipeDirectory, ".recipe-catalog.lock");
        await using (var externalLock = new FileStream(
                         lockPath,
                         FileMode.OpenOrCreate,
                         FileAccess.ReadWrite,
                         FileShare.None))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                repository.CreateAsync(new Recipe { Id = "cancelled" }, cancellation.Token));
        }

        _ = await repository.CreateAsync(new Recipe { Id = "retry" });
        Assert.NotNull(await repository.GetAsync("retry"));
    }

    [Fact]
    public async Task ConcurrentLifecycleDuplicateAllowsExactlyOneWinner()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var source = Task7Fixtures.CreateSingleModelRecipe();
        var firstEvents = new List<string>();
        var secondEvents = new List<string>();
        var firstService = new RecipeTemplateLifecycleService(
            new JsonRecipeRepository(paths),
            new RecordingResourceManager(firstEvents),
            new RecordingLog());
        var secondService = new RecipeTemplateLifecycleService(
            new JsonRecipeRepository(paths),
            new RecordingResourceManager(secondEvents),
            new RecordingLog());

        var attempts = await Task.WhenAll(
            TryDuplicateAsync(firstService, source),
            TryDuplicateAsync(secondService, source));

        Assert.Single(attempts, succeeded => succeeded);
        Assert.Equal("recipe-copy", (await new JsonRecipeRepository(paths).GetAsync("recipe-copy"))!.Id);
    }

    [Fact]
    public async Task PreCancelledUpdateDoesNotTruncateExistingRecipeJson()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        var original = await repository.CreateAsync(
            new Recipe { Id = "atomic", Name = "original" });
        var recipePath = Path.Combine(paths.RecipeDirectory, "atomic.recipe.json");
        var originalBytes = await File.ReadAllBytesAsync(recipePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.SaveAsync(original with { Name = "changed" }, cancellation.Token));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(recipePath));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(paths.RecipeDirectory),
            path => Path.GetFileName(path).Contains("staging", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreCancelledCreateDoesNotCreateRecipeOrStagingFile()
    {
        var paths = new RuntimePaths(_baseDirectory);
        var repository = new JsonRecipeRepository(paths);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.CreateAsync(new Recipe { Id = "new-recipe" }, cancellation.Token));

        Assert.Empty(Directory.EnumerateFiles(paths.RecipeDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDirectory))
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
    }

    private static async Task<bool> TryCreateAsync(
        IRecipeRepository repository,
        Recipe recipe)
    {
        try
        {
            _ = await repository.CreateAsync(recipe, default);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<bool> TryDuplicateAsync(
        IRecipeTemplateLifecycleService service,
        Recipe source)
    {
        try
        {
            _ = await service.DuplicateAsync(source, "recipe-copy", default);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task WaitForCatalogGateAsync(JsonRecipeRepository repository)
    {
        var coordinator = typeof(JsonRecipeRepository)
            .GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(repository)!;
        var gate = (SemaphoreSlim)coordinator.GetType()
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (gate.CurrentCount != 0)
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    private static void AssertNoStagingFiles(RuntimePaths paths)
    {
        Assert.DoesNotContain(
            Directory.EnumerateFiles(paths.RecipeDirectory),
            path => Path.GetFileName(path).Contains("staging", StringComparison.OrdinalIgnoreCase));
    }
}

internal static class Task7Fixtures
{
    public static Recipe CreateSourceRecipe()
    {
        return new Recipe
        {
            Id = "recipe-source",
            Name = "source",
            ProductCode = "SOURCE",
            CurrentFlowId = "flow-a",
            Flows =
            [
                new VisionFlowDefinition
                {
                    Id = "flow-a",
                    Tools =
                    [
                        CreateModelTool("active-halcon", "Halcon", 'a'),
                        new VisionToolDefinition
                        {
                            Id = "plain-tool",
                            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["engine"] = "OpenCv",
                                ["plain"] = "preserve"
                            }
                        }
                    ]
                },
                new VisionFlowDefinition
                {
                    Id = "flow-b",
                    Tools = [CreateModelTool("inactive-halcon", "OpenCv", 'd')]
                }
            ]
        }.WithNormalizedFlows();
    }

    public static Recipe CreateSingleModelRecipe()
    {
        var source = CreateSourceRecipe();
        var firstFlow = source.EffectiveFlows[0];
        return (source with
        {
            Flows = [firstFlow with { Tools = [firstFlow.Tools[0]] }]
        }).WithNormalizedFlows();
    }

    public static Recipe CreateMutableGraphRecipe()
    {
        return new Recipe
        {
            Id = "recipe-source",
            StorageRevision = "v1-source-revision",
            Camera = new CameraSettings
            {
                CameraCalibration = new CameraCalibrationResult
                {
                    Pattern = new ChessboardCalibrationPattern
                    {
                        Columns = 8,
                        Rows = 5,
                        SquareSize = 2.5,
                        Unit = "mm"
                    },
                    CameraMatrix = [1, 2, 3],
                    DistortionCoefficients = [8, 9],
                    Views =
                    [
                        new CameraCalibrationViewResult
                        {
                            FrameId = "frame-a",
                            RotationVector = [4, 5, 6],
                            TranslationVector = [7, 8, 9],
                            ReprojectionError = 0.1
                        }
                    ]
                },
                PlaneCalibration = new PlaneCalibrationResult
                {
                    ImageToWorldMatrix = [6, 7, 8],
                    WorldToImageMatrix = [9, 10, 11],
                    PointErrors =
                    [
                        new PlaneCalibrationPointError
                        {
                            ImagePoint = new Point2D(1, 2),
                            ExpectedWorldPoint = new Point2D(3, 4),
                            MappedWorldPoint = new Point2D(5, 6),
                            Error = 0.2
                        }
                    ]
                }
            },
            ProductParameters = [new ProductParameterDefinition { Id = "product-parameter" }],
            Variables = [new RecipeVariableDefinition { Id = "variable" }],
            SignalMappings = [new SignalMappingDefinition { Id = "signal-mapping" }],
            CurrentFlowId = "flow-a",
            Flows =
            [
                new VisionFlowDefinition
                {
                    Id = "flow-a",
                    Rois =
                    [
                        new RoiDefinition
                        {
                            Id = "roi-a",
                            Shape = RoiShapeKind.Polygon,
                            Points = [new Point2D(10, 20), new Point2D(30, 40)]
                        }
                    ],
                    Tools =
                    [
                        new VisionToolDefinition
                        {
                            Id = "plain-tool",
                            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["plain"] = "source"
                            }
                        }
                    ]
                }
            ],
            MotionSequences =
            [
                new MotionSequenceDefinition
                {
                    Id = "motion-sequence",
                    Steps =
                    [
                        new MotionStepDefinition
                        {
                            Id = "motion-step",
                            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["motion"] = "source"
                            }
                        }
                    ]
                }
            ],
            ProcessSteps =
            [
                new ProcessStepDefinition
                {
                    Id = "process-step",
                    AxisTargets = [new AxisTargetDefinition { AxisKey = "X", Position = 12 }],
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["process"] = "source"
                    }
                }
            ],
            VisionResults = [new VisionResultDefinition { Id = "vision-result" }],
            PlcSignals = [new PlcSignalDefinition { Id = "plc-signal" }],
            TracePolicy = new TracePolicy
            {
                SaveOkImages = false,
                SaveNgImages = true,
                ImageFormat = "Png",
                RetentionDays = 15,
                MaxStorageMegabytes = 1024
            }
        }.WithNormalizedFlows();
    }

    public static VisionToolDefinition GetTool(Recipe recipe, string flowId, string toolId)
    {
        return recipe.EffectiveFlows
            .Single(flow => flow.Id == flowId)
            .Tools.Single(tool => tool.Id == toolId);
    }

    public static HalconTemplateModelState ReadState(Recipe recipe, string flowId, string toolId)
    {
        return TemplateModelParameterCodec.ReadHalcon(GetTool(recipe, flowId, toolId).Parameters)
               ?? throw new InvalidOperationException("Fixture must contain a complete HALCON state.");
    }

    private static VisionToolDefinition CreateModelTool(string id, string engine, char checksumSeed)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = engine,
            ["standardX"] = "legacy-preserve",
            ["halcon.futureExtension"] = "preserve"
        };
        TemplateModelParameterCodec.WriteHalcon(
            parameters,
            new HalconTemplateModelState(
                new TemplateModelReference(
                    $"source/flow/{id}/model.shm",
                    $"source/flow/{id}/model.json",
                    TemplateModelParameterCodec.HalconScaledShapeModelFormat,
                    new string(checksumSeed, 64),
                    new string((char)(checksumSeed + 1), 64),
                    $"generation-{id}",
                    "halcon-scaled-shape-v1",
                    "26.05.0.0",
                    new string((char)(checksumSeed + 2), 64)),
                new TemplateLearnedGeometry(
                    new Pose2D(10, 20, 30) { Scale = 1.1 },
                    100,
                    80)));
        return new VisionToolDefinition
        {
            Id = id,
            Kind = VisionToolKind.TemplateLocate,
            Parameters = parameters
        };
    }
}

internal sealed class RecordingTemplateModelStore : ITemplateModelStore
{
    private int _copyCount;

    public List<string> Events { get; } = [];

    public List<(TemplateModelOwner SourceOwner, TemplateModelReference SourceReference, TemplateModelOwner TargetOwner)> CopyCalls { get; } = [];

    public List<TemplateModelOwner> DeletedOwners { get; } = [];

    public List<(TemplateModelOwner Owner, TemplateModelReference Reference)> DeletedGenerations { get; } = [];

    public List<(TemplateModelOwner Owner, TemplateModelReference Reference)> DeleteGenerationCalls { get; } = [];

    public int? FailCopyNumber { get; init; }

    public int? CancelCopyNumber { get; init; }

    public int? CancelCopyWithCleanupNumber { get; init; }

    public int? FailCopyWithCleanupNumber { get; init; }

    public OperationCanceledException? LastCopyCancellation { get; private set; }

    public Exception? LastCopyFailure { get; private set; }

    public TemplateModelOwner? FailDeleteOwner { get; init; }

    public TemplateModelOwner? CancelDeleteOwner { get; init; }

    public bool CancelDeleteGeneration { get; init; }

    public bool FailDeleteGeneration { get; init; }

    public int DeleteGenerationFailuresRemaining { get; set; }

    public Action<TemplateModelOwner>? OnDeleted { get; init; }

    public Task<TemplateModelWriteSession> BeginWriteAsync(TemplateModelOwner owner, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<TemplateModelReference> CommitAsync(
        TemplateModelWriteSession session,
        ReadOnlyMemory<byte> metadataJson,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ResolvedTemplateModel> ResolveAsync(
        TemplateModelOwner owner,
        TemplateModelReference reference,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<TemplateModelReference> CopyGenerationAsync(
        TemplateModelOwner sourceOwner,
        TemplateModelReference sourceReference,
        TemplateModelOwner targetOwner,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _copyCount++;
        CopyCalls.Add((sourceOwner, sourceReference, targetOwner));
        Events.Add($"copy:{targetOwner.FlowId}/{targetOwner.ToolId}");
        if (FailCopyNumber == _copyCount)
        {
            throw new InvalidOperationException("Injected copy failure.");
        }

        if (CancelCopyNumber == _copyCount)
        {
            LastCopyCancellation = new OperationCanceledException("Injected copy cancellation.");
            throw LastCopyCancellation;
        }

        if (CancelCopyWithCleanupNumber == _copyCount)
        {
            LastCopyCancellation = new OperationCanceledException("Injected copy cancellation.");
            throw new TemplateModelGenerationCleanupException(
                "Injected copy cancellation with an exact generation cleanup failure.",
                [
                    new TemplateModelGenerationCleanupFailure(
                        targetOwner,
                        $"partial-copy-{_copyCount}",
                        new IOException("Injected partial copy cleanup failure."))
                ],
                LastCopyCancellation);
        }

        if (FailCopyWithCleanupNumber == _copyCount)
        {
            LastCopyFailure = new InvalidOperationException("Injected structured copy failure.");
            throw new TemplateModelGenerationCleanupException(
                "Injected copy failure with an exact generation cleanup failure.",
                [
                    new TemplateModelGenerationCleanupFailure(
                        targetOwner,
                        $"partial-copy-{_copyCount}",
                        new IOException("Injected partial copy cleanup failure."))
                ],
                LastCopyFailure);
        }

        return Task.FromResult(sourceReference with
        {
            ModelPath = $"{targetOwner.RecipeId}/{targetOwner.FlowId}/{targetOwner.ToolId}/model-{_copyCount}.shm",
            MetadataPath = $"{targetOwner.RecipeId}/{targetOwner.FlowId}/{targetOwner.ToolId}/model-{_copyCount}.json",
            ModelChecksum = new string('1', 64),
            MetadataChecksum = new string('2', 64),
            Generation = $"copy-{_copyCount}"
        });
    }

    public Task DeleteOwnerResourcesAsync(TemplateModelOwner owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeletedOwners.Add(owner);
        OnDeleted?.Invoke(owner);
        Events.Add($"delete:{owner.FlowId}/{owner.ToolId}");
        if (owner == CancelDeleteOwner)
        {
            throw new OperationCanceledException("Injected owner deletion cancellation.");
        }

        if (owner == FailDeleteOwner)
        {
            throw new InvalidOperationException("Injected owner deletion failure.");
        }

        return Task.CompletedTask;
    }

    public Task DeleteGenerationAsync(
        TemplateModelOwner owner,
        TemplateModelReference reference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteGenerationCalls.Add((owner, reference));
        if (CancelDeleteGeneration)
        {
            throw new OperationCanceledException("Injected generation cleanup cancellation.");
        }

        if (FailDeleteGeneration)
        {
            throw new InvalidOperationException("Injected generation cleanup failure.");
        }

        if (DeleteGenerationFailuresRemaining > 0)
        {
            DeleteGenerationFailuresRemaining--;
            throw new InvalidOperationException("Injected generation cleanup failure.");
        }

        DeletedGenerations.Add((owner, reference));
        Events.Add($"delete-generation:{owner.FlowId}/{owner.ToolId}/{reference.Generation}");
        return Task.CompletedTask;
    }
}

internal sealed class RecordingRetirementSink(List<string> events) : ITemplateModelRetirementSink
{
    public List<TemplateModelOwner> Owners { get; } = [];

    public TemplateModelOwner? FailOwner { get; init; }

    public TemplateModelOwner? CancelOwner { get; init; }

    public Action<TemplateModelOwner>? OnRetired { get; init; }

    public ValueTask RetireAsync(TemplateModelOwner owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Owners.Add(owner);
        events.Add($"retire:{owner.FlowId}/{owner.ToolId}");
        OnRetired?.Invoke(owner);
        if (owner == CancelOwner)
        {
            throw new OperationCanceledException("Injected retirement cancellation.");
        }

        if (owner == FailOwner)
        {
            throw new InvalidOperationException("Injected retire failure.");
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingResourceManager(List<string> events) : ITemplateModelResourceManager
{
    public bool FailPrepare { get; init; }

    public bool FailDelete { get; init; }

    public int ReservationReleaseFailures { get; init; }

    public IReadOnlyList<TemplateModelGenerationCleanupFailure> RollbackFailures { get; init; } =
        Array.Empty<TemplateModelGenerationCleanupFailure>();

    public RecordingCopySession? LastSession { get; private set; }

    public Recipe? LastDeletedRecipe { get; private set; }

    public Task<TemplateRecipeCopySession> PrepareRecipeCopyAsync(
        Recipe source,
        string newRecipeId,
        CancellationToken cancellationToken)
    {
        events.Add("prepare");
        if (FailPrepare)
        {
            throw new InvalidOperationException("Injected prepare failure.");
        }

        LastSession = new RecordingCopySession(
            source.WithNormalizedFlows() with { Id = newRecipeId },
            events,
            ReservationReleaseFailures,
            RollbackFailures);
        return Task.FromResult<TemplateRecipeCopySession>(LastSession);
    }

    public Task RetireToolAsync(TemplateModelOwner owner, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task DeleteRecipeResourcesAsync(Recipe deletedRecipe, CancellationToken cancellationToken)
    {
        events.Add("resources-delete");
        LastDeletedRecipe = deletedRecipe;
        if (FailDelete)
        {
            throw new InvalidOperationException("Injected resource cleanup failure.");
        }

        return Task.CompletedTask;
    }
}

internal sealed class RecordingCopySession(
    Recipe recipe,
    List<string> events,
    int reservationReleaseFailures,
    IReadOnlyList<TemplateModelGenerationCleanupFailure>? rollbackFailures = null) : TemplateRecipeCopySession
{
    private bool _rolledBack;

    public override Recipe Recipe { get; } = recipe;

    public bool Committed { get; private set; }

    public bool Disposed { get; private set; }

    public int ReleaseAttempts { get; private set; }

    protected override ValueTask RollbackAsync()
    {
        events.Add("dispose");
        _rolledBack = true;
        if (rollbackFailures is { Count: > 0 })
        {
            throw new TemplateModelGenerationCleanupException(
                "Injected exact template generation rollback failure.",
                rollbackFailures);
        }

        Disposed = true;
        return ValueTask.CompletedTask;
    }

    protected override Exception? TryReleaseReservation()
    {
        ReleaseAttempts++;
        if (!_rolledBack)
        {
            events.Add("commit");
        }

        if (ReleaseAttempts <= reservationReleaseFailures)
        {
            return new InvalidOperationException("Injected reservation release failure.");
        }

        Committed = !_rolledBack;
        return null;
    }
}

internal sealed class ThrowingReleaseCopySession(Recipe recipe) : TemplateRecipeCopySession
{
    public override Recipe Recipe { get; } = recipe;

    public int ReleaseAttempts { get; private set; }

    public bool RolledBack { get; private set; }

    protected override ValueTask RollbackAsync()
    {
        RolledBack = true;
        return ValueTask.CompletedTask;
    }

    protected override Exception? TryReleaseReservation()
    {
        ReleaseAttempts++;
        if (ReleaseAttempts == 1)
        {
            throw new InvalidOperationException("Injected release hook contract violation.");
        }

        return null;
    }
}

internal sealed class RecordingRecipeRepository(List<string> events) : IRecipeRepository
{
    public Dictionary<string, Recipe> StoredRecipes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<Recipe> SavedRecipes { get; } = [];

    public bool FailSaveAfterWriting { get; init; }

    public bool FailCreate { get; init; }

    public bool CancelCreate { get; init; }

    public OperationCanceledException? LastCreateCancellation { get; private set; }

    public bool FailDelete { get; init; }

    public Task<Recipe> GetCurrentAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(StoredRecipes.Values.First());

    public Task<string> GetCurrentRecipeIdAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(StoredRecipes.Keys.FirstOrDefault() ?? "default");

    public Task SetCurrentRecipeAsync(string recipeId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<Recipe?> GetAsync(string recipeId, CancellationToken cancellationToken = default)
    {
        events.Add("target-check");
        StoredRecipes.TryGetValue(recipeId, out var recipe);
        return Task.FromResult(recipe);
    }

    public Task<IReadOnlyList<Recipe>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Recipe>>(StoredRecipes.Values.ToArray());

    public Task<Recipe> CreateAsync(Recipe recipe, CancellationToken cancellationToken = default)
    {
        events.Add("create");
        if (FailCreate)
        {
            throw new InvalidOperationException("Injected create failure.");
        }

        if (CancelCreate)
        {
            LastCreateCancellation = new OperationCanceledException("Injected create cancellation.");
            throw LastCreateCancellation;
        }

        if (StoredRecipes.ContainsKey(recipe.Id))
        {
            throw new InvalidOperationException("Recipe already exists.");
        }

        var stored = WithStorageRevision(recipe);
        StoredRecipes.Add(recipe.Id, stored);
        return Task.FromResult(stored);
    }

    public Task<Recipe> SaveAsync(Recipe recipe, CancellationToken cancellationToken = default)
    {
        events.Add("save");
        SavedRecipes.Add(recipe);
        var stored = WithStorageRevision(recipe);
        StoredRecipes[recipe.Id] = stored;
        if (FailSaveAfterWriting)
        {
            throw new InvalidOperationException("Injected save failure after partial write.");
        }

        return Task.FromResult(stored);
    }

    public Task DeleteAsync(string recipeId, CancellationToken cancellationToken = default)
    {
        events.Add("repo-delete");
        if (FailDelete)
        {
            throw new InvalidOperationException("Injected delete failure.");
        }

        StoredRecipes.Remove(recipeId);
        return Task.CompletedTask;
    }

    public Task<RecipeMutationSession> BeginMutationAsync(
        string recipeId,
        CancellationToken cancellationToken = default)
    {
        events.Add("mutation-begin");
        return Task.FromResult<RecipeMutationSession>(
            new RecordingRecipeMutationSession(this, recipeId, events));
    }

    private sealed class RecordingRecipeMutationSession(
        RecordingRecipeRepository repository,
        string recipeId,
        List<string> events) : RecipeMutationSession
    {
        public override string RecipeId { get; } = recipeId;

        public override async Task<Recipe?> GetAsync(
            CancellationToken cancellationToken = default)
        {
            events.Add("target-check");
            repository.StoredRecipes.TryGetValue(RecipeId, out var recipe);
            await Task.CompletedTask;
            return recipe;
        }

        public override Task<Recipe> CreateAsync(
            Recipe recipe,
            CancellationToken cancellationToken = default)
        {
            return repository.CreateAsync(recipe, cancellationToken);
        }

        public override Task<Recipe> UpdateAsync(
            Recipe recipe,
            CancellationToken cancellationToken = default)
        {
            if (!repository.StoredRecipes.TryGetValue(RecipeId, out var stored) ||
                !string.Equals(
                    stored.StorageRevision,
                    recipe.StorageRevision,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Recipe revision changed.");
            }

            return repository.SaveAsync(recipe, cancellationToken);
        }

        public override Task DeleteAsync(
            Recipe expected,
            CancellationToken cancellationToken = default)
        {
            if (!repository.StoredRecipes.TryGetValue(RecipeId, out var recipe) ||
                !string.Equals(
                    recipe.StorageRevision,
                    expected.StorageRevision,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Recipe revision changed.");
            }

            return repository.DeleteAsync(RecipeId, cancellationToken);
        }

        public override ValueTask DisposeAsync()
        {
            events.Add("mutation-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private static Recipe WithStorageRevision(Recipe recipe)
    {
        var revision = recipe.Name;
        return recipe with { StorageRevision = revision };
    }
}

internal sealed class RecordingLog : IAppLogService
{
    public event EventHandler<AppLogEntry>? LogWritten
    {
        add { }
        remove { }
    }

    public List<string> Warnings { get; } = [];

    public void Info(string source, string message)
    {
    }

    public void Warning(string source, string message)
    {
        Warnings.Add(message);
    }

    public void Error(string source, string message)
    {
    }

    public void Critical(string source, string message)
    {
    }

    public IReadOnlyList<AppLogEntry> Recent(int count) => Array.Empty<AppLogEntry>();
}

internal sealed class ThrowingDisposeFileStream(string path) : FileStream(
    path,
    FileMode.Create,
    FileAccess.ReadWrite,
    FileShare.None)
{
    private bool _thrown;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && !_thrown)
        {
            _thrown = true;
            throw new IOException("Injected file-lock dispose failure.");
        }
    }
}
