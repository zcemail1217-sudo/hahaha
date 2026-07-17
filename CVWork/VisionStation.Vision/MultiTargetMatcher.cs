using System.Globalization;
using OpenCvSharp;
using VisionStation.Domain;
using VisionStation.Vision.Tools;

namespace VisionStation.Vision;

public sealed record MultiTargetMatchCandidate(
    double X,
    double Y,
    double Angle,
    double Score,
    int Width,
    int Height,
    string Shape = "Rectangle",
    double Radius = 0)
{
    public double Scale { get; init; } = 1.0;

    public Pose2D Pose => new(X, Y, Angle) { Scale = Scale };
}

public sealed record MultiTargetMatchResult(
    InspectionOutcome Outcome,
    string Message,
    IReadOnlyList<MultiTargetMatchCandidate> Matches,
    TemplateSearchRegion SearchRegion,
    bool UsedAutoTemplate);

public static class MultiTargetMatcher
{
    private const int MinimumTemplateSize = 12;

    public static MultiTargetMatchResult Match(
        ImageFrame frame,
        RoiDefinition? searchRoi,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var gray = GeometryToolSupport.ToGrayMat(frame);
        return Match(frame, searchRoi, parameters, gray, cancellationToken);
    }

    internal static MultiTargetMatchResult Match(
        ImageFrame frame,
        RoiDefinition? searchRoi,
        IReadOnlyDictionary<string, string> parameters,
        Mat gray,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryReadTemplate(parameters, out var template))
        {
            return CreateFailedResult(frame, "Multi-target template has not been learned.", false);
        }

