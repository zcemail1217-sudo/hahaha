using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using VisionStation.Domain;

namespace VisionStation.Vision;

internal sealed class TemplateModelResourceManager : ITemplateModelResourceManager
{
    private static readonly ConditionalWeakTable<ITemplateModelStore, RecipeReservations> Reservations = new();

    private readonly ITemplateModelStore _store;
    private readonly ITemplateModelRetirementSink _retirementSink;
    private readonly IAppLogService _log;
    private readonly RecipeReservations _reservations;

    internal TemplateModelResourceManager(
        ITemplateModelStore store,
        ITemplateModelRetirementSink retirementSink,
        IAppLogService log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _retirementSink = retirementSink ?? throw new ArgumentNullException(nameof(retirementSink));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _reservations = Reservations.GetValue(_store, static _ => new RecipeReservations());
    }

    public async Task<TemplateRecipeCopySession> PrepareRecipeCopyAsync(
        Recipe source,
        string newRecipeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateNewRecipeId(source.Id, newRecipeId);
        cancellationToken.ThrowIfCancellationRequested();
        ReserveRecipeId(newRecipeId);

        var copiedGenerations = new List<CopiedGeneration>();
        try
        {
            var normalized = CloneRecipeGraph(source);
            var copiedFlows = new List<VisionFlowDefinition>(normalized.EffectiveFlows.Count);
            foreach (var flow in normalized.EffectiveFlows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var copiedTools = new List<VisionToolDefinition>(flow.Tools.Count);
                foreach (var tool in flow.Tools)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var state = TemplateModelParameterCodec.ReadHalcon(tool.Parameters);
                    var parameters = new Dictionary<string, string>(
                        tool.Parameters,
                        StringComparer.OrdinalIgnoreCase);
                    if (state is not null)
                    {
                        var sourceOwner = new TemplateModelOwner(source.Id, flow.Id, tool.Id);
                        var targetOwner = new TemplateModelOwner(newRecipeId, flow.Id, tool.Id);
                        var copiedReference = await _store.CopyGenerationAsync(
                            sourceOwner,
                            state.Reference,
                            targetOwner,
                            cancellationToken).ConfigureAwait(false);
                        copiedGenerations.Add(new CopiedGeneration(targetOwner, copiedReference));
                        TemplateModelParameterCodec.WriteHalcon(
                            parameters,
                            state with { Reference = copiedReference });
                    }

                    copiedTools.Add(tool with { Parameters = parameters });
                }

                copiedFlows.Add(flow with { Tools = copiedTools.ToArray() });
            }

            var copiedRecipe = (normalized with
            {
                Id = newRecipeId,
                Flows = copiedFlows.ToArray(),
                UpdatedAt = DateTimeOffset.Now
            }).WithNormalizedFlows();
            return new ResourceCopySession(
                copiedRecipe,
                _store,
                copiedGenerations,
                () => ReleaseRecipeId(newRecipeId));
        }
        catch (Exception copyFailure)
        {
            List<GenerationCleanupFailure> cleanupFailures;
            try
            {
                cleanupFailures = await CleanupGenerationsAsync(
                    _store,
                    copiedGenerations,
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                ReleaseRecipeId(newRecipeId);
            }

            var storeCleanupFailure = copyFailure as TemplateModelGenerationCleanupException;
            var primaryFailure = storeCleanupFailure?.PrimaryException ?? copyFailure;
            var exactCleanupFailures = new List<TemplateModelGenerationCleanupFailure>();
            if (storeCleanupFailure is not null)
            {
                exactCleanupFailures.AddRange(storeCleanupFailure.Failures);
            }

            exactCleanupFailures.AddRange(cleanupFailures.Select(ToExactCleanupFailure));

            if (primaryFailure is OperationCanceledException)
            {
                LogCancellationOrphans(exactCleanupFailures);
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
                throw new InvalidOperationException("Unreachable template copy cancellation path.");
            }

            if (storeCleanupFailure is not null)
            {
                throw new TemplateModelGenerationCleanupException(
                    "Template recipe copy failed and one or more exact target generations could not be cleaned.",
                    exactCleanupFailures,
                    storeCleanupFailure.PrimaryException);
            }

            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    "Template recipe copy failed and one or more target owners could not be cleaned.",
                    [copyFailure, .. cleanupFailures.Select(failure => failure.Exception)]);
            }

