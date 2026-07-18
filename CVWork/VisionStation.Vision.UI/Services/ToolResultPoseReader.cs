using System.Globalization;
using VisionStation.Domain;
using VisionStation.Vision;

namespace VisionStation.Vision.UI.Services;

internal enum PositionInputReadStatus
{
    Missing,
    Success,
    Invalid
}

internal sealed record PositionInputConfigurationFailure(string Code, string Message);

internal sealed record PositionInputPoseReadResult(
    PositionInputReadStatus Status,
    Pose2D? Pose,
    PositionInputConfigurationFailure? Failure)
{
    public static PositionInputPoseReadResult Missing()
    {
        return new PositionInputPoseReadResult(PositionInputReadStatus.Missing, null, null);
    }

    public static PositionInputPoseReadResult Success(Pose2D pose)
    {
        return new PositionInputPoseReadResult(PositionInputReadStatus.Success, pose, null);
    }

    public static PositionInputPoseReadResult Invalid(string parameter)
    {
        return new PositionInputPoseReadResult(
            PositionInputReadStatus.Invalid,
            null,
            new PositionInputConfigurationFailure(
                TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                $"Position input mapping failed: {parameter} must be finite and greater than zero."));
    }

    public static PositionInputPoseReadResult Invalid(PositionInputConfigurationFailure failure)
    {
        return new PositionInputPoseReadResult(PositionInputReadStatus.Invalid, null, failure);
    }
}

internal static class ToolResultPoseReader
{
    public static PositionInputPoseReadResult Read(ToolResult? result)
    {
        if (result?.Outcome != InspectionOutcome.Ok)
        {
            return PositionInputPoseReadResult.Missing();
        }

        var data = result.Data;
        var hasX = data.TryGetValue("x", out var xRaw);
        var hasY = data.TryGetValue("y", out var yRaw);
        var hasAngle = data.TryGetValue("angle", out var angleRaw);
        var hasScale = data.TryGetValue("scale", out var scaleRaw);
        if (!hasX && !hasY && !hasAngle && !hasScale)
        {
            return PositionInputPoseReadResult.Missing();
        }

        var x = 0d;
        if (hasX && !TryParseFinite(xRaw, out x))
        {
            return InvalidFinite("PositionInput.X", xRaw);
        }

        var y = 0d;
        if (hasY && !TryParseFinite(yRaw, out y))
        {
            return InvalidFinite("PositionInput.Y", yRaw);
        }

        var angle = 0d;
        if (hasAngle && !TryParseFinite(angleRaw, out angle))
        {
            return InvalidFinite("PositionInput.Angle", angleRaw);
        }

        var scale = 1d;
        if (hasScale &&
            (!TryParseFinite(scaleRaw, out scale) || !PoseSimilarityTransform.IsValidScale(scale)))
        {
            return PositionInputPoseReadResult.Invalid("PositionInput.Scale");
        }

        if (!hasX)
        {
            return InvalidFinite("PositionInput.X", "<missing>");
        }

        if (!hasY)
        {
            return InvalidFinite("PositionInput.Y", "<missing>");
        }

        return PositionInputPoseReadResult.Success(new Pose2D(x, y, angle) { Scale = scale });
    }

    private static PositionInputPoseReadResult InvalidFinite(string parameter, string? rawValue)
    {
        return PositionInputPoseReadResult.Invalid(
            new PositionInputConfigurationFailure(
                TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                $"Position input mapping failed: {parameter} must be finite; actual value is '{rawValue ?? "<missing>"}'."));
    }

    internal static bool TryParseFinite(string? raw, out double value)
    {
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               double.IsFinite(value);
    }
}

internal static class PositionInputReferencePoseReader
{
    public static PositionInputPoseReadResult ReadConfigured(
        IReadOnlyDictionary<string, string> parameters,
        Recipe? recipe,
        string sourceToolId)
    {
        var taught = ReadTaught(parameters, sourceToolId);
        return taught.Status == PositionInputReadStatus.Missing
            ? ReadActiveTemplate(recipe, sourceToolId)
            : taught;
    }

