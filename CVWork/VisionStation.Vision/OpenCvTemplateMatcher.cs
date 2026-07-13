using System.Globalization;
using System.Text.Json;
using OpenCvSharp;
using VisionStation.Domain;

namespace VisionStation.Vision;

internal static class OpenCvTemplateMatcher
{
    private const string TemplateVersion = "opencv-1";
    private const int MinimumTemplateSize = 12;

    public static Dictionary<string, string> Learn(
        ImageFrame frame,
        RoiDefinition? searchRoi,
        IReadOnlyDictionary<string, string> parameters)
    {
        using var gray = ToGrayMat(frame);
        var searchRegion = TemplateMatcher.GetSearchRegion(frame, searchRoi);
        var templateRegion = GetTemplateRegion(frame, searchRegion, parameters);
        using var templateView = new Mat(gray, ToCvRect(templateRegion));
        using var template = templateView.Clone();

        template.GetArray(out byte[] rawPixels);
        var encoded = template.ToBytes(".png");
        using var templateMask = CreateTemplateMask(template.Size(), templateRegion, parameters);
        var edgeOverlay = CreateTemplateEdgeOverlay(template, parameters, templateMask);

        var learned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "OpenCv",
            ["matchMode"] = GetMatchMode(parameters),
            ["templateVersion"] = TemplateVersion,
            ["templateX"] = templateRegion.X.ToString(CultureInfo.InvariantCulture),
            ["templateY"] = templateRegion.Y.ToString(CultureInfo.InvariantCulture),
            ["templateWidth"] = templateRegion.Width.ToString(CultureInfo.InvariantCulture),
            ["templateHeight"] = templateRegion.Height.ToString(CultureInfo.InvariantCulture),
            ["templateFrameWidth"] = frame.Width.ToString(CultureInfo.InvariantCulture),
            ["templateFrameHeight"] = frame.Height.ToString(CultureInfo.InvariantCulture),
            ["templatePixels"] = Convert.ToBase64String(rawPixels),
            ["templateImagePng"] = Convert.ToBase64String(encoded),
            ["templateEdgeOverlayPng"] = Convert.ToBase64String(edgeOverlay),
            ["templateMaskPng"] = templateMask is null ? string.Empty : Convert.ToBase64String(templateMask.ToBytes(".png")),
            ["templateSourceRoiId"] = searchRoi?.Id ?? string.Empty
        };

        if (GetMatchMode(parameters).Equals("Shape", StringComparison.OrdinalIgnoreCase))
        {
            learned["shapeScoreVersion"] = "2";
            learned["shapeCoverageDistance"] = GetNormalizedShapeCoverageDistance(parameters)
                .ToString("0.###", CultureInfo.InvariantCulture);
        }

