using System.Diagnostics;
using System.Text.Json;
using VisionStation.Domain;

namespace VisionStation.Vision.Tools;

public sealed class MultiTargetMatchTool : IVisionTool
{
    private readonly ITemplateMatchingService _matchingService;

    public MultiTargetMatchTool(ITemplateMatchingService matchingService)
    {
        _matchingService = matchingService ?? throw new ArgumentNullException(nameof(matchingService));
    }

    public VisionToolKind Kind => VisionToolKind.MultiTargetMatch;

    public async Task<ToolResult> ExecuteAsync(
        VisionToolDefinition definition,
        VisionToolContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);
        RemoveOutputs(context, definition);
        var stopwatch = Stopwatch.StartNew();
        if (!context.TryGetInputImage(definition, out var frame))
        {
            stopwatch.Stop();
            return PublishEmptyCount(
                context,
                definition,
                GeometryToolSupport.CreateMissingImageInputResult(
                    definition,
                    Kind,
                    stopwatch.Elapsed));
        }

        var sourceRoi = GeometryToolSupport.FindBoundRoi(context.Recipe, definition);
        RoiDefinition? roi = sourceRoi;
        if (sourceRoi is null &&
            !GeometryToolSupport.TryValidatePositionInputMapping(
                context,
                definition,
                out var missingRoiMappingFailure))
        {
            stopwatch.Stop();
            return PublishEmptyCount(
                context,
                definition,
                GeometryToolSupport.CreatePositionInputMappingFailureResult(
                    definition,
                    Kind,
                    stopwatch.Elapsed,
                    frame,
                    missingRoiMappingFailure!));
        }

