using System.Diagnostics;
using OpenCvSharp;
using VisionStation.Domain;

namespace VisionStation.Vision.Tools;

public sealed class FindCircleTool : IVisionTool
{
    public VisionToolKind Kind => VisionToolKind.FindCircle;

    public Task<ToolResult> ExecuteAsync(VisionToolDefinition definition, VisionToolContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!context.TryGetInputImage(definition, out var frame))
        {
            stopwatch.Stop();
            return Task.FromResult(GeometryToolSupport.CreateMissingImageInputResult(definition, Kind, stopwatch.Elapsed));
        }

        var sourceRoi = GeometryToolSupport.FindBoundRoi(context.Recipe, definition);
        if (sourceRoi is null)
        {
            stopwatch.Stop();
            return Task.FromResult(CreateResult(definition, stopwatch.Elapsed, InspectionOutcome.Ng, "找圆失败：未绑定圆 ROI", frame));
        }

        var roi = GeometryToolSupport.MapRoiForPositionInput(context, definition, sourceRoi);
        if (roi.Shape != RoiShapeKind.Circle)
        {
            stopwatch.Stop();
            return Task.FromResult(CreateResult(definition, stopwatch.Elapsed, InspectionOutcome.Ng, "找圆失败：ROI 必须是圆", frame));
        }

        var gray = context.GetGrayMat(frame);
        var caliperCount = Math.Clamp((int)Math.Round(definition.Parameters.GetDouble("caliperCount", 24)), 3, 720);
        var threshold = Math.Clamp(definition.Parameters.GetDouble("edgeThreshold", 30), 0, 255);
        var caliperWidth = Math.Clamp(definition.Parameters.GetDouble("caliperWidth", 4), 1, Math.Max(1, roi.Radius));
        var searchWidth = Math.Clamp(definition.Parameters.GetDouble("searchWidth", 24), 2, Math.Max(2, roi.Radius * 2));
        var points = CollectEdgePoints(
            gray,
            roi,
            caliperCount,
            caliperWidth,
            searchWidth,
            threshold,
            definition.Parameters.GetValueOrDefault("circlePolarity") ?? "从暗到明",
            definition.Parameters.GetValueOrDefault("searchDirection") ?? "从内到外",
            definition.Parameters.GetValueOrDefault("resultSelection") ?? "最强",
            cancellationToken);
        var score = points.Count / (double)caliperCount;
        var minScore = Math.Clamp(definition.Parameters.GetDouble("minScore", 0.5), 0, 1);

        stopwatch.Stop();
        if (points.Count < 3 || score < minScore || !TryFitCircle(points, out var fitted))
        {
            var missing = CreateResult(
                definition,
                stopwatch.Elapsed,
                InspectionOutcome.Ng,
                $"找圆失败：有效卡尺 {points.Count}/{caliperCount}",
                frame);
            AddCaliperData(missing.Data, roi, points.Count, caliperCount, caliperWidth, searchWidth, score);
            return Task.FromResult(missing);
        }

