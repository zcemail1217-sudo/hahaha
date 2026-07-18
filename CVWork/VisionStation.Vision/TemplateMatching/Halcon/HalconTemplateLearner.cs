using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using VisionStation.Domain;

namespace VisionStation.Vision;

internal sealed class HalconTemplateLearner
{
    private readonly IHalconRuntimeProbe _runtimeProbe;
    private readonly IHalconTemplateFeatureExtractor _featureExtractor;
    private readonly ITemplateModelStore _modelStore;
    private readonly IHalconOperationScheduler _scheduler;
    private readonly IHalconOperatorBackend _operators;

    public HalconTemplateLearner(
        IHalconRuntimeProbe runtimeProbe,
        IHalconTemplateFeatureExtractor featureExtractor,
        ITemplateModelStore modelStore,
        IHalconOperationScheduler scheduler,
        IHalconOperatorBackend operators)
    {
        _runtimeProbe = runtimeProbe ?? throw new ArgumentNullException(nameof(runtimeProbe));
        _featureExtractor = featureExtractor ?? throw new ArgumentNullException(nameof(featureExtractor));
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _operators = operators ?? throw new ArgumentNullException(nameof(operators));
    }

    public async Task<TemplateLearningResult> LearnAsync(
        TemplateLearningRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HalconTemplateMatchingParameters parameters;
        try
        {
            ValidateRequest(request);
            parameters = TemplateMatchingParameterCatalog.ParseHalcon(
                request.Parameters,
                ResolveCardinality(request.Parameters));
        }
        catch (TemplateMatchingConfigurationException exception)
        {
            return Failed(ToDiagnostic(exception));
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return Failed(TemplateMatchingDiagnostics.Create(
                TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                $"HALCON learning request validation failed; ExceptionType={exception.GetType().Name}."));
        }

        HalconTemplateInputValidationResult inputValidation = _featureExtractor.ValidateInput(
            request.Frame,
            request.TemplateRoi,
            cancellationToken);
        if (!inputValidation.Success)
        {
            return Failed(inputValidation.Diagnostic ?? TemplateMatchingDiagnostics.Create(
                TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                "HALCON input preflight returned no diagnostic."));
        }

        HalconRuntimeProbeResult runtime = await _runtimeProbe
            .EnsureReadyAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!runtime.IsReady)
        {
            return Failed(runtime.Diagnostic ?? TemplateMatchingDiagnostics.Create(
                TemplateMatchingDiagnosticCodes.RuntimeNotFound,
                "HALCON runtime probe returned neither a descriptor nor a diagnostic."));
        }

        HalconTemplateFeatureExtractionResult extraction = _featureExtractor.Extract(
            request.Frame,
            request.TemplateRoi,
            cancellationToken);
        if (!extraction.Success)
        {
            return Failed(extraction.Diagnostic ?? TemplateMatchingDiagnostics.Create(
                TemplateMatchingDiagnosticCodes.ModelTemplateIncomplete,
                "HALCON feature extraction returned no features or diagnostic."));
        }

        HalconTemplateFeatureSet features = extraction.Features!;
        TightGray8Image templateImage;
        try
        {
            TightGray8Image fullImage = HalconImageFactory.CreateTightGray8(request.Frame);
            templateImage = HalconImageFactory.Crop(
                fullImage,
                features.CropOriginX,
                features.CropOriginY,
                features.TemplateWidth,
                features.TemplateHeight);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return Failed(TemplateMatchingDiagnostics.Create(
                TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                $"HALCON template image conversion failed; ExceptionType={exception.GetType().Name}."));
        }

        TemplateModelGenerationParameters generationParameters =
            TemplateModelGenerationParameters.From(parameters);
        string generationFingerprint =
            TemplateModelGenerationFingerprint.Compute(generationParameters);
        var geometry = new TemplateLearnedGeometry(
            new Pose2D(
                features.CropOriginX + features.ReferenceColumn,
                features.CropOriginY + features.ReferenceRow,
                0)
            {
                Scale = 1
            },
            features.TemplateWidth,
            features.TemplateHeight);
        var creationRequest = new HalconShapeModelCreationRequest(
            templateImage,
            features.ModelDomain,
            generationParameters,
            features.ReferenceRow,
            features.ReferenceColumn);

