using System.Diagnostics;
using OpenCvSharp;
using VisionStation.Domain;

namespace VisionStation.Vision.Tools;

public sealed class FindLineTool : IVisionTool
{
    public VisionToolKind Kind => VisionToolKind.FindLine;

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
            return Task.FromResult(CreateResult(definition, stopwatch.Elapsed, InspectionOutcome.Ng, "找线失败：未绑定矩形二 ROI", frame));
        }

        var roi = GeometryToolSupport.MapRoiForPositionInput(context, definition, sourceRoi);
        if (roi.Shape != RoiShapeKind.RotatedRectangle)
        {
            stopwatch.Stop();
            return Task.FromResult(CreateResult(definition, stopwatch.Elapsed, InspectionOutcome.Ng, "找线失败：ROI 必须是带方向的矩形二", frame));
        }

        var gray = context.GetGrayMat(frame);
        var caliperCount = Math.Clamp((int)Math.Round(definition.Parameters.GetDouble("caliperCount", 20)), 2, 300);
        var threshold = Math.Clamp(definition.Parameters.GetDouble("edgeThreshold", 30), 0, 255);
        var caliperWidth = Math.Clamp(definition.Parameters.GetDouble("caliperWidth", 4), 1, Math.Max(1, roi.Height));
        var edgeSearch = CollectEdgePoints(
            gray,
            roi,
            caliperCount,
            caliperWidth,
            threshold,
            definition.Parameters.GetValueOrDefault("linePolarity") ?? "从暗到明",
            definition.Parameters.GetValueOrDefault("resultSelection") ?? "最强",
            cancellationToken);
        var minScore = Math.Clamp(definition.Parameters.GetDouble("minScore", 0.5), 0, 1);
        var inlierTolerance = Math.Max(1.5, caliperWidth);
        var fitSucceeded = TryFitLine(
            edgeSearch.SelectedPoints,
            roi,
            GetBool(definition.Parameters, "extendLine", false),
            frame.Width,
            frame.Height,
            inlierTolerance,
            out var fitted,
            out var inliers);
        var score = inliers.Count / (double)caliperCount;
        if (!fitSucceeded || score < minScore)
        {
            var consensusPoints = SelectConsensusPoints(edgeSearch.Calipers, inlierTolerance);
            var consensusSucceeded = TryFitLine(
                consensusPoints,
                roi,
                GetBool(definition.Parameters, "extendLine", false),
                frame.Width,
                frame.Height,
                inlierTolerance,
                out var consensusLine,
                out var consensusInliers);
            var consensusScore = consensusInliers.Count / (double)caliperCount;
            if (consensusSucceeded && consensusScore > score)
            {
                fitted = consensusLine;
                inliers = consensusInliers;
                score = consensusScore;
                fitSucceeded = true;
            }
        }

        stopwatch.Stop();
        if (!fitSucceeded || score < minScore)
        {
            var missing = CreateResult(
                definition,
                stopwatch.Elapsed,
                InspectionOutcome.Ng,
                $"找线失败：有效卡尺 {inliers.Count}/{caliperCount}",
                frame);
            AddCaliperData(missing.Data, roi, inliers.Count, caliperCount, score, edgeSearch.SelectedPoints.Count, edgeSearch.SelectedPoints);
            return Task.FromResult(missing);
        }

        var midpoint = new Point2D(
            (fitted.Start.X + fitted.End.X) / 2.0,
            (fitted.Start.Y + fitted.End.Y) / 2.0);
        context.SetPortOutput(definition, "LineOutput", new VisionStation.Domain.Line2D(fitted.Start, fitted.End));
        context.SetPortOutput(definition, "MidPointOutput", midpoint);

        var result = CreateResult(
            definition,
            stopwatch.Elapsed,
            InspectionOutcome.Ok,
            $"找线完成，有效卡尺 {inliers.Count}/{caliperCount}",
            frame);
        result.Data["x1"] = fitted.Start.X.ToInvariant();
        result.Data["y1"] = fitted.Start.Y.ToInvariant();
        result.Data["x2"] = fitted.End.X.ToInvariant();
        result.Data["y2"] = fitted.End.Y.ToInvariant();
        result.Data["midX"] = midpoint.X.ToInvariant();
        result.Data["midY"] = midpoint.Y.ToInvariant();
        result.Data["length"] = fitted.Length.ToInvariant();
        result.Data["angle"] = fitted.Angle.ToInvariant();
        AddCaliperData(result.Data, roi, inliers.Count, caliperCount, score, edgeSearch.SelectedPoints.Count, edgeSearch.SelectedPoints);
        return Task.FromResult(result);
    }

    private static LineEdgeSearch CollectEdgePoints(
        Mat gray,
        RoiDefinition roi,
        int caliperCount,
        double caliperWidth,
        double threshold,
        string polarity,
        string resultSelection,
        CancellationToken cancellationToken)
    {
        var selectedPoints = new List<Point2D>(caliperCount);
        var calipers = new List<CaliperEdges>(caliperCount);
        var xStart = -roi.Width / 2.0 + 1;
        var xEnd = roi.Width / 2.0 - 1;
        var xStep = Math.Max(1.0, roi.Width / 420.0);

        for (var index = 0; index < caliperCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localY = roi.Height <= 2
                ? 0
                : -roi.Height / 2.0 + roi.Height * (index + 0.5) / caliperCount;
            var candidates = new List<EdgeCandidate>();

            for (var localX = xStart; localX <= xEnd; localX += xStep)
            {
                if (!TryAverageStrip(gray, roi, localX - xStep, localY, caliperWidth, out var before) ||
                    !TryAverageStrip(gray, roi, localX + xStep, localY, caliperWidth, out var after))
                {
                    continue;
                }

                var gradient = after - before;
                if (!AcceptGradient(gradient, polarity, threshold))
                {
                    continue;
                }

                candidates.Add(new EdgeCandidate(localX, gradient));
            }

            var edges = CollapseEdgeRuns(candidates, xStep);
            calipers.Add(new CaliperEdges(
                index,
                edges.Select(edge => ToImagePoint(roi, edge.LocalX, localY)).ToArray()));
            var selected = SelectCandidate(edges, resultSelection);
            if (selected is null)
            {
                continue;
            }

            selectedPoints.Add(ToImagePoint(roi, selected.LocalX, localY));
        }

        return new LineEdgeSearch(selectedPoints, calipers);
    }

    private static IReadOnlyList<EdgeCandidate> CollapseEdgeRuns(IReadOnlyList<EdgeCandidate> samples, double xStep)
    {
        if (samples.Count < 2)
        {
            return samples;
        }

        var edges = new List<EdgeCandidate>();
        var best = samples[0];
        var previous = samples[0];
        var maxSampleGap = Math.Max(1, xStep) * 1.6;
        foreach (var sample in samples.Skip(1))
        {
            if (sample.LocalX - previous.LocalX <= maxSampleGap)
            {
                if (Math.Abs(sample.Gradient) > Math.Abs(best.Gradient))
                {
                    best = sample;
                }

                previous = sample;
                continue;
            }

            edges.Add(best);
            best = sample;
            previous = sample;
        }

        edges.Add(best);
        return edges;
    }

    private static IReadOnlyList<Point2D> SelectConsensusPoints(IReadOnlyList<CaliperEdges> calipers, double tolerance)
    {
        var indexedPoints = calipers
            .SelectMany(caliper => caliper.Points.Select(point => new IndexedPoint(caliper.Index, point)))
            .ToArray();
        IReadOnlyList<Point2D> best = Array.Empty<Point2D>();
        var bestResidual = double.PositiveInfinity;

        for (var firstIndex = 0; firstIndex < indexedPoints.Length - 1; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < indexedPoints.Length; secondIndex++)
            {
                var first = indexedPoints[firstIndex];
                var second = indexedPoints[secondIndex];
                if (first.CaliperIndex == second.CaliperIndex ||
                    Distance(first.Point, second.Point) < tolerance)
                {
                    continue;
                }

                var inliers = new List<Point2D>();
                var residual = 0.0;
                foreach (var caliper in calipers)
                {
                    var nearest = caliper.Points
                        .Select(point => new { Point = point, Distance = DistanceToLine(point, first.Point, second.Point) })
                        .Where(candidate => candidate.Distance <= tolerance)
                        .OrderBy(candidate => candidate.Distance)
                        .FirstOrDefault();
                    if (nearest is null)
                    {
                        continue;
                    }

                    inliers.Add(nearest.Point);
                    residual += nearest.Distance;
                }

                if (inliers.Count > best.Count ||
                    (inliers.Count == best.Count && residual < bestResidual))
                {
                    best = inliers;
                    bestResidual = residual;
                }
            }
        }

        return best;
    }

    private static EdgeCandidate? SelectCandidate(IReadOnlyList<EdgeCandidate> candidates, string resultSelection)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        return resultSelection switch
        {
            "第一个" or "first" => candidates.OrderBy(candidate => candidate.LocalX).First(),
            "最后一个" or "last" => candidates.OrderByDescending(candidate => candidate.LocalX).First(),
            _ => candidates.OrderByDescending(candidate => Math.Abs(candidate.Gradient)).First()
        };
    }

    private static bool AcceptGradient(double gradient, string polarity, double threshold)
    {
        return polarity switch
        {
            "从明到暗" or "bright_to_dark" => -gradient >= threshold,
            "全部" or "all" => Math.Abs(gradient) >= threshold,
            _ => gradient >= threshold
        };
    }

    private static bool TryAverageStrip(
        Mat gray,
        RoiDefinition roi,
        double localX,
        double localY,
        double caliperWidth,
        out double average)
    {
        average = 0;
        var halfWidth = Math.Max(0.5, caliperWidth / 2.0);
        var sampleCount = Math.Clamp((int)Math.Ceiling(caliperWidth), 1, 96);
        var sum = 0.0;
        var valid = 0;

        for (var index = 0; index < sampleCount; index++)
        {
            var offset = sampleCount == 1
                ? 0
                : -halfWidth + caliperWidth * index / (sampleCount - 1);
            var point = ToImagePoint(roi, localX, localY + offset);
            var x = (int)Math.Round(point.X);
            var y = (int)Math.Round(point.Y);
            if (x < 0 || x >= gray.Width || y < 0 || y >= gray.Height)
            {
                continue;
            }

            sum += gray.At<byte>(y, x);
            valid++;
        }

        if (valid == 0)
        {
            return false;
        }

        average = sum / valid;
        return true;
    }

    private static bool TryFitLine(
        IReadOnlyList<Point2D> points,
        RoiDefinition roi,
        bool extendLine,
        int imageWidth,
        int imageHeight,
        double inlierTolerance,
        out FittedLine fitted,
        out IReadOnlyList<Point2D> inliers)
    {
        fitted = default!;
        inliers = Array.Empty<Point2D>();
        if (points.Count < 2)
        {
            return false;
        }

        inliers = FindBestLineInliers(points, inlierTolerance);
        if (inliers.Count < 2)
        {
            return false;
        }

        fitted = FitLine(inliers, roi, extendLine, imageWidth, imageHeight);
        var refined = GetLineInliers(points, fitted, inlierTolerance);
        if (refined.Count >= 2)
        {
            inliers = refined;
            fitted = FitLine(inliers, roi, extendLine, imageWidth, imageHeight);
        }

        return true;
    }

    private static IReadOnlyList<Point2D> FindBestLineInliers(IReadOnlyList<Point2D> points, double tolerance)
    {
        var best = Array.Empty<Point2D>();
        var bestResidual = double.PositiveInfinity;
        for (var firstIndex = 0; firstIndex < points.Count - 1; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < points.Count; secondIndex++)
            {
                var start = points[firstIndex];
                var end = points[secondIndex];
                if (Distance(start, end) < tolerance)
                {
                    continue;
                }

                var candidates = points
                    .Where(point => DistanceToLine(point, start, end) <= tolerance)
                    .ToArray();
                if (candidates.Length < 2)
                {
                    continue;
                }

                var residual = candidates.Sum(point => DistanceToLine(point, start, end));
                if (candidates.Length > best.Length ||
                    (candidates.Length == best.Length && residual < bestResidual))
                {
                    best = candidates;
                    bestResidual = residual;
                }
            }
        }

        return best.Length == 0 ? points.ToArray() : best;
    }

    private static IReadOnlyList<Point2D> GetLineInliers(IReadOnlyList<Point2D> points, FittedLine line, double tolerance)
    {
        return points
            .Where(point => DistanceToLine(point, line.Start, line.End) <= tolerance)
            .ToArray();
    }

    private static FittedLine FitLine(IReadOnlyList<Point2D> points, RoiDefinition roi, bool extendLine, int imageWidth, int imageHeight)
    {
        var centerX = points.Average(point => point.X);
        var centerY = points.Average(point => point.Y);
        var sxx = 0.0;
        var syy = 0.0;
        var sxy = 0.0;
        foreach (var point in points)
        {
            var dx = point.X - centerX;
            var dy = point.Y - centerY;
            sxx += dx * dx;
            syy += dy * dy;
            sxy += dx * dy;
        }

        var angleRadians = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        var directionX = Math.Cos(angleRadians);
        var directionY = Math.Sin(angleRadians);
        Point2D start;
        Point2D end;
        if (extendLine &&
            TryExtendLineToImageBounds(centerX, centerY, directionX, directionY, imageWidth, imageHeight, out var extendedStart, out var extendedEnd))
        {
            start = extendedStart;
            end = extendedEnd;
        }
        else
        {
            var projections = points
                .Select(point => (point.X - centerX) * directionX + (point.Y - centerY) * directionY)
                .ToArray();
            var min = projections.Min();
            var max = projections.Max();
            start = new Point2D(centerX + directionX * min, centerY + directionY * min);
            end = new Point2D(centerX + directionX * max, centerY + directionY * max);
        }

        var length = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
        return new FittedLine(start, end, length, angleRadians * 180.0 / Math.PI);
    }

    private static double Distance(Point2D first, Point2D second)
    {
        return Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));
    }

    private static double DistanceToLine(Point2D point, Point2D start, Point2D end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-9)
        {
            return Distance(point, start);
        }

        return Math.Abs(dy * point.X - dx * point.Y + end.X * start.Y - end.Y * start.X) / length;
    }

    private static bool TryExtendLineToImageBounds(
        double centerX,
        double centerY,
        double directionX,
        double directionY,
        int imageWidth,
        int imageHeight,
        out Point2D start,
        out Point2D end)
    {
        start = new Point2D(0, 0);
        end = new Point2D(0, 0);
        var right = Math.Max(0, imageWidth - 1);
        var bottom = Math.Max(0, imageHeight - 1);
        var intersections = new List<(double T, Point2D Point)>();

        if (Math.Abs(directionX) > 1e-9)
        {
            AddVerticalIntersection(0);
            AddVerticalIntersection(right);
        }

        if (Math.Abs(directionY) > 1e-9)
        {
            AddHorizontalIntersection(0);
            AddHorizontalIntersection(bottom);
        }

        var distinct = intersections
            .GroupBy(item => $"{Math.Round(item.Point.X, 3)}:{Math.Round(item.Point.Y, 3)}")
            .Select(group => group.First())
            .OrderBy(item => item.T)
            .ToArray();
        if (distinct.Length < 2)
        {
            return false;
        }

        start = distinct.First().Point;
        end = distinct.Last().Point;
        return true;

        void AddVerticalIntersection(double x)
        {
            var t = (x - centerX) / directionX;
            var y = centerY + directionY * t;
            if (y >= -1e-6 && y <= bottom + 1e-6)
            {
                intersections.Add((t, new Point2D(Math.Clamp(x, 0, right), Math.Clamp(y, 0, bottom))));
            }
        }

        void AddHorizontalIntersection(double y)
        {
            var t = (y - centerY) / directionY;
            var x = centerX + directionX * t;
            if (x >= -1e-6 && x <= right + 1e-6)
            {
                intersections.Add((t, new Point2D(Math.Clamp(x, 0, right), Math.Clamp(y, 0, bottom))));
            }
        }
    }

    private static Point2D ToImagePoint(RoiDefinition roi, double localX, double localY)
    {
        var radians = roi.Angle * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new Point2D(
            roi.X + localX * cos - localY * sin,
            roi.Y + localX * sin + localY * cos);
    }

    private static void AddCaliperData(
        Dictionary<string, string> data,
        RoiDefinition roi,
        int validCaliperCount,
        int caliperCount,
        double score,
        int edgePointCount,
        IReadOnlyList<Point2D> selectedPoints)
    {
        data["score"] = score.ToInvariant();
        data["validCaliperCount"] = validCaliperCount.ToString();
        data["caliperCount"] = caliperCount.ToString();
        data["edgePointCount"] = edgePointCount.ToString();
        if (selectedPoints.Count > 0)
        {
            data["caliperPoints"] = string.Join(
                ";",
                selectedPoints.Select(point => $"{point.X.ToInvariant()},{point.Y.ToInvariant()}"));
        }

        GeometryToolSupport.AddSearchRoiData(data, roi);
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> parameters, string key, bool fallback)
    {
        return parameters.TryGetValue(key, out var raw) && bool.TryParse(raw, out var value)
            ? value
            : fallback;
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
            Kind = VisionToolKind.FindLine,
            Outcome = outcome,
            Duration = duration,
            Message = message,
            Data = new Dictionary<string, string>
            {
                ["inputFrameId"] = frame.Id
            }
        };
    }

    private sealed record EdgeCandidate(double LocalX, double Gradient);

    private sealed record CaliperEdges(int Index, IReadOnlyList<Point2D> Points);

    private sealed record IndexedPoint(int CaliperIndex, Point2D Point);

    private sealed record LineEdgeSearch(IReadOnlyList<Point2D> SelectedPoints, IReadOnlyList<CaliperEdges> Calipers);

    private sealed record FittedLine(Point2D Start, Point2D End, double Length, double Angle);
}
