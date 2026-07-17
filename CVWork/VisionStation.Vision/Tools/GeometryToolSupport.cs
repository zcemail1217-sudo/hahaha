using System.Globalization;
using OpenCvSharp;
using VisionStation.Domain;

namespace VisionStation.Vision.Tools;

internal static class GeometryToolSupport
{
    public static ToolResult CreateMissingImageInputResult(
        VisionToolDefinition definition,
        VisionToolKind kind,
        TimeSpan duration)
    {
        return new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = kind,
            Outcome = InspectionOutcome.Ng,
            Duration = duration,
            Message = "未连接输入图像",
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["missingInput"] = "ImageInput"
            }
        };
    }

    public static ToolResult CreatePositionInputMappingFailureResult(
        VisionToolDefinition definition,
        VisionToolKind kind,
        TimeSpan duration,
        ImageFrame frame,
        PositionInputMappingFailure failure)
    {
        return new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = kind,
            Outcome = InspectionOutcome.Ng,
            Duration = duration,
            Message = failure.Message,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["code"] = failure.Code,
                ["inputFrameId"] = frame.Id
            }
        };
    }

    public static RoiDefinition? FindBoundRoi(Recipe recipe, VisionToolDefinition definition)
    {
        if (!definition.Parameters.TryGetValue("roiId", out var roiId) || string.IsNullOrWhiteSpace(roiId))
        {
            return null;
        }

        return recipe.Rois.FirstOrDefault(roi => string.Equals(roi.Id, roiId, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryMapRoiForPositionInput(
        VisionToolContext context,
        VisionToolDefinition definition,
        RoiDefinition sourceRoi,
        out RoiDefinition mappedRoi,
        out PositionInputMappingFailure? failure)
    {
        mappedRoi = sourceRoi;
        if (!TryResolvePositionInputMapping(
                context,
                definition,
                out var currentPose,
                out var referencePose,
                out failure))
        {
            return false;
        }

        if (currentPose is null || referencePose is null)
        {
            return true;
        }

        mappedRoi = PoseSimilarityTransform.MapRoi(sourceRoi, referencePose, currentPose);
        return true;
    }

    public static bool TryValidatePositionInputMapping(
        VisionToolContext context,
        VisionToolDefinition definition,
        out PositionInputMappingFailure? failure)
    {
        return TryResolvePositionInputMapping(
            context,
            definition,
            out _,
            out _,
            out failure);
    }

    private static bool TryResolvePositionInputMapping(
        VisionToolContext context,
        VisionToolDefinition definition,
        out Pose2D? currentPose,
        out Pose2D? referencePose,
        out PositionInputMappingFailure? failure)
    {
        currentPose = null;
        referencePose = null;
        failure = null;
        if (!context.TryGetPortInput<Pose2D>(definition, "PositionInput", out var positionInput))
        {
            return true;
        }

        if (!PoseSimilarityTransform.IsValidScale(positionInput.Scale))
        {
            failure = CreateInvalidScaleFailure(
                "PositionInput.Scale",
                positionInput.Scale.ToString("R", CultureInfo.InvariantCulture));
            return false;
        }

        if (!TryGetTaughtReferencePose(definition, out var resolvedReferencePose, out failure) &&
            failure is null &&
            !TryGetTemplateReferencePose(context.Recipe, definition, out resolvedReferencePose, out failure))
        {
            return failure is null;
        }

        if (failure is not null)
        {
            return false;
        }

        currentPose = positionInput;
        referencePose = resolvedReferencePose;
        return true;
    }

    public static Mat ToGrayMat(ImageFrame frame)
    {
        return ImageFrameMatFactory.ToGrayMat(frame);
    }

    public static Rect GetCropBounds(RoiDefinition roi, ImageFrame frame)
    {
        var bounds = GetBounds(roi);
        var left = Math.Clamp((int)Math.Floor(bounds.X), 0, Math.Max(0, frame.Width - 1));
        var top = Math.Clamp((int)Math.Floor(bounds.Y), 0, Math.Max(0, frame.Height - 1));
        var right = Math.Clamp((int)Math.Ceiling(bounds.X + bounds.Width), left + 1, frame.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(bounds.Y + bounds.Height), top + 1, frame.Height);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    public static Mat CreateMaskedCrop(Mat gray, RoiDefinition roi, Rect cropBounds)
    {
        using var cropView = new Mat(gray, cropBounds);
        var crop = cropView.Clone();
        using var mask = CreateMask(crop.Size(), roi, cropBounds);
        Cv2.BitwiseAnd(crop, crop, crop, mask);
        return crop;
    }

    public static void AddSearchRoiData(Dictionary<string, string> data, RoiDefinition roi)
    {
        var overlayRoi = roi.Shape == RoiShapeKind.Polygon ? CreatePolygonBoundsRoi(roi) : roi;
        data["searchRoiShape"] = overlayRoi.Shape.ToString();
        data["searchRoiX"] = overlayRoi.X.ToInvariant();
        data["searchRoiY"] = overlayRoi.Y.ToInvariant();
        data["searchRoiWidth"] = overlayRoi.Width.ToInvariant();
        data["searchRoiHeight"] = overlayRoi.Height.ToInvariant();
        data["searchRoiAngle"] = overlayRoi.Angle.ToInvariant();
        data["searchRoiRadius"] = overlayRoi.Radius.ToInvariant();
    }

    private static Mat CreateMask(Size size, RoiDefinition roi, Rect cropBounds)
    {
        var mask = new Mat(size, MatType.CV_8UC1, Scalar.Black);
        switch (roi.Shape)
        {
            case RoiShapeKind.Circle:
                Cv2.Circle(
                    mask,
                    RelativePoint(roi.X, roi.Y, cropBounds),
                    Math.Max(1, (int)Math.Round(roi.Radius)),
                    Scalar.White,
                    -1);
                break;
            case RoiShapeKind.RotatedRectangle:
                var rotated = new RotatedRect(
                    new Point2f((float)(roi.X - cropBounds.X), (float)(roi.Y - cropBounds.Y)),
                    new Size2f((float)Math.Max(1, roi.Width), (float)Math.Max(1, roi.Height)),
                    (float)roi.Angle);
                Cv2.FillConvexPoly(mask, ToCvPoints(rotated.Points()), Scalar.White);
                break;
            case RoiShapeKind.Polygon when roi.Points.Count >= 3:
                Cv2.FillPoly(mask, [roi.Points.Select(point => RelativePoint(point.X, point.Y, cropBounds)).ToArray()], Scalar.White);
                break;
            default:
                Cv2.Rectangle(mask, ToRelativeRect(roi, cropBounds, size), Scalar.White, -1);
                break;
        }

        return mask;
    }

    private static bool TryGetTemplateReferencePose(
        Recipe recipe,
        VisionToolDefinition definition,
        out Pose2D pose,
        out PositionInputMappingFailure? failure)
    {
        pose = new Pose2D(0, 0, 0);
        failure = null;
        var sourceToolId = definition.Parameters.GetValueOrDefault("input:PositionInput:toolId") ?? string.Empty;
        var source = recipe.Tools.FirstOrDefault(tool => string.Equals(tool.Id, sourceToolId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return false;
        }

        if (!TryGetScale(source.Parameters, "standardScale", out var scale, out failure))
        {
            return false;
        }

        if (TryGetStandardPose(source.Parameters, scale, out pose))
        {
            return true;
        }

        return TryGetLearnedTemplatePose(source.Parameters, scale, out pose);
    }

    private static bool TryGetTaughtReferencePose(
        VisionToolDefinition definition,
        out Pose2D pose,
        out PositionInputMappingFailure? failure)
    {
        pose = new Pose2D(0, 0, 0);
        failure = null;
        if (!TryGetDouble(definition.Parameters, "roiReferencePoseX", out var x) ||
            !TryGetDouble(definition.Parameters, "roiReferencePoseY", out var y))
        {
            return false;
        }

        var sourceToolId = definition.Parameters.GetValueOrDefault("input:PositionInput:toolId") ?? string.Empty;
        var referenceToolId = definition.Parameters.GetValueOrDefault("roiReferencePoseToolId") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(referenceToolId) &&
            !string.Equals(referenceToolId, sourceToolId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        TryGetDouble(definition.Parameters, "roiReferencePoseAngle", out var angle);
        if (!TryGetScale(definition.Parameters, "roiReferencePoseScale", out var scale, out failure))
        {
            return false;
        }

        pose = new Pose2D(x, y, angle) { Scale = scale };
        return true;
    }

    private static bool TryGetStandardPose(
        IReadOnlyDictionary<string, string> parameters,
        double scale,
        out Pose2D pose)
    {
        pose = new Pose2D(0, 0, 0);
        if (!TryGetDouble(parameters, "standardX", out var x) ||
            !TryGetDouble(parameters, "standardY", out var y))
        {
            return false;
        }

        TryGetDouble(parameters, "standardAngle", out var angle);
        pose = new Pose2D(x, y, angle) { Scale = scale };
        return true;
    }

    private static bool TryGetLearnedTemplatePose(
        IReadOnlyDictionary<string, string> parameters,
        double scale,
        out Pose2D pose)
    {
        pose = new Pose2D(0, 0, 0);
        if (!TryGetDouble(parameters, "templateX", out var x) ||
            !TryGetDouble(parameters, "templateY", out var y) ||
            !TryGetDouble(parameters, "templateWidth", out var width) ||
            !TryGetDouble(parameters, "templateHeight", out var height) ||
            width <= 0 ||
            height <= 0)
        {
            return false;
        }

        pose = new Pose2D(x + width / 2.0, y + height / 2.0, 0) { Scale = scale };
        return true;
    }

    private static RoiDefinition CreatePolygonBoundsRoi(RoiDefinition roi)
    {
        var bounds = GetBounds(roi);
        return roi with
        {
            Shape = RoiShapeKind.Rectangle,
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            Angle = 0,
            Radius = 0,
            Points = Array.Empty<Point2D>()
        };
    }

    private static Bounds GetBounds(RoiDefinition roi)
    {
        return roi.Shape switch
        {
            RoiShapeKind.Circle => new Bounds(roi.X - roi.Radius, roi.Y - roi.Radius, roi.Radius * 2, roi.Radius * 2),
            RoiShapeKind.RotatedRectangle => GetRotatedRectangleBounds(roi),
            RoiShapeKind.Polygon when roi.Points.Count > 0 => new Bounds(
                roi.Points.Min(point => point.X),
                roi.Points.Min(point => point.Y),
                roi.Points.Max(point => point.X) - roi.Points.Min(point => point.X),
                roi.Points.Max(point => point.Y) - roi.Points.Min(point => point.Y)),
            _ => new Bounds(roi.X, roi.Y, roi.Width, roi.Height)
        };
    }

    private static Bounds GetRotatedRectangleBounds(RoiDefinition roi)
    {
        var corners = GetRotatedRectangleCorners(roi);
        return new Bounds(
            corners.Min(point => point.X),
            corners.Min(point => point.Y),
            corners.Max(point => point.X) - corners.Min(point => point.X),
            corners.Max(point => point.Y) - corners.Min(point => point.Y));
    }

    private static IReadOnlyList<Point2D> GetRotatedRectangleCorners(RoiDefinition roi)
    {
        var radians = roi.Angle * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return
        [
            Rotate(-roi.Width / 2.0, -roi.Height / 2.0),
            Rotate(roi.Width / 2.0, -roi.Height / 2.0),
            Rotate(roi.Width / 2.0, roi.Height / 2.0),
            Rotate(-roi.Width / 2.0, roi.Height / 2.0)
        ];

        Point2D Rotate(double x, double y)
        {
            return new Point2D(roi.X + x * cos - y * sin, roi.Y + x * sin + y * cos);
        }
    }

    private static Rect ToRelativeRect(RoiDefinition roi, Rect cropBounds, Size size)
    {
        var x = Math.Clamp((int)Math.Round(roi.X - cropBounds.X), 0, Math.Max(0, size.Width - 1));
        var y = Math.Clamp((int)Math.Round(roi.Y - cropBounds.Y), 0, Math.Max(0, size.Height - 1));
        var width = Math.Clamp((int)Math.Round(Math.Max(1, roi.Width)), 1, size.Width - x);
        var height = Math.Clamp((int)Math.Round(Math.Max(1, roi.Height)), 1, size.Height - y);
        return new Rect(x, y, width, height);
    }

    private static Point RelativePoint(double x, double y, Rect cropBounds)
    {
        return new Point((int)Math.Round(x - cropBounds.X), (int)Math.Round(y - cropBounds.Y));
    }

    private static Point[] ToCvPoints(IEnumerable<Point2f> points)
    {
        return points.Select(point => new Point((int)Math.Round(point.X), (int)Math.Round(point.Y))).ToArray();
    }

    private static bool TryGetDouble(IReadOnlyDictionary<string, string> parameters, string key, out double value)
    {
        value = 0;
        return parameters.TryGetValue(key, out var raw) &&
               double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetScale(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        out double scale,
        out PositionInputMappingFailure? failure)
    {
        failure = null;
        if (!parameters.TryGetValue(key, out var rawValue))
        {
            scale = 1;
            return true;
        }

        if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out scale))
        {
            failure = CreateInvalidScaleFailure(key, rawValue);
            return false;
        }

        if (!PoseSimilarityTransform.IsValidScale(scale))
        {
            failure = CreateInvalidScaleFailure(key, rawValue);
            return false;
        }

        return true;
    }

    private static PositionInputMappingFailure CreateInvalidScaleFailure(string parameter, string value)
    {
        return new PositionInputMappingFailure(
            "CONFIG_INVALID_PARAMETER",
            $"Position input mapping failed: {parameter} must be finite and greater than zero; actual value is '{value}'.");
    }

    private sealed record Bounds(double X, double Y, double Width, double Height);
}

internal sealed record PositionInputMappingFailure(string Code, string Message);
