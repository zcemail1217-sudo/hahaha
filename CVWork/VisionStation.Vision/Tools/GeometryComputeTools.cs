using System.Diagnostics;
using System.Globalization;
using VisionStation.Domain;

namespace VisionStation.Vision.Tools;

public sealed class LineAngleTool : IVisionTool
{
    public VisionToolKind Kind => VisionToolKind.LineAngle;

    public Task<ToolResult> ExecuteAsync(
        VisionToolDefinition definition,
        VisionToolContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        if (!context.TryGetPortInput<Line2D>(definition, "Line1Input", out var firstLine) ||
            !context.TryGetPortInput<Line2D>(definition, "Line2Input", out var secondLine))
        {
            stopwatch.Stop();
            return Task.FromResult(GeometryComputeSupport.CreateFailure(
                definition,
                Kind,
                stopwatch.Elapsed,
                "线线角度计算失败：未连接线1或线2"));
        }

        var angle = GeometryComputeSupport.IncludedAngle(firstLine, secondLine);
        stopwatch.Stop();

        context.SetPortOutput(definition, "AngleOutput", angle);
        context.SetPortOutput(definition, "MeasureValueOutput", angle);

        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["angle"] = angle.ToInvariant(),
            ["value"] = angle.ToInvariant(),
            ["unit"] = "deg"
        };
        GeometryComputeSupport.AddLineData(data, "line1", firstLine);
        GeometryComputeSupport.AddLineData(data, "line2", secondLine);

        return Task.FromResult(new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = Kind,
            Outcome = InspectionOutcome.Ok,
            Duration = stopwatch.Elapsed,
            Message = $"线线角度计算完成：{angle.ToInvariant()} deg",
            Data = data
        });
    }
}

public sealed class LineIntersectionTool : IVisionTool
{
    public VisionToolKind Kind => VisionToolKind.LineIntersection;

    public Task<ToolResult> ExecuteAsync(
        VisionToolDefinition definition,
        VisionToolContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        if (!context.TryGetPortInput<Line2D>(definition, "Line1Input", out var firstLine) ||
            !context.TryGetPortInput<Line2D>(definition, "Line2Input", out var secondLine))
        {
            stopwatch.Stop();
            return Task.FromResult(GeometryComputeSupport.CreateFailure(
                definition,
                Kind,
                stopwatch.Elapsed,
                "线线交点计算失败：未连接线1或线2"));
        }

        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        GeometryComputeSupport.AddLineData(data, "line1", firstLine);
        GeometryComputeSupport.AddLineData(data, "line2", secondLine);

        if (!GeometryComputeSupport.TryIntersect(firstLine, secondLine, out var point))
        {
            stopwatch.Stop();
            data["parallel"] = "true";
            return Task.FromResult(new ToolResult
            {
                ToolId = definition.Id,
                ToolName = definition.Name,
                Kind = Kind,
                Outcome = InspectionOutcome.Ng,
                Duration = stopwatch.Elapsed,
                Message = "线线交点计算失败：两条线平行或重合",
                Data = data
            });
        }

        stopwatch.Stop();
        context.SetPortOutput(definition, "PointOutput", point);
        context.SetPortOutput(definition, "XOutput", point.X);
        context.SetPortOutput(definition, "YOutput", point.Y);
        GeometryComputeSupport.AddPointData(data, string.Empty, point);

        return Task.FromResult(new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = Kind,
            Outcome = InspectionOutcome.Ok,
            Duration = stopwatch.Elapsed,
            Message = $"线线交点计算完成：X={point.X.ToInvariant()} Y={point.Y.ToInvariant()}",
            Data = data
        });
    }
}

public sealed class FitLineFromPointsTool : IVisionTool
{
    public VisionToolKind Kind => VisionToolKind.FitLineFromPoints;

    public Task<ToolResult> ExecuteAsync(
        VisionToolDefinition definition,
        VisionToolContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        if (!context.TryGetPortInput<Point2D>(definition, "Point1Input", out var firstPoint) ||
            !context.TryGetPortInput<Point2D>(definition, "Point2Input", out var secondPoint))
        {
            stopwatch.Stop();
            return Task.FromResult(GeometryComputeSupport.CreateFailure(
                definition,
                Kind,
                stopwatch.Elapsed,
                "两点拟合线失败：未连接点1或点2"));
        }

        var length = GeometryComputeSupport.Distance(firstPoint, secondPoint);
        if (length < 1e-6)
        {
            stopwatch.Stop();
            return Task.FromResult(GeometryComputeSupport.CreateFailure(
                definition,
                Kind,
                stopwatch.Elapsed,
                "两点拟合线失败：两个点重合"));
        }

        var line = new Line2D(firstPoint, secondPoint);
        var midpoint = new Point2D((firstPoint.X + secondPoint.X) / 2.0, (firstPoint.Y + secondPoint.Y) / 2.0);
        var angle = GeometryComputeSupport.LineAngle(line);
        stopwatch.Stop();

        context.SetPortOutput(definition, "LineOutput", line);
        context.SetPortOutput(definition, "MidPointOutput", midpoint);
        context.SetPortOutput(definition, "AngleOutput", angle);
        context.SetPortOutput(definition, "LengthOutput", length);

        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["midX"] = midpoint.X.ToInvariant(),
            ["midY"] = midpoint.Y.ToInvariant(),
            ["angle"] = angle.ToInvariant(),
            ["length"] = length.ToInvariant()
        };
        GeometryComputeSupport.AddPointData(data, "p1", firstPoint);
        GeometryComputeSupport.AddPointData(data, "p2", secondPoint);
        GeometryComputeSupport.AddLineData(data, string.Empty, line);