    public static PositionInputPoseReadResult ReadTaught(
        IReadOnlyDictionary<string, string> parameters,
        string sourceToolId)
    {
        if (string.IsNullOrWhiteSpace(sourceToolId))
        {
            return PositionInputPoseReadResult.Missing();
        }

        var referenceToolId = parameters.GetValueOrDefault("roiReferencePoseToolId") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(referenceToolId) &&
            !string.Equals(referenceToolId, sourceToolId, StringComparison.OrdinalIgnoreCase))
        {
            return PositionInputPoseReadResult.Missing();
        }

        var hasX = parameters.TryGetValue("roiReferencePoseX", out var xRaw);
        var hasY = parameters.TryGetValue("roiReferencePoseY", out var yRaw);
        var hasAngle = parameters.TryGetValue("roiReferencePoseAngle", out var angleRaw);
        var hasScale = parameters.TryGetValue("roiReferencePoseScale", out var scaleRaw);
        if (!hasX && !hasY && !hasAngle && !hasScale)
        {
            return PositionInputPoseReadResult.Missing();
        }

        var x = 0d;
        if (hasX && !ToolResultPoseReader.TryParseFinite(xRaw, out x))
        {
            return InvalidFinite("roiReferencePoseX", xRaw);
        }

        var y = 0d;
        if (hasY && !ToolResultPoseReader.TryParseFinite(yRaw, out y))
        {
            return InvalidFinite("roiReferencePoseY", yRaw);
        }

        var angle = 0d;
        if (hasAngle && !ToolResultPoseReader.TryParseFinite(angleRaw, out angle))
        {
            return InvalidFinite("roiReferencePoseAngle", angleRaw);
        }

        var scale = 1d;
        if (hasScale &&
            (!ToolResultPoseReader.TryParseFinite(scaleRaw, out scale) ||
             !PoseSimilarityTransform.IsValidScale(scale)))
        {
            return PositionInputPoseReadResult.Invalid("roiReferencePoseScale");
        }

        if (!hasX)
        {
            return InvalidFinite("roiReferencePoseX", "<missing>");
        }

        if (!hasY)
        {
            return InvalidFinite("roiReferencePoseY", "<missing>");
        }

        return PositionInputPoseReadResult.Success(new Pose2D(x, y, angle) { Scale = scale });
    }

    private static PositionInputPoseReadResult ReadActiveTemplate(Recipe? recipe, string sourceToolId)
    {
        if (recipe is null || string.IsNullOrWhiteSpace(sourceToolId))
        {
            return PositionInputPoseReadResult.Missing();
        }

        var source = recipe.GetActiveFlow().Tools.FirstOrDefault(tool =>
            string.Equals(tool.Id, sourceToolId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return PositionInputPoseReadResult.Missing();
        }

        try
        {
            var geometry = TemplateReferencePoseCodec.ReadActive(source.Parameters);
            return geometry is null
                ? PositionInputPoseReadResult.Missing()
                : PositionInputPoseReadResult.Success(geometry.StandardPose);
        }
        catch (TemplateMatchingConfigurationException exception)
        {
            var details = string.IsNullOrWhiteSpace(exception.TechnicalDetails)
                ? exception.Message
                : exception.TechnicalDetails;
            return PositionInputPoseReadResult.Invalid(
                new PositionInputConfigurationFailure(exception.Code, details));
        }
    }

    private static PositionInputPoseReadResult InvalidFinite(string parameter, string? rawValue)
    {
        return PositionInputPoseReadResult.Invalid(
            new PositionInputConfigurationFailure(
                TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                $"Position input mapping failed: {parameter} must be finite; actual value is '{rawValue ?? "<missing>"}'."));
    }
}

internal enum SearchRoiReadStatus
{
    Missing,
    Success,
    Invalid
}

internal sealed record SearchRoiReadResult(
    SearchRoiReadStatus Status,
    RoiDefinition? Roi,
    PositionInputConfigurationFailure? Failure)
{
    public static SearchRoiReadResult Missing()
    {
        return new SearchRoiReadResult(SearchRoiReadStatus.Missing, null, null);
    }

    public static SearchRoiReadResult Success(RoiDefinition roi)
    {
        return new SearchRoiReadResult(SearchRoiReadStatus.Success, roi, null);
    }

    public static SearchRoiReadResult Invalid(string message)
    {
        return new SearchRoiReadResult(
            SearchRoiReadStatus.Invalid,
            null,
            new PositionInputConfigurationFailure(
                TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                $"{TemplateMatchingDiagnosticCodes.ConfigInvalidParameter}: " +
                $"Position input preview failed: runtime search ROI {message}."));
    }
}

