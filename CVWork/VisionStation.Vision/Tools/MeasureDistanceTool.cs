using System.Diagnostics;
using VisionStation.Domain;

namespace VisionStation.Vision.Tools;

public sealed class MeasureDistanceTool : IVisionTool
{
    private const string Unit = "px";

    public VisionToolKind Kind => VisionToolKind.MeasureDistance;

    public Task<ToolResult> ExecuteAsync(
        VisionToolDefinition definition,
        VisionToolContext context,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var frame = context.GetInputImage(definition);
        var mode = GetMeasurementMode(definition);
        var unit = definition.Parameters.GetValueOrDefault("unit") ?? Unit;

        if (!TryMeasure(definition, context, mode, out var measured, out var data, out var failureMessage))
        {
            stopwatch.Stop();
            return Task.FromResult(CreateFailureResult(
                definition,
                stopwatch.Elapsed,
                frame,
                unit,
                failureMessage));
        }
        stopwatch.Stop();

        context.SetPortOutput(definition, "MeasureValueOutput", measured);
        data["value"] = measured.ToInvariant();
        data["unit"] = unit;
        data["inputFrameId"] = frame.Id;
        data["mode"] = mode;

        return Task.FromResult(new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = Kind,
            Outcome = InspectionOutcome.Ok,
            Duration = stopwatch.Elapsed,
            Message = $"{GetModeDisplayName(mode)} completed: {measured.ToInvariant()} {unit}",
            Data = data
        });
    }

    private static bool TryMeasure(
        VisionToolDefinition definition,
        VisionToolContext context,
        string mode,
        out double measured,
        out Dictionary<string, string> data,
        out string failureMessage)
    {
        measured = 0;
        data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        failureMessage = string.Empty;

        switch (mode)
        {
            case "PointLine":
                if (!context.TryGetPortInput<Point2D>(definition, "PointInput", out var point) ||
                    !context.TryGetPortInput<Line2D>(definition, "LineInput", out var line))
                {
                    failureMessage = "点线距离失败：未连接点或线";
                    return false;
                }

                var foot = ProjectPointToLine(point, line);
                measured = Distance(point, foot);
                context.SetPortOutput(definition, "FootPointOutput", foot);
                AddPointData(data, "point", point);
                AddPointData(data, "foot", foot);
                AddLineData(data, "line", line);
                return true;

            case "LineLine":
                if (!context.TryGetPortInput<Line2D>(definition, "Line1Input", out var firstLine) ||
                    !context.TryGetPortInput<Line2D>(definition, "Line2Input", out var secondLine))
                {
                    failureMessage = "线线距离失败：未连接线1或线2";
                    return false;
                }

                measured = SegmentDistance(firstLine, secondLine);
                AddLineData(data, "line1", firstLine);
                AddLineData(data, "line2", secondLine);
                return true;

            default:
                if (!context.TryGetPortInput<Point2D>(definition, "Point1Input", out var firstPoint) ||
                    !context.TryGetPortInput<Point2D>(definition, "Point2Input", out var secondPoint))
                {
                    failureMessage = "点点距离失败：未连接点1或点2";
                    return false;
                }

                measured = Distance(firstPoint, secondPoint);
                AddPointData(data, "p1", firstPoint);
                AddPointData(data, "p2", secondPoint);
                return true;
        }
    }

    private static ToolResult CreateFailureResult(
        VisionToolDefinition definition,
        TimeSpan duration,
        ImageFrame frame,
        string unit,
        string message)
    {
        return new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = VisionToolKind.MeasureDistance,
            Outcome = InspectionOutcome.Ng,
            Duration = duration,
            Message = message,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["unit"] = unit,
                ["inputFrameId"] = frame.Id
            }
        };
    }

    private static string GetMeasurementMode(VisionToolDefinition definition)
    {
        return NormalizeMeasurementMode(
            definition.Parameters.GetValueOrDefault("measurementMode") ??
            GuessMeasurementMode(definition.Name));
    }

    private static string GuessMeasurementMode(string name)
    {
        if (name.Contains("点线", StringComparison.OrdinalIgnoreCase))
        {
            return "PointLine";
        }

        if (name.Contains("线线", StringComparison.OrdinalIgnoreCase))
        {
            return "LineLine";
        }

        return "PointPoint";
    }

    private static string NormalizeMeasurementMode(string value)
    {
        return value.Trim() switch
        {
            "PointLine" or "point_line" or "点线距离" => "PointLine",
            "LineLine" or "line_line" or "线线距离" => "LineLine",
            _ => "PointPoint"
        };
    }

    private static string GetModeDisplayName(string mode)
    {
        return mode switch
        {
            "PointLine" => "点线距离",
            "LineLine" => "线线距离",
            _ => "点点距离"
        };
    }

    private static void AddPointData(Dictionary<string, string> data, string prefix, Point2D point)
    {
        data[$"{prefix}X"] = point.X.ToInvariant();
        data[$"{prefix}Y"] = point.Y.ToInvariant();
    }

    private static void AddLineData(Dictionary<string, string> data, string prefix, Line2D line)
    {
        data[$"{prefix}X1"] = line.Start.X.ToInvariant();
        data[$"{prefix}Y1"] = line.Start.Y.ToInvariant();
        data[$"{prefix}X2"] = line.End.X.ToInvariant();
        data[$"{prefix}Y2"] = line.End.Y.ToInvariant();
    }

    private static double Distance(Point2D first, Point2D second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double PointToLineDistance(Point2D point, Line2D line)
    {
        return Distance(point, ProjectPointToLine(point, line));
    }

    private static Point2D ProjectPointToLine(Point2D point, Line2D line)
    {
        var dx = line.End.X - line.Start.X;
        var dy = line.End.Y - line.Start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-9)
        {
            return line.Start;
        }

        var t = ((point.X - line.Start.X) * dx + (point.Y - line.Start.Y) * dy) / lengthSquared;
        return new Point2D(line.Start.X + t * dx, line.Start.Y + t * dy);
    }

    private static double SegmentDistance(Line2D first, Line2D second)
    {
        if (SegmentsIntersect(first.Start, first.End, second.Start, second.End))
        {
            return 0;
        }

        return new[]
        {
            PointToSegmentDistance(first.Start, second.Start, second.End),
            PointToSegmentDistance(first.End, second.Start, second.End),
            PointToSegmentDistance(second.Start, first.Start, first.End),
            PointToSegmentDistance(second.End, first.Start, first.End)
        }.Min();
    }

    private static double PointToSegmentDistance(Point2D point, Point2D start, Point2D end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-9)
        {
            return Distance(point, start);
        }

        var t = Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared, 0, 1);
        return Distance(point, new Point2D(start.X + t * dx, start.Y + t * dy));
    }

    private static bool SegmentsIntersect(Point2D a, Point2D b, Point2D c, Point2D d)
    {
        var o1 = Orientation(a, b, c);
        var o2 = Orientation(a, b, d);
        var o3 = Orientation(c, d, a);
        var o4 = Orientation(c, d, b);
        return o1 * o2 < 0 && o3 * o4 < 0;
    }

    private static double Orientation(Point2D a, Point2D b, Point2D c)
    {
        return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    }
}
