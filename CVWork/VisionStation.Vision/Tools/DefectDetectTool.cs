using System.Diagnostics;
using OpenCvSharp;
using VisionStation.Domain;
using VisionStation.Vision;

namespace VisionStation.Vision.Tools;

public sealed class DefectDetectTool : IVisionTool
{
    public VisionToolKind Kind => VisionToolKind.DefectDetect;

    public Task<ToolResult> ExecuteAsync(VisionToolDefinition definition, VisionToolContext context, CancellationToken cancellationToken = default)
    {
        RemoveOutputs(context, definition);
        var stopwatch = Stopwatch.StartNew();
        if (!context.TryGetInputImage(definition, out var frame))
        {
            stopwatch.Stop();
            return Task.FromResult(GeometryToolSupport.CreateMissingImageInputResult(definition, Kind, stopwatch.Elapsed));
        }

        var sourceRoi = GeometryToolSupport.FindBoundRoi(context.Recipe, definition);
        RoiDefinition? mappedRoi = sourceRoi;
        if (sourceRoi is null &&
            !GeometryToolSupport.TryValidatePositionInputMapping(context, definition, out var missingRoiMappingFailure))
        {
            stopwatch.Stop();
            return Task.FromResult(GeometryToolSupport.CreatePositionInputMappingFailureResult(
                definition,
                Kind,
                stopwatch.Elapsed,
                frame,
                missingRoiMappingFailure!));
        }

        if (sourceRoi is not null &&
            !GeometryToolSupport.TryMapRoiForPositionInput(
                context,
                definition,
                sourceRoi,
                out mappedRoi,
                out var mappingFailure))
        {
            stopwatch.Stop();
            return Task.FromResult(GeometryToolSupport.CreatePositionInputMappingFailureResult(
                definition,
                Kind,
                stopwatch.Elapsed,
                frame,
                mappingFailure!));
        }

        var gray = context.GetGrayMat(frame);
        var cropBounds = mappedRoi is null
            ? new Rect(0, 0, frame.Width, frame.Height)
            : GeometryToolSupport.GetCropBounds(mappedRoi, frame);
        using var crop = new Mat(gray, cropBounds);
        using var mask = CreateMask(crop.Size(), mappedRoi, cropBounds);
        using var binary = CreateBinary(crop, mask, definition.Parameters);

        ApplyMorphology(binary, definition.Parameters);
        var candidates = FindBlobs(binary, cropBounds, definition.Parameters, cancellationToken);
        var selected = SelectBlobs(candidates, mappedRoi, frame, definition.Parameters);
        var minCount = Math.Clamp((int)Math.Round(definition.Parameters.GetDouble("minCount", 1)), 0, 1_000_000);
        var maxCount = Math.Max(minCount, (int)Math.Round(definition.Parameters.GetDouble("maxCount", 1_000_000)));
        var outcome = selected.Count >= minCount && selected.Count <= maxCount
            ? InspectionOutcome.Ok
            : InspectionOutcome.Ng;

        context.SetPortOutput(definition, "CountOutput", selected.Count);
        context.SetPortOutput(definition, "AllCentersOutput", selected.Select(blob => blob.Center).ToArray());
        if (selected.FirstOrDefault() is { } best)
        {
            context.SetPortOutput(definition, "BestCenterOutput", best.Center);
            context.SetPortOutput(definition, "BestAreaOutput", best.Area);
            context.SetPortOutput(definition, "BestCircularityOutput", best.Circularity);
            context.SetPortOutput(definition, "BestWidthOutput", best.Width);
            context.SetPortOutput(definition, "BestHeightOutput", best.Height);
            context.SetPortOutput(definition, "BestAspectRatioOutput", best.AspectRatio);
            context.SetPortOutput(definition, "BestPerimeterOutput", best.Perimeter);
            context.SetPortOutput(definition, "BestCircleOutput", new Circle2D(best.CircleCenter, best.CircleRadius));
            context.SetPortOutput(definition, "BestContourOutput", best.Contour.ToArray());
            context.SetPortOutput(
                definition,
                "BestRectOutput",
                new RoiDefinition
                {
                    Id = $"{definition.Id}-best-blob",
                    Name = $"{definition.Name} 外接矩形",
                    Shape = RoiShapeKind.Rectangle,
                    X = best.Left,
                    Y = best.Top,
                    Width = best.Width,
                    Height = best.Height
                });
        }

        stopwatch.Stop();
        var data = CreateResultData(frame, mappedRoi, cropBounds, binary, selected, minCount, maxCount, definition.Parameters);
        return Task.FromResult(new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = Kind,
            Outcome = outcome,
            Duration = stopwatch.Elapsed,
            Message = outcome == InspectionOutcome.Ok
                ? $"斑点分析完成：{selected.Count} 个"
                : $"斑点分析 NG：找到 {selected.Count} 个，要求 {minCount}-{maxCount} 个",
            Data = data
        });
    }

    private static void RemoveOutputs(VisionToolContext context, VisionToolDefinition definition)
    {
        context.RemovePortOutput(definition, "CountOutput");
        context.RemovePortOutput(definition, "AllCentersOutput");
        context.RemovePortOutput(definition, "BestCenterOutput");
        context.RemovePortOutput(definition, "BestAreaOutput");
        context.RemovePortOutput(definition, "BestCircularityOutput");
        context.RemovePortOutput(definition, "BestWidthOutput");
        context.RemovePortOutput(definition, "BestHeightOutput");
        context.RemovePortOutput(definition, "BestAspectRatioOutput");
        context.RemovePortOutput(definition, "BestPerimeterOutput");
        context.RemovePortOutput(definition, "BestCircleOutput");
        context.RemovePortOutput(definition, "BestContourOutput");
        context.RemovePortOutput(definition, "BestRectOutput");
    }

    private static Mat CreateBinary(Mat crop, Mat mask, IReadOnlyDictionary<string, string> parameters)
    {
        var binary = new Mat();
        var mode = NormalizeThresholdMode(parameters.GetValueOrDefault("thresholdMode"));
        var darkBlob = IsDarkBlob(parameters.GetValueOrDefault("polarity"));
        var thresholdType = darkBlob ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;

        if (mode == "Range")
        {
            var lower = NormalizeThreshold(parameters.GetDouble("grayMin", parameters.GetDouble("grayLower", 0)));
            var upper = NormalizeThreshold(parameters.GetDouble("grayMax", parameters.GetDouble("grayUpper", 255)));
            if (upper < lower)
            {
                (lower, upper) = (upper, lower);
            }

            Cv2.InRange(crop, new Scalar(lower), new Scalar(upper), binary);
        }
        else if (mode == "Adaptive")
        {
            var blockSize = NormalizeAdaptiveBlockSize(parameters.GetDouble("adaptiveBlockSize", 41));
            var constant = parameters.GetDouble("adaptiveC", 5);
            Cv2.AdaptiveThreshold(crop, binary, 255, AdaptiveThresholdTypes.GaussianC, thresholdType, blockSize, constant);
        }
        else
        {
            var threshold = NormalizeThreshold(parameters.GetDouble("threshold", 128));
            var effectiveType = mode == "Otsu" ? thresholdType | ThresholdTypes.Otsu : thresholdType;
            Cv2.Threshold(crop, binary, threshold, 255, effectiveType);
        }

        Cv2.BitwiseAnd(binary, binary, binary, mask);
        return binary;
    }

    private static void ApplyMorphology(Mat binary, IReadOnlyDictionary<string, string> parameters)
    {
        var openSize = Math.Clamp((int)Math.Round(parameters.GetDouble("morphOpen", 1)), 0, 31);
        var closeSize = Math.Clamp((int)Math.Round(parameters.GetDouble("morphClose", 0)), 0, 31);
        if (openSize > 0)
        {
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(openSize * 2 + 1, openSize * 2 + 1));
            Cv2.MorphologyEx(binary, binary, MorphTypes.Open, kernel);
        }

        if (closeSize > 0)
        {
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(closeSize * 2 + 1, closeSize * 2 + 1));
            Cv2.MorphologyEx(binary, binary, MorphTypes.Close, kernel);
        }
    }

    private static IReadOnlyList<BlobAnalysisBlob> FindBlobs(
        Mat binary,
        Rect cropBounds,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        Cv2.FindContours(
            binary,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        var minArea = Math.Max(0, parameters.GetDouble("minArea", 30));
        var maxArea = Math.Max(minArea, parameters.GetDouble("maxArea", 1_000_000));
        var minWidth = Math.Max(0, parameters.GetDouble("minWidth", 0));
        var maxWidth = Math.Max(minWidth, parameters.GetDouble("maxWidth", 1_000_000));
        var minHeight = Math.Max(0, parameters.GetDouble("minHeight", 0));
        var maxHeight = Math.Max(minHeight, parameters.GetDouble("maxHeight", 1_000_000));
        var minCircularity = Math.Clamp(parameters.GetDouble("minCircularity", 0), 0, 1);
        var maxCircularity = Math.Clamp(parameters.GetDouble("maxCircularity", 1), minCircularity, 1);
        var minAspectRatio = Math.Max(0, parameters.GetDouble("minAspectRatio", 0));
        var maxAspectRatio = Math.Max(minAspectRatio, parameters.GetDouble("maxAspectRatio", 1_000_000));
        var blobs = new List<BlobAnalysisBlob>();

        foreach (var contour in contours)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var area = Cv2.ContourArea(contour);
            if (area < minArea || area > maxArea)
            {
                continue;
            }

            var bounds = Cv2.BoundingRect(contour);
            if (bounds.Width < minWidth || bounds.Width > maxWidth ||
                bounds.Height < minHeight || bounds.Height > maxHeight)
            {
                continue;
            }

            var shortSide = Math.Max(1, Math.Min(bounds.Width, bounds.Height));
            var aspectRatio = Math.Max(bounds.Width, bounds.Height) / (double)shortSide;
            if (aspectRatio < minAspectRatio || aspectRatio > maxAspectRatio)
            {
                continue;
            }

            var perimeter = Cv2.ArcLength(contour, true);
            var circularity = perimeter <= 0 ? 0 : Math.Clamp(4.0 * Math.PI * area / (perimeter * perimeter), 0, 1);
            if (circularity < minCircularity || circularity > maxCircularity)
            {
                continue;
            }

            var moments = Cv2.Moments(contour);
            var centerX = moments.M00 == 0 ? bounds.X + bounds.Width / 2.0 : moments.M10 / moments.M00;
            var centerY = moments.M00 == 0 ? bounds.Y + bounds.Height / 2.0 : moments.M01 / moments.M00;
            var contourPoints = CreateContourPoints(contour, cropBounds);
            Cv2.MinEnclosingCircle(contour, out var circleCenter, out var circleRadius);
            blobs.Add(new BlobAnalysisBlob(
                centerX + cropBounds.X,
                centerY + cropBounds.Y,
                area,
                bounds.X + cropBounds.X,
                bounds.Y + cropBounds.Y,
                bounds.Width,
                bounds.Height,
                circularity,
                aspectRatio,
                perimeter,
                circleCenter.X + cropBounds.X,
                circleCenter.Y + cropBounds.Y,
                circleRadius,
                contourPoints));
        }

        return blobs;
    }

    private static IReadOnlyList<BlobAnalysisBlob> SelectBlobs(
        IReadOnlyList<BlobAnalysisBlob> candidates,
        RoiDefinition? roi,
        ImageFrame frame,
        IReadOnlyDictionary<string, string> parameters)
    {
        var maxResults = Math.Clamp((int)Math.Round(parameters.GetDouble("maxResults", 128)), 1, 10_000);
        var center = GetReferenceCenter(roi, frame);
        var sorted = NormalizeSelection(parameters.GetValueOrDefault("selection")) switch
        {
            "Smallest" => candidates.OrderBy(blob => blob.Area),
            "ClosestCenter" => candidates.OrderBy(blob => DistanceSquared(blob.Center, center)),
            "TopLeft" => candidates.OrderBy(blob => blob.Top).ThenBy(blob => blob.Left),
            _ => candidates.OrderByDescending(blob => blob.Area)
        };

        return sorted.Take(maxResults).ToArray();
    }

    private static Dictionary<string, string> CreateResultData(
        ImageFrame frame,
        RoiDefinition? roi,
        Rect cropBounds,
        Mat binary,
        IReadOnlyList<BlobAnalysisBlob> blobs,
        int minCount,
        int maxCount,
        IReadOnlyDictionary<string, string> parameters)
    {
        var best = blobs.FirstOrDefault();
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["count"] = blobs.Count.ToString(),
            ["minCount"] = minCount.ToString(),
            ["maxCount"] = maxCount.ToString(),
            ["threshold"] = NormalizeThreshold(parameters.GetDouble("threshold", 128)).ToInvariant(),
            ["grayMin"] = NormalizeThreshold(parameters.GetDouble("grayMin", parameters.GetDouble("grayLower", 0))).ToInvariant(),
            ["grayMax"] = NormalizeThreshold(parameters.GetDouble("grayMax", parameters.GetDouble("grayUpper", 255))).ToInvariant(),
            ["thresholdMode"] = NormalizeThresholdMode(parameters.GetValueOrDefault("thresholdMode")),
            ["polarity"] = IsDarkBlob(parameters.GetValueOrDefault("polarity")) ? "Dark" : "Bright",
            ["adaptiveBlockSize"] = NormalizeAdaptiveBlockSize(parameters.GetDouble("adaptiveBlockSize", 41)).ToString(),
            ["adaptiveC"] = parameters.GetDouble("adaptiveC", 5).ToInvariant(),
            ["inputFrameId"] = frame.Id,
            ["cropX"] = cropBounds.X.ToString(),
            ["cropY"] = cropBounds.Y.ToString(),
            ["cropWidth"] = cropBounds.Width.ToString(),
            ["cropHeight"] = cropBounds.Height.ToString(),
            ["foregroundPixels"] = Cv2.CountNonZero(binary).ToString(),
            ["blobs"] = BlobAnalysisResultCodec.FormatBlobs(blobs)
        };

        if (best is not null)
        {
            data["x"] = best.X.ToInvariant();
            data["y"] = best.Y.ToInvariant();
            data["area"] = best.Area.ToInvariant();
            data["width"] = best.Width.ToInvariant();
            data["height"] = best.Height.ToInvariant();
            data["left"] = best.Left.ToInvariant();
            data["top"] = best.Top.ToInvariant();
            data["right"] = best.Right.ToInvariant();
            data["bottom"] = best.Bottom.ToInvariant();
            data["circularity"] = best.Circularity.ToInvariant();
            data["aspectRatio"] = best.AspectRatio.ToInvariant();
            data["perimeter"] = best.Perimeter.ToInvariant();
            data["circleX"] = best.CircleX.ToInvariant();
            data["circleY"] = best.CircleY.ToInvariant();
            data["circleRadius"] = best.CircleRadius.ToInvariant();
        }

        if (roi is not null)
        {
            GeometryToolSupport.AddSearchRoiData(data, roi);
        }

        AddCriteriaData(data, parameters, minCount, maxCount);

        return data;
    }

    private static void AddCriteriaData(
        Dictionary<string, string> data,
        IReadOnlyDictionary<string, string> parameters,
        int minCount,
        int maxCount)
    {
        data["criteriaGray"] = $"{data["grayMin"]}-{data["grayMax"]}";
        data["criteriaArea"] = $"{Math.Max(0, parameters.GetDouble("minArea", 30)).ToInvariant()}-{Math.Max(0, parameters.GetDouble("maxArea", 1_000_000)).ToInvariant()}";
        data["criteriaWidth"] = $"{Math.Max(0, parameters.GetDouble("minWidth", 0)).ToInvariant()}-{Math.Max(0, parameters.GetDouble("maxWidth", 1_000_000)).ToInvariant()}";
        data["criteriaHeight"] = $"{Math.Max(0, parameters.GetDouble("minHeight", 0)).ToInvariant()}-{Math.Max(0, parameters.GetDouble("maxHeight", 1_000_000)).ToInvariant()}";
        data["criteriaCircularity"] = $"{Math.Clamp(parameters.GetDouble("minCircularity", 0), 0, 1).ToInvariant()}-{Math.Clamp(parameters.GetDouble("maxCircularity", 1), 0, 1).ToInvariant()}";
        data["criteriaAspectRatio"] = $"{Math.Max(0, parameters.GetDouble("minAspectRatio", 0)).ToInvariant()}-{Math.Max(0, parameters.GetDouble("maxAspectRatio", 1_000_000)).ToInvariant()}";
        data["criteriaCount"] = $"{minCount}-{maxCount}";
        data["criteriaMode"] = NormalizeThresholdMode(parameters.GetValueOrDefault("thresholdMode"));
        data["criteriaSelection"] = NormalizeSelection(parameters.GetValueOrDefault("selection"));
        data["criteriaAdaptive"] = $"{data["adaptiveBlockSize"]}/{data["adaptiveC"]}";
    }

    private static IReadOnlyList<Point2D> CreateContourPoints(Point[] contour, Rect cropBounds)
    {
        if (contour.Length == 0)
        {
            return Array.Empty<Point2D>();
        }

        using var source = InputArray.Create(contour);
        using var approximated = new Mat();
        var perimeter = Cv2.ArcLength(source, true);
        Cv2.ApproxPolyDP(source, approximated, Math.Max(0.75, perimeter * 0.002), true);
        _ = approximated.GetArray(out Point[] points);
        if (points.Length == 0)
        {
            points = contour;
        }

        const int maxContourPoints = 240;
        if (points.Length > maxContourPoints)
        {
            var step = Math.Max(1, (int)Math.Ceiling(points.Length / (double)maxContourPoints));
            points = points
                .Where((_, index) => index % step == 0)
                .Take(maxContourPoints)
                .ToArray();
        }

        return points
            .Select(point => new Point2D(point.X + cropBounds.X, point.Y + cropBounds.Y))
            .ToArray();
    }

    private static Mat CreateMask(Size size, RoiDefinition? roi, Rect cropBounds)
    {
        var mask = new Mat(size, MatType.CV_8UC1, Scalar.White);
        if (roi is null)
        {
            return mask;
        }

        mask.SetTo(Scalar.Black);
        switch (roi.Shape)
        {
            case RoiShapeKind.Circle:
                Cv2.Circle(
                    mask,
                    new Point((int)Math.Round(roi.X - cropBounds.X), (int)Math.Round(roi.Y - cropBounds.Y)),
                    Math.Max(1, (int)Math.Round(roi.Radius)),
                    Scalar.White,
                    -1);
                break;
            case RoiShapeKind.RotatedRectangle:
                var rotated = new RotatedRect(
                    new Point2f((float)(roi.X - cropBounds.X), (float)(roi.Y - cropBounds.Y)),
                    new Size2f((float)Math.Max(1, roi.Width), (float)Math.Max(1, roi.Height)),
                    (float)roi.Angle);
                Cv2.FillConvexPoly(mask, rotated.Points().Select(ToPoint).ToArray(), Scalar.White);
                break;
            case RoiShapeKind.Polygon when roi.Points.Count >= 3:
                Cv2.FillPoly(mask, [roi.Points.Select(point => new Point((int)Math.Round(point.X - cropBounds.X), (int)Math.Round(point.Y - cropBounds.Y))).ToArray()], Scalar.White);
                break;
            default:
                var x = Math.Clamp((int)Math.Round(roi.X - cropBounds.X), 0, Math.Max(0, size.Width - 1));
                var y = Math.Clamp((int)Math.Round(roi.Y - cropBounds.Y), 0, Math.Max(0, size.Height - 1));
                var right = Math.Clamp((int)Math.Round(roi.X + roi.Width - cropBounds.X), x + 1, size.Width);
                var bottom = Math.Clamp((int)Math.Round(roi.Y + roi.Height - cropBounds.Y), y + 1, size.Height);
                Cv2.Rectangle(mask, new Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y)), Scalar.White, -1);
                break;
        }

        return mask;
    }

    private static Point ToPoint(Point2f point)
    {
        return new Point((int)Math.Round(point.X), (int)Math.Round(point.Y));
    }

    private static string NormalizeThresholdMode(string? value)
    {
        return value?.Trim() switch
        {
            "Range" or "range" or "GrayRange" or "gray_range" or "灰度范围" or "灰度上下限" => "Range",
            "Otsu" or "otsu" or "大津" or "自动阈值" => "Otsu",
            "Adaptive" or "adaptive" or "自适应" => "Adaptive",
            _ => "Fixed"
        };
    }

    private static string NormalizeSelection(string? value)
    {
        return value?.Trim() switch
        {
            "Smallest" or "smallest" or "最小" or "最小面积" => "Smallest",
            "ClosestCenter" or "closest_center" or "靠近中心" or "最近中心" => "ClosestCenter",
            "TopLeft" or "top_left" or "从左上" => "TopLeft",
            _ => "Largest"
        };
    }

    private static bool IsDarkBlob(string? value)
    {
        return value?.Trim() switch
        {
            "Bright" or "bright" or "亮斑" or "亮目标" => false,
            _ => true
        };
    }

    private static double NormalizeThreshold(double threshold)
    {
        return Math.Clamp(threshold <= 1 ? threshold * 255.0 : threshold, 0, 255);
    }

    private static int NormalizeAdaptiveBlockSize(double value)
    {
        var blockSize = Math.Clamp((int)Math.Round(value), 3, 501);
        return blockSize % 2 == 0 ? Math.Min(501, blockSize + 1) : blockSize;
    }

    private static Point2D GetReferenceCenter(RoiDefinition? roi, ImageFrame frame)
    {
        if (roi is null)
        {
            return new Point2D(frame.Width / 2.0, frame.Height / 2.0);
        }

        return roi.Shape switch
        {
            RoiShapeKind.Rectangle => new Point2D(roi.X + roi.Width / 2.0, roi.Y + roi.Height / 2.0),
            RoiShapeKind.Polygon when roi.Points.Count > 0 => new Point2D(roi.Points.Average(point => point.X), roi.Points.Average(point => point.Y)),
            _ => new Point2D(roi.X, roi.Y)
        };
    }

    private static double DistanceSquared(Point2D first, Point2D second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return dx * dx + dy * dy;
    }
}