internal static class ToolResultSearchRoiReader
{
    public static SearchRoiReadResult Read(
        IReadOnlyDictionary<string, string> data,
        params RoiShapeKind[] allowedShapes)
    {
        var hasRuntimeData = data.Keys.Any(key =>
            key.StartsWith("searchRoi", StringComparison.OrdinalIgnoreCase));
        if (!hasRuntimeData)
        {
            return SearchRoiReadResult.Missing();
        }

        if (!data.TryGetValue("searchRoiShape", out var shapeRaw) ||
            !Enum.TryParse<RoiShapeKind>(shapeRaw, true, out var shape) ||
            !allowedShapes.Contains(shape))
        {
            return SearchRoiReadResult.Invalid("shape is missing, invalid, or unsupported");
        }

        if (!TryReadFinite(data, "searchRoiX", out var x) ||
            !TryReadFinite(data, "searchRoiY", out var y))
        {
            return SearchRoiReadResult.Invalid("center is incomplete or invalid");
        }

        switch (shape)
        {
            case RoiShapeKind.Circle:
                if (!TryReadFinite(data, "searchRoiRadius", out var radius) || radius <= 0)
                {
                    return SearchRoiReadResult.Invalid("radius is missing, non-finite, or not positive");
                }

                return SearchRoiReadResult.Success(new RoiDefinition
                {
                    Name = "Runtime ROI",
                    Shape = RoiShapeKind.Circle,
                    X = x,
                    Y = y,
                    Radius = radius
                });

            case RoiShapeKind.Rectangle:
            case RoiShapeKind.RotatedRectangle:
                if (!TryReadFinite(data, "searchRoiWidth", out var width) || width <= 0 ||
                    !TryReadFinite(data, "searchRoiHeight", out var height) || height <= 0)
                {
                    return SearchRoiReadResult.Invalid("size is incomplete, non-finite, or not positive");
                }

                var angle = 0d;
                if (shape == RoiShapeKind.RotatedRectangle &&
                    !TryReadFinite(data, "searchRoiAngle", out angle))
                {
                    return SearchRoiReadResult.Invalid("angle is missing or invalid");
                }

                return SearchRoiReadResult.Success(new RoiDefinition
                {
                    Name = "Runtime ROI",
                    Shape = shape,
                    X = x,
                    Y = y,
                    Width = width,
                    Height = height,
                    Angle = angle
                });

            default:
                return SearchRoiReadResult.Invalid("shape is unsupported");
        }
    }

    private static bool TryReadFinite(
        IReadOnlyDictionary<string, string> data,
        string key,
        out double value)
    {
        value = 0;
        return data.TryGetValue(key, out var raw) &&
               ToolResultPoseReader.TryParseFinite(raw, out value);
    }
}

internal static class PositionInputPreviewRoiResolver
{
    public static SearchRoiReadResult ResolveFallback(
        RoiDefinition sourceRoi,
        IReadOnlyDictionary<string, string> parameters,
        Recipe? recipe,
        ToolResult? sourceResult)
    {
        var sourceToolId = parameters.GetValueOrDefault("input:PositionInput:toolId") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceToolId))
        {
            return SearchRoiReadResult.Success(sourceRoi);
        }

        var current = ToolResultPoseReader.Read(sourceResult);
        if (current.Status == PositionInputReadStatus.Invalid)
        {
            return Invalid(current.Failure!);
        }

        var reference = PositionInputReferencePoseReader.ReadConfigured(parameters, recipe, sourceToolId);
        if (reference.Status == PositionInputReadStatus.Invalid)
        {
            return Invalid(reference.Failure!);
        }

        if (current.Status != PositionInputReadStatus.Success ||
            reference.Status != PositionInputReadStatus.Success)
        {
            return SearchRoiReadResult.Success(sourceRoi);
        }

        return SearchRoiReadResult.Success(
            PoseSimilarityTransform.MapRoi(sourceRoi, reference.Pose!, current.Pose!));
    }

    private static SearchRoiReadResult Invalid(PositionInputConfigurationFailure failure)
    {
        return new SearchRoiReadResult(SearchRoiReadStatus.Invalid, null, failure);
    }
}