        context.SetPortOutput(definition, "CircleOutput", new Circle2D(fitted.Center, fitted.Radius));
        context.SetPortOutput(definition, "CenterOutput", fitted.Center);
        context.SetPortOutput(definition, "RadiusOutput", fitted.Radius);
        var result = CreateResult(
            definition,
            stopwatch.Elapsed,
            InspectionOutcome.Ok,
            $"找圆完成，有效卡尺 {points.Count}/{caliperCount}",
            frame);
        result.Data["x"] = fitted.Center.X.ToInvariant();
        result.Data["y"] = fitted.Center.Y.ToInvariant();
        result.Data["radius"] = fitted.Radius.ToInvariant();
        AddCaliperData(result.Data, roi, points.Count, caliperCount, caliperWidth, searchWidth, score);
        return Task.FromResult(result);
    }

    private static IReadOnlyList<Point2D> CollectEdgePoints(
        Mat gray,
        RoiDefinition roi,
        int caliperCount,
        double caliperWidth,
        double searchWidth,
        double threshold,
        string polarity,
        string searchDirection,
        string resultSelection,
        CancellationToken cancellationToken)
    {
        var points = new List<Point2D>(caliperCount);
        var radiusStart = Math.Max(1, roi.Radius - searchWidth / 2.0);
        var radiusEnd = Math.Max(radiusStart + 1, roi.Radius + searchWidth / 2.0);
        var step = Math.Max(0.5, searchWidth / 160.0);
        var outward = IsOutward(searchDirection);

        for (var index = 0; index < caliperCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var angle = index * Math.PI * 2.0 / caliperCount;
            var candidates = new List<EdgeCandidate>();
            if (outward)
            {
                SampleCandidates(radiusStart, radiusEnd, step);
            }
            else
            {
                SampleCandidates(radiusEnd, radiusStart, -step);
            }

            var selected = SelectCandidate(candidates, resultSelection);
            if (selected is not null)
            {
                points.Add(ToImagePoint(roi, angle, selected.Radius));
            }

            void SampleCandidates(double start, double end, double signedStep)
            {
                for (var radius = start; signedStep > 0 ? radius <= end : radius >= end; radius += signedStep)
                {
                    if (!TryAverageTangentialStrip(gray, roi, angle, radius - signedStep, caliperWidth, out var before) ||
                        !TryAverageTangentialStrip(gray, roi, angle, radius + signedStep, caliperWidth, out var after))
                    {
                        continue;
                    }

                    var gradient = after - before;
                    if (AcceptGradient(gradient, polarity, threshold))
                    {
                        candidates.Add(new EdgeCandidate(radius, gradient));
                    }
                }
            }
        }

        return points;
    }

    private static bool TryAverageTangentialStrip(
        Mat gray,
        RoiDefinition roi,
        double angle,
        double radius,
        double caliperWidth,
        out double average)
    {
        average = 0;
        var tangentX = -Math.Sin(angle);
        var tangentY = Math.Cos(angle);
        var radialX = Math.Cos(angle);
        var radialY = Math.Sin(angle);
        var halfWidth = Math.Max(0.5, caliperWidth / 2.0);
        var sampleCount = Math.Clamp((int)Math.Ceiling(caliperWidth), 1, 96);
        var sum = 0.0;
        var valid = 0;

        for (var index = 0; index < sampleCount; index++)
        {
            var offset = sampleCount == 1
                ? 0
                : -halfWidth + caliperWidth * index / (sampleCount - 1);
            var x = roi.X + radialX * radius + tangentX * offset;
            var y = roi.Y + radialY * radius + tangentY * offset;
            var pixelX = (int)Math.Round(x);
            var pixelY = (int)Math.Round(y);
            if (pixelX < 0 || pixelX >= gray.Width || pixelY < 0 || pixelY >= gray.Height)
            {
                continue;
            }

            sum += gray.At<byte>(pixelY, pixelX);
            valid++;
        }

        if (valid == 0)
        {
            return false;
        }

        average = sum / valid;
        return true;
    }

    private static EdgeCandidate? SelectCandidate(IReadOnlyList<EdgeCandidate> candidates, string resultSelection)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (IsFirstSelection(resultSelection))
        {
            return candidates.First();
        }

        if (IsLastSelection(resultSelection))
        {
            return candidates.Last();
        }

        return resultSelection switch
        {
            "第一个" or "first" => candidates.First(),
            "最后一个" or "last" => candidates.Last(),
            _ => candidates.OrderByDescending(candidate => Math.Abs(candidate.Gradient)).First()
        };
    }

    private static bool AcceptGradient(double gradient, string polarity, double threshold)
    {
        if (IsBrightToDark(polarity))
        {
            return -gradient >= threshold;
        }

        if (IsAnyPolarity(polarity))
        {
            return Math.Abs(gradient) >= threshold;
        }

        return polarity switch
        {
            "从明到暗" or "bright_to_dark" => -gradient >= threshold,
            "全部" or "all" => Math.Abs(gradient) >= threshold,
            _ => gradient >= threshold
        };
    }

    private static bool TryFitCircle(IReadOnlyList<Point2D> points, out FittedCircle circle)
    {
        circle = default!;
        if (points.Count < 3)
        {
            return false;
        }

        var matrix = new double[3, 4];
        foreach (var point in points)
        {
            var x = point.X;
            var y = point.Y;
            var z = -(x * x + y * y);
            matrix[0, 0] += x * x;
            matrix[0, 1] += x * y;
            matrix[0, 2] += x;
            matrix[0, 3] += x * z;
            matrix[1, 0] += x * y;
            matrix[1, 1] += y * y;
            matrix[1, 2] += y;
            matrix[1, 3] += y * z;
            matrix[2, 0] += x;
            matrix[2, 1] += y;
            matrix[2, 2] += 1;
            matrix[2, 3] += z;
        }

        if (!Solve3x3(matrix, out var solution))
        {
            return false;
        }

        var center = new Point2D(-solution[0] / 2.0, -solution[1] / 2.0);
        var radiusSquared = center.X * center.X + center.Y * center.Y - solution[2];
        if (!double.IsFinite(radiusSquared) || radiusSquared <= 0)
        {
            return false;
        }

        var radius = Math.Sqrt(radiusSquared);
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) || !double.IsFinite(radius))
        {
            return false;
        }

        circle = new FittedCircle(center, radius);
        return true;
    }

    private static bool Solve3x3(double[,] matrix, out double[] solution)
    {
        solution = new double[3];
        for (var column = 0; column < 3; column++)
        {
            var pivot = column;
            for (var row = column + 1; row < 3; row++)
            {
                if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column]))
                {
                    pivot = row;
                }
            }

            if (Math.Abs(matrix[pivot, column]) < 1e-9)
            {
                return false;
            }

            if (pivot != column)
            {
                for (var item = column; item < 4; item++)
                {
                    (matrix[column, item], matrix[pivot, item]) = (matrix[pivot, item], matrix[column, item]);
                }
            }

            var divisor = matrix[column, column];
            for (var item = column; item < 4; item++)
            {
                matrix[column, item] /= divisor;
            }

            for (var row = 0; row < 3; row++)
            {
                if (row == column)
                {
                    continue;
                }

                var factor = matrix[row, column];
                for (var item = column; item < 4; item++)
                {
                    matrix[row, item] -= factor * matrix[column, item];
                }
            }
        }

        solution[0] = matrix[0, 3];
        solution[1] = matrix[1, 3];
        solution[2] = matrix[2, 3];
        return true;
    }

    private static Point2D ToImagePoint(RoiDefinition roi, double angle, double radius)
    {
        return new Point2D(roi.X + Math.Cos(angle) * radius, roi.Y + Math.Sin(angle) * radius);
    }

    private static bool IsOutward(string searchDirection)
    {
        if (searchDirection is "从外到内" or "從外到內")
        {
            return false;
        }

        return searchDirection is not ("从外到内" or "outside_to_inside");
    }

    private static bool IsFirstSelection(string resultSelection)
    {
        return resultSelection is "\u7B2C\u4E00\u4E2A" or "\u7B2C\u4E00\u500B";
    }

    private static bool IsLastSelection(string resultSelection)
    {
        return resultSelection is "\u6700\u540E\u4E00\u4E2A" or "\u6700\u5F8C\u4E00\u500B";
    }

    private static bool IsBrightToDark(string polarity)
    {
        return polarity is "\u4ECE\u660E\u5230\u6697" or "\u5F9E\u660E\u5230\u6697";
    }

    private static bool IsAnyPolarity(string polarity)
    {
        return polarity is "\u5168\u90E8";
    }