        using (template)
        {
            var searchRegion = TemplateMatcher.GetSearchRegion(frame, searchRoi);
            if (searchRegion.Width < template.Width || searchRegion.Height < template.Height)
            {
                return CreateFailedResult(frame, "Search region is smaller than the learned template.", false, searchRegion);
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var searchView = new Mat(gray, ToCvRect(searchRegion));
            using var search = searchView.Clone();
            var matchMode = ResolveEffectiveMode(
                NormalizeMode(GetString(parameters, "multiMatchMode", GetString(parameters, "matchMode", "Shape"))),
                template,
                parameters,
                cancellationToken);
            if (matchMode == "CircularBlob")
            {
                return MatchCircularBlobs(frame, searchRegion, search, template, parameters, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var searchSource = CreateMatchSource(search, matchMode, parameters);
            var minScore = GetDouble(parameters, "minScore", 0.75);
            var maxMatches = Math.Clamp(GetInt(parameters, "matchCount", GetInt(parameters, "maxMatches", 32)), 1, 256);
            var minCount = Math.Clamp(GetInt(parameters, "minCount", 1), 1, 256);
            var overlapThreshold = Math.Clamp(GetDouble(parameters, "nmsOverlap", 0.35), 0.05, 0.95);
            var minDistance = Math.Max(0, GetDouble(parameters, "minDistance", Math.Min(template.Width, template.Height) * 0.35));

            var rawCandidates = new List<MatchCandidate>();
            foreach (var angle in EnumerateAngles(parameters))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var rotatedTemplate = CreateRotatedTemplate(template, angle, matchMode, parameters);
                if (rotatedTemplate.Width < MinimumTemplateSize ||
                    rotatedTemplate.Height < MinimumTemplateSize ||
                    rotatedTemplate.Width > searchSource.Width ||
                    rotatedTemplate.Height > searchSource.Height)
                {
                    continue;
                }

                if (matchMode == "Shape" && Cv2.CountNonZero(rotatedTemplate) < Math.Max(8, rotatedTemplate.Width * rotatedTemplate.Height * 0.004))
                {
                    continue;
                }

                using var result = new Mat();
                Cv2.MatchTemplate(searchSource, rotatedTemplate, result, TemplateMatchModes.CCoeffNormed);
                ExtractCandidates(
                    result,
                    rawCandidates,
                    minScore,
                    angle,
                    rotatedTemplate.Width,
                    rotatedTemplate.Height,
                    searchRegion,
                    Math.Min(maxMatches * 4, 160));
            }

            var matches = ApplyNms(rawCandidates, maxMatches, overlapThreshold, minDistance, cancellationToken)
                .Select(candidate => new MultiTargetMatchCandidate(
                    candidate.X,
                    candidate.Y,
                    ToImagePoseAngle(candidate.Angle),
                    candidate.Score,
                    candidate.Width,
                    candidate.Height,
                    candidate.Shape,
                    candidate.Radius))
                .ToArray();

            var outcome = matches.Length >= minCount ? InspectionOutcome.Ok : InspectionOutcome.Ng;
            var message = outcome == InspectionOutcome.Ok
                ? $"Multi-target {matchMode} OK, found {matches.Length}."
                : $"Multi-target {matchMode} NG, found {matches.Length}, required {minCount}.";

            return new MultiTargetMatchResult(outcome, message, matches, searchRegion, false);
        }
    }

    private static void ExtractCandidates(
        Mat scoreMap,
        List<MatchCandidate> candidates,
        double minScore,
        double angle,
        int width,
        int height,
        TemplateSearchRegion searchRegion,
        int maxCandidates)
    {
        using var working = scoreMap.Clone();
        for (var index = 0; index < maxCandidates; index++)
        {
            Cv2.MinMaxLoc(working, out _, out var maxValue, out _, out var maxLocation);
            if (!double.IsFinite(maxValue) || maxValue < minScore)
            {
                break;
            }

            candidates.Add(new MatchCandidate(
                searchRegion.X + maxLocation.X + width / 2.0,
                searchRegion.Y + maxLocation.Y + height / 2.0,
                angle,
                maxValue,
                width,
                height,
                searchRegion.X + maxLocation.X,
                searchRegion.Y + maxLocation.Y));

            var left = Math.Max(0, maxLocation.X - width / 2);
            var top = Math.Max(0, maxLocation.Y - height / 2);
            var right = Math.Min(working.Width, maxLocation.X + width + width / 2);
            var bottom = Math.Min(working.Height, maxLocation.Y + height + height / 2);
            if (right <= left || bottom <= top)
            {
                break;
            }

            Cv2.Rectangle(working, new Rect(left, top, right - left, bottom - top), new Scalar(-1), -1);
        }
    }

    private static IReadOnlyList<MatchCandidate> ApplyNms(
        IEnumerable<MatchCandidate> candidates,
        int maxMatches,
        double overlapThreshold,
        double minDistance,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selected = new List<MatchCandidate>();
        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Score))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selected.Any(existing =>
                    IntersectionOverUnion(existing, candidate) > overlapThreshold ||
                    Distance(existing, candidate) < minDistance))
            {
                continue;
            }

            selected.Add(candidate);
            if (selected.Count >= maxMatches)
            {
                break;
            }
        }

        return selected;
    }

    private static MultiTargetMatchResult MatchCircularBlobs(
        ImageFrame frame,
        TemplateSearchRegion searchRegion,
        Mat search,
        Mat template,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = EstimateCircularTemplate(template, cancellationToken);
        var minScore = GetCircularMinScore(parameters);
        var maxMatches = Math.Clamp(GetInt(parameters, "matchCount", GetInt(parameters, "maxMatches", 64)), 1, 512);
        var minCount = Math.Clamp(GetInt(parameters, "minCount", 1), 1, 512);
        var radiusTolerance = Math.Clamp(GetDouble(parameters, "circleRadiusTolerance", 0.45), 0.05, 2.0);
        var minCircularity = Math.Clamp(GetDouble(parameters, "minCircularity", 0.5), 0.1, 1.0);
        var minDistance = Math.Max(0, GetDouble(parameters, "minDistance", Math.Max(3, model.Radius * 1.35)));
        var minRadius = Math.Max(1.5, model.Radius * (1.0 - radiusTolerance));
        var maxRadius = Math.Max(minRadius + 1, model.Radius * (1.0 + radiusTolerance));

        using var binary = SegmentCircularTargets(search, model.IsDarkTarget, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var contourSource = binary.Clone();
        cancellationToken.ThrowIfCancellationRequested();
        Cv2.FindContours(
            contourSource,
            out OpenCvSharp.Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        var candidates = new List<MatchCandidate>();
        foreach (var contour in contours)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (contour.Length < 5)
            {
                continue;
            }

            var area = Cv2.ContourArea(contour);
            if (area < Math.PI * minRadius * minRadius * 0.35)
            {
                continue;
            }

            var perimeter = Cv2.ArcLength(contour, true);
            if (perimeter <= 0)
            {
                continue;
            }

            var circularity = Math.Clamp(4.0 * Math.PI * area / (perimeter * perimeter), 0, 1);
            if (circularity < minCircularity)
            {
                continue;
            }

            Cv2.MinEnclosingCircle(contour, out var center, out var radius);
            if (radius < minRadius || radius > maxRadius)
            {
                continue;
            }

            var moments = Cv2.Moments(contour);
            var centerX = Math.Abs(moments.M00) > double.Epsilon ? moments.M10 / moments.M00 : center.X;
            var centerY = Math.Abs(moments.M00) > double.Epsilon ? moments.M01 / moments.M00 : center.Y;
            var contrastScore = EstimateCircularContrastScore(search, centerX, centerY, radius, model.IsDarkTarget);
            if (contrastScore < 0.18)
            {
                continue;
            }

            var radiusScore = Math.Clamp(1.0 - Math.Abs(radius - model.Radius) / Math.Max(model.Radius, 1), 0, 1);
            var fillRatio = Math.Clamp(area / Math.Max(Math.PI * radius * radius, 1), 0, 1);
            var score = Math.Clamp(circularity * 0.35 + radiusScore * 0.30 + fillRatio * 0.10 + contrastScore * 0.25, 0, 1);
            if (score < minScore)
            {
                continue;
            }

            var diameter = Math.Max(2, (int)Math.Round(radius * 2));
            candidates.Add(new MatchCandidate(
                searchRegion.X + centerX,
                searchRegion.Y + centerY,
                0,
                score,
                diameter,
                diameter,
                searchRegion.X + centerX - radius,
                searchRegion.Y + centerY - radius,
                "Circle",
                radius));
        }

        AddHoughCircleCandidates(
            candidates,
            searchRegion,
            search,
            model,
            parameters,
            minRadius,
            maxRadius,
            minDistance,
            minScore,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var matches = ApplyNms(candidates, maxMatches, 0.35, minDistance, cancellationToken)
            .Select(candidate => new MultiTargetMatchCandidate(
                candidate.X,
                candidate.Y,
                0,
                candidate.Score,
                candidate.Width,
                candidate.Height,
                candidate.Shape,
                candidate.Radius))
            .ToArray();

        var outcome = matches.Length >= minCount ? InspectionOutcome.Ok : InspectionOutcome.Ng;
        var message = outcome == InspectionOutcome.Ok
            ? $"圆形多目标 OK，找到 {matches.Length} 个。"
            : $"圆形多目标 NG，找到 {matches.Length} 个，要求 {minCount} 个。";

        return new MultiTargetMatchResult(outcome, message, matches, searchRegion, false);
    }

    private static double GetCircularMinScore(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("circleMinScore", out var raw) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var circleScore))
        {
            return Math.Clamp(circleScore, 0.05, 1);
        }

        var configured = GetDouble(parameters, "minScore", 0.65);
        return configured >= 0.8 ? 0.62 : Math.Clamp(configured, 0.05, 1);
    }

    private static void AddHoughCircleCandidates(
        List<MatchCandidate> candidates,
        TemplateSearchRegion searchRegion,
        Mat search,
        CircularTemplateModel model,
        IReadOnlyDictionary<string, string> parameters,
        double minRadius,
        double maxRadius,
        double minDistance,
        double minScore,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var blurred = new Mat();
        Cv2.MedianBlur(search, blurred, 5);

        var houghParam1 = Math.Clamp(GetDouble(parameters, "houghCannyHigh", GetDouble(parameters, "cannyHigh", 140)), 20, 255);
        var houghParam2 = Math.Clamp(GetDouble(parameters, "houghAccumulator", Math.Max(8, model.Radius * 0.65)), 5, 80);
        cancellationToken.ThrowIfCancellationRequested();
        var circles = Cv2.HoughCircles(
            blurred,
            HoughModes.Gradient,
            1.2,
            Math.Max(2, minDistance),
            houghParam1,
            houghParam2,
            Math.Max(1, (int)Math.Floor(minRadius)),
            Math.Max(2, (int)Math.Ceiling(maxRadius)));

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var circle in circles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var radius = Math.Max(1, circle.Radius);
            var contrastScore = EstimateCircularContrastScore(
                search,
                circle.Center.X,
                circle.Center.Y,
                radius,
                model.IsDarkTarget);
            if (contrastScore < 0.18)
            {
                continue;
            }

            var radiusScore = Math.Clamp(1.0 - Math.Abs(radius - model.Radius) / Math.Max(model.Radius, 1), 0, 1);
            var score = Math.Clamp(0.45 + radiusScore * 0.25 + contrastScore * 0.30, 0, 1);
            if (score < minScore)
            {
                continue;
            }

            var diameter = Math.Max(2, (int)Math.Round(radius * 2));
            candidates.Add(new MatchCandidate(
                searchRegion.X + circle.Center.X,
                searchRegion.Y + circle.Center.Y,
                0,
                score,
                diameter,
                diameter,
                searchRegion.X + circle.Center.X - radius,
                searchRegion.Y + circle.Center.Y - radius,
                "Circle",
                radius));
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static double EstimateCircularContrastScore(Mat search, double centerX, double centerY, double radius, bool darkTarget)
    {
        var sampleRadius = Math.Max(2, radius);
        var margin = Math.Max(3, sampleRadius * 1.7);
        var left = Math.Clamp((int)Math.Floor(centerX - margin), 0, Math.Max(0, search.Width - 1));
        var top = Math.Clamp((int)Math.Floor(centerY - margin), 0, Math.Max(0, search.Height - 1));
        var right = Math.Clamp((int)Math.Ceiling(centerX + margin), left + 1, search.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(centerY + margin), top + 1, search.Height);
        var bounds = new Rect(left, top, right - left, bottom - top);
        if (bounds.Width < 3 || bounds.Height < 3)
        {
            return 0;
        }

        using var crop = new Mat(search, bounds);
        using var innerMask = new Mat(bounds.Size, MatType.CV_8UC1, Scalar.Black);
        using var ringMask = new Mat(bounds.Size, MatType.CV_8UC1, Scalar.Black);
        var relativeCenter = new OpenCvSharp.Point(
            (int)Math.Round(centerX - bounds.X),
            (int)Math.Round(centerY - bounds.Y));

        Cv2.Circle(innerMask, relativeCenter, Math.Max(1, (int)Math.Round(sampleRadius * 0.72)), Scalar.White, -1);
        Cv2.Circle(ringMask, relativeCenter, Math.Max(2, (int)Math.Round(sampleRadius * 1.55)), Scalar.White, -1);
        Cv2.Circle(ringMask, relativeCenter, Math.Max(1, (int)Math.Round(sampleRadius * 1.05)), Scalar.Black, -1);

        var innerMean = Cv2.Mean(crop, innerMask).Val0;
        var ringMean = Cv2.Mean(crop, ringMask).Val0;
        var contrast = darkTarget ? ringMean - innerMean : innerMean - ringMean;
        if (contrast <= 0)
        {
            return 0;
        }

        var targetMean = darkTarget ? innerMean : 255 - innerMean;
        var targetToneScore = Math.Clamp((150 - targetMean) / 110.0, 0, 1);
        var contrastScore = Math.Clamp(contrast / 70.0, 0, 1);
        return Math.Clamp(contrastScore * 0.7 + targetToneScore * 0.3, 0, 1);
    }

    private static CircularTemplateModel EstimateCircularTemplate(Mat template, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var blurred = new Mat();
        Cv2.GaussianBlur(template, blurred, new Size(3, 3), 0);

        var dark = EvaluateCircularTemplatePolarity(blurred, true, cancellationToken);
        var bright = EvaluateCircularTemplatePolarity(blurred, false, cancellationToken);
        if (dark.Score >= bright.Score && dark.Radius > 0)
        {
            return dark;
        }

        if (bright.Radius > 0)
        {
            return bright;
        }

        return new CircularTemplateModel(
            Math.Max(2, Math.Min(template.Width, template.Height) * 0.35),
            Cv2.Mean(template).Val0 < 128,
            0);
    }

    private static CircularTemplateModel EvaluateCircularTemplatePolarity(
        Mat template,
        bool darkTarget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var binary = new Mat();
        Cv2.Threshold(
            template,
            binary,
            0,
            255,
            darkTarget ? ThresholdTypes.BinaryInv | ThresholdTypes.Otsu : ThresholdTypes.Binary | ThresholdTypes.Otsu);

        cancellationToken.ThrowIfCancellationRequested();
        using var contourSource = binary.Clone();
        cancellationToken.ThrowIfCancellationRequested();
        Cv2.FindContours(
            contourSource,
            out OpenCvSharp.Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        var templateCenter = new Point2d((template.Width - 1) / 2.0, (template.Height - 1) / 2.0);
        var best = new CircularTemplateModel(0, darkTarget, 0);
        foreach (var contour in contours)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (contour.Length < 5)
            {
                continue;
            }

            var area = Cv2.ContourArea(contour);
            if (area < 8 || area > template.Width * template.Height * 0.85)
            {
                continue;
            }

            var perimeter = Cv2.ArcLength(contour, true);
            if (perimeter <= 0)
            {
                continue;
            }

            Cv2.MinEnclosingCircle(contour, out var center, out var radius);
            if (radius <= 0)
            {
                continue;
            }

            var circularity = Math.Clamp(4.0 * Math.PI * area / (perimeter * perimeter), 0, 1);
            var dx = center.X - templateCenter.X;
            var dy = center.Y - templateCenter.Y;
            var centerDistance = Math.Sqrt(dx * dx + dy * dy);
            var centerScore = Math.Clamp(1.0 - centerDistance / Math.Max(template.Width, template.Height), 0, 1);
            var score = circularity * 0.75 + centerScore * 0.25;
            if (score > best.Score)
            {
                best = new CircularTemplateModel(radius, darkTarget, score);
            }
        }

        return best;
    }

    private static Mat SegmentCircularTargets(
        Mat search,
        bool darkTarget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var blurred = new Mat();
        Cv2.GaussianBlur(search, blurred, new Size(3, 3), 0);

        cancellationToken.ThrowIfCancellationRequested();
        var binary = new Mat();
        try
        {
            Cv2.Threshold(
                blurred,
                binary,
                0,
                255,
                darkTarget ? ThresholdTypes.BinaryInv | ThresholdTypes.Otsu : ThresholdTypes.Binary | ThresholdTypes.Otsu);

            cancellationToken.ThrowIfCancellationRequested();
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
            Cv2.MorphologyEx(binary, binary, MorphTypes.Open, kernel);
            cancellationToken.ThrowIfCancellationRequested();
            Cv2.MorphologyEx(binary, binary, MorphTypes.Close, kernel);
            return binary;
        }
        catch
        {
            binary.Dispose();
            throw;
        }
    }

    private static double IntersectionOverUnion(MatchCandidate a, MatchCandidate b)
    {
        var left = Math.Max(a.Left, b.Left);
        var top = Math.Max(a.Top, b.Top);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static double Distance(MatchCandidate a, MatchCandidate b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Mat CreateMatchSource(Mat source, string mode, IReadOnlyDictionary<string, string> parameters)
    {
        if (mode != "Shape")
        {
            return source.Clone();
        }

        return CreateEdges(source, parameters);
    }

    private static Mat CreateRotatedTemplate(
        Mat template,
        double angle,
        string mode,
        IReadOnlyDictionary<string, string> parameters)
    {
        using var rotated = Math.Abs(angle) < 0.001 ? template.Clone() : RotateKeepBounds(template, angle);
        return mode == "Shape" ? CreateEdges(rotated, parameters) : rotated.Clone();
    }

    private static Mat CreateEdges(Mat source, IReadOnlyDictionary<string, string> parameters)
    {
        var low = Math.Clamp(GetDouble(parameters, "cannyLow", 60), 1, 254);
        var high = Math.Clamp(GetDouble(parameters, "cannyHigh", 160), low + 1, 255);
        using var blurred = new Mat();
        Cv2.GaussianBlur(source, blurred, new Size(3, 3), 0);
        var edges = new Mat();
        Cv2.Canny(blurred, edges, low, high);
        return edges;
    }

    private static Mat RotateKeepBounds(Mat source, double angle)
    {
        var center = new Point2f(source.Width / 2f, source.Height / 2f);
        using var matrix = Cv2.GetRotationMatrix2D(center, angle, 1.0);
        var radians = angle * Math.PI / 180.0;
        var sin = Math.Abs(Math.Sin(radians));
        var cos = Math.Abs(Math.Cos(radians));
        var width = Math.Max(1, (int)Math.Ceiling(source.Width * cos + source.Height * sin));
        var height = Math.Max(1, (int)Math.Ceiling(source.Width * sin + source.Height * cos));

        matrix.Set(0, 2, matrix.At<double>(0, 2) + width / 2.0 - center.X);
        matrix.Set(1, 2, matrix.At<double>(1, 2) + height / 2.0 - center.Y);

        var rotated = new Mat();
        Cv2.WarpAffine(source, rotated, matrix, new Size(width, height), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.Black);
        return rotated;
    }

    private static IEnumerable<double> EnumerateAngles(IReadOnlyDictionary<string, string> parameters)
    {
        var start = GetDouble(parameters, "angleStart", -30);
        var extent = GetDouble(parameters, "angleExtent", 60);
        var step = Math.Max(0.5, Math.Abs(GetDouble(parameters, "angleStep", 5)));
        var direction = extent < 0 ? -1 : 1;
        var count = Math.Max(1, (int)Math.Floor(Math.Abs(extent) / step) + 1);
        for (var index = 0; index < count; index++)
        {
            yield return start + direction * index * step;
        }

        var end = start + extent;
        if (Math.Abs(end - (start + direction * (count - 1) * step)) > 0.001)
        {
            yield return end;
        }
    }

    private static bool TryReadTemplate(IReadOnlyDictionary<string, string> parameters, out Mat template)
    {
        template = null!;
        if (!TryGetInt(parameters, "templateWidth", out var width) ||
            !TryGetInt(parameters, "templateHeight", out var height) ||
            width < MinimumTemplateSize ||
            height < MinimumTemplateSize)
        {
            return false;
        }

        byte[]? raw = null;
        if (parameters.TryGetValue("templatePixels", out var pixels) && !string.IsNullOrWhiteSpace(pixels))
        {
            try
            {
                raw = Convert.FromBase64String(pixels);
            }
            catch (FormatException)
            {
                raw = null;
            }
        }

        if ((raw is null || raw.Length < width * height) &&
            parameters.TryGetValue("modelPath", out var modelPath) &&
            !string.IsNullOrWhiteSpace(modelPath) &&
            File.Exists(modelPath))
        {
            raw = File.ReadAllBytes(modelPath);
        }

        if (raw is null || raw.Length < width * height)
        {
            return false;
        }

        template = Mat.FromPixelData(height, width, MatType.CV_8UC1, raw).Clone();
        return !template.Empty();
    }

    private static MultiTargetMatchResult CreateFailedResult(
        ImageFrame frame,
        string message,
        bool usedAutoTemplate,
        TemplateSearchRegion? searchRegion = null)
    {
        return new MultiTargetMatchResult(
            InspectionOutcome.Ng,
            message,
            Array.Empty<MultiTargetMatchCandidate>(),
            searchRegion ?? new TemplateSearchRegion(0, 0, frame.Width, frame.Height),
            usedAutoTemplate);
    }

    private static Rect ToCvRect(TemplateSearchRegion region)
    {
        return new Rect(region.X, region.Y, Math.Max(1, region.Width), Math.Max(1, region.Height));
    }

    private static string NormalizeMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Shape";
        }

        return value.Trim() switch
        {
            "Circle" or "Circular" or "CircularBlob" or "BlobCircle" => "CircularBlob",
            "Gray" or "Ncc" or "NCC" or "GrayNcc" => "GrayNcc",
            _ => "Shape"
        };
    }

    private static string ResolveEffectiveMode(
        string requestedMode,
        Mat template,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        if (requestedMode == "CircularBlob" ||
            !GetBool(parameters, "autoCircularTarget", true))
        {
            return requestedMode;
        }

        if (requestedMode != "Shape")
        {
            return requestedMode;
        }

        var templateShape = GetString(parameters, "templateShape", string.Empty);
        if (templateShape.Contains("圆", StringComparison.OrdinalIgnoreCase) ||
            templateShape.Contains("Circle", StringComparison.OrdinalIgnoreCase))
        {
            return "CircularBlob";
        }

        var model = EstimateCircularTemplate(template, cancellationToken);
        return model.Score >= 0.72 ? "CircularBlob" : requestedMode;
    }

    private static double ToImagePoseAngle(double openCvRotationAngle)
    {
        return NormalizeAngle(-openCvRotationAngle);
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

    private static string GetString(IReadOnlyDictionary<string, string> parameters, string key, string fallback)
    {
        return parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> parameters, string key, int fallback)
    {
        return TryGetInt(parameters, key, out var value) ? value : fallback;
    }

    private static bool TryGetInt(IReadOnlyDictionary<string, string> parameters, string key, out int value)
    {
        value = 0;
        if (!parameters.TryGetValue(key, out var raw))
        {
            return false;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            value = (int)Math.Round(doubleValue);
            return true;
        }

        return false;
    }

    private static double GetDouble(IReadOnlyDictionary<string, string> parameters, string key, double fallback)
    {
        return parameters.TryGetValue(key, out var raw) &&
               double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> parameters, string key, bool fallback)
    {
        return parameters.TryGetValue(key, out var raw) && bool.TryParse(raw, out var value)
            ? value
            : fallback;
    }

    private sealed record MatchCandidate(
        double X,
        double Y,
        double Angle,
        double Score,
        int Width,
        int Height,
        double Left,
        double Top,
        string Shape = "Rectangle",
        double Radius = 0)
    {
        public double Right => Left + Width;

        public double Bottom => Top + Height;
    }

    private sealed record CircularTemplateModel(double Radius, bool IsDarkTarget, double Score);
}
