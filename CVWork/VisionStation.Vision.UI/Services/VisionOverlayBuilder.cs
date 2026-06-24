using System.Globalization;
using VisionStation.Vision.UI.Models;
using VisionStation.Domain;
using VisionStation.Vision;

namespace VisionStation.Vision.UI.Services;

public sealed class VisionOverlayBuilder : IVisionOverlayBuilder
{
    public IReadOnlyList<VisionOverlayItem> Build(
        Recipe recipe,
        ImageFrame frame,
        IReadOnlyList<ToolResult> toolResults,
        InspectionOutcome outcome)
    {
        var overlays = new List<VisionOverlayItem>();
        var state = outcome == InspectionOutcome.Ok ? VisionOverlayState.Ok : VisionOverlayState.Ng;
        var runtimeGeometryRoiIds = GetRuntimeGeometryRoiIds(recipe, toolResults);

        foreach (var roi in recipe.Rois)
        {
            if (runtimeGeometryRoiIds.Contains(roi.Id))
            {
                continue;
            }

            overlays.Add(VisionOverlayItem.FromRoi(roi, VisionOverlayState.Neutral));
        }

        foreach (var result in toolResults)
        {
            var toolState = result.Outcome == InspectionOutcome.Ok ? VisionOverlayState.Ok : VisionOverlayState.Ng;
            switch (result.Kind)
            {
                case VisionToolKind.TemplateLocate:
                    AddLocateOverlay(overlays, result, toolState);
                    break;
                case VisionToolKind.MultiTargetMatch:
                    AddMultiTargetOverlay(overlays, result, toolState);
                    break;
                case VisionToolKind.FindLine:
                    if (result.Outcome != InspectionOutcome.Ok)
                    {
                        AddSearchRoiOverlay(overlays, result);
                    }

                    AddFindLineOverlay(overlays, result, toolState);
                    break;
                case VisionToolKind.FindCircle:
                    if (result.Outcome != InspectionOutcome.Ok)
                    {
                        AddSearchRoiOverlay(overlays, result);
                    }

                    AddFindCircleOverlay(overlays, result, toolState);
                    break;
                case VisionToolKind.MeasureDistance:
                    AddMeasurementOverlay(overlays, recipe, frame, result, toolState);
                    break;
                case VisionToolKind.LineAngle:
                    AddLineAngleOverlay(overlays, result, toolState);
                    break;
                case VisionToolKind.LineIntersection:
                    AddPointOverlay(overlays, result, "交点", toolState);
                    break;
                case VisionToolKind.FitLineFromPoints:
                    AddFitLineOverlay(overlays, result, toolState);
                    break;
                case VisionToolKind.TemplatePoint:
                    AddPointOverlay(overlays, result, "模板点", toolState);
                    break;
                case VisionToolKind.CodeRead:
                case VisionToolKind.Ocr:
                    overlays.Add(CreateCodeOverlay(recipe, frame, result, toolState));
                    break;
                case VisionToolKind.DefectDetect:
                    AddBlobOverlay(overlays, recipe, frame, result, toolState);
                    break;
            }
        }

        overlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.DirectionAxis,
            State = state,
            Label = outcome.ToString(),
            X = 32,
            Y = 32,
            X2 = 132,
            Y2 = 32
        });

        return overlays;
    }

    private static HashSet<string> GetRuntimeGeometryRoiIds(Recipe recipe, IReadOnlyList<ToolResult> toolResults)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in toolResults)
        {
            if (result.Kind is not (VisionToolKind.FindLine or VisionToolKind.FindCircle) ||
                !result.Data.ContainsKey("searchRoiX"))
            {
                continue;
            }

            var tool = recipe.Tools.FirstOrDefault(item => string.Equals(item.Id, result.ToolId, StringComparison.OrdinalIgnoreCase));
            if (tool?.Parameters.TryGetValue("roiId", out var roiId) == true &&
                !string.IsNullOrWhiteSpace(roiId))
            {
                ids.Add(roiId);
            }
        }

        return ids;
    }

    private static void AddLocateOverlay(List<VisionOverlayItem> overlays, ToolResult result, VisionOverlayState state)
    {
        if (!TryGetDouble(result.Data, "x", out var x) ||
            !TryGetDouble(result.Data, "y", out var y) ||
            !TryGetDouble(result.Data, "angle", out var angle))
        {
            return;
        }

        var hasShapePoints = TryParsePointList(result.Data.GetValueOrDefault("shapePoints"), out var shapePoints);
        var shapeContours = ParseContours(result.Data.GetValueOrDefault("shapeContours")).ToArray();
        var hasShapeOverlay = hasShapePoints || shapeContours.Length > 0;

        if (!hasShapeOverlay &&
            TryGetDouble(result.Data, "templateWidth", out var width) &&
            TryGetDouble(result.Data, "templateHeight", out var height) &&
            width > 0 &&
            height > 0)
        {
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.RotatedRectangle,
                State = state,
                Label = $"Template {result.Data.GetValueOrDefault("score", "-")}",
                X = x,
                Y = y,
                Width = width,
                Height = height,
                Angle = angle
            });
        }

        if (hasShapePoints)
        {
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.PointCloud,
                State = VisionOverlayState.Warning,
                Label = string.Empty,
                Points = shapePoints
            });
        }

        foreach (var contour in shapeContours)
        {
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Polyline,
                State = VisionOverlayState.Warning,
                Label = string.Empty,
                Points = contour
            });
        }

        overlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.Cross,
            State = state,
            Label = $"Locate {result.Data.GetValueOrDefault("score", "-")}",
            X = x,
            Y = y,
            Angle = angle
        });
    }

    private static void AddMultiTargetOverlay(List<VisionOverlayItem> overlays, ToolResult result, VisionOverlayState state)
    {
        var index = 0;
        foreach (var match in ParseMultiTargetMatches(result.Data.GetValueOrDefault("matches")))
        {
            index++;
            if (match.Shape.Equals("Circle", StringComparison.OrdinalIgnoreCase))
            {
                overlays.Add(new VisionOverlayItem
                {
                    Kind = VisionOverlayKind.Circle,
                    State = state,
                    Label = $"#{index}",
                    X = match.X,
                    Y = match.Y,
                    Radius = match.Radius > 0 ? match.Radius : Math.Max(match.Width, match.Height) / 2.0
                });
                overlays.Add(new VisionOverlayItem
                {
                    Kind = VisionOverlayKind.Cross,
                    State = state,
                    Label = string.Empty,
                    X = match.X,
                    Y = match.Y,
                    Angle = match.Angle
                });
                continue;
            }

            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.RotatedRectangle,
                State = state,
                Label = $"#{index}",
                X = match.X,
                Y = match.Y,
                Width = Math.Max(12, match.Width),
                Height = Math.Max(12, match.Height),
                Angle = match.Angle
            });
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Cross,
                State = state,
                Label = string.Empty,
                X = match.X,
                Y = match.Y,
                Angle = match.Angle
            });
        }
    }

    private static IReadOnlyList<OverlayMatch> ParseMultiTargetMatches(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<OverlayMatch>();
        }

        var matches = new List<OverlayMatch>();
        foreach (var item in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 6 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var angle) ||
                !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var score) ||
                !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var width) ||
                !double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
            {
                continue;
            }

            var shape = parts.Length >= 7 ? parts[6] : "Rectangle";
            var radius = parts.Length >= 8 &&
                         double.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRadius)
                ? parsedRadius
                : 0;

            matches.Add(new OverlayMatch(x, y, angle, score, width, height, shape, radius));
        }

        return matches;
    }

    private static bool TryParsePointList(string? text, out IReadOnlyList<Point2D> points)
    {
        var parsed = new List<Point2D>();
        points = parsed;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var item in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                continue;
            }

            parsed.Add(new Point2D(x, y));
        }

        return parsed.Count > 0;
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
            if (TryParsePointList(contourText, out var contour) && contour.Count >= 2)
            {
                contours.Add(contour);
            }
        }

        return contours;
    }

    private static void AddFindLineOverlay(List<VisionOverlayItem> overlays, ToolResult result, VisionOverlayState state)
    {
        if (TryParsePointList(result.Data.GetValueOrDefault("caliperPoints"), out var caliperPoints))
        {
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.XMarker,
                State = VisionOverlayState.Neutral,
                Label = string.Empty,
                Points = caliperPoints
            });
        }

        if (!TryGetDouble(result.Data, "x1", out var x1) ||
            !TryGetDouble(result.Data, "y1", out var y1) ||
            !TryGetDouble(result.Data, "x2", out var x2) ||
            !TryGetDouble(result.Data, "y2", out var y2))
        {
            return;
        }

        overlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.LineSegment,
            State = state,
            Label = result.Outcome == InspectionOutcome.Ok
                ? $"{result.ToolName} {result.Data.GetValueOrDefault("angle", "-")} deg"
                : result.Message,
            X = x1,
            Y = y1,
            X2 = x2,
            Y2 = y2
        });
    }

    private static void AddFindCircleOverlay(List<VisionOverlayItem> overlays, ToolResult result, VisionOverlayState state)
    {
        if (!TryGetDouble(result.Data, "x", out var x) ||
            !TryGetDouble(result.Data, "y", out var y) ||
            !TryGetDouble(result.Data, "radius", out var radius))
        {
            return;
        }

        overlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.Circle,
            State = state,
            Label = result.Outcome == InspectionOutcome.Ok
                ? $"{result.ToolName} R={radius:0.0}"
                : result.Message,
            X = x,
            Y = y,
            Radius = radius
        });
    }

    private static void AddSearchRoiOverlay(List<VisionOverlayItem> overlays, ToolResult result)
    {
        if (!result.Data.TryGetValue("searchRoiShape", out var shapeText) ||
            !Enum.TryParse<RoiShapeKind>(shapeText, true, out var shape) ||
            !TryGetDouble(result.Data, "searchRoiX", out var x) ||
            !TryGetDouble(result.Data, "searchRoiY", out var y))
        {
            return;
        }

        var label = $"{result.ToolName} ROI";
        switch (shape)
        {
            case RoiShapeKind.Circle when TryGetDouble(result.Data, "searchRoiRadius", out var radius):
                if (result.Kind == VisionToolKind.FindCircle)
                {
                    AddCircleCaliperRoiOverlay(overlays, result, label, x, y, radius);
                    break;
                }

                overlays.Add(new VisionOverlayItem
                {
                    Kind = VisionOverlayKind.Circle,
                    State = VisionOverlayState.Neutral,
                    Label = label,
                    X = x,
                    Y = y,
                    Radius = radius
                });
                break;
            case RoiShapeKind.RotatedRectangle
                when TryGetDouble(result.Data, "searchRoiWidth", out var rotatedWidth) &&
                     TryGetDouble(result.Data, "searchRoiHeight", out var rotatedHeight):
                TryGetDouble(result.Data, "searchRoiAngle", out var angle);
                overlays.Add(new VisionOverlayItem
                {
                    Kind = VisionOverlayKind.RotatedRectangle,
                    State = VisionOverlayState.Neutral,
                    Label = label,
                    X = x,
                    Y = y,
                    Width = rotatedWidth,
                    Height = rotatedHeight,
                    Angle = angle
                });
                break;
            default:
                if (!TryGetDouble(result.Data, "searchRoiWidth", out var width) ||
                    !TryGetDouble(result.Data, "searchRoiHeight", out var height))
                {
                    return;
                }

                overlays.Add(new VisionOverlayItem
                {
                    Kind = VisionOverlayKind.Rectangle,
                    State = VisionOverlayState.Neutral,
                    Label = label,
                    X = x,
                    Y = y,
                    Width = width,
                    Height = height
                });
                break;
        }
    }

    private static void AddCircleCaliperRoiOverlay(
        List<VisionOverlayItem> overlays,
        ToolResult result,
        string label,
        double x,
        double y,
        double radius)
    {
        var searchWidth = TryGetDouble(result.Data, "searchWidth", out var parsedSearchWidth)
            ? Math.Max(2, parsedSearchWidth)
            : Math.Max(2, radius * 0.2);
        var caliperWidth = TryGetDouble(result.Data, "caliperWidth", out var parsedCaliperWidth)
            ? Math.Max(1, parsedCaliperWidth)
            : Math.Max(1, searchWidth * 0.18);
        var caliperCount = TryGetInt(result.Data, "caliperCount", out var parsedCaliperCount)
            ? Math.Clamp(parsedCaliperCount, 3, 720)
            : 24;
        var innerRadius = Math.Max(1, radius - searchWidth / 2.0);
        var outerRadius = Math.Max(innerRadius + 1, radius + searchWidth / 2.0);
        var visualCaliperLength = Math.Clamp(searchWidth * 0.35, 3, Math.Min(searchWidth, 10));
        var visualCaliperWidth = Math.Clamp(caliperWidth, 1, 3);

        overlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.CircleAnnulus,
            State = VisionOverlayState.Warning,
            Label = label,
            X = x,
            Y = y,
            Width = innerRadius,
            Radius = outerRadius
        });

        var visibleCalipers = Math.Min(caliperCount, 96);
        for (var index = 0; index < visibleCalipers; index++)
        {
            var angle = index * Math.PI * 2.0 / visibleCalipers;
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.RotatedRectangleOutline,
                State = VisionOverlayState.Ng,
                X = x + Math.Cos(angle) * radius,
                Y = y + Math.Sin(angle) * radius,
                Width = visualCaliperLength,
                Height = visualCaliperWidth,
                Angle = angle * 180.0 / Math.PI
            });
        }
    }

    private static void AddMeasurementOverlay(
        List<VisionOverlayItem> overlays,
        Recipe recipe,
        ImageFrame frame,
        ToolResult result,
        VisionOverlayState state)
    {
        var label = $"{result.Data.GetValueOrDefault("value", "-")} {result.Data.GetValueOrDefault("unit", "")}".Trim();
        var mode = result.Data.GetValueOrDefault("mode", string.Empty);
        if (string.Equals(mode, "PointLine", StringComparison.OrdinalIgnoreCase) &&
            TryGetPoint(result.Data, "point", out var point) &&
            TryGetPoint(result.Data, "foot", out var foot) &&
            TryGetLine(result.Data, "line", out var line))
        {
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.LineSegment,
                State = VisionOverlayState.Neutral,
                X = line.Start.X,
                Y = line.Start.Y,
                X2 = line.End.X,
                Y2 = line.End.Y
            });
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.LineSegment,
                State = state,
                Label = label,
                X = point.X,
                Y = point.Y,
                X2 = foot.X,
                Y2 = foot.Y
            });
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Cross,
                State = VisionOverlayState.Warning,
                X = point.X,
                Y = point.Y
            });
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Cross,
                State = VisionOverlayState.Warning,
                X = foot.X,
                Y = foot.Y
            });
            return;
        }

        if (string.Equals(mode, "PointPoint", StringComparison.OrdinalIgnoreCase) &&
            TryGetPoint(result.Data, "p1", out var firstPoint) &&
            TryGetPoint(result.Data, "p2", out var secondPoint))
        {
            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.LineSegment,
                State = state,
                Label = label,
                X = firstPoint.X,
                Y = firstPoint.Y,
                X2 = secondPoint.X,
                Y2 = secondPoint.Y
            });
            return;
        }

        overlays.Add(CreateFallbackMeasurementOverlay(recipe, frame, result, state));
    }

    private static VisionOverlayItem CreateFallbackMeasurementOverlay(
        Recipe recipe,
        ImageFrame frame,
        ToolResult result,
        VisionOverlayState state)
    {
        var roi = FindBoundRoi(recipe, result, "measure", "尺寸", "测量") ?? recipe.Rois.FirstOrDefault();
        var bounds = GetRoiBounds(roi, frame);
        var y = bounds.Y + bounds.Height * 0.78;

        return new VisionOverlayItem
        {
            Kind = VisionOverlayKind.Line,
            State = state,
            Label = $"{result.Data.GetValueOrDefault("value", "-")} {result.Data.GetValueOrDefault("unit", "")}",
            X = bounds.X + bounds.Width * 0.12,
            Y = y,
            X2 = bounds.X + bounds.Width * 0.88,
            Y2 = y
        };
    }

    private static void AddLineAngleOverlay(List<VisionOverlayItem> overlays, ToolResult result, VisionOverlayState state)
    {
        if (!TryGetLine(result.Data, "line1", out var firstLine) ||
            !TryGetLine(result.Data, "line2", out var secondLine))
        {
            return;
        }

        overlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.LineSegment,
            State = VisionOverlayState.Neutral,
            X = firstLine.Start.X,
            Y = firstLine.Start.Y,
            X2 = firstLine.End.X,
            Y2 = firstLine.End.Y
        });
        overlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.LineSegment,
            State = state,
            Label = $"{result.Data.GetValueOrDefault("angle", "-")} deg",
            X = secondLine.Start.X,
            Y = secondLine.Start.Y,
            X2 = secondLine.End.X,
            Y2 = secondLine.End.Y
        });
    }

    private static void AddFitLineOverlay(List<VisionOverlayItem> overlays, ToolResult result, VisionOverlayState state)
    {
        if (!TryGetLine(result.Data, string.Empty, out var line))
        {
            return;
        }

        overlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.LineSegment,
            State = state,
            Label = $"A={result.Data.GetValueOrDefault("angle", "-")}",
            X = line.Start.X,
            Y = line.Start.Y,
            X2 = line.End.X,
            Y2 = line.End.Y
        });

        if (TryGetPoint(result.Data, "p1", out var firstPoint))
        {
            AddCross(overlays, firstPoint, VisionOverlayState.Warning);
        }

        if (TryGetPoint(result.Data, "p2", out var secondPoint))
        {
            AddCross(overlays, secondPoint, VisionOverlayState.Warning);
        }
    }

    private static void AddPointOverlay(
        List<VisionOverlayItem> overlays,
        ToolResult result,
        string label,
        VisionOverlayState state)
    {
        if (!TryGetPoint(result.Data, string.Empty, out var point))
        {
            return;
        }

        overlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.Cross,
            State = state,
            Label = label,
            X = point.X,
            Y = point.Y
        });
    }

    private static void AddCross(List<VisionOverlayItem> overlays, Point2D point, VisionOverlayState state)
    {
        overlays.Add(new VisionOverlayItem
        {
            Kind = VisionOverlayKind.Cross,
            State = state,
            X = point.X,
            Y = point.Y
        });
    }

    private static bool TryGetPoint(IReadOnlyDictionary<string, string> data, string prefix, out Point2D point)
    {
        point = default!;
        if (!TryGetDouble(data, $"{prefix}X", out var x) ||
            !TryGetDouble(data, $"{prefix}Y", out var y))
        {
            return false;
        }

        point = new Point2D(x, y);
        return true;
    }

    private static bool TryGetLine(IReadOnlyDictionary<string, string> data, string prefix, out Line2D line)
    {
        line = default!;
        if (!TryGetDouble(data, $"{prefix}X1", out var x1) ||
            !TryGetDouble(data, $"{prefix}Y1", out var y1) ||
            !TryGetDouble(data, $"{prefix}X2", out var x2) ||
            !TryGetDouble(data, $"{prefix}Y2", out var y2))
        {
            return false;
        }

        line = new Line2D(new Point2D(x1, y1), new Point2D(x2, y2));
        return true;
    }

    private static VisionOverlayItem CreateCodeOverlay(
        Recipe recipe,
        ImageFrame frame,
        ToolResult result,
        VisionOverlayState state)
    {
        var roi = FindBoundRoi(recipe, result, "code", "读码", "条码");
        var bounds = GetRoiBounds(roi, frame);

        return new VisionOverlayItem
        {
            Kind = VisionOverlayKind.Rectangle,
            State = state,
            Label = result.Data.GetValueOrDefault("code", "Code"),
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height
        };
    }

    private static void AddBlobOverlay(
        List<VisionOverlayItem> overlays,
        Recipe recipe,
        ImageFrame frame,
        ToolResult result,
        VisionOverlayState state)
    {
        var blobs = BlobAnalysisResultCodec.ParseBlobs(result.Data.GetValueOrDefault("blobs"));
        if (blobs.Count > 0)
        {
            var centers = new List<Point2D>(blobs.Count);
            foreach (var blob in blobs)
            {
                if (blob.Contour.Count >= 3)
                {
                    overlays.Add(new VisionOverlayItem
                    {
                        Kind = VisionOverlayKind.Polyline,
                        State = state,
                        Label = string.Empty,
                        Points = blob.Contour
                    });
                }
                else
                {
                    overlays.Add(new VisionOverlayItem
                    {
                        Kind = VisionOverlayKind.Rectangle,
                        State = state,
                        Label = string.Empty,
                        X = blob.Left,
                        Y = blob.Top,
                        Width = blob.Width,
                        Height = blob.Height
                    });
                }

                centers.Add(new Point2D(blob.X, blob.Y));
            }

            overlays.Add(new VisionOverlayItem
            {
                Kind = VisionOverlayKind.XMarker,
                State = state,
                Label = string.Empty,
                Points = centers
            });
            return;
        }

        overlays.Add(CreateBlobFallbackOverlay(recipe, frame, result, state));
    }

    private static VisionOverlayItem CreateBlobFallbackOverlay(
        Recipe recipe,
        ImageFrame frame,
        ToolResult result,
        VisionOverlayState state)
    {
        var roi = FindBoundRoi(recipe, result, "defect", "缺陷", "外观");
        if (roi is not null)
        {
            var overlay = VisionOverlayItem.FromRoi(roi, state);
            return overlay with { Label = result.Message };
        }

        var bounds = GetRoiBounds(null, frame);
        return new VisionOverlayItem
        {
            Kind = VisionOverlayKind.Rectangle,
            State = state,
            Label = result.Message,
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height
        };
    }

    private static RoiDefinition? FindRoi(Recipe recipe, params string[] tokens)
    {
        return recipe.Rois.FirstOrDefault(roi =>
            tokens.Any(token => roi.Id.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                                roi.Name.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    private static RoiDefinition? FindBoundRoi(Recipe recipe, ToolResult result, params string[] fallbackTokens)
    {
        var tool = recipe.Tools.FirstOrDefault(definition => definition.Id == result.ToolId);
        if (tool is not null &&
            tool.Parameters.TryGetValue("roiId", out var roiId) &&
            !string.IsNullOrWhiteSpace(roiId))
        {
            var boundRoi = recipe.Rois.FirstOrDefault(roi => string.Equals(roi.Id, roiId, StringComparison.OrdinalIgnoreCase));
            if (boundRoi is not null)
            {
                return boundRoi;
            }
        }

        return FindRoi(recipe, fallbackTokens);
    }

    private static OverlayBounds GetRoiBounds(RoiDefinition? roi, ImageFrame frame)
    {
        if (roi is null)
        {
            return new OverlayBounds(frame.Width * 0.35, frame.Height * 0.32, frame.Width * 0.3, frame.Height * 0.22);
        }

        return roi.Shape switch
        {
            RoiShapeKind.Circle => new OverlayBounds(roi.X - roi.Radius, roi.Y - roi.Radius, roi.Radius * 2, roi.Radius * 2),
            RoiShapeKind.Polygon when roi.Points.Count > 0 => new OverlayBounds(
                roi.Points.Min(point => point.X),
                roi.Points.Min(point => point.Y),
                roi.Points.Max(point => point.X) - roi.Points.Min(point => point.X),
                roi.Points.Max(point => point.Y) - roi.Points.Min(point => point.Y)),
            RoiShapeKind.RotatedRectangle => new OverlayBounds(roi.X - roi.Width / 2, roi.Y - roi.Height / 2, roi.Width, roi.Height),
            _ => new OverlayBounds(roi.X, roi.Y, roi.Width, roi.Height)
        };
    }

    private static bool TryGetDouble(IReadOnlyDictionary<string, string> data, string key, out double value)
    {
        value = 0;
        return data.TryGetValue(key, out var raw) &&
               double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetInt(IReadOnlyDictionary<string, string> data, string key, out int value)
    {
        value = 0;
        return data.TryGetValue(key, out var raw) &&
               int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private sealed record OverlayBounds(double X, double Y, double Width, double Height);

    private sealed record OverlayMatch(
        double X,
        double Y,
        double Angle,
        double Score,
        double Width,
        double Height,
        string Shape = "Rectangle",
        double Radius = 0);
}