/*
    private static bool IsFirstSelection(string resultSelection)
    {
        return resultSelection is "第一个" or "第一個";
    }

    private static bool IsLastSelection(string resultSelection)
    {
        return resultSelection is "最后一个" or "最後一個";
    }

    private static bool IsBrightToDark(string polarity)
    {
        return polarity is "从明到暗" or "從明到暗";
    }

    private static bool IsAnyPolarity(string polarity)
    {
        return polarity is "全部";
    }

*/
    private static void AddCaliperData(
        Dictionary<string, string> data,
        RoiDefinition roi,
        int validCaliperCount,
        int caliperCount,
        double caliperWidth,
        double searchWidth,
        double score)
    {
        data["score"] = score.ToInvariant();
        data["validCaliperCount"] = validCaliperCount.ToString();
        data["caliperCount"] = caliperCount.ToString();
        data["caliperWidth"] = caliperWidth.ToInvariant();
        data["searchWidth"] = searchWidth.ToInvariant();
        GeometryToolSupport.AddSearchRoiData(data, roi);
    }

    private static ToolResult CreateResult(
        VisionToolDefinition definition,
        TimeSpan duration,
        InspectionOutcome outcome,
        string message,
        ImageFrame frame)
    {
        return new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = VisionToolKind.FindCircle,
            Outcome = outcome,
            Duration = duration,
            Message = message,
            Data = new Dictionary<string, string>
            {
                ["inputFrameId"] = frame.Id
            }
        };
    }

    private sealed record EdgeCandidate(double Radius, double Gradient);

    private sealed record FittedCircle(Point2D Center, double Radius);
}
