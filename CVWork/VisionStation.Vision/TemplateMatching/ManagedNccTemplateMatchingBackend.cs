using VisionStation.Domain;

namespace VisionStation.Vision;

internal sealed class ManagedNccTemplateMatchingBackend : ITemplateMatchingBackend
{
    public TemplateMatchingEngine Engine => TemplateMatchingEngine.ManagedNcc;

    public Task<TemplateLearningResult> LearnAsync(
        TemplateLearningRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runtimeParameters = LegacyTemplateMatchingAdapterSupport.WithTemplateRoi(request);
        var learned = TemplateMatcher.LearnManaged(
            request.Frame,
            request.SearchRoi,
            runtimeParameters);
        var parameters = LegacyTemplateMatchingAdapterSupport.MergeLearningParameters(
            runtimeParameters,
            learned,
            runtimeParameters["templateSourceRoiId"],
            Engine);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TemplateLearningResult(
            Engine,
            true,
            parameters,
            "Managed NCC template learned.",
            null)
        {
            Geometry = TemplateReferencePoseCodec.ReadActive(parameters)
        });
    }

    public Task<TemplateMatchBatchResult> MatchAsync(
        TemplateMatchingRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Cardinality == TemplateMatchCardinality.ExactCount)
        {
            var diagnostic = TemplateMatchingDiagnostics.Create(
                TemplateMatchingDiagnosticCodes.ConfigUnsupportedMode,
                "ManagedNcc does not support multi-target matching.");
            return Task.FromResult(new TemplateMatchBatchResult(
                Engine,
                InspectionOutcome.Ng,
                false,
                Array.Empty<TemplateMatchBatchCandidate>(),
                TemplateMatcher.GetSearchRegion(request.Frame, request.SearchRoi),
                diagnostic.UserMessage,
                false,
                diagnostic));
        }

        if (request.Cardinality != TemplateMatchCardinality.Single)
        {
            throw new TemplateMatchingConfigurationException(
                TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                    $"Unsupported template match cardinality '{request.Cardinality}'."));
        }

        var source = TemplateMatcher.MatchManaged(
            request.Frame,
            request.SearchRoi,
            request.Parameters,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(FromSingle(source));
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private static TemplateMatchBatchResult FromSingle(TemplateMatchResult source)
    {
        var diagnostic = LegacyTemplateMatchingAdapterSupport.ReadDiagnostic(
            source.FailureCode,
            source.FailureStage,
            source.Message,
            source.TechnicalDetails);
        if (!source.HasMatch)
        {
            return new TemplateMatchBatchResult(
                TemplateMatchingEngine.ManagedNcc,
                source.Outcome,
                false,
                Array.Empty<TemplateMatchBatchCandidate>(),
                source.SearchRegion,
                source.Message,
                source.UsedAutoTemplate,
                diagnostic);
        }

        var candidate = new TemplateMatchBatchCandidate(
            source.Pose,
            source.Score,
            source.TemplateWidth,
            source.TemplateHeight,
            source.ShapeContours ?? Array.Empty<IReadOnlyList<Point2D>>(),
            LegacyTemplateMatchingAdapterSupport.CreateRectangleContours(
                source.Pose,
                source.TemplateWidth,
                source.TemplateHeight))
        {
            ShapeCoverage = source.ShapeCoverage,
            ShapeReverseScore = source.ShapeReverseScore
        };
        if (!LegacyTemplateMatchingAdapterSupport.TryValidateCandidate(candidate, out var failure))
        {
            return LegacyTemplateMatchingAdapterSupport.ContractFailure(
                TemplateMatchingEngine.ManagedNcc,
                source.SearchRegion,
                source.UsedAutoTemplate,
                failure);
        }

        return new TemplateMatchBatchResult(
            TemplateMatchingEngine.ManagedNcc,
            source.Outcome,
            source.HasMatch,
            [candidate],
            source.SearchRegion,
            source.Message,
            source.UsedAutoTemplate,
            diagnostic);
    }
}