            throw;
        }
    }

    public async Task RetireToolAsync(
        TemplateModelOwner owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        await _retirementSink.RetireAsync(owner, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteRecipeResourcesAsync(
        Recipe deletedRecipe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deletedRecipe);
        cancellationToken.ThrowIfCancellationRequested();
        var owners = GetReferencedOwners(deletedRecipe);
        var retireFailures = new List<Exception>();
        foreach (var owner in owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _retirementSink.RetireAsync(owner, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                retireFailures.Add(exception);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (retireFailures.Count > 0)
        {
            throw new AggregateException(
                "One or more template model owners could not be retired; persistent files were kept.",
                retireFailures);
        }

        var deleteFailures = new List<Exception>();
        foreach (var owner in owners)
        {
            try
            {
                // All runtime handles are retired. Persistent deletion is now a
                // convergence phase and must not stop between owners on late caller cancellation.
                await _store.DeleteOwnerResourcesAsync(
                    owner,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                deleteFailures.Add(exception);
            }
        }

        if (deleteFailures.Count > 0)
        {
            throw new AggregateException(
                "One or more retired template model owners could not be deleted.",
                deleteFailures);
        }
    }

    private static IReadOnlyList<TemplateModelOwner> GetReferencedOwners(Recipe recipe)
    {
        var owners = new List<TemplateModelOwner>();
        var uniqueOwners = new HashSet<TemplateModelOwner>();
        foreach (var flow in recipe.WithNormalizedFlows().EffectiveFlows)
        {
            foreach (var tool in flow.Tools)
            {
                if (TemplateModelParameterCodec.ReadHalcon(tool.Parameters) is null)
                {
                    continue;
                }

                var owner = new TemplateModelOwner(recipe.Id, flow.Id, tool.Id);
                if (uniqueOwners.Add(owner))
                {
                    owners.Add(owner);
                }
            }
        }

        return owners;
    }

    private static Recipe CloneRecipeGraph(Recipe source)
    {
        var normalized = source.WithNormalizedFlows();
        var clonedFlows = normalized.EffectiveFlows
            .Select(CloneFlow)
            .ToArray();

        // Recipe is a mutable object graph despite its record shells. Keep this explicit
        // field list synchronized whenever a new Recipe collection or nested reference is added.
        return (normalized with
        {
            StorageRevision = string.Empty,
            Camera = CloneCamera(normalized.Camera),
            ProductParameters = normalized.ProductParameters.Select(item => item with { }).ToArray(),
            Variables = normalized.Variables.Select(item => item with { }).ToArray(),
            SignalMappings = normalized.SignalMappings.Select(item => item with { }).ToArray(),
            Rois = Array.Empty<RoiDefinition>(),
            Tools = Array.Empty<VisionToolDefinition>(),
            Flows = clonedFlows,
            MotionSequences = normalized.MotionSequences.Select(CloneMotionSequence).ToArray(),
            ProcessSteps = normalized.ProcessSteps.Select(CloneProcessStep).ToArray(),
            VisionResults = normalized.VisionResults.Select(item => item with { }).ToArray(),
            PlcSignals = normalized.PlcSignals.Select(item => item with { }).ToArray(),
            TracePolicy = normalized.TracePolicy with { }
        }).WithNormalizedFlows();
    }

    private static CameraSettings CloneCamera(CameraSettings source)
    {
        return source with
        {
            CameraCalibration = source.CameraCalibration is null
                ? null
                : CloneCameraCalibration(source.CameraCalibration),
            PlaneCalibration = source.PlaneCalibration is null
                ? null
                : ClonePlaneCalibration(source.PlaneCalibration)
        };
    }

    private static CameraCalibrationResult CloneCameraCalibration(CameraCalibrationResult source)
    {
        return source with
        {
            Pattern = source.Pattern with { },
            CameraMatrix = source.CameraMatrix.ToArray(),
            DistortionCoefficients = source.DistortionCoefficients.ToArray(),
            Views = source.Views.Select(CloneCameraCalibrationView).ToArray()
        };
    }

    private static CameraCalibrationViewResult CloneCameraCalibrationView(
        CameraCalibrationViewResult source)
    {
        return source with
        {
            RotationVector = source.RotationVector.ToArray(),
            TranslationVector = source.TranslationVector.ToArray()
        };
    }

    private static PlaneCalibrationResult ClonePlaneCalibration(PlaneCalibrationResult source)
    {
        return source with
        {
            ImageToWorldMatrix = source.ImageToWorldMatrix.ToArray(),
            WorldToImageMatrix = source.WorldToImageMatrix.ToArray(),
            PointErrors = source.PointErrors.Select(ClonePlaneCalibrationPointError).ToArray()
        };
    }

    private static PlaneCalibrationPointError ClonePlaneCalibrationPointError(
        PlaneCalibrationPointError source)
    {
        return source with
        {
            ImagePoint = source.ImagePoint with { },
            ExpectedWorldPoint = source.ExpectedWorldPoint with { },
            MappedWorldPoint = source.MappedWorldPoint with { }
        };
    }

    private static VisionFlowDefinition CloneFlow(VisionFlowDefinition source)
    {
        return source with
        {
            Rois = source.Rois.Select(CloneRoi).ToArray(),
            Tools = source.Tools.Select(CloneTool).ToArray()
        };
    }

    private static RoiDefinition CloneRoi(RoiDefinition source)
    {
        return source with
        {
            Points = source.Points.Select(point => point with { }).ToArray()
        };
    }

    private static VisionToolDefinition CloneTool(VisionToolDefinition source)
    {
        return source with
        {
            Parameters = CloneParameters(source.Parameters)
        };
    }

    private static MotionSequenceDefinition CloneMotionSequence(MotionSequenceDefinition source)
    {
        return source with
        {
            Steps = source.Steps.Select(CloneMotionStep).ToArray()
        };
    }

    private static MotionStepDefinition CloneMotionStep(MotionStepDefinition source)
    {
        return source with
        {
            Parameters = CloneParameters(source.Parameters)
        };
    }

    private static ProcessStepDefinition CloneProcessStep(ProcessStepDefinition source)
    {
        return source with
        {
            AxisTargets = source.AxisTargets.Select(item => item with { }).ToArray(),
            Parameters = CloneParameters(source.Parameters)
        };
    }

    private static Dictionary<string, string> CloneParameters(
        Dictionary<string, string> source)
    {
        return new Dictionary<string, string>(source, source.Comparer);
    }

    private static async Task<List<GenerationCleanupFailure>> CleanupGenerationsAsync(
        ITemplateModelStore store,
        IReadOnlyList<CopiedGeneration> copiedGenerations,
        CancellationToken cancellationToken)
    {
        var failures = new List<GenerationCleanupFailure>();
        for (var index = copiedGenerations.Count - 1; index >= 0; index--)
        {
            var copiedGeneration = copiedGenerations[index];
            try
            {
                await store.DeleteGenerationAsync(
                    copiedGeneration.Owner,
                    copiedGeneration.Reference,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(new GenerationCleanupFailure(copiedGeneration, exception));
            }
        }

        return failures;
    }

    private static TemplateModelGenerationCleanupFailure ToExactCleanupFailure(
        GenerationCleanupFailure failure)
    {
        return new TemplateModelGenerationCleanupFailure(
            failure.Generation.Owner,
            failure.Generation.Reference.Generation,
            failure.Exception);
    }

    private void LogCancellationOrphans(
        IReadOnlyList<TemplateModelGenerationCleanupFailure> cleanupFailures)
    {
        foreach (var cleanupFailure in cleanupFailures)
        {
            var owner = cleanupFailure.Owner;
            try
            {
                _log.Warning(
                    "TemplateModel",
                    $"Template recipe copy cancellation left target generation " +
                    $"'{cleanupFailure.Generation}' for owner " +
                    $"'{owner.RecipeId}/{owner.FlowId}/{owner.ToolId}': " +
                    cleanupFailure.CleanupException.Message);
            }
            catch
            {
                // Cancellation identity is part of the API contract. Logging must not
                // replace the original OperationCanceledException during convergence.
            }
        }
    }

    private void ReserveRecipeId(string recipeId)
    {
        _reservations.Reserve(recipeId);
    }

    private void ReleaseRecipeId(string recipeId)
    {
        _reservations.Release(recipeId);
    }

    private static void ValidateNewRecipeId(string sourceRecipeId, string newRecipeId)
    {
        if (string.IsNullOrWhiteSpace(newRecipeId) ||
            !string.Equals(newRecipeId, newRecipeId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A non-empty, trimmed target recipe id is required.", nameof(newRecipeId));
        }

        if (string.Equals(sourceRecipeId, newRecipeId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The target recipe id must differ from the source recipe id.",
                nameof(newRecipeId));
        }
    }

    private sealed class ResourceCopySession : TemplateRecipeCopySession
    {
        private readonly ITemplateModelStore _store;
        private readonly IReadOnlyList<CopiedGeneration> _copiedGenerations;
        private readonly Action _releaseReservation;

        public ResourceCopySession(
            Recipe recipe,
            ITemplateModelStore store,
            IReadOnlyList<CopiedGeneration> copiedGenerations,
            Action releaseReservation)
        {
            Recipe = recipe;
            _store = store;
            _copiedGenerations = copiedGenerations;
            _releaseReservation = releaseReservation;
        }

        public override Recipe Recipe { get; }

        protected override async ValueTask RollbackAsync()
        {
            var cleanupFailures = await CleanupGenerationsAsync(
                _store,
                _copiedGenerations,
                CancellationToken.None).ConfigureAwait(false);
            if (cleanupFailures.Count > 0)
            {
                throw new TemplateModelGenerationCleanupException(
                    "One or more uncommitted template copy generations could not be cleaned.",
                    cleanupFailures.Select(ToExactCleanupFailure).ToArray());
            }
        }

        protected override Exception? TryReleaseReservation()
        {
            try
            {
                _releaseReservation();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }
    }

    private sealed record CopiedGeneration(
        TemplateModelOwner Owner,
        TemplateModelReference Reference);

    private sealed record GenerationCleanupFailure(
        CopiedGeneration Generation,
        Exception Exception);

    private sealed class RecipeReservations
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _recipeIds = new(StringComparer.OrdinalIgnoreCase);

        public void Reserve(string recipeId)
        {
            lock (_gate)
            {
                if (!_recipeIds.Add(recipeId))
                {
                    throw new InvalidOperationException(
                        $"Template resources for target recipe '{recipeId}' are already being prepared.");
                }
            }
        }

        public void Release(string recipeId)
        {
            lock (_gate)
            {
                _recipeIds.Remove(recipeId);
            }
        }
    }
}
