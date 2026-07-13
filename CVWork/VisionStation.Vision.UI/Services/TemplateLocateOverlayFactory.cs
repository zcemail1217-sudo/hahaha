using System.Globalization;
using VisionStation.Domain;
using VisionStation.Vision;
using VisionStation.Vision.UI.Models;

namespace VisionStation.Vision.UI.Services;

public static class TemplateLocateOverlayFactory
{
    public static IReadOnlyList<VisionOverlayItem> Create(TemplateMatchResult result)
    {
        var hasPosition = result.HasMatch &&
                          double.IsFinite(result.Pose.X) &&
                          double.IsFinite(result.Pose.Y) &&
                          double.IsFinite(result.Pose.Angle) &&
                          double.IsFinite(result.Score);

        return CreateCore(
            hasPosition,
            result.Pose.X,
            result.Pose.Y,
            result.Pose.Angle,
            result.TemplateWidth,
            result.TemplateHeight,
            result.Score,
            result.ShapeCoverage,
            result.Outcome,
            result.ShapePoints ?? Array.Empty<Point2D>(),
            result.ShapeContours ?? Array.Empty<IReadOnlyList<Point2D>>(),
            result.MatchedTemplateRoiContours ?? Array.Empty<IReadOnlyList<Point2D>>());
    }

    public static IReadOnlyList<VisionOverlayItem> Create(ToolResult result)
    {
        var hasX = TryGetDouble(result.Data, "x", out var x);
        var hasY = TryGetDouble(result.Data, "y", out var y);
        var hasAngle = TryGetDouble(result.Data, "angle", out var angle);
        TryGetDouble(result.Data, "templateWidth", out var templateWidth);
        TryGetDouble(result.Data, "templateHeight", out var templateHeight);
        var hasScore = TryGetDouble(result.Data, "score", out var score);
        var hasPosition = hasX && hasY && hasAngle && hasScore;
        if (result.Data.TryGetValue("hasMatch", out var rawHasMatch))
        {
            hasPosition = bool.TryParse(rawHasMatch, out var parsedHasMatch) &&
                          parsedHasMatch &&
                          hasPosition;
        }

        double? coverage = TryGetDouble(result.Data, "shapeCoverage", out var parsedCoverage)
            ? parsedCoverage
            : null;
        var shapePoints = ParsePointList(result.Data.GetValueOrDefault("shapePoints"));
        var shapeContours = ParseContours(result.Data.GetValueOrDefault("shapeContours"));
        var roiContours = string.Equals(
            result.Data.GetValueOrDefault("overlaySchemaVersion"),
            "2",
            StringComparison.Ordinal)
            ? ParseContours(result.Data.GetValueOrDefault("matchedTemplateRoiContours"))
            : Array.Empty<IReadOnlyList<Point2D>>();

        return CreateCore(
            hasPosition,
            x,
            y,
            angle,
            templateWidth,
            templateHeight,
            score,
            coverage,
            result.Outcome,
            shapePoints,
            shapeContours,
            roiContours);
    }

    private static IReadOnlyList<VisionOverlayItem> CreateCore(
        bool hasPosition,
        double x,
        double y,
        double angle,
        double templateWidth,
        double templateHeight,
        double score,
        double? coverage,
        InspectionOutcome outcome,
        IReadOnlyList<Point2D> shapePoints,
        IReadOnlyList<IReadOnlyList<Point2D>> shapeContours,
        IReadOnlyList<IReadOnlyList<Point2D>> roiContours)
    {
        var overlays = new List<VisionOverlayItem>();
        var state = outcome == InspectionOutcome.Ok ? VisionOverlayState.Ok : VisionOverlayState.Ng;

        if (shapePoints.Count > 0)
        {
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.PointCloud,
                State = VisionOverlayState.Warning,
                Points = shapePoints
            });
        }

        foreach (var contour in shapeContours.Where(contour => contour.Count >= 2))
        {
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Polyline,
                State = VisionOverlayState.Warning,
                Points = contour
            });
        }

        foreach (var contour in roiContours.Where(contour => contour.Count >= 2))
        {
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Polyline,
                State = VisionOverlayState.Info,
                Points = contour
            });
        }

        var hasShapeOverlay = shapePoints.Count > 0 || shapeContours.Count > 0 || roiContours.Count > 0;
        if (hasPosition &&
            !hasShapeOverlay &&
            double.IsFinite(templateWidth) &&
            double.IsFinite(templateHeight) &&
            templateWidth > 0 &&
            templateHeight > 0)
        {
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.RotatedRectangle,
                State = state,
                X = x,
                Y = y,
                Width = templateWidth,
                Height = templateHeight,
                Angle = angle
            });
        }

        if (hasPosition)
        {
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Cross,
                State = state,
                PreserveLabelInResult = true,
                Label = coverage is { } shapeCoverage
                    ? $"匹配 S={score.ToString("0.000", CultureInfo.InvariantCulture)} C={shapeCoverage.ToString("0.000", CultureInfo.InvariantCulture)}"
                    : $"匹配 S={score.ToString("0.000", CultureInfo.InvariantCulture)}",
                X = x,
                Y = y,
                Angle = angle
            });
        }

        return overlays;
    }

    private static IReadOnlyList<Point2D> ParsePointList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<Point2D>();
        }

        var points = new List<Point2D>();
        foreach (var item in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                !double.IsFinite(x) ||
                !double.IsFinite(y))
            {
                continue;
            }

            points.Add(new Point2D(x, y));
        }

        return points;
    }

    private static IReadOnlyList<IReadOnlyList<Point2D>> ParseContours(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<IReadOnlyList<Point2D>>();
        }

        var contours = new List<IReadOnlyList<Point2D>>();
        foreach (var contourText in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var contour = ParsePointList(contourText);
            if (contour.Count >= 2)
            {
                contours.Add(contour);
            }
        }

        return contours;
    }

    private static bool TryGetDouble(IReadOnlyDictionary<string, string> data, string key, out double value)
    {
        value = 0;
        if (!data.TryGetValue(key, out var raw) ||
            !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            !double.IsFinite(parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }
}