        TemplateModelWriteSession? session = null;
        OperationCanceledException? cancellation = null;
        TemplateModelReference? committedReference = null;
        TemplateMatchingDiagnostic? primaryFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            try
            {
                session = await _modelStore
                    .BeginWriteAsync(request.Owner, cancellationToken)
                    .ConfigureAwait(false);
                await _scheduler.RunAsync(
                    () =>
                    {
                        _operators.CreateAndWriteShapeModel(creationRequest, session.StagingModelPath);
                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                await _scheduler.RunAsync(
                    () =>
                    {
                        using IHalconRawModelHandle handle =
                            _operators.LoadShapeModelAndValidate(session.StagingModelPath);
                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                string modelChecksum = await ComputeSha256Async(
                    session.StagingModelPath,
                    cancellationToken).ConfigureAwait(false);
                var metadata = new HalconTemplateModelMetadata(
                    request.Owner,
                    session.Generation,
                    $"model-{session.Generation}.shm",
                    modelChecksum,
                    geometry,
                    features.ReferenceRow,
                    features.ReferenceColumn,
                    features.ModelDomain.CentroidRow,
                    features.ModelDomain.CentroidColumn,
                    features.IsDarkForeground,
                    features.OuterContour,
                    features.InnerFeatureGroups,
                    features.MinimumValidInnerGroupCount,
                    features.FilledSupport,
                    generationParameters,
                    generationFingerprint,
                    HalconTemplateValidationDefaults.From(parameters));
                byte[] metadataJson = HalconTemplateModelMetadataJson.Serialize(metadata);
                committedReference = await _modelStore
                    .CommitAsync(session, metadataJson, cancellationToken)
                    .ConfigureAwait(false);

                // Commit is the durable publication point. Do not observe cancellation after it.
            }
            catch (OperationCanceledException exception)
            {
                cancellation = exception;
            }
            catch (HalconOperatorFailure exception)
            {
                primaryFailure = TemplateMatchingDiagnostics.Create(
                    exception.Code,
                    exception.TechnicalDetails ?? $"ExceptionType={exception.GetType().Name}.");
            }
            catch (TemplateMatchingConfigurationException exception)
            {
                primaryFailure = ToDiagnostic(exception);
            }
            catch (Exception exception)
            {
                primaryFailure = TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.ModelLoadFailed,
                    $"HALCON model learning/persistence failed; ExceptionType={exception.GetType().Name}.");
            }
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }
        }

        if (committedReference is not null)
        {
            // Success construction happens once, only after mandatory cleanup. If the committed
            // reference is malformed, propagate that programming/persistence fault as-is.
            return Successful(request, committedReference, geometry, features);
        }

        if (cancellation is not null)
        {
            if (cleanupFailure is not null)
            {
                cancellation.Data["HalconSessionCleanupExceptionType"] =
                    cleanupFailure.GetType().Name;
            }

            ExceptionDispatchInfo.Capture(cancellation).Throw();
        }

        if (primaryFailure is null)
        {
            throw cleanupFailure ?? new InvalidOperationException(
                "HALCON learning completed without a result or primary exception.");
        }

        TemplateLearningResult outcome = Failed(primaryFailure);
        if (!outcome.Success && cleanupFailure is not null)
        {
            outcome = AppendCleanupFailure(outcome, cleanupFailure);
        }

        // A secondary cleanup fault enriches, but never replaces, the primary failure.
        return outcome;
    }

