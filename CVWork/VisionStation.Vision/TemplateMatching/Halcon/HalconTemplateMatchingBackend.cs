using VisionStation.Domain;

namespace VisionStation.Vision;

internal sealed class HalconTemplateMatchingBackend : ITemplateMatchingBackend
{
    private readonly object _disposeSync = new();
    private readonly IHalconTemplateLearner _learner;
    private readonly ITemplateModelStore _modelStore;
    private readonly IHalconRuntimeProbe _runtimeProbe;
    private readonly HalconTemplateModelCache _modelCache;
    private readonly IHalconOperationScheduler _scheduler;
    private readonly IHalconCandidateSource _candidateSource;
    private readonly ITemplateCandidateEvidenceBuilder _evidenceBuilder;
    private readonly TemplateCandidateValidator _validator;
    private readonly ITemplateMatchingDiagnosticSink _diagnostics;
    private Task? _disposeTask;

    public HalconTemplateMatchingBackend(
        IHalconTemplateLearner learner,
        ITemplateModelStore modelStore,
        IHalconRuntimeProbe runtimeProbe,
        HalconTemplateModelCache modelCache,
        IHalconOperationScheduler scheduler,
        IHalconCandidateSource candidateSource,
        ITemplateCandidateEvidenceBuilder evidenceBuilder,
        TemplateCandidateValidator validator,
        ITemplateMatchingDiagnosticSink diagnostics)
    {
        _learner = learner ?? throw new ArgumentNullException(nameof(learner));
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _runtimeProbe = runtimeProbe ?? throw new ArgumentNullException(nameof(runtimeProbe));
        _modelCache = modelCache ?? throw new ArgumentNullException(nameof(modelCache));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _candidateSource = candidateSource ?? throw new ArgumentNullException(nameof(candidateSource));
        _evidenceBuilder = evidenceBuilder ?? throw new ArgumentNullException(nameof(evidenceBuilder));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public TemplateMatchingEngine Engine => TemplateMatchingEngine.Halcon;

    public Task<TemplateLearningResult> LearnAsync(
        TemplateLearningRequest request,
        CancellationToken cancellationToken)
    {
        return _learner.LearnAsync(request, cancellationToken);
    }

    public async Task<TemplateMatchBatchResult> MatchAsync(
        TemplateMatchingRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HalconTemplateMatchingParameters parameters;
        HalconTemplateModelState modelState;
        try
        {
            (parameters, modelState) = ValidateManagedRequest(request);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TemplateMatchingConfigurationException exception)
        {
            return Failed(request, ToDiagnostic(exception));
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return Failed(
                request,
                TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                    $"HALCON matching request validation failed; ExceptionType={exception.GetType().Name}."));
        }

        ValidatedHalconModelDescriptor descriptor;
        try
        {
            ResolvedTemplateModel resolved = await _modelStore.ResolveAsync(
                request.Owner,
                modelState.Reference,
                cancellationToken).ConfigureAwait(false);
            descriptor = HalconModelMetadataValidator.Validate(
                resolved,
                request.Owner,
                modelState,
                parameters);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TemplateModelStoreException exception)
        {
            return Failed(
                request,
                new TemplateMatchingDiagnostic(
                    exception.Code,
                    exception.Message,
                    exception.FailureStage,
                    exception.TechnicalDetails));
        }
        catch (TemplateMatchingConfigurationException exception)
        {
            return Failed(request, ToDiagnostic(exception));
        }
        catch (Exception exception)
        {
            return UnexpectedFailure(request, exception, "resolve-model");
        }

        HalconRuntimeProbeResult runtime;
        try
        {
            runtime = await _runtimeProbe
                .EnsureReadyAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return UnexpectedFailure(request, exception, "runtime-probe");
        }

        if (!runtime.IsReady)
        {
            return Failed(
                request,
                runtime.Diagnostic ?? TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.RuntimeNotFound,
                    "HALCON runtime probe returned no descriptor or diagnostic."));
        }