        return Task.FromResult(new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = Kind,
            Outcome = InspectionOutcome.Ok,
            Duration = stopwatch.Elapsed,
            Message = $"两点拟合线完成：A={angle.ToInvariant()} L={length.ToInvariant()}",
            Data = data
        });
    }
}

public sealed class TemplatePointTool : IVisionTool
{
    public VisionToolKind Kind => VisionToolKind.TemplatePoint;

    public Task<ToolResult> ExecuteAsync(
        VisionToolDefinition definition,
        VisionToolContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        if (!context.TryGetPortInput<Pose2D>(definition, "PositionInput", out var currentPose))
        {
            stopwatch.Stop();
            return Task.FromResult(GeometryComputeSupport.CreateFailure(
                definition,
                Kind,
                stopwatch.Elapsed,
                "模板点查找失败：未输入模板位置"));
        }

        var point = GeometryComputeSupport.ResolveTemplatePoint(definition.Parameters, currentPose);
        stopwatch.Stop();

        context.SetPortOutput(definition, "PointOutput", point);
        context.SetPortOutput(definition, "XOutput", point.X);
        context.SetPortOutput(definition, "YOutput", point.Y);

        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x"] = point.X.ToInvariant(),
            ["y"] = point.Y.ToInvariant(),
            ["poseX"] = currentPose.X.ToInvariant(),
            ["poseY"] = currentPose.Y.ToInvariant(),
            ["poseAngle"] = currentPose.Angle.ToInvariant()
        };

        return Task.FromResult(new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = Kind,
            Outcome = InspectionOutcome.Ok,
            Duration = stopwatch.Elapsed,
            Message = $"模板点查找完成：X={point.X.ToInvariant()} Y={point.Y.ToInvariant()}",
            Data = data
        });
    }
}

internal static class GeometryComputeSupport
{
    public static ToolResult CreateFailure(
        VisionToolDefinition definition,
        VisionToolKind kind,
        TimeSpan duration,
        string message)
    {
        return new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = kind,
            Outcome = InspectionOutcome.Ng,
            Duration = duration,
            Message = message,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    public static void AddPointData(Dictionary<string, string> data, string prefix, Point2D point)
    {
        data[$"{prefix}X"] = point.X.ToInvariant();
        data[$"{prefix}Y"] = point.Y.ToInvariant();
    }

    public static void AddLineData(Dictionary<string, string> data, string prefix, Line2D line)
    {
        data[$"{prefix}X1"] = line.Start.X.ToInvariant();
        data[$"{prefix}Y1"] = line.Start.Y.ToInvariant();
        data[$"{prefix}X2"] = line.End.X.ToInvariant();
        data[$"{prefix}Y2"] = line.End.Y.ToInvariant();
    }

    public static double Distance(Point2D first, Point2D second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static double LineAngle(Line2D line)
    {
        return NormalizeAngle(Math.Atan2(line.End.Y - line.Start.Y, line.End.X - line.Start.X) * 180.0 / Math.PI);
    }

    public static double IncludedAngle(Line2D firstLine, Line2D secondLine)
    {
        var diff = Math.Abs(NormalizeAngle(LineAngle(firstLine) - LineAngle(secondLine)));
        return diff > 180 ? 360 - diff : diff;
    }

    public static bool TryIntersect(Line2D firstLine, Line2D secondLine, out Point2D point)
    {
        var x1 = firstLine.Start.X;
        var y1 = firstLine.Start.Y;
        var x2 = firstLine.End.X;
        var y2 = firstLine.End.Y;
        var x3 = secondLine.Start.X;
        var y3 = secondLine.Start.Y;
        var x4 = secondLine.End.X;
        var y4 = secondLine.End.Y;
        var denominator = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
        if (Math.Abs(denominator) < 1e-9)
        {
            point = default!;
            return false;
        }

        var firstDeterminant = x1 * y2 - y1 * x2;
        var secondDeterminant = x3 * y4 - y3 * x4;
        point = new Point2D(
            (firstDeterminant * (x3 - x4) - (x1 - x2) * secondDeterminant) / denominator,
            (firstDeterminant * (y3 - y4) - (y1 - y2) * secondDeterminant) / denominator);
        return true;
    }

    public static Point2D ResolveTemplatePoint(IReadOnlyDictionary<string, string> parameters, Pose2D currentPose)
    {
        if (TryGetDouble(parameters, "pointX", out var taughtX) &&
            TryGetDouble(parameters, "pointY", out var taughtY))
        {
            var referencePose = new Pose2D(
                GetDouble(parameters, "referenceX", currentPose.X),
                GetDouble(parameters, "referenceY", currentPose.Y),
                GetDouble(parameters, "referenceAngle", currentPose.Angle))
            {
                Scale = GetDouble(parameters, "referenceScale", 1)
            };
            return PoseSimilarityTransform.MapPoint(new Point2D(taughtX, taughtY), referencePose, currentPose);
        }

        var offsetX = GetDouble(parameters, "offsetX", 0);
        var offsetY = GetDouble(parameters, "offsetY", 0);
        return PoseSimilarityTransform.MapPoint(
            new Point2D(offsetX, offsetY),
            new Pose2D(0, 0, 0),
            currentPose);
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > 180)
        {
            angle -= 360;
        }

        while (angle <= -180)
        {
            angle += 360;
        }

        return angle;
    }

    private static double GetDouble(IReadOnlyDictionary<string, string> parameters, string key, double fallback)
    {
        return TryGetDouble(parameters, key, out var value) ? value : fallback;
    }

    private static bool TryGetDouble(IReadOnlyDictionary<string, string> parameters, string key, out double value)
    {
        value = 0;
        return parameters.TryGetValue(key, out var raw) &&
               (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value));
    }
}