        if (sourceRoi is not null &&
            !GeometryToolSupport.TryMapRoiForPositionInput(
                context,
                definition,
                sourceRoi,
                out roi,
                out var mappingFailure))
        {
            stopwatch.Stop();
            return PublishEmptyCount(
                context,
                definition,
                GeometryToolSupport.CreatePositionInputMappingFailureResult(
                    definition,
                    Kind,
                    stopwatch.Elapsed,
                    frame,
                    mappingFailure!));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var expectedCountResolution = TemplateMatchingExpectedCountResolver.Resolve(definition.Parameters);
        if (!expectedCountResolution.IsValid)
        {
            stopwatch.Stop();
            return PublishEmptyCount(
                context,
                definition,
                CreateConfigurationFailure(
                    definition,
                    frame,
                    stopwatch.Elapsed,
                    expectedCountResolution.Diagnostic!));
        }

        var expectedCount = expectedCountResolution.ExpectedCount;
        var activeFlow = context.Recipe.GetActiveFlow();
        var request = new TemplateMatchingRequest(
            new TemplateModelOwner(context.Recipe.Id, activeFlow.Id, definition.Id),
            frame,
            roi,
            definition.Parameters,
            TemplateMatchCardinality.ExactCount,
            expectedCount);
        var batch = await _matchingService.MatchAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var result = TemplateMatchResultProjector.ToMulti(batch);
        if (result.Outcome == InspectionOutcome.Ok && result.Matches.Count != expectedCount)
        {
            var diagnostic = TemplateMatchingDiagnostics.Create(
                TemplateMatchingDiagnosticCodes.MatchOperatorFailed,
                $"Backend returned {result.Matches.Count} candidates for ExactCount={expectedCount} with Outcome=Ok.");
            result = result with
            {
                Outcome = InspectionOutcome.Ng,
                Message = $"Template exact-count NG, found {result.Matches.Count}, required {expectedCount}.",
                FailureCode = diagnostic.Code,
                FailureStage = diagnostic.FailureStage,
                TechnicalDetails = diagnostic.TechnicalDetails,
                Diagnostic = diagnostic
            };
        }

        var matches = result.Matches;
        var best = matches.FirstOrDefault();
        var operational = batch.HasMatch &&
                          result.Outcome == InspectionOutcome.Ok &&
                          matches.Count == expectedCount;

        context.SetPortOutput(definition, "CountOutput", matches.Count);
        if (operational && best is not null)
        {
            var bestPose = best.Pose;
            var poses = matches.Select(match => match.Pose).ToArray();
            var scores = matches.Select(match => match.Score).ToArray();
            var scales = matches.Select(match => match.Pose.Scale).ToArray();
            context.Properties["pose"] = bestPose;
            context.SetPortOutput(definition, "PositionOutput", bestPose);
            context.SetPortOutput(definition, "OriginOutput", bestPose);
            context.SetPortOutput(definition, "BestPositionOutput", bestPose);
            context.SetPortOutput(definition, "ScoreOutput", best.Score);
            context.SetPortOutput(definition, "XOutput", best.X);
            context.SetPortOutput(definition, "YOutput", best.Y);
            context.SetPortOutput(definition, "AngleOutput", best.Angle);
            context.SetPortOutput(definition, "AllPositionsOutput", poses);
            context.SetPortOutput(definition, "ScoresOutput", scores);
            context.SetPortOutput(definition, "ScalesOutput", scales);
        }

        stopwatch.Stop();
        var data = CreateData(definition, frame, roi, result);
        return new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = Kind,
            Outcome = result.Outcome,
            Duration = stopwatch.Elapsed,
            Message = result.Message,
            Data = data
        };
    }

    private static Dictionary<string, string> CreateData(
        VisionToolDefinition definition,
        ImageFrame frame,
        RoiDefinition? roi,
        MultiTargetMatchResult result)
    {
        var matches = result.Matches;
        var best = matches.FirstOrDefault();
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["count"] = matches.Count.ToString(),
            ["score"] = best?.Score.ToInvariant() ?? "0",
            ["bestScore"] = best?.Score.ToInvariant() ?? "0",
            ["x"] = best?.X.ToInvariant() ?? "0",
            ["y"] = best?.Y.ToInvariant() ?? "0",
            ["angle"] = best?.Angle.ToInvariant() ?? "0",
            ["scale"] = best?.Scale.ToRoundTripScaleInvariant() ?? "1",
            ["inputFrameId"] = frame.Id,
            ["templateWidth"] = best?.Width.ToString() ??
                                definition.Parameters.GetValueOrDefault("templateWidth") ??
                                "0",
            ["templateHeight"] = best?.Height.ToString() ??
                                 definition.Parameters.GetValueOrDefault("templateHeight") ??
                                 "0",
            ["searchX"] = result.SearchRegion.X.ToString(),
            ["searchY"] = result.SearchRegion.Y.ToString(),
            ["searchWidth"] = result.SearchRegion.Width.ToString(),
            ["searchHeight"] = result.SearchRegion.Height.ToString(),
            ["autoTemplate"] = result.UsedAutoTemplate.ToString(),
            ["engine"] = result.Engine.ToString(),
            ["matchMode"] = definition.Parameters.GetValueOrDefault("matchMode") ??
                            definition.Parameters.GetValueOrDefault("multiMatchMode") ??
                            "Shape",
            ["matches"] = FormatMatches(matches),
            ["overlaySchemaVersion"] = "2",
            ["matchSchemaVersion"] = "2",
            ["matchesV2"] = FormatMatchesV2(matches),
            ["scores"] = string.Join(",", matches.Select(match => match.Score.ToInvariant())),
            ["scales"] = string.Join(",", matches.Select(match => match.Pose.Scale.ToRoundTripScaleInvariant()))
        };

        if (roi is not null)
        {
            GeometryToolSupport.AddSearchRoiData(data, roi);
        }

        AddFailureDiagnostics(data, result.Diagnostic);
        return data;
    }

    private static ToolResult PublishEmptyCount(
        VisionToolContext context,
        VisionToolDefinition definition,
        ToolResult result)
    {
        context.SetPortOutput(definition, "CountOutput", 0);
        return result with
        {
            Data = new Dictionary<string, string>(result.Data, StringComparer.OrdinalIgnoreCase)
            {
                ["count"] = "0"
            }
        };
    }

    private static void AddFailureDiagnostics(
        IDictionary<string, string> data,
        TemplateMatchingDiagnostic? diagnostic)
    {
        if (diagnostic is null)
        {
            return;
        }

        data["failureCode"] = diagnostic.Code;
        data["failureStage"] = diagnostic.FailureStage;
    }

    private static ToolResult CreateConfigurationFailure(
        VisionToolDefinition definition,
        ImageFrame frame,
        TimeSpan duration,
        TemplateMatchingDiagnostic diagnostic)
    {
        return new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = VisionToolKind.MultiTargetMatch,
            Outcome = InspectionOutcome.Ng,
            Duration = duration,
            Message = diagnostic.UserMessage,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["inputFrameId"] = frame.Id,
                ["failureCode"] = diagnostic.Code,
                ["failureStage"] = diagnostic.FailureStage
            }
        };
    }

    private static void RemoveOutputs(VisionToolContext context, VisionToolDefinition definition)
    {
        context.Properties.Remove("pose");
        foreach (var port in new[]
                 {
                     "PositionOutput",
                     "OriginOutput",
                     "BestPositionOutput",
                     "ScoreOutput",
                     "XOutput",
                     "YOutput",
                     "AngleOutput",
                     "CountOutput",
                     "AllPositionsOutput",
                     "ScoresOutput",
                     "ScalesOutput"
                 })
        {
            context.RemovePortOutput(definition, port);
        }
    }

    private static string FormatMatches(IReadOnlyList<MultiTargetMatchCandidate> matches)
    {
        return string.Join(
            ";",
            matches.Select(match =>
                $"{match.X.ToInvariant()},{match.Y.ToInvariant()},{match.Angle.ToInvariant()}," +
                $"{match.Score.ToInvariant()},{match.Width},{match.Height},{match.Shape}," +
                match.Radius.ToInvariant()));
    }

    private static string FormatMatchesV2(IReadOnlyList<MultiTargetMatchCandidate> matches)
    {
        return JsonSerializer.Serialize(matches.Select(match => new
        {
            x = match.X,
            y = match.Y,
            angle = match.Angle,
            scale = match.Pose.Scale,
            score = match.Score,
            outerCoverage = match.OuterCoverage,
            innerCoverage = match.InnerCoverage,
            edgeDistanceP95Px = match.EdgeDistanceP95Px,
            polarityAgreement = match.PolarityAgreement,
            width = match.Width,
            height = match.Height,
            shape = match.Shape,
            radius = match.Radius
        }));
    }
}
