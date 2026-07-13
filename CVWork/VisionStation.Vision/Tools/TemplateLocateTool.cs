using System.Diagnostics;
using VisionStation.Domain;

namespace VisionStation.Vision.Tools;

public sealed class TemplateLocateTool : IVisionTool
{
    private const string OverlaySchemaVersion = "2";

    public VisionToolKind Kind => VisionToolKind.TemplateLocate;

    public Task<ToolResult> ExecuteAsync(VisionToolDefinition definition, VisionToolContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!context.TryGetInputImage(definition, out var frame))
        {
            stopwatch.Stop();
            var missingInputResult = GeometryToolSupport.CreateMissingImageInputResult(definition, Kind, stopwatch.Elapsed);
            return Task.FromResult(missingInputResult with
            {
                Data = new Dictionary<string, string>(missingInputResult.Data, StringComparer.OrdinalIgnoreCase)
                {
                    ["overlaySchemaVersion"] = OverlaySchemaVersion
                }
            });
        }

        var roi = FindBoundRoi(context.Recipe, definition);
        var match = TemplateMatcher.Match(frame, roi, definition.Parameters, context.GetGrayMat(frame), cancellationToken);

        if (match.HasMatch)
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

        var data = new Dictionary<string, string>
        {
            ["overlaySchemaVersion"] = OverlaySchemaVersion,
            ["score"] = match.Score.ToInvariant(),
            ["x"] = match.Pose.X.ToInvariant(),
            ["y"] = match.Pose.Y.ToInvariant(),
            ["angle"] = match.Pose.Angle.ToInvariant(),
            ["inputFrameId"] = frame.Id,
            ["templateWidth"] = match.TemplateWidth.ToString(),
            ["templateHeight"] = match.TemplateHeight.ToString(),
            ["searchX"] = match.SearchRegion.X.ToString(),
            ["searchY"] = match.SearchRegion.Y.ToString(),
            ["searchWidth"] = match.SearchRegion.Width.ToString(),
            ["searchHeight"] = match.SearchRegion.Height.ToString(),
            ["autoTemplate"] = match.UsedAutoTemplate.ToString(),
            ["engine"] = definition.Parameters.GetValueOrDefault("engine") ?? "OpenCv",
            ["matchMode"] = definition.Parameters.GetValueOrDefault("matchMode") ?? "Shape"
        };

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

        return Task.FromResult(new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = Kind,
            Outcome = match.Outcome,
            Duration = stopwatch.Elapsed,
            Message = match.Message,
            Data = data
        });
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

    private static RoiDefinition? FindBoundRoi(Recipe recipe, VisionToolDefinition definition)
    {
        if (!definition.Parameters.TryGetValue("roiId", out var roiId) || string.IsNullOrWhiteSpace(roiId))
        {
            return null;
        }

        return recipe.Rois.FirstOrDefault(roi => string.Equals(roi.Id, roiId, StringComparison.OrdinalIgnoreCase));
    }
}