    private static TemplateLearningResult Successful(
        TemplateLearningRequest request,
        TemplateModelReference reference,
        TemplateLearnedGeometry geometry,
        HalconTemplateFeatureSet features)
    {
        var resultParameters = new Dictionary<string, string>(
            request.Parameters,
            StringComparer.OrdinalIgnoreCase)
        {
            [TemplateMatchingParameterCatalog.Engine] = "Halcon"
        };
        TemplateModelParameterCodec.WriteHalcon(
            resultParameters,
            new HalconTemplateModelState(reference, geometry));
        return new TemplateLearningResult(
            TemplateMatchingEngine.Halcon,
            true,
            resultParameters,
            "HALCON scaled-shape template learned and verified.",
            null)
        {
            Geometry = geometry,
            Preview = new TemplateLearningPreview(
                new Point2D(geometry.StandardPose.X, geometry.StandardPose.Y),
                features.OuterContour,
                features.InnerFeatureGroups)
        };
    }

    private static TemplateLearningResult AppendCleanupFailure(
        TemplateLearningResult primary,
        Exception cleanupFailure)
    {
        TemplateMatchingDiagnostic diagnostic = primary.Diagnostic
            ?? TemplateMatchingDiagnostics.Create(TemplateMatchingDiagnosticCodes.ModelLoadFailed);
        string cleanup = $"CleanupExceptionType={cleanupFailure.GetType().Name}.";
        string details = string.IsNullOrWhiteSpace(diagnostic.TechnicalDetails)
            ? cleanup
            : $"{diagnostic.TechnicalDetails} {cleanup}";
        var enriched = diagnostic with { TechnicalDetails = details };
        return new TemplateLearningResult(
            primary.Engine,
            false,
            primary.Parameters,
            primary.Message,
            enriched)
        {
            Geometry = primary.Geometry,
            Preview = primary.Preview
        };
    }

    private static void ValidateRequest(TemplateLearningRequest? request)
    {
        if (request is null || request.Frame is null || request.TemplateRoi is null || request.Parameters is null)
        {
            throw new TemplateMatchingConfigurationException(
                TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                    "HALCON learning requires a frame, template ROI and parameter set."));
        }

        TemplateModelOwner owner = request.Owner;
        if (!IsRequired(owner.RecipeId) || !IsRequired(owner.FlowId) || !IsRequired(owner.ToolId))
        {
            throw new TemplateMatchingConfigurationException(
                TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                    "HALCON learning owner recipe/flow/tool identifiers must be non-empty and trimmed."));
        }

        if (TryGet(request.Parameters, TemplateMatchingParameterCatalog.Engine, out string engine) &&
            !string.Equals(engine, "Halcon", StringComparison.Ordinal))
        {
            throw new TemplateMatchingConfigurationException(
                TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                    "HALCON learner received parameters for a different engine."));
        }

        if (TryGet(request.Parameters, TemplateMatchingParameterCatalog.MatchMode, out string matchMode) &&
            !string.Equals(matchMode, "Shape", StringComparison.Ordinal))
        {
            throw new TemplateMatchingConfigurationException(
                TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                    "HALCON learner supports only Shape match mode."));
        }
    }

    private static TemplateMatchCardinality ResolveCardinality(
        IReadOnlyDictionary<string, string> parameters)
    {
        return TryGet(parameters, TemplateMatchingParameterCatalog.ExpectedCount, out _) ||
               TryGet(parameters, TemplateMatchingParameterCatalog.LegacyMatchCount, out _)
            ? TemplateMatchCardinality.ExactCount
            : TemplateMatchCardinality.Single;
    }

    private static bool TryGet(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        out string value)
    {
        foreach (KeyValuePair<string, string> pair in parameters)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool IsRequired(string? value) =>
        !string.IsNullOrWhiteSpace(value) && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
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

    private static TemplateLearningResult Failed(TemplateMatchingDiagnostic diagnostic)
    {
        return new TemplateLearningResult(
            TemplateMatchingEngine.Halcon,
            false,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            diagnostic.UserMessage,
            diagnostic);
    }
}
