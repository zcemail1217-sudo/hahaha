using System.Diagnostics;
using VisionStation.Domain;

namespace VisionStation.Vision.Tools;

public sealed class TemplateLocateTool : IVisionTool
{
    private const string OverlaySchemaVersion = "2";
    private readonly ITemplateMatchingService _matchingService;

    public TemplateLocateTool(ITemplateMatchingService matchingService)
    {
        _matchingService = matchingService ?? throw new ArgumentNullException(nameof(matchingService));
    }

    public VisionToolKind Kind => VisionToolKind.TemplateLocate;

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
            var missingInputResult = GeometryToolSupport.CreateMissingImageInputResult(
                definition,
                Kind,
                stopwatch.Elapsed);
            return missingInputResult with
            {
                Data = new Dictionary<string, string>(missingInputResult.Data, StringComparer.OrdinalIgnoreCase)
                {
                    ["overlaySchemaVersion"] = OverlaySchemaVersion,
                    ["hasMatch"] = false.ToString()
                }
            };
        }

        var activeFlow = context.Recipe.GetActiveFlow();
        var roi = GeometryToolSupport.FindBoundRoi(context.Recipe, definition);
        var request = new TemplateMatchingRequest(
            new TemplateModelOwner(context.Recipe.Id, activeFlow.Id, definition.Id),
            frame,
            roi,
            definition.Parameters,
            TemplateMatchCardinality.Single,
            1);
        var batch = await _matchingService.MatchAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var match = TemplateMatchResultProjector.ToSingle(batch);
        var operational = match.HasMatch && match.Outcome == InspectionOutcome.Ok;
        if (operational)
        {
            context.Properties["pose"] = match.Pose;
            context.SetPortOutput(definition, "PositionOutput", match.Pose);
            context.SetPortOutput(definition, "OriginOutput", match.Pose);
            context.SetPortOutput(definition, "ScoreOutput", match.Score);
            context.SetPortOutput(definition, "XOutput", match.Pose.X);
            context.SetPortOutput(definition, "YOutput", match.Pose.Y);
            context.SetPortOutput(definition, "AngleOutput", match.Pose.Angle);
        }

        stopwatch.Stop();
        var data = CreateData(definition, frame, match, batch.Matches.Count > 0, operational);
        return new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = Kind,
            Outcome = match.Outcome,
            Duration = stopwatch.Elapsed,
            Message = match.Message,
            Data = data
        };
    }

    private static Dictionary<string, string> CreateData(
        VisionToolDefinition definition,
        ImageFrame frame,
        TemplateMatchResult match,
        bool hasCandidate,
        bool operational)
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["overlaySchemaVersion"] = OverlaySchemaVersion,
            ["hasMatch"] = match.HasMatch.ToString(),
            ["inputFrameId"] = frame.Id,
            ["templateWidth"] = match.TemplateWidth.ToString(),
            ["templateHeight"] = match.TemplateHeight.ToString(),
            ["searchX"] = match.SearchRegion.X.ToString(),
            ["searchY"] = match.SearchRegion.Y.ToString(),
            ["searchWidth"] = match.SearchRegion.Width.ToString(),
            ["searchHeight"] = match.SearchRegion.Height.ToString(),
            ["autoTemplate"] = match.UsedAutoTemplate.ToString(),
            ["engine"] = match.Engine.ToString(),
            ["matchMode"] = definition.Parameters.GetValueOrDefault("matchMode") ?? "Shape"
        };

        if (hasCandidate)
        {
            var prefix = operational ? string.Empty : "rejectedCandidate.";
            data[$"{prefix}score"] = match.Score.ToInvariant();
            data[$"{prefix}x"] = match.Pose.X.ToInvariant();
            data[$"{prefix}y"] = match.Pose.Y.ToInvariant();
            data[$"{prefix}angle"] = match.Pose.Angle.ToInvariant();
            data[$"{prefix}scale"] = match.Scale.ToRoundTripScaleInvariant();
            data[$"{prefix}outerCoverage"] = match.OuterCoverage.ToInvariant();
            data[$"{prefix}innerCoverage"] = match.InnerCoverage.ToInvariant();
            data[$"{prefix}edgeDistanceP95Px"] = match.EdgeDistanceP95Px.ToInvariant();
            data[$"{prefix}polarityAgreement"] = match.PolarityAgreement.ToInvariant();
        }

        AddGeometryDiagnostics(data, match);
        AddFailureDiagnostics(data, match.Diagnostic);
        return data;
    }

    private static void AddGeometryDiagnostics(
        IDictionary<string, string> data,
        TemplateMatchResult match)
    {
        if (match.ShapePoints is { Count: > 0 })
        {
            data["shapePoints"] = string.Join(
                ";",
                match.ShapePoints.Select(point => $"{point.X.ToInvariant()},{point.Y.ToInvariant()}"));
        }

        if (match.ShapeContours is { Count: > 0 })
        {
            var shapeContours = SerializeContours(match.ShapeContours);
            if (!string.IsNullOrEmpty(shapeContours))
            {
                data["shapeContours"] = shapeContours;
            }
        }

        if (match.MatchedTemplateRoiContours is { Count: > 0 })
        {
            var matchedTemplateRoiContours = SerializeContours(match.MatchedTemplateRoiContours);
            if (!string.IsNullOrEmpty(matchedTemplateRoiContours))
            {
                data["matchedTemplateRoiContours"] = matchedTemplateRoiContours;
            }
        }

        if (match.ShapeCoverage is { } shapeCoverage)
        {
            data["shapeCoverage"] = shapeCoverage.ToInvariant();
        }

        if (match.ShapeReverseScore is { } shapeReverseScore)
        {
            data["shapeReverseScore"] = shapeReverseScore.ToInvariant();
        }
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

    private static void RemoveOutputs(VisionToolContext context, VisionToolDefinition definition)
    {
        context.Properties.Remove("pose");
        foreach (var port in new[]
                 {
                     "PositionOutput",
                     "OriginOutput",
                     "ScoreOutput",
                     "XOutput",
                     "YOutput",
                     "AngleOutput",
                     "ScaleOutput"
                 })
        {
            context.RemovePortOutput(definition, port);
        }
    }

    private static string SerializeContours(IEnumerable<IReadOnlyList<Point2D>> contours)
    {
        return string.Join(
            "|",
            contours
                .Where(contour => contour.Count >= 2)
                .Select(contour => string.Join(
                    ";",
                    contour.Select(point => $"{point.X.ToInvariant()},{point.Y.ToInvariant()}"))));
    }

}
