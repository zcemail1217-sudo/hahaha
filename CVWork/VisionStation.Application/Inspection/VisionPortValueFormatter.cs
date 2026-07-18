using VisionStation.Domain;
using VisionStation.Vision;

namespace VisionStation.Application.Inspection;

internal static class VisionPortValueFormatter
{
    public static string FormatPortValue(ToolResult result, string portKey, VisionPipelineResult? pipelineResult)
    {
        if (string.IsNullOrWhiteSpace(portKey))
        {
            return string.Empty;
        }

        if (portKey.EndsWith("ResultOutput", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(portKey, "OverallResultOutput", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(portKey, "OverallResultOutput", StringComparison.OrdinalIgnoreCase) && pipelineResult is not null
                ? pipelineResult.Outcome.ToString()
                : result.Outcome.ToString();
        }

        return portKey switch
        {
            "ImageOutput" => result.Data.GetValueOrDefault("outputFrameId")
                             ?? result.Data.GetValueOrDefault("frameId")
                             ?? result.Data.GetValueOrDefault("inputFrameId")
                             ?? result.Data.GetValueOrDefault("source")
                             ?? string.Empty,
            "PositionOutput" or "OriginOutput" or "BestPositionOutput" => FormatPose(result),
            "PointOutput" or "CenterOutput" or "BestCenterOutput" or "MidPointOutput" or "FootPointOutput" => FormatPoint(result, portKey),
            "LineOutput" => FormatLine(result),
            "CircleOutput" or "BestCircleOutput" => FormatCircle(result, portKey),
            "RoiOutput" or "BestRectOutput" => result.Data.GetValueOrDefault("roi")
                                               ?? result.Data.GetValueOrDefault("bestRect")
                                               ?? string.Empty,
            "ScoreOutput" => FirstNonEmpty(result.Data, "score"),
            "CountOutput" => FirstNonEmpty(result.Data, "count"),
            "XOutput" => FirstNonEmpty(result.Data, "x", "worldX"),
            "YOutput" => FirstNonEmpty(result.Data, "y", "worldY"),
            "AngleOutput" => FirstNonEmpty(result.Data, "angle"),
            "ScaleOutput" => FirstNonEmpty(result.Data, "scale"),
            "RadiusOutput" => FirstNonEmpty(result.Data, "radius"),
            "MeasureValueOutput" => FirstNonEmpty(result.Data, "measured", "distance", "value", "angle"),
            "DeviationOutput" => FirstNonEmpty(result.Data, "deviation"),
            "AbsDeviationOutput" => FirstNonEmpty(result.Data, "absDeviation"),
            "MarginOutput" => FirstNonEmpty(result.Data, "margin"),
            "NominalOutput" => FirstNonEmpty(result.Data, "nominal"),
            "LowerLimitOutput" => FirstNonEmpty(result.Data, "lowerLimit"),
            "UpperLimitOutput" => FirstNonEmpty(result.Data, "upperLimit"),
            "LengthOutput" => FirstNonEmpty(result.Data, "length"),
            "CodeOutput" => FirstNonEmpty(result.Data, "code"),
            "TextOutput" => FirstNonEmpty(result.Data, "text", "code"),
            "AllPositionsOutput" => FirstNonEmpty(result.Data, "matches"),
            "ScoresOutput" => FirstNonEmpty(result.Data, "scores"),
            "ScalesOutput" => FirstNonEmpty(result.Data, "scales"),
            "AllCentersOutput" => FirstNonEmpty(result.Data, "allCenters", "centers"),
            "BestAreaOutput" => FirstNonEmpty(result.Data, "area", "bestArea"),
            "BestWidthOutput" => FirstNonEmpty(result.Data, "width", "bestWidth"),
            "BestHeightOutput" => FirstNonEmpty(result.Data, "height", "bestHeight"),
            "BestAspectRatioOutput" => FirstNonEmpty(result.Data, "aspectRatio", "bestAspectRatio"),
            "BestPerimeterOutput" => FirstNonEmpty(result.Data, "perimeter", "bestPerimeter"),
            "BestCircularityOutput" => FirstNonEmpty(result.Data, "circularity", "bestCircularity"),
            "BestContourOutput" => FirstNonEmpty(result.Data, "contour", "bestContour"),
            _ => result.Data.GetValueOrDefault(portKey) ?? string.Empty
        };
    }

    private static string FormatPose(ToolResult result)
    {
        var x = FirstNonEmpty(result.Data, "x");
        var y = FirstNonEmpty(result.Data, "y");
        var angle = FirstNonEmpty(result.Data, "angle");
        return string.IsNullOrWhiteSpace(x) && string.IsNullOrWhiteSpace(y) && string.IsNullOrWhiteSpace(angle)
            ? string.Empty
            : $"{x},{y},{angle}";
    }

    private static string FormatPoint(ToolResult result, string portKey)
    {
        return portKey switch
        {
            "FootPointOutput" => BuildTuple(FirstNonEmpty(result.Data, "footX"), FirstNonEmpty(result.Data, "footY")),
            "BestCenterOutput" => BuildTuple(FirstNonEmpty(result.Data, "centerX", "bestCenterX", "x"), FirstNonEmpty(result.Data, "centerY", "bestCenterY", "y")),
            _ => BuildTuple(FirstNonEmpty(result.Data, "x", "centerX"), FirstNonEmpty(result.Data, "y", "centerY"))
        };
    }

    private static string FormatLine(ToolResult result)
    {
        var startX = FirstNonEmpty(result.Data, "startX", "x1");
        var startY = FirstNonEmpty(result.Data, "startY", "y1");
        var endX = FirstNonEmpty(result.Data, "endX", "x2");
        var endY = FirstNonEmpty(result.Data, "endY", "y2");
        return string.IsNullOrWhiteSpace(startX) && string.IsNullOrWhiteSpace(endX)
            ? string.Empty
            : $"{startX},{startY};{endX},{endY}";
    }

    private static string FormatCircle(ToolResult result, string portKey)
    {
        var centerX = FirstNonEmpty(result.Data, "centerX", "x");
        var centerY = FirstNonEmpty(result.Data, "centerY", "y");
        var radius = portKey == "BestCircleOutput"
            ? FirstNonEmpty(result.Data, "circleRadius", "radius")
            : FirstNonEmpty(result.Data, "radius");
        return string.IsNullOrWhiteSpace(centerX) && string.IsNullOrWhiteSpace(radius)
            ? string.Empty
            : $"{centerX},{centerY},{radius}";
    }

    private static string BuildTuple(string first, string second)
    {
        return string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(second)
            ? string.Empty
            : $"{first},{second}";
    }

    private static string FirstNonEmpty(IReadOnlyDictionary<string, string> data, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (data.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }
}