        return learned;
    }

    public static TemplateMatchResult Match(
        ImageFrame frame,
        RoiDefinition? searchRoi,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        using var gray = ImageFrameMatFactory.ToGrayMat(frame);
        return Match(frame, searchRoi, parameters, gray, cancellationToken);
    }

    internal static TemplateMatchResult Match(
        ImageFrame frame,
        RoiDefinition? searchRoi,
        IReadOnlyDictionary<string, string> parameters,
        Mat gray,
        CancellationToken cancellationToken = default)
    {
        var usedAutoTemplate = false;
        var runtimeParameters = parameters;
        if (!TryReadTemplate(parameters, out var template))
        {
            if (!GetBool(parameters, "autoLearnTemplate", false))
            {
                return CreateFailedResult(frame, "OpenCV template has not been learned.", false);
            }

            var learned = Learn(frame, searchRoi, parameters);
            var merged = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in learned)
            {
                merged[parameter.Key] = parameter.Value;
            }

            runtimeParameters = merged;
            usedAutoTemplate = true;
            if (!TryReadTemplate(runtimeParameters, out template))
            {
                return CreateFailedResult(frame, "OpenCV template model is not available.", usedAutoTemplate);
            }
        }

        using (template)
        {
            var searchRegion = TemplateMatcher.GetSearchRegion(frame, searchRoi);
            using var searchView = new Mat(gray, ToCvRect(searchRegion));
            using var search = searchView.Clone();
            var requestedMode = GetMatchMode(runtimeParameters);
            if (requestedMode.Equals("Shape", StringComparison.OrdinalIgnoreCase) &&
                !TryGetShapeScoreVersion(runtimeParameters, out _))
            {
                return CreateFailedResult(
                    frame,
                    "Unsupported OpenCV Shape score version.",
                    usedAutoTemplate,
                    searchRegion,
                    template.Width,
                    template.Height);
            }

            var mode = ResolveEffectiveMode(search, template, runtimeParameters);
            if (!mode.Equals("Shape", StringComparison.OrdinalIgnoreCase) &&
                (searchRegion.Width < template.Width || searchRegion.Height < template.Height))
            {
                return CreateFailedResult(
                    frame,
                    "Search region is smaller than the learned template.",
                    usedAutoTemplate,
                    searchRegion,
                    template.Width,
                    template.Height);
            }

            var candidate = FindBest(search, template, mode, runtimeParameters, cancellationToken);

            if (!candidate.HasMatch)
            {
                return CreateFailedResult(
                    frame,
                    "OpenCV template matching failed.",
                    usedAutoTemplate,
                    searchRegion,
                    template.Width,
                    template.Height);
            }

            var minScore = GetDouble(runtimeParameters, "minScore", 0.85);
            var pose = new Pose2D(
                searchRegion.X + candidate.X + candidate.Width / 2.0,
                searchRegion.Y + candidate.Y + candidate.Height / 2.0,
                ToImagePoseAngle(candidate.Angle));
            var outcome = candidate.Score >= minScore ? InspectionOutcome.Ok : InspectionOutcome.Ng;
            var shapeContours = mode.Equals("Shape", StringComparison.OrdinalIgnoreCase)
                ? CreateMatchedShapeContours(template, candidate, runtimeParameters, searchRegion)
                : Array.Empty<IReadOnlyList<Point2D>>();
            var matchedRoiContours = CreateMatchedTemplateRoiContours(candidate, runtimeParameters, searchRegion);
            var modeName = GetMatchModeDisplayName(mode);
            var message = outcome == InspectionOutcome.Ok
                ? $"OpenCV {modeName} match OK, score {candidate.Score.ToString("0.000", CultureInfo.InvariantCulture)}"
                : $"OpenCV {modeName} match NG, score {candidate.Score.ToString("0.000", CultureInfo.InvariantCulture)}";

            if (usedAutoTemplate)
            {
                message += " (auto template)";
            }

            return new TemplateMatchResult(
                true,
                outcome,
                candidate.Score,
                pose,
                searchRegion.X + candidate.X,
                searchRegion.Y + candidate.Y,
                candidate.Width,
                candidate.Height,
                searchRegion,
                message,
                usedAutoTemplate,
                ShapeContours: shapeContours)
            {
                MatchedTemplateRoiContours = matchedRoiContours,
                ShapeCoverage = candidate.ShapeCoverage,
                ShapeReverseScore = candidate.ShapeReverseScore
            };
        }
    }

    private static IReadOnlyList<IReadOnlyList<Point2D>> CreateMatchedTemplateRoiContours(
        MatchCandidate candidate,
        IReadOnlyDictionary<string, string> parameters,
        TemplateSearchRegion searchRegion)
    {
        if (!TryReadTemplateRoi(parameters, out var roi) ||
            !TryGetLearnedTemplateRegion(parameters, out var templateX, out var templateY, out var templateWidth, out var templateHeight))
        {
            return Array.Empty<IReadOnlyList<Point2D>>();
        }

        var contour = GetTemplateRoiContour(roi);
        if (contour.Count < 2)
        {
            return Array.Empty<IReadOnlyList<Point2D>>();
        }

        var templateCenter = new Point2D(templateX + templateWidth / 2.0, templateY + templateHeight / 2.0);
        var matchedCenter = new Point2D(
            searchRegion.X + candidate.X + candidate.Width / 2.0,
            searchRegion.Y + candidate.Y + candidate.Height / 2.0);
        var radians = ToImagePoseAngle(candidate.Angle) * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        return
        [
            contour.Select(point =>
            {
                var dx = point.X - templateCenter.X;
                var dy = point.Y - templateCenter.Y;
                return new Point2D(
                    matchedCenter.X + dx * cos - dy * sin,
                    matchedCenter.Y + dx * sin + dy * cos);
            }).ToArray()
        ];
    }

    private static IReadOnlyList<Point2D> GetTemplateRoiContour(RoiDefinition roi)
    {
        return roi.Shape switch
        {
            RoiShapeKind.Rectangle =>
            [
                new Point2D(roi.X, roi.Y),
                new Point2D(roi.X + roi.Width, roi.Y),
                new Point2D(roi.X + roi.Width, roi.Y + roi.Height),
                new Point2D(roi.X, roi.Y + roi.Height)
            ],
            RoiShapeKind.Polygon when roi.Points.Count >= 3 => roi.Points,
            RoiShapeKind.Circle => Enumerable.Range(0, 64)
                .Select(index =>
                {
                    var angle = index * Math.PI * 2.0 / 64.0;
                    return new Point2D(roi.X + roi.Radius * Math.Cos(angle), roi.Y + roi.Radius * Math.Sin(angle));
                })
                .ToArray(),
            RoiShapeKind.RotatedRectangle => GetRotatedRectangleCorners(roi),
            _ => Array.Empty<Point2D>()
        };
    }

    private static IReadOnlyList<Point2D> GetRotatedRectangleCorners(RoiDefinition roi)
    {
        var center = new Point2D(roi.X, roi.Y);
        var radians = roi.Angle * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var halfWidth = roi.Width / 2.0;
        var halfHeight = roi.Height / 2.0;
        return
        [
            Rotate(-halfWidth, -halfHeight),
            Rotate(halfWidth, -halfHeight),
            Rotate(halfWidth, halfHeight),
            Rotate(-halfWidth, halfHeight)
        ];

        Point2D Rotate(double x, double y)
        {
            return new Point2D(center.X + x * cos - y * sin, center.Y + x * sin + y * cos);
        }
    }

    private static IReadOnlyList<IReadOnlyList<Point2D>> CreateMatchedShapeContours(
        Mat template,
        MatchCandidate candidate,
        IReadOnlyDictionary<string, string> parameters,
        TemplateSearchRegion searchRegion)
    {
        using var edges = CreateShapeTemplateEdges(template, candidate.Angle, parameters);
        var maxPoints = Math.Clamp(GetInt(parameters, "shapeOverlayMaxPoints", 1400), 150, 6000);
        using var contourSource = edges.Clone();
        Cv2.FindContours(
            contourSource,
            out OpenCvSharp.Point[][] contours,
            out _,
            RetrievalModes.List,
            ContourApproximationModes.ApproxSimple);

        var result = new List<IReadOnlyList<Point2D>>();
        var usedPoints = 0;
        foreach (var contour in contours
                     .Where(contour => contour.Length >= 2)
                     .OrderByDescending(contour => Cv2.ArcLength(contour, true)))
        {
            if (usedPoints >= maxPoints)
            {
                break;
            }

            var remaining = maxPoints - usedPoints;
            var stride = Math.Max(1, (int)Math.Ceiling(contour.Length / (double)Math.Max(2, remaining)));
            var points = contour
                .Where((_, index) => index % stride == 0)
                .Select(point => new Point2D(searchRegion.X + candidate.X + point.X, searchRegion.Y + candidate.Y + point.Y))
                .Take(remaining)
                .ToArray();

            if (points.Length < 2)
            {
                continue;
            }

            result.Add(points);
            usedPoints += points.Length;
        }

        return result;
    }

    private static MatchCandidate FindBest(
        Mat search,
        Mat template,
        string mode,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        return mode switch
        {
            "Shape" => FindBestShape(search, template, parameters, cancellationToken),
            "FeatureOrb" => FindBestFeatureOrb(search, template, parameters, cancellationToken),
            _ => FindBestGray(search, template, mode, parameters, cancellationToken)
        };
    }

    private static MatchCandidate FindBestGray(
        Mat search,
        Mat template,
        string mode,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var best = MatchCandidate.None;
        var matchMode = GetOpenCvGrayMatchMode(mode);
        var lowerIsBetter = matchMode is TemplateMatchModes.SqDiff or TemplateMatchModes.SqDiffNormed;
        using var templateMask = TryReadTemplateMask(parameters, template.Size());
        var hasTemplateMask = templateMask is not null && Cv2.CountNonZero(templateMask) >= 8;

        foreach (var angle in EnumerateAngles(parameters, false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var rotatedTemplate = Math.Abs(angle) < 0.001 ? template.Clone() : Rotate(template, angle);
            if (rotatedTemplate.Width > search.Width || rotatedTemplate.Height > search.Height)
            {
                continue;
            }

            using var result = new Mat();
            if (hasTemplateMask)
            {
                using var rotatedMask = Math.Abs(angle) < 0.001 ? templateMask!.Clone() : RotateMask(templateMask!, angle);
                if (Cv2.CountNonZero(rotatedMask) < 8)
                {
                    continue;
                }

                Cv2.MatchTemplate(search, rotatedTemplate, result, matchMode, rotatedMask);
            }
            else
            {
                Cv2.MatchTemplate(search, rotatedTemplate, result, matchMode);
            }

            Cv2.MinMaxLoc(result, out var minValue, out var maxValue, out var minLocation, out var maxLocation);
            var rawScore = lowerIsBetter ? 1.0 - minValue : maxValue;
            if (double.IsNaN(rawScore) || double.IsInfinity(rawScore))
            {
                continue;
            }

            var score = Math.Clamp(rawScore, 0, 1);
            var location = lowerIsBetter ? minLocation : maxLocation;

            if (score > best.Score)
            {
                best = new MatchCandidate(
                    true,
                    location.X,
                    location.Y,
                    rotatedTemplate.Width,
                    rotatedTemplate.Height,
                    angle,
                    score);
            }
        }

        return best;
    }

    private static TemplateMatchModes GetOpenCvGrayMatchMode(string mode)
    {
        return mode switch
        {
            "GrayCcorr" => TemplateMatchModes.CCorrNormed,
            "GraySqDiff" => TemplateMatchModes.SqDiffNormed,
            _ => TemplateMatchModes.CCoeffNormed
        };
    }

    private static MatchCandidate FindBestFeatureOrb(
        Mat search,
        Mat template,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var maxFeatures = Math.Clamp(GetInt(parameters, "orbMaxFeatures", 600), 100, 4000);
        var minMatches = Math.Clamp(GetInt(parameters, "orbMinMatches", 8), 4, 80);
        var ratio = Math.Clamp(GetDouble(parameters, "orbRatio", 0.75), 0.45, 0.95);
        var inlierThreshold = Math.Clamp(GetDouble(parameters, "orbReprojectionThreshold", 10), 2, 80);

        using var orb = ORB.Create(maxFeatures);
        using var templateDescriptors = new Mat();
        using var searchDescriptors = new Mat();
        using var templateMask = TryReadTemplateMask(parameters, template.Size());
        using var emptyMask = new Mat();

        orb.DetectAndCompute(template, templateMask ?? emptyMask, out KeyPoint[] templateKeypoints, templateDescriptors);
        orb.DetectAndCompute(search, emptyMask, out KeyPoint[] searchKeypoints, searchDescriptors);

        if (templateKeypoints.Length < minMatches ||
            searchKeypoints.Length < minMatches ||
            templateDescriptors.Empty() ||
            searchDescriptors.Empty())
        {
            return MatchCandidate.None;
        }

        using var matcher = new BFMatcher(NormTypes.Hamming, false);
        var knnMatches = matcher.KnnMatch(templateDescriptors, searchDescriptors, 2);
        var goodMatches = knnMatches
            .Where(matches => matches.Length >= 2 && matches[0].Distance <= matches[1].Distance * ratio)
            .Select(matches => matches[0])
            .OrderBy(match => match.Distance)
            .Take(Math.Max(minMatches * 6, 80))
            .ToArray();

        if (goodMatches.Length < minMatches)
        {
            return MatchCandidate.None;
        }

        var angleSamples = goodMatches
            .Where(match => searchKeypoints[match.TrainIdx].Angle >= 0 && templateKeypoints[match.QueryIdx].Angle >= 0)
            .Select(match => NormalizeAngle(searchKeypoints[match.TrainIdx].Angle - templateKeypoints[match.QueryIdx].Angle))
            .ToArray();
        var estimatedAngle = angleSamples.Length > 0 ? Median(angleSamples) : 0;
        var angleRadians = estimatedAngle * Math.PI / 180.0;
        var cos = Math.Cos(angleRadians);
        var sin = Math.Sin(angleRadians);
        var templateCenterX = (template.Width - 1) / 2.0;
        var templateCenterY = (template.Height - 1) / 2.0;

        var centerCandidates = goodMatches
            .Select(match =>
            {
                var templatePoint = templateKeypoints[match.QueryIdx].Pt;
                var searchPoint = searchKeypoints[match.TrainIdx].Pt;
                var vectorX = templateCenterX - templatePoint.X;
                var vectorY = templateCenterY - templatePoint.Y;
                var rotatedX = vectorX * cos - vectorY * sin;
                var rotatedY = vectorX * sin + vectorY * cos;
                return (X: searchPoint.X + rotatedX, Y: searchPoint.Y + rotatedY);
            })
            .ToArray();

        var centerX = Median(centerCandidates.Select(point => point.X));
        var centerY = Median(centerCandidates.Select(point => point.Y));
        var inliers = centerCandidates.Count(point =>
            Distance(point.X, point.Y, centerX, centerY) <= inlierThreshold);
        if (inliers < minMatches)
        {
            return MatchCandidate.None;
        }

        var inlierRatio = inliers / (double)goodMatches.Length;
        var countScore = Math.Clamp(inliers / (double)Math.Max(minMatches, Math.Min(templateKeypoints.Length, 80)), 0, 1);
        var score = Math.Clamp(countScore * 0.65 + inlierRatio * 0.35, 0, 1);
        var x = Math.Clamp((int)Math.Round(centerX - template.Width / 2.0), 0, Math.Max(0, search.Width - template.Width));
        var y = Math.Clamp((int)Math.Round(centerY - template.Height / 2.0), 0, Math.Max(0, search.Height - template.Height));

        return new MatchCandidate(
            true,
            x,
            y,
            template.Width,
            template.Height,
            -estimatedAngle,
            score);
    }

    private static MatchCandidate FindBestShape(
        Mat search,
        Mat template,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        if (!TryGetShapeScoreVersion(parameters, out var scoreVersion))
        {
            return MatchCandidate.None;
        }

        var angleStep = GetAngleStep(parameters);
        var coarseAngleStep = Math.Clamp(GetDouble(parameters, "shapeCoarseAngleStep", Math.Max(4, angleStep * 2)), angleStep, 20);
        var coarseScale = GetShapeCoarseScale(search, template, parameters);
        if (coarseScale >= 0.999)
        {
            return FindBestShapePass(
                search,
                template,
                EnumerateAngles(parameters, true),
                1.0,
                scoreVersion,
                parameters,
                cancellationToken);
        }

        using var coarseSearch = ResizeForShapeSearch(search, coarseScale);
        using var coarseTemplate = ResizeForShapeSearch(template, coarseScale);
        var coarsePassScale = Math.Min(
            coarseTemplate.Width / (double)template.Width,
            coarseTemplate.Height / (double)template.Height);
        var coarse = FindBestShapePass(
            coarseSearch,
            coarseTemplate,
            EnumerateAngles(parameters, true, coarseAngleStep),
            coarsePassScale,
            scoreVersion,
            parameters,
            cancellationToken);
        if (!coarse.HasMatch)
        {
            return MatchCandidate.None;
        }

        var scaledCoarse = ScaleCoarseCandidate(coarse, coarseSearch.Size(), search.Size(), template.Size());
        var refineAngles = EnumerateRefineAngles(coarse.Angle, coarseAngleStep, angleStep).ToArray();
        var refineRegion = CreateRefineRegion(search.Size(), template.Size(), scaledCoarse, refineAngles, parameters);
        if (refineRegion.Width <= 0 || refineRegion.Height <= 0)
        {
            return MatchCandidate.None;
        }

        using var refineView = new Mat(search, refineRegion);
        using var refineSearch = refineView.Clone();
        var refined = FindBestShapePass(
            refineSearch,
            template,
            refineAngles,
            1.0,
            scoreVersion,
            parameters,
            cancellationToken);

        return refined.HasMatch
            ? refined with { X = refineRegion.X + refined.X, Y = refineRegion.Y + refined.Y }
            : MatchCandidate.None;
    }

    private static MatchCandidate FindBestShapePass(
        Mat search,
        Mat template,
        IEnumerable<double> angles,
        double passScale,
        int scoreVersion,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        using var searchEdges = PrepareForMatch(search, "Shape", parameters);
        if (Cv2.CountNonZero(searchEdges) < 8)
        {
            return MatchCandidate.None;
        }

        using var distanceSeed = new Mat();
        Cv2.Threshold(searchEdges, distanceSeed, 0, 255, ThresholdTypes.BinaryInv);

        using var edgeDistance = new Mat();
        Cv2.DistanceTransform(distanceSeed, edgeDistance, DistanceTypes.L2, DistanceTransformMasks.Mask3);

        using var templateMask = TryReadTemplateMask(parameters, template.Size());
        using var templateEdges = CreateShapeBaseEdges(template, parameters, templateMask);
        using var templateSupport = scoreVersion == 2
            ? CreateShapeSupportMask(template.Size(), templateMask)
            : null;
        var best = MatchCandidate.None;
        foreach (var angle in angles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var rotation = CreateKeepBoundsRotation(templateEdges.Size(), angle);
            using var rotatedEdges = WarpBinaryKeepBounds(templateEdges, rotation);
            if (rotatedEdges.Width > search.Width || rotatedEdges.Height > search.Height)
            {
                continue;
            }

            var edgeCount = Cv2.CountNonZero(rotatedEdges);
            if (edgeCount < 8)
            {
                continue;
            }

            using var chamferKernel = new Mat();
            rotatedEdges.ConvertTo(chamferKernel, MatType.CV_32FC1, 1.0 / (255.0 * edgeCount));

            using var result = new Mat();
            Cv2.MatchTemplate(edgeDistance, chamferKernel, result, TemplateMatchModes.CCorr);
            Cv2.MinMaxLoc(result, out var minDistance, out _, out var minLocation, out _);

            var score = scoreVersion == 1
                ? CreateShapeScore(minDistance, templateEdges.Width, templateEdges.Height, parameters)
                : 0;
            double? coverage = null;
            double? reverseScore = null;
            if (scoreVersion == 2)
            {
                using var rotatedSupport = WarpBinaryKeepBounds(templateSupport!, rotation);
                var quality = EvaluateShapeV2(
                    searchEdges,
                    edgeDistance,
                    rotatedEdges,
                    rotatedSupport,
                    minLocation,
                    minDistance,
                    parameters,
                    passScale);
                if (quality is null)
                {
                    continue;
                }

                score = quality.Value.Score;
                coverage = quality.Value.Coverage;
                reverseScore = quality.Value.ReverseScore;
            }

            if (score > best.Score)
            {
                best = new MatchCandidate(
                    true,
                    minLocation.X,
                    minLocation.Y,
                    rotatedEdges.Width,
                    rotatedEdges.Height,
                    angle,
                    score,
                    coverage,
                    reverseScore);
            }
        }

        return best;
    }

    private static string ResolveEffectiveMode(Mat search, Mat template, IReadOnlyDictionary<string, string> parameters)
    {
        var mode = GetMatchMode(parameters);
        if (mode != "Shape")
        {
            return mode;
        }

        if (TryGetShapeScoreVersion(parameters, out var scoreVersion) && scoreVersion == 2)
        {
            return mode;
        }

        using var templateEdges = CreateShapeBaseEdges(template, parameters);
        using var searchEdges = PrepareForMatch(search, "Shape", parameters);
        return Cv2.CountNonZero(templateEdges) >= 8 && Cv2.CountNonZero(searchEdges) >= 8 ? "Shape" : "GrayNcc";
    }

    private static Mat PrepareForMatch(Mat source, string mode, IReadOnlyDictionary<string, string> parameters)
    {
        if (!mode.Equals("Shape", StringComparison.OrdinalIgnoreCase))
        {
            return source.Clone();
        }

        using var blurred = new Mat();
        Cv2.GaussianBlur(source, blurred, new Size(3, 3), 0);
        var (low, high) = GetEdgeThresholds(blurred, parameters);
        var edges = new Mat();
        Cv2.Canny(blurred, edges, low, high);
        return edges;
    }

    private static (double Low, double High) GetEdgeThresholds(Mat source, IReadOnlyDictionary<string, string> parameters)
    {
        if (GetBool(parameters, "autoContrast", false))
        {
            using var thresholded = new Mat();
            var otsu = Cv2.Threshold(source, thresholded, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            var autoHigh = Math.Clamp(otsu <= 1 ? 160 : otsu, 2, 255);
            var autoLow = Math.Clamp(autoHigh * 0.4, 1, autoHigh - 1);
            return (autoLow, autoHigh);
        }

        var lowThreshold = GetDouble(parameters, "contrast", GetDouble(parameters, "cannyLow", 60));
        var manualLow = Math.Clamp(lowThreshold, 1, 254);
        var manualHigh = Math.Clamp(Math.Max(manualLow + 1, GetDouble(parameters, "cannyHigh", manualLow * 2.5)), 2, 255);
        return (manualLow, manualHigh);
    }

    private static Mat CreateShapeTemplateEdges(Mat template, double angle, IReadOnlyDictionary<string, string> parameters)
    {
        using var templateEdges = CreateShapeBaseEdges(template, parameters);
        return RotateShapeEdges(templateEdges, angle);
    }

    private static Mat CreateShapeBaseEdges(
        Mat template,
        IReadOnlyDictionary<string, string> parameters,
        Mat? maskOverride = null)
    {
        var templateEdges = PrepareForMatch(template, "Shape", parameters);
        if (maskOverride is not null)
        {
            return ApplyBinaryMask(templateEdges, maskOverride);
        }

        using var templateMask = TryReadTemplateMask(parameters, template.Size());
        if (templateMask is not null)
        {
            return ApplyBinaryMask(templateEdges, templateMask);
        }

        return templateEdges;
    }

    private static Mat ApplyBinaryMask(Mat source, Mat mask)
    {
        var masked = new Mat();
        Cv2.BitwiseAnd(source, mask, masked);
        source.Dispose();
        return masked;
    }

    private static Mat CreateShapeSupportMask(Size templateSize, Mat? templateMask)
    {
        return templateMask?.Clone() ?? new Mat(templateSize, MatType.CV_8UC1, Scalar.White);
    }

    private static ShapeQuality? EvaluateShapeV2(
        Mat searchEdges,
        Mat searchDistance,
        Mat rotatedEdges,
        Mat rotatedSupport,
        Point location,
        double forwardMean,
        IReadOnlyDictionary<string, string> parameters,
        double passScale)
    {
        var templateEdgeCount = Cv2.CountNonZero(rotatedEdges);
        if (templateEdgeCount < 8)
        {
            return null;
        }

        var rect = new Rect(location.X, location.Y, rotatedEdges.Width, rotatedEdges.Height);
        using var distancePatch = new Mat(searchDistance, rect);
        using var covered = new Mat();
        var coverageTolerance = GetShapeCoverageTolerance(parameters, passScale);
        Cv2.InRange(distancePatch, new Scalar(0), new Scalar(coverageTolerance), covered);

        using var coveredTemplateEdges = new Mat();
        Cv2.BitwiseAnd(covered, rotatedEdges, coveredTemplateEdges);
        var coverage = Cv2.CountNonZero(coveredTemplateEdges) / (double)templateEdgeCount;

        using var searchPatch = new Mat(searchEdges, rect);
        using var supportedSearchEdges = new Mat();
        Cv2.BitwiseAnd(searchPatch, rotatedSupport, supportedSearchEdges);
        if (Cv2.CountNonZero(supportedSearchEdges) < 8)
        {
            return null;
        }

        using var inverseTemplateEdges = new Mat();
        Cv2.BitwiseNot(rotatedEdges, inverseTemplateEdges);
        using var templateDistance = new Mat();
        Cv2.DistanceTransform(
            inverseTemplateEdges,
            templateDistance,
            DistanceTypes.L2,
            DistanceTransformMasks.Mask3);
        var reverseMean = Cv2.Mean(templateDistance, supportedSearchEdges).Val0;

        var scale = GetShapeV2ScoreScale(rotatedEdges.Size(), parameters, passScale);
        var forward = Math.Exp(-Math.Max(0, forwardMean) / scale);
        var reverse = Math.Exp(-Math.Max(0, reverseMean) / scale);
        var reverseScore = Math.Clamp(reverse, 0, 1);
        var score = Math.Clamp(Math.Min(forward, reverse) * coverage, 0, 1);
        return new ShapeQuality(score, coverage, reverseScore);
    }

    private static Mat RotateShapeEdges(Mat templateEdges, double angle)
    {
        return WarpBinaryKeepBounds(templateEdges, angle);
    }

    private static Mat WarpBinaryKeepBounds(Mat source, double angle)
    {
        using var rotation = CreateKeepBoundsRotation(source.Size(), angle);
        return WarpBinaryKeepBounds(source, rotation);
    }

    private static Mat WarpBinaryKeepBounds(Mat source, KeepBoundsRotation rotation)
    {
        var rotated = new Mat();
        Cv2.WarpAffine(
            source,
            rotated,
            rotation.Matrix,
            rotation.CanvasSize,
            InterpolationFlags.Nearest,
            BorderTypes.Constant,
            Scalar.Black);
        return rotated;
    }

    private static KeepBoundsRotation CreateKeepBoundsRotation(Size sourceSize, double angle)
    {
        var sourceCenter = new Point2f((sourceSize.Width - 1) / 2.0f, (sourceSize.Height - 1) / 2.0f);
        var canvasSize = GetRotatedCanvasSize(sourceSize, angle);
        var matrix = Cv2.GetRotationMatrix2D(sourceCenter, angle, 1.0);
        matrix.Set(0, 2, matrix.At<double>(0, 2) + (canvasSize.Width - 1) / 2.0 - sourceCenter.X);
        matrix.Set(1, 2, matrix.At<double>(1, 2) + (canvasSize.Height - 1) / 2.0 - sourceCenter.Y);
        return new KeepBoundsRotation(canvasSize, matrix);
    }

    private static Size GetRotatedCanvasSize(Size sourceSize, double angle)
    {
        var radians = angle * Math.PI / 180.0;
        var cos = Math.Abs(SnapTrig(Math.Cos(radians)));
        var sin = Math.Abs(SnapTrig(Math.Sin(radians)));
        var width = Math.Max(1, (int)Math.Ceiling(sourceSize.Width * cos + sourceSize.Height * sin));
        var height = Math.Max(1, (int)Math.Ceiling(sourceSize.Width * sin + sourceSize.Height * cos));
        return new Size(width, height);
    }

    private static double SnapTrig(double value)
    {
        if (Math.Abs(value) < 1e-12)
        {
            return 0;
        }

        if (Math.Abs(Math.Abs(value) - 1) < 1e-12)
        {
            return Math.Sign(value);
        }

        return value;
    }

    private static byte[] CreateTemplateEdgeOverlay(Mat template, IReadOnlyDictionary<string, string> parameters, Mat? templateMask)
    {
        using var edges = CreateShapeBaseEdges(template, parameters, templateMask);
        using var overlay = new Mat(template.Size(), MatType.CV_8UC4, Scalar.All(0));
        overlay.SetTo(new Scalar(87, 200, 255, 235), edges);
        return overlay.ToBytes(".png");
    }

    private static Mat? CreateTemplateMask(
        Size templateSize,
        TemplateSearchRegion templateRegion,
        IReadOnlyDictionary<string, string> parameters)
    {
        var hasTemplateRoi = TryReadTemplateRoi(parameters, out var templateRoi);
        var maskRois = ReadTemplateMaskRois(parameters);
        var restrictToTemplateRoi = hasTemplateRoi && ShouldRestrictToTemplateRoi(templateRoi, templateRegion);
        if (!restrictToTemplateRoi && maskRois.Count == 0)
        {
            return null;
        }

        var mask = new Mat(templateSize, MatType.CV_8UC1, restrictToTemplateRoi ? Scalar.Black : Scalar.White);
        if (restrictToTemplateRoi)
        {
            FillTemplateMask(mask, templateRoi, templateRegion, Scalar.White);
        }

        foreach (var maskRoi in maskRois)
        {
            FillTemplateMask(mask, maskRoi, templateRegion, Scalar.Black);
        }

        var expand = Math.Clamp(GetInt(parameters, "templateMaskExpand", 2), 0, 16);
        if (maskRois.Count > 0 && expand > 0)
        {
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(expand * 2 + 1, expand * 2 + 1));
            Cv2.Erode(mask, mask, kernel);
        }

        return mask;
    }

    private static Mat? TryReadTemplateMask(IReadOnlyDictionary<string, string> parameters, Size templateSize)
    {
        if (!parameters.TryGetValue("templateMaskPng", out var encodedMask) || string.IsNullOrWhiteSpace(encodedMask))
        {
            return null;
        }

        try
        {
            var maskBuffer = Convert.FromBase64String(encodedMask);
            var decoded = Cv2.ImDecode(maskBuffer, ImreadModes.Grayscale);
            if (decoded.Empty())
            {
                decoded.Dispose();
                return null;
            }

            if (decoded.Size() == templateSize)
            {
                return decoded;
            }

            var resized = new Mat();
            Cv2.Resize(decoded, resized, templateSize, 0, 0, InterpolationFlags.Nearest);
            decoded.Dispose();
            return resized;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IReadOnlyList<RoiDefinition> ReadTemplateMaskRois(IReadOnlyDictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("templateMaskRoisJson", out var json) || string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<RoiDefinition>();
        }

        try
        {
            return JsonSerializer.Deserialize<RoiDefinition[]>(json) ?? Array.Empty<RoiDefinition>();
        }
        catch (JsonException)
        {
            return Array.Empty<RoiDefinition>();
        }
    }

    private static bool TryReadTemplateRoi(IReadOnlyDictionary<string, string> parameters, out RoiDefinition roi)
    {
        roi = default!;
        if (parameters.TryGetValue("templateRoiJson", out var json) && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<RoiDefinition>(json);
                if (parsed is not null)
                {
                    roi = parsed;
                    return true;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        if (!TryGetTemplateRoi(parameters, out var x, out var y, out var width, out var height))
        {
            return false;
        }

        var shape = parameters.TryGetValue("templateRoiShape", out var shapeText) &&
                    Enum.TryParse<RoiShapeKind>(shapeText, true, out var parsedShape)
            ? parsedShape
            : RoiShapeKind.Rectangle;

        roi = new RoiDefinition
        {
            Id = "template-roi",
            Name = "Template ROI",
            Shape = shape,
            X = shape is RoiShapeKind.Circle or RoiShapeKind.RotatedRectangle ? x + width / 2.0 : x,
            Y = shape is RoiShapeKind.Circle or RoiShapeKind.RotatedRectangle ? y + height / 2.0 : y,
            Width = width,
            Height = height,
            Radius = Math.Min(width, height) / 2.0,
            Angle = GetDouble(parameters, "templateRoiAngle", 0)
        };
        return true;
    }

    private static bool TryGetLearnedTemplateRegion(
        IReadOnlyDictionary<string, string> parameters,
        out double x,
        out double y,
        out double width,
        out double height)
    {
        if (TryGetDouble(parameters, "templateX", out x) &&
            TryGetDouble(parameters, "templateY", out y) &&
            TryGetDouble(parameters, "templateWidth", out width) &&
            TryGetDouble(parameters, "templateHeight", out height))
        {
            return true;
        }

        return TryGetTemplateRoi(parameters, out x, out y, out width, out height);
    }

    private static bool ShouldRestrictToTemplateRoi(RoiDefinition roi, TemplateSearchRegion templateRegion)
    {
        if (roi.Shape == RoiShapeKind.Polygon)
        {
            return roi.Points.Count >= 3;
        }

        if (roi.Shape is RoiShapeKind.Circle or RoiShapeKind.RotatedRectangle)
        {
            return true;
        }

        return Math.Abs(roi.X - templateRegion.X) > 0.5 ||
               Math.Abs(roi.Y - templateRegion.Y) > 0.5 ||
               Math.Abs(roi.Width - templateRegion.Width) > 0.5 ||
               Math.Abs(roi.Height - templateRegion.Height) > 0.5;
    }

    private static void FillTemplateMask(Mat mask, RoiDefinition roi, TemplateSearchRegion templateRegion, Scalar color)
    {
        switch (roi.Shape)
        {
            case RoiShapeKind.Circle:
                Cv2.Circle(
                    mask,
                    new Point((int)Math.Round(roi.X - templateRegion.X), (int)Math.Round(roi.Y - templateRegion.Y)),
                    Math.Max(1, (int)Math.Round(roi.Radius)),
                    color,
                    -1);
                return;
            case RoiShapeKind.RotatedRectangle:
                var rotated = new RotatedRect(
                    new Point2f((float)(roi.X - templateRegion.X), (float)(roi.Y - templateRegion.Y)),
                    new Size2f((float)roi.Width, (float)roi.Height),
                    (float)roi.Angle);
                Cv2.FillConvexPoly(mask, ToCvPoints(rotated.Points()), color);
                return;
            case RoiShapeKind.Polygon when roi.Points.Count >= 3:
                Cv2.FillPoly(mask, [ToCvPoints(roi.Points.Select(point => new Point2f(
                    (float)(point.X - templateRegion.X),
                    (float)(point.Y - templateRegion.Y))).ToArray())], color);
                return;
            default:
                Cv2.Rectangle(
                    mask,
                    new Point((int)Math.Round(roi.X - templateRegion.X), (int)Math.Round(roi.Y - templateRegion.Y)),
                    new Point((int)Math.Round(roi.X + roi.Width - templateRegion.X), (int)Math.Round(roi.Y + roi.Height - templateRegion.Y)),
                    color,
                    -1);
                return;
        }
    }

    private static Point[] ToCvPoints(IEnumerable<Point2f> points)
    {
        return points.Select(point => new Point((int)Math.Round(point.X), (int)Math.Round(point.Y))).ToArray();
    }

    private static double CreateShapeScore(
        double averageEdgeDistance,
        int templateWidth,
        int templateHeight,
        IReadOnlyDictionary<string, string> parameters)
    {
        var defaultScale = Math.Clamp(Math.Min(templateWidth, templateHeight) * 0.18, 12, 30);
        var scale = Math.Max(1, GetDouble(parameters, "shapeScoreScale", defaultScale));
        return Math.Clamp(Math.Exp(-Math.Max(0, averageEdgeDistance) / scale), 0, 1);
    }

    private static double GetShapeCoverageTolerance(
        IReadOnlyDictionary<string, string> parameters,
        double passScale)
    {
        var safePassScale = double.IsFinite(passScale) ? passScale : 1;
        return Math.Max(0.75, GetNormalizedShapeCoverageDistance(parameters) * safePassScale);
    }

    private static double GetNormalizedShapeCoverageDistance(IReadOnlyDictionary<string, string> parameters)
    {
        var configured = TryGetDouble(parameters, "shapeCoverageDistance", out var value) && double.IsFinite(value)
            ? value
            : 3;
        return Math.Clamp(configured, 0.5, 20);
    }

    private static double GetShapeV2ScoreScale(
        Size currentTemplate,
        IReadOnlyDictionary<string, string> parameters,
        double passScale)
    {
        if (TryGetDouble(parameters, "shapeScoreScale", out var configured) && double.IsFinite(configured))
        {
            return Math.Max(0.25, configured * passScale);
        }

        var safeScale = double.IsFinite(passScale) ? Math.Max(0.01, passScale) : 1;
        var fullShort = Math.Min(
            currentTemplate.Width / safeScale,
            currentTemplate.Height / safeScale);
        return Math.Max(0.25, Math.Clamp(fullShort * 0.18, 12, 30) * safeScale);
    }

    private static Mat ResizeForShapeSearch(Mat source, double scale)
    {
        var size = new Size(
            Math.Max(MinimumTemplateSize, (int)Math.Round(source.Width * scale)),
            Math.Max(MinimumTemplateSize, (int)Math.Round(source.Height * scale)));
        var resized = new Mat();
        Cv2.Resize(source, resized, size, 0, 0, InterpolationFlags.Area);
        return resized;
    }

    private static double GetShapeCoarseScale(Mat search, Mat template, IReadOnlyDictionary<string, string> parameters)
    {
        var configured = GetDouble(parameters, "shapeCoarseScale", 0);
        if (configured > 0)
        {
            return Math.Clamp(configured, 0.25, 1);
        }

        var scale = Math.Min(1, 720.0 / Math.Max(search.Width, search.Height));
        var minimumTemplateDimension = Math.Max(MinimumTemplateSize, Math.Min(template.Width, template.Height));
        scale = Math.Max(scale, Math.Min(1, 28.0 / minimumTemplateDimension));
        return Math.Clamp(scale, 0.25, 1);
    }

    private static MatchCandidate ScaleCoarseCandidate(
        MatchCandidate coarse,
        Size coarseSearch,
        Size fullSearch,
        Size fullTemplate)
    {
        var centerX = coarse.CenterX * fullSearch.Width / coarseSearch.Width;
        var centerY = coarse.CenterY * fullSearch.Height / coarseSearch.Height;
        var canvas = GetRotatedCanvasSize(fullTemplate, coarse.Angle);
        var x = Math.Clamp(
            (int)Math.Round(centerX - canvas.Width / 2.0),
            0,
            Math.Max(0, fullSearch.Width - canvas.Width));
        var y = Math.Clamp(
            (int)Math.Round(centerY - canvas.Height / 2.0),
            0,
            Math.Max(0, fullSearch.Height - canvas.Height));

        return coarse with
        {
            X = x,
            Y = y,
            Width = canvas.Width,
            Height = canvas.Height
        };
    }

    private static Rect CreateRefineRegion(
        Size search,
        Size template,
        MatchCandidate coarse,
        IReadOnlyCollection<double> angles,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (angles.Count == 0)
        {
            return new Rect();
        }

        var requiredWidth = 0;
        var requiredHeight = 0;
        foreach (var angle in angles)
        {
            var canvas = GetRotatedCanvasSize(template, angle);
            requiredWidth = Math.Max(requiredWidth, canvas.Width);
            requiredHeight = Math.Max(requiredHeight, canvas.Height);
        }

        var marginFactor = Math.Clamp(GetDouble(parameters, "shapeRefineMargin", 0.9), 0.25, 3);
        var marginX = Math.Max(24, (int)Math.Ceiling(requiredWidth * marginFactor));
        var marginY = Math.Max(24, (int)Math.Ceiling(requiredHeight * marginFactor));
        var width = Math.Min(search.Width, requiredWidth + 2 * marginX);
        var height = Math.Min(search.Height, requiredHeight + 2 * marginY);
        var left = Math.Clamp(
            (int)Math.Round(coarse.CenterX - width / 2.0),
            0,
            Math.Max(0, search.Width - width));
        var top = Math.Clamp(
            (int)Math.Round(coarse.CenterY - height / 2.0),
            0,
            Math.Max(0, search.Height - height));
        return new Rect(left, top, width, height);
    }

    private static Mat Rotate(Mat source, double angle)
    {
        var center = new Point2f((source.Width - 1) / 2.0f, (source.Height - 1) / 2.0f);
        using var matrix = Cv2.GetRotationMatrix2D(center, angle, 1.0);
        var rotated = new Mat();
        Cv2.WarpAffine(
            source,
            rotated,
            matrix,
            source.Size(),
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            Scalar.Black);
        return rotated;
    }

    private static Mat RotateMask(Mat source, double angle)
    {
        var center = new Point2f((source.Width - 1) / 2.0f, (source.Height - 1) / 2.0f);
        using var matrix = Cv2.GetRotationMatrix2D(center, angle, 1.0);
        var rotated = new Mat();
        Cv2.WarpAffine(
            source,
            rotated,
            matrix,
            source.Size(),
            InterpolationFlags.Nearest,
            BorderTypes.Constant,
            Scalar.Black);
        return rotated;
    }

    private static double ToImagePoseAngle(double openCvRotationAngle)
    {
        // OpenCV's warp angle rotates a candidate counter-clockwise on screen.
        // ROI editors and calipers use image-space clockwise-positive angles.
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

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static IEnumerable<double> EnumerateAngles(
        IReadOnlyDictionary<string, string> parameters,
        bool expandLegacyShapeRange,
        double? stepOverride = null)
    {
        var start = GetDouble(parameters, "angleStart", expandLegacyShapeRange ? -45 : 0);
        var extent = GetDouble(parameters, "angleExtent", expandLegacyShapeRange ? 90 : 0);
        if (expandLegacyShapeRange &&
            Math.Abs(start + 10) < 0.001 &&
            Math.Abs(extent - 20) < 0.001)
        {
            start = -45;
            extent = 90;
        }

        if (Math.Abs(extent) < 0.001)
        {
            yield return 0;
            yield break;
        }

        var step = stepOverride ?? GetAngleStep(parameters);
        var end = start + extent;
        if (start <= end)
        {
            for (var angle = start; angle <= end + 0.001; angle += step)
            {
                yield return Math.Round(angle, 3);
            }
        }
        else
        {
            for (var angle = start; angle >= end - 0.001; angle -= step)
            {
                yield return Math.Round(angle, 3);
            }
        }

        if (start > 0 || end < 0)
        {
            yield return 0;
        }
    }

    private static IEnumerable<double> EnumerateRefineAngles(double center, double radius, double step)
    {
        var start = center - radius;
        var end = center + radius;
        for (var angle = start; angle <= end + 0.001; angle += step)
        {
            yield return Math.Round(angle, 3);
        }

        if (!IsOnAngleStep(center, start, step))
        {
            yield return Math.Round(center, 3);
        }
    }

    private static double GetAngleStep(IReadOnlyDictionary<string, string> parameters)
    {
        return Math.Clamp(Math.Abs(GetDouble(parameters, "angleStep", 2)), 0.5, 15);
    }

    private static bool IsOnAngleStep(double value, double start, double step)
    {
        return Math.Abs((value - start) / step - Math.Round((value - start) / step)) < 0.001;
    }

    private static bool TryReadTemplate(IReadOnlyDictionary<string, string> parameters, out Mat template)
    {
        template = new Mat();
        if (parameters.TryGetValue("templateImagePng", out var encodedPng) &&
            !string.IsNullOrWhiteSpace(encodedPng))
        {
            try
            {
                var buffer = Convert.FromBase64String(encodedPng);
                template = Cv2.ImDecode(buffer, ImreadModes.Grayscale);
                return !template.Empty() && template.Width >= MinimumTemplateSize && template.Height >= MinimumTemplateSize;
            }
            catch (FormatException)
            {
                template.Dispose();
                template = new Mat();
            }
        }

        if (!TryGetInt(parameters, "templateWidth", out var width) ||
            !TryGetInt(parameters, "templateHeight", out var height) ||
            width < MinimumTemplateSize ||
            height < MinimumTemplateSize)
        {
            return false;
        }

        if (!parameters.TryGetValue("templatePixels", out var encodedRaw) || string.IsNullOrWhiteSpace(encodedRaw))
        {
            return false;
        }

        try
        {
            var pixels = Convert.FromBase64String(encodedRaw);
            if (pixels.Length != width * height)
            {
                return false;
            }

            using var raw = Mat.FromPixelData(height, width, MatType.CV_8UC1, pixels);
            template = raw.Clone();
            return true;
        }
        catch (FormatException)
        {
            template.Dispose();
            template = new Mat();
            return false;
        }
    }

    private static TemplateSearchRegion GetTemplateRegion(
        ImageFrame frame,
        TemplateSearchRegion searchRegion,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (TryGetTemplateRoi(parameters, out var x, out var y, out var width, out var height) &&
            width >= MinimumTemplateSize &&
            height >= MinimumTemplateSize)
        {
            return ClampRegion(frame, x, y, width, height);
        }

        var templateWidth = Math.Clamp((int)Math.Round(searchRegion.Width * 0.22), 96, 280);
        var templateHeight = Math.Clamp((int)Math.Round(searchRegion.Height * 0.16), 72, 180);
        templateWidth = Math.Min(templateWidth, Math.Max(MinimumTemplateSize, searchRegion.Width));
        templateHeight = Math.Min(templateHeight, Math.Max(MinimumTemplateSize, searchRegion.Height));

        var centerX = searchRegion.X + searchRegion.Width / 2;
        var centerY = searchRegion.Y + searchRegion.Height / 2;
        var left = Math.Clamp(centerX - templateWidth / 2, searchRegion.X, Math.Max(searchRegion.X, searchRegion.Right - templateWidth));
        var top = Math.Clamp(centerY - templateHeight / 2, searchRegion.Y, Math.Max(searchRegion.Y, searchRegion.Bottom - templateHeight));
        return ClampRegion(frame, left, top, templateWidth, templateHeight);
    }

    private static bool TryGetTemplateRoi(
        IReadOnlyDictionary<string, string> parameters,
        out double x,
        out double y,
        out double width,
        out double height)
    {
        x = 0;
        y = 0;
        width = 0;
        height = 0;

        if (TryGetDouble(parameters, "templateRoiX", out x) &&
            TryGetDouble(parameters, "templateRoiY", out y) &&
            TryGetDouble(parameters, "templateRoiWidth", out width) &&
            TryGetDouble(parameters, "templateRoiHeight", out height))
        {
            return true;
        }

        return TryGetDouble(parameters, "templateX", out x) &&
               TryGetDouble(parameters, "templateY", out y) &&
               TryGetDouble(parameters, "templateWidth", out width) &&
               TryGetDouble(parameters, "templateHeight", out height);
    }

    private static TemplateSearchRegion ClampRegion(ImageFrame frame, double x, double y, double width, double height)
    {
        var left = Math.Clamp((int)Math.Floor(x), 0, Math.Max(0, frame.Width - 1));
        var top = Math.Clamp((int)Math.Floor(y), 0, Math.Max(0, frame.Height - 1));
        var right = Math.Clamp((int)Math.Ceiling(x + width), left + 1, frame.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(y + height), top + 1, frame.Height);

        return new TemplateSearchRegion(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static Rect ToCvRect(TemplateSearchRegion region)
    {
        return new Rect(region.X, region.Y, region.Width, region.Height);
    }

    private static Mat ToGrayMat(ImageFrame frame)
    {
        return ImageFrameMatFactory.ToGrayMat(frame);
    }

    private static TemplateMatchResult CreateFailedResult(
        ImageFrame frame,
        string message,
        bool usedAutoTemplate,
        TemplateSearchRegion? searchRegion = null,
        int templateWidth = 0,
        int templateHeight = 0)
    {
        return new TemplateMatchResult(
            false,
            InspectionOutcome.Ng,
            0,
            new Pose2D(frame.Width / 2.0, frame.Height / 2.0, 0),
            0,
            0,
            templateWidth,
            templateHeight,
            searchRegion ?? new TemplateSearchRegion(0, 0, frame.Width, frame.Height),
            message,
            usedAutoTemplate);
    }

    private static string GetMatchMode(IReadOnlyDictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("matchMode", out var mode) || string.IsNullOrWhiteSpace(mode))
        {
            return "Shape";
        }

        return mode.Trim() switch
        {
            "Gray" => "GrayNcc",
            "Ncc" => "GrayNcc",
            "NCC" => "GrayNcc",
            "GrayNcc" => "GrayNcc",
            "GrayCcorr" => "GrayCcorr",
            "GraySqDiff" => "GraySqDiff",
            "FeatureOrb" => "FeatureOrb",
            "ORB" => "FeatureOrb",
            _ => "Shape"
        };
    }

    private static bool TryGetShapeScoreVersion(
        IReadOnlyDictionary<string, string> parameters,
        out int version)
    {
        version = 1;
        if (!parameters.TryGetValue("shapeScoreVersion", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out version) &&
               version is 1 or 2;
    }

    private static string GetMatchModeDisplayName(string mode)
    {
        return mode switch
        {
            "GrayNcc" => "gray NCC",
            "GrayCcorr" => "gray correlation",
            "GraySqDiff" => "gray squared-difference",
            "FeatureOrb" => "ORB feature",
            _ => "shape"
        };
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
            value = Math.Max(0, (int)Math.Round(doubleValue));
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

    private static bool TryGetDouble(IReadOnlyDictionary<string, string> parameters, string key, out double value)
    {
        value = 0;
        return parameters.TryGetValue(key, out var raw) &&
               double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static int GetInt(IReadOnlyDictionary<string, string> parameters, string key, int fallback)
    {
        return parameters.TryGetValue(key, out var raw) &&
               int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
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
        bool HasMatch,
        int X,
        int Y,
        int Width,
        int Height,
        double Angle,
        double Score,
        double? ShapeCoverage = null,
        double? ShapeReverseScore = null)
    {
        public double CenterX => X + Width / 2.0;

        public double CenterY => Y + Height / 2.0;

        public static MatchCandidate None { get; } = new(false, 0, 0, 0, 0, 0, double.NegativeInfinity);
    }

    private readonly record struct ShapeQuality(
        double Score,
        double Coverage,
        double ReverseScore);

    private sealed class KeepBoundsRotation : IDisposable
    {
        public KeepBoundsRotation(Size canvasSize, Mat matrix)
        {
            CanvasSize = canvasSize;
            Matrix = matrix;
        }

        public Size CanvasSize { get; }

        public Mat Matrix { get; }

        public void Dispose()
        {
            Matrix.Dispose();
        }
    }
}