        try
        {
            await using HalconTemplateModelLease modelLease = await _modelCache.AcquireAsync(
                request.Owner,
                descriptor,
                cancellationToken).ConfigureAwait(false);
            HalconCandidateBatch candidateBatch;
            await using (HalconTemplateModelOperationLease operation =
                         await modelLease.EnterOperationAsync(cancellationToken)
                             .ConfigureAwait(false))
            {
                candidateBatch = await _candidateSource.FindAsync(
                    operation,
                    request.Frame,
                    request.SearchRoi,
                    parameters,
                    cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<TemplateCandidateEvidence> evidence = _evidenceBuilder.BuildBatch(
                request.Frame,
                request.SearchRoi,
                descriptor.Metadata,
                candidateBatch.Candidates,
                parameters,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            TemplateCandidateValidationResult validation = _validator.ValidateAndDeduplicate(
                evidence,
                descriptor.Metadata,
                parameters,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return ProjectResult(
                request,
                candidateBatch,
                validation,
                descriptor.Metadata,
                parameters);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HalconOperatorFailure exception)
        {
            return Failed(
                request,
                TemplateMatchingDiagnostics.Create(
                    exception.Code,
                    exception.TechnicalDetails ?? $"ExceptionType={exception.GetType().Name}."));
        }
        catch (TemplateMatchingConfigurationException exception)
        {
            return Failed(request, ToDiagnostic(exception));
        }
        catch (Exception exception)
        {
            return UnexpectedFailure(request, exception, "match");
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeOwnedResourcesAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private static (HalconTemplateMatchingParameters Parameters, HalconTemplateModelState State)
        ValidateManagedRequest(TemplateMatchingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateOwner(request.Owner);
        TemplateMatchingEngine resolved = TemplateMatchingEngineResolver.Resolve(request.Parameters);
        if (resolved != TemplateMatchingEngine.Halcon)
        {
            throw new TemplateMatchingConfigurationException(
                TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                    "The HALCON backend received parameters for a different engine."));
        }

        TemplateMatchingEngineResolver.EnsureHalconShapeMode(
            request.Parameters,
            request.Cardinality == TemplateMatchCardinality.ExactCount);
        HalconTemplateMatchingParameters parameters = TemplateMatchingParameterCatalog.ParseHalcon(
            request.Parameters,
            request.Cardinality);
        if (request.Cardinality == TemplateMatchCardinality.ExactCount &&
            request.ExpectedCount != parameters.ExpectedCount)
        {
            throw new TemplateMatchingConfigurationException(
                TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                    $"HALCON request ExpectedCount={request.ExpectedCount} differs from persisted ExpectedCount={parameters.ExpectedCount}."));
        }

        HalconTemplateModelState? state = TemplateModelParameterCodec.ReadHalcon(request.Parameters);
        if (state is null)
        {
            throw new TemplateMatchingConfigurationException(
                TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.ModelRelearnRequired,
                    "The active HALCON tool has no complete learned model state."));
        }

        HalconScaledShapeCandidateSource.ValidateInput(request.Frame, request.SearchRoi);

        return (parameters, state);
    }

    private static void ValidateOwner(TemplateModelOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!IsRequired(owner.RecipeId) ||
            !IsRequired(owner.FlowId) ||
            !IsRequired(owner.ToolId))
        {
            throw new TemplateMatchingConfigurationException(
                TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                    "HALCON matching owner recipe/flow/tool identifiers must be non-empty and trimmed."));
        }
    }

    private static TemplateMatchBatchResult ProjectResult(
        TemplateMatchingRequest request,
        HalconCandidateBatch candidateBatch,
        TemplateCandidateValidationResult validation,
        HalconTemplateModelMetadata metadata,
        HalconTemplateMatchingParameters parameters)
    {
        List<TemplateMatchBatchCandidate> accepted = validation.Accepted
            .Select(evidence => ProjectCandidate(evidence, metadata))
            .ToList();
        TemplateMatchingDiagnostic? firstRejection = validation.Decisions
            .FirstOrDefault(decision => !decision.Accepted)
            ?.Diagnostic;

        if (request.Cardinality == TemplateMatchCardinality.Single)
        {
            if (accepted.Count > 0)
            {
                return new TemplateMatchBatchResult(
                    TemplateMatchingEngine.Halcon,
                    InspectionOutcome.Ok,
                    true,
                    [accepted[0]],
                    candidateBatch.SearchRegion,
                    "HALCON template matched.",
                    false);
            }

            return new TemplateMatchBatchResult(
                TemplateMatchingEngine.Halcon,
                InspectionOutcome.Ng,
                false,
                Array.Empty<TemplateMatchBatchCandidate>(),
                candidateBatch.SearchRegion,
                firstRejection?.UserMessage ?? "No HALCON template candidate passed validation.",
                false,
                firstRejection);
        }

        int actualCount = accepted.Count;
        int expectedCount = parameters.ExpectedCount;
        if (actualCount > expectedCount)
        {
            return ExactCountNg(
                candidateBatch.SearchRegion,
                accepted,
                actualCount,
                expectedCount,
                CreateCountMismatch(actualCount, expectedCount));
        }

        if (candidateBatch.LimitReached)
        {
            TemplateMatchingDiagnostic limit = TemplateMatchingDiagnostics.Create(
                TemplateMatchingDiagnosticCodes.MatchCandidateLimitReached,
                $"HALCON returned CandidateLimit={parameters.CandidateLimit}; accepted={actualCount}; expected={expectedCount}.");
            return ExactCountNg(
                candidateBatch.SearchRegion,
                accepted,
                actualCount,
                expectedCount,
                limit);
        }

        if (actualCount == expectedCount)
        {
            return new TemplateMatchBatchResult(
                TemplateMatchingEngine.Halcon,
                InspectionOutcome.Ok,
                true,
                accepted,
                candidateBatch.SearchRegion,
                $"HALCON exact-count matched {actualCount} target(s).",
                false);
        }

        return ExactCountNg(
            candidateBatch.SearchRegion,
            accepted,
            actualCount,
            expectedCount,
            firstRejection ?? CreateCountMismatch(actualCount, expectedCount));
    }

