using System.Globalization;
using VisionStation.Domain;
using VisionStation.Vision;
using VisionStation.Vision.UI.Models;

namespace VisionStation.Vision.UI.Services;

public static class TemplateLocateOverlayFactory
{
    public static IReadOnlyList<VisionOverlayItem> CreateLearningPreview(TemplateLearningPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!TryTranslate(preview.OuterContour, preview.Origin, out var outerContour))
        {
            return Array.Empty<VisionOverlayItem>();
        }

        var innerGroups = new List<IReadOnlyList<Point2D>>(preview.InnerFeatureGroups.Count);
        foreach (var group in preview.InnerFeatureGroups)
        {
            if (!TryTranslate(group, preview.Origin, out var translated))
            {
                return Array.Empty<VisionOverlayItem>();
            }

            innerGroups.Add(translated);
        }

        var overlays = new List<VisionOverlayItem>();
        if (outerContour.Count >= 2)
        {
            outerContour = CloseContour(outerContour);
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Polyline,
                State = VisionOverlayState.Info,
                Label = "学习外轮廓",
                Points = outerContour
            });
        }

        for (var index = 0; index < innerGroups.Count; index++)
        {
            if (innerGroups[index].Count == 0)
            {
                continue;
            }

            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.PointCloud,
                State = VisionOverlayState.Warning,
                Label = $"学习内部特征 #{index + 1}",
                Points = innerGroups[index]
            });
        }

        overlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.Cross,
            State = VisionOverlayState.Neutral,
            PreserveLabelInResult = true,
            Label = "模型原点",
            X = preview.Origin.X,
            Y = preview.Origin.Y
        });
        return overlays;
    }

    public static IReadOnlyList<VisionOverlayItem> Create(TemplateMatchResult result)
    {
        var hasPosition = result.HasMatch &&
                          double.IsFinite(result.Pose.X) &&
                          double.IsFinite(result.Pose.Y) &&
                          double.IsFinite(result.Pose.Angle) &&
                          double.IsFinite(result.Pose.Scale) &&
                          result.Pose.Scale > 0 &&
                          double.IsFinite(result.Score);

        return CreateCore(
            hasPosition,
            result.Pose.X,
            result.Pose.Y,
            result.Pose.Angle,
            result.Pose.Scale,
            result.TemplateWidth,
            result.TemplateHeight,
            result.Score,
            result.ShapeCoverage,
            result.Engine == TemplateMatchingEngine.Halcon,
            result.OuterCoverage,
            result.InnerCoverage,
            result.EdgeDistanceP95Px,
            result.PolarityAgreement,
            result.Outcome,
            result.ShapePoints ?? Array.Empty<Point2D>(),
            result.ShapeContours ?? Array.Empty<IReadOnlyList<Point2D>>(),
            result.MatchedTemplateRoiContours ?? Array.Empty<IReadOnlyList<Point2D>>());
    }

    public static IReadOnlyList<VisionOverlayItem> Create(
        MultiTargetMatchCandidate candidate,
        TemplateMatchingEngine engine,
        InspectionOutcome outcome,
        int index)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (index <= 0 ||
            !double.IsFinite(candidate.X) ||
            !double.IsFinite(candidate.Y) ||
            !double.IsFinite(candidate.Angle) ||
            !double.IsFinite(candidate.Scale) ||
            candidate.Scale <= 0 ||
            !double.IsFinite(candidate.Score))
        {
            return Array.Empty<VisionOverlayItem>();
        }

        var state = outcome == InspectionOutcome.Ok ? VisionOverlayState.Ok : VisionOverlayState.Ng;
        var overlays = new List<VisionOverlayItem>();
        if (candidate.Shape.Equals("Circle", StringComparison.OrdinalIgnoreCase))
        {
            var radius = (candidate.Radius > 0
                ? candidate.Radius
                : Math.Max(candidate.Width, candidate.Height) / 2d) * candidate.Scale;
            if (double.IsFinite(radius) && radius > 0)
            {
                overlays.Add(new VisionOverlayItem
                {
                    Kind = VisionOverlayKind.Circle,
                    State = state,
                    X = candidate.X,
                    Y = candidate.Y,
                    Radius = radius
                });
            }
        }
        else
        {
            var width = Math.Max(12d, candidate.Width * candidate.Scale);
            var height = Math.Max(12d, candidate.Height * candidate.Scale);
            if (double.IsFinite(width) && double.IsFinite(height))
            {
                overlays.Add(new VisionOverlayItem
                {
                    Kind = VisionOverlayKind.RotatedRectangle,
                    State = state,
                    X = candidate.X,
                    Y = candidate.Y,
                    Width = width,
                    Height = height,
                    Angle = NormalizeAngle(candidate.Angle)
                });
            }
        }

        overlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.Cross,
            State = state,
            PreserveLabelInResult = true,
            Label = $"#{index} " + CreateMatchLabel(
                candidate.Score,
                coverage: null,
                engine == TemplateMatchingEngine.Halcon,
                candidate.OuterCoverage,
                candidate.InnerCoverage,
                candidate.EdgeDistanceP95Px,
                candidate.PolarityAgreement),
            X = candidate.X,
            Y = candidate.Y,
            Angle = NormalizeAngle(candidate.Angle)
        });
        return overlays;
    }

    public static IReadOnlyList<VisionOverlayItem> Create(ToolResult result)
    {
        var hasX = TryGetDouble(result.Data, "x", out var x);
        var hasY = TryGetDouble(result.Data, "y", out var y);
        var hasAngle = TryGetDouble(result.Data, "angle", out var angle);
        var hasScale = TryGetOptionalScale(result.Data, "scale", out var scale);
        TryGetDouble(result.Data, "templateWidth", out var templateWidth);
        TryGetDouble(result.Data, "templateHeight", out var templateHeight);
        var hasScore = TryGetDouble(result.Data, "score", out var score);
        var hasPosition = hasX && hasY && hasAngle && hasScale && hasScore;
        if (result.Data.TryGetValue("hasMatch", out var rawHasMatch))
        {
            hasPosition = bool.TryParse(rawHasMatch, out var parsedHasMatch) &&
                          parsedHasMatch &&
                          hasPosition;
        }

        var usesRejectedCandidate = false;
        if (!hasPosition &&
            result.Outcome != InspectionOutcome.Ok &&
            TryGetDouble(result.Data, "rejectedCandidate.x", out var rejectedX) &&
            TryGetDouble(result.Data, "rejectedCandidate.y", out var rejectedY) &&
            TryGetDouble(result.Data, "rejectedCandidate.angle", out var rejectedAngle) &&
            TryGetOptionalScale(result.Data, "rejectedCandidate.scale", out var rejectedScale) &&
            TryGetDouble(result.Data, "rejectedCandidate.score", out var rejectedScore))
        {
            x = rejectedX;
            y = rejectedY;
            angle = rejectedAngle;
            scale = rejectedScale;
            score = rejectedScore;
            hasPosition = true;
            usesRejectedCandidate = true;
        }

        double? coverage = TryGetDouble(result.Data, "shapeCoverage", out var parsedCoverage)
            ? parsedCoverage
            : null;
        var isHalconResult = string.Equals(
            result.Data.GetValueOrDefault("engine"),
            TemplateMatchingEngine.Halcon.ToString(),
            StringComparison.OrdinalIgnoreCase);
        var metricPrefix = usesRejectedCandidate ? "rejectedCandidate." : string.Empty;
        var hasOuterCoverage =
            TryGetDouble(result.Data, $"{metricPrefix}outerCoverage", out var outerCoverage);
        var hasInnerCoverage =
            TryGetDouble(result.Data, $"{metricPrefix}innerCoverage", out var innerCoverage);
        var hasEdgeDistance =
            TryGetDouble(result.Data, $"{metricPrefix}edgeDistanceP95Px", out var edgeDistanceP95Px);
        var hasPolarityAgreement =
            TryGetDouble(result.Data, $"{metricPrefix}polarityAgreement", out var polarityAgreement);
        var hasGateMetrics = hasOuterCoverage &&
                             hasInnerCoverage &&
                             hasEdgeDistance &&
                             hasPolarityAgreement;
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
            scale,
            templateWidth,
            templateHeight,
            score,
            coverage,
            isHalconResult && hasGateMetrics,
            outerCoverage,
            innerCoverage,
            edgeDistanceP95Px,
            polarityAgreement,
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
        double scale,
        double templateWidth,
        double templateHeight,
        double score,
        double? coverage,
        bool showGateMetrics,
        double outerCoverage,
        double innerCoverage,
        double edgeDistanceP95Px,
        double polarityAgreement,
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
            TryCalculateFallbackBounds(
                x,
                y,
                angle,
                scale,
                templateWidth,
                templateHeight,
                out var left,
                out var top,
                out var boundsWidth,
                out var boundsHeight))
        {
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Rectangle,
                State = state,
                X = left,
                Y = top,
                Width = boundsWidth,
                Height = boundsHeight
            });
        }

        if (hasPosition)
        {
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Cross,
                State = state,
                PreserveLabelInResult = true,
                Label = CreateMatchLabel(
                    score,
                    coverage,
                    showGateMetrics,
                    outerCoverage,
                    innerCoverage,
                    edgeDistanceP95Px,
                    polarityAgreement),
                X = x,
                Y = y,
                Angle = NormalizeAngle(angle)
            });
        }

        return overlays;
    }

    private static string CreateMatchLabel(
        double score,
        double? coverage,
        bool showGateMetrics,
        double outerCoverage,
        double innerCoverage,
        double edgeDistanceP95Px,
        double polarityAgreement)
    {
        var label = coverage is { } shapeCoverage
            ? $"匹配 S={score.ToString("0.000", CultureInfo.InvariantCulture)} C={shapeCoverage.ToString("0.000", CultureInfo.InvariantCulture)}"
            : $"匹配 S={score.ToString("0.000", CultureInfo.InvariantCulture)}";
        if (!showGateMetrics ||
            !IsUnitInterval(outerCoverage) ||
            !IsUnitInterval(innerCoverage) ||
            !double.IsFinite(edgeDistanceP95Px) ||
            edgeDistanceP95Px < 0 ||
            !IsUnitInterval(polarityAgreement))
        {
            return label;
        }

        return $"{label} " +
               $"O={outerCoverage.ToString("0.000", CultureInfo.InvariantCulture)} " +
               $"I={innerCoverage.ToString("0.000", CultureInfo.InvariantCulture)} " +
               $"P95={edgeDistanceP95Px.ToString("0.000", CultureInfo.InvariantCulture)} " +
               $"P={polarityAgreement.ToString("0.000", CultureInfo.InvariantCulture)}";
    }

    private static bool TryTranslate(
        IReadOnlyList<Point2D> relativePoints,
        Point2D origin,
        out IReadOnlyList<Point2D> translatedPoints)
    {
        var translated = new List<Point2D>(relativePoints.Count);
        foreach (var point in relativePoints)
        {
            var x = origin.X + point.X;
            var y = origin.Y + point.Y;
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                translatedPoints = Array.Empty<Point2D>();
                return false;
            }

            translated.Add(new Point2D(x, y));
        }

        translatedPoints = translated;
        return true;
    }

    private static IReadOnlyList<Point2D> CloseContour(IReadOnlyList<Point2D> contour)
    {
        var first = contour[0];
        var last = contour[^1];
        if (first.X.Equals(last.X) && first.Y.Equals(last.Y))
        {
            return contour;
        }

        var closed = new List<Point2D>(contour.Count + 1);
        closed.AddRange(contour);
        closed.Add(new Point2D(first.X, first.Y));
        return closed;
    }

    private static bool IsUnitInterval(double value)
    {
        return double.IsFinite(value) && value is >= 0 and <= 1;
    }

    private static bool TryCalculateFallbackBounds(
        double x,
        double y,
        double angle,
        double scale,
        double templateWidth,
        double templateHeight,
        out double left,
        out double top,
        out double boundsWidth,
        out double boundsHeight)
    {
        left = 0;
        top = 0;
        boundsWidth = 0;
        boundsHeight = 0;
        if (!double.IsFinite(x) ||
            !double.IsFinite(y) ||
            !double.IsFinite(angle) ||
            !double.IsFinite(scale) ||
            !double.IsFinite(templateWidth) ||
            !double.IsFinite(templateHeight) ||
            scale <= 0 ||
            templateWidth <= 0 ||
            templateHeight <= 0)
        {
            return false;
        }

        var scaledWidth = templateWidth * scale;
        var scaledHeight = templateHeight * scale;
        if (!double.IsFinite(scaledWidth) ||
            !double.IsFinite(scaledHeight) ||
            scaledWidth <= 0 ||
            scaledHeight <= 0)
        {
            return false;
        }

        var radians = NormalizeAngle(angle) * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        boundsWidth = Math.Abs(scaledWidth * cosine) + Math.Abs(scaledHeight * sine);
        boundsHeight = Math.Abs(scaledWidth * sine) + Math.Abs(scaledHeight * cosine);
        if (!double.IsFinite(boundsWidth) ||
            !double.IsFinite(boundsHeight) ||
            boundsWidth <= 0 ||
            boundsHeight <= 0)
        {
            return false;
        }

        left = x - boundsWidth / 2d;
        top = y - boundsHeight / 2d;
        var right = left + boundsWidth;
        var bottom = top + boundsHeight;
        return double.IsFinite(left) &&
               double.IsFinite(top) &&
               double.IsFinite(right) &&
               double.IsFinite(bottom);
    }

    private static double NormalizeAngle(double angle)
    {
        return double.IsFinite(angle) ? angle % 360d : 0d;
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

    private static bool TryGetOptionalScale(
        IReadOnlyDictionary<string, string> data,
        string key,
        out double scale)
    {
        scale = 1.0;
        if (!data.ContainsKey(key))
        {
            return true;
        }

        return TryGetDouble(data, key, out scale) && scale > 0;
    }
}