    private static TemplateMatchBatchResult ExactCountNg(
        TemplateSearchRegion searchRegion,
        IReadOnlyList<TemplateMatchBatchCandidate> accepted,
        int actualCount,
        int expectedCount,
        TemplateMatchingDiagnostic? diagnostic)
    {
        return new TemplateMatchBatchResult(
            TemplateMatchingEngine.Halcon,
            InspectionOutcome.Ng,
            false,
            accepted,
            searchRegion,
            diagnostic?.Code == TemplateMatchingDiagnosticCodes.MatchCountMismatch
                ? $"{diagnostic.UserMessage} 实际 {actualCount}，期望 {expectedCount}。"
                : diagnostic?.UserMessage ??
                  $"HALCON exact-count NG, found {actualCount}, required {expectedCount}.",
            false,
            diagnostic);
    }

    private static TemplateMatchingDiagnostic CreateCountMismatch(
        int actualCount,
        int expectedCount)
    {
        return TemplateMatchingDiagnostics.Create(
            TemplateMatchingDiagnosticCodes.MatchCountMismatch,
            $"HALCON exact-count mismatch; accepted={actualCount}; expected={expectedCount}.");
    }

    private static TemplateMatchBatchCandidate ProjectCandidate(
        TemplateCandidateEvidence evidence,
        HalconTemplateModelMetadata metadata)
    {
        IReadOnlyList<IReadOnlyList<Point2D>> shapeContours =
            [evidence.OuterContour, .. evidence.InnerFeatureGroups];
        IReadOnlyList<IReadOnlyList<Point2D>> roiContours =
            evidence.TemplateRoiContour.Count == 0
                ? Array.Empty<IReadOnlyList<Point2D>>()
                : [evidence.TemplateRoiContour];
        return new TemplateMatchBatchCandidate(
            evidence.Candidate.Pose,
            evidence.Candidate.Score,
            metadata.TemplateWidth,
            metadata.TemplateHeight,
            shapeContours,
            roiContours)
        {
            OuterCoverage = evidence.OuterCoverage,
            InnerCoverage = evidence.InnerCoverage,
            EdgeDistanceP95Px = evidence.EdgeDistanceP95Px,
            PolarityAgreement = evidence.PolarityAgreement
        };
    }

    private TemplateMatchBatchResult UnexpectedFailure(
        TemplateMatchingRequest request,
        Exception exception,
        string operation)
    {
        TryLogError(
            nameof(HalconTemplateMatchingBackend),
            $"Unexpected HALCON {operation} failure. {exception}");
        return Failed(
            request,
            TemplateMatchingDiagnostics.Create(
                TemplateMatchingDiagnosticCodes.MatchOperatorFailed,
                $"Operation={operation}; ExceptionType={exception.GetType().Name}."));
    }

    private async Task DisposeOwnedResourcesAsync()
    {
        var failures = new List<Exception>();
        try
        {
            await _modelCache.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await _scheduler.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count == 1)
        {
            throw failures[0];
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(
                "HALCON cache and scheduler both failed during shutdown.",
                failures);
        }
    }

    private static TemplateMatchBatchResult Failed(
        TemplateMatchingRequest request,
        TemplateMatchingDiagnostic diagnostic)
    {
        return new TemplateMatchBatchResult(
            TemplateMatchingEngine.Halcon,
            InspectionOutcome.Ng,
            false,
            Array.Empty<TemplateMatchBatchCandidate>(),
            SafeSearchRegion(request),
            diagnostic.UserMessage,
            false,
            diagnostic);
    }

    private static TemplateSearchRegion SafeSearchRegion(TemplateMatchingRequest request)
    {
        try
        {
            return TemplateMatcher.GetSearchRegion(request.Frame, request.SearchRoi);
        }
        catch
        {
            return new TemplateSearchRegion(
                0,
                0,
                Math.Max(1, request.Frame.Width),
                Math.Max(1, request.Frame.Height));
        }
    }

    private static TemplateMatchingDiagnostic ToDiagnostic(
        TemplateMatchingConfigurationException exception)
    {
        return new TemplateMatchingDiagnostic(
            exception.Code,
            exception.Message,
            exception.FailureStage,
            exception.TechnicalDetails);
    }

    private void TryLogError(string source, string message)
    {
        try
        {
            _diagnostics.Error(source, message);
        }
        catch
        {
            // Diagnostic logging is non-operational and cannot replace the stable match result.
        }
    }

    private static bool IsRequired(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);
}
