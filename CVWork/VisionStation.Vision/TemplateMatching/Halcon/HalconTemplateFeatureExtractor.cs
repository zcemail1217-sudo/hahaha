using System.Collections.ObjectModel;
using OpenCvSharp;
using VisionStation.Domain;

namespace VisionStation.Vision;

internal sealed class HalconTemplateFeatureSet
{
    public HalconTemplateFeatureSet(
        int templateWidth,
        int templateHeight,
        double referenceRow,
        double referenceColumn,
        double modelDomainCentroidRow,
        double modelDomainCentroidColumn,
        bool isDarkForeground,
        IReadOnlyList<Point2D> outerContour,
        IReadOnlyList<IReadOnlyList<Point2D>> innerFeatureGroups,
        int minimumValidInnerGroupCount,
        HalconFilledSupportRegion filledSupport)
    {
        if (templateWidth <= 0 || templateHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(templateWidth));
        }

        if (!double.IsFinite(referenceRow) ||
            !double.IsFinite(referenceColumn) ||
            !double.IsFinite(modelDomainCentroidRow) ||
            !double.IsFinite(modelDomainCentroidColumn))
        {
            throw new ArgumentOutOfRangeException(nameof(referenceRow));
        }

        if (!isDarkForeground)
        {
            throw new ArgumentException("HALCON template features require dark foreground polarity.", nameof(isDarkForeground));
        }

        IReadOnlyList<Point2D> outerSnapshot =
            HalconMetadataSnapshots.Points(outerContour, nameof(outerContour));
        IReadOnlyList<IReadOnlyList<Point2D>> innerSnapshot =
            HalconMetadataSnapshots.Groups(innerFeatureGroups, nameof(innerFeatureGroups));
        if (outerSnapshot.Count == 0)
        {
            throw new ArgumentException("An outer contour is required.", nameof(outerContour));
        }

        if (innerSnapshot.Count == 0)
        {
            throw new ArgumentException("At least one inner feature group is required.", nameof(innerFeatureGroups));
        }

        if (minimumValidInnerGroupCount < 1 || minimumValidInnerGroupCount > innerSnapshot.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumValidInnerGroupCount));
        }

        ArgumentNullException.ThrowIfNull(filledSupport);
        TemplateWidth = templateWidth;
        TemplateHeight = templateHeight;
        ReferenceRow = referenceRow;
        ReferenceColumn = referenceColumn;
        ModelDomainCentroidRow = modelDomainCentroidRow;
        ModelDomainCentroidColumn = modelDomainCentroidColumn;
        IsDarkForeground = true;
        OuterContour = outerSnapshot;
        InnerFeatureGroups = innerSnapshot;
        MinimumValidInnerGroupCount = minimumValidInnerGroupCount;
        FilledSupport = new HalconFilledSupportRegion(
            filledSupport.OriginX,
            filledSupport.OriginY,
            filledSupport.Runs);
    }

    public int TemplateWidth { get; }

    public int TemplateHeight { get; }

    public double ReferenceRow { get; }

    public double ReferenceColumn { get; }

    public double ModelDomainCentroidRow { get; }

    public double ModelDomainCentroidColumn { get; }

    public bool IsDarkForeground { get; }

    public IReadOnlyList<Point2D> OuterContour { get; }

    public IReadOnlyList<IReadOnlyList<Point2D>> InnerFeatureGroups { get; }

    public int MinimumValidInnerGroupCount { get; }

    public HalconFilledSupportRegion FilledSupport { get; }
}

internal sealed class HalconTemplateFeatureExtractionResult
{
    private HalconTemplateFeatureExtractionResult(
        HalconTemplateFeatureSet? features,
        TemplateMatchingDiagnostic? diagnostic)
    {
        if ((features is null) == (diagnostic is null))
        {
            throw new ArgumentException("Feature extraction must return exactly one of features or a diagnostic.");
        }

        Features = features;
        Diagnostic = diagnostic;
    }

    public bool Success => Features is not null;

    public HalconTemplateFeatureSet? Features { get; }

    public TemplateMatchingDiagnostic? Diagnostic { get; }

    public static HalconTemplateFeatureExtractionResult FromFeatures(HalconTemplateFeatureSet features)
    {
        return new HalconTemplateFeatureExtractionResult(
            features ?? throw new ArgumentNullException(nameof(features)),
            null);
    }

    public static HalconTemplateFeatureExtractionResult FromDiagnostic(
        TemplateMatchingDiagnostic diagnostic)
    {
        return new HalconTemplateFeatureExtractionResult(
            null,
            diagnostic ?? throw new ArgumentNullException(nameof(diagnostic)));
    }
}

internal sealed class Gray8Histogram
{
    private readonly int[] _counts = new int[256];

    public int Count { get; private set; }

    public void Add(byte value)
    {
        _counts[value]++;
        Count++;
    }

    public double Median()
    {
        if (Count == 0)
        {
            return double.NaN;
        }

        int upperIndex = Count / 2;
        int upper = ValueAtIndex(upperIndex);
        if (Count % 2 != 0)
        {
            return upper;
        }

        int lower = ValueAtIndex(upperIndex - 1);
        return (lower + upper) / 2.0;
    }

    public double NearestRankPercentile(double percentile)
    {
        if (!double.IsFinite(percentile) || percentile < 0 || percentile > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile));
        }

        if (Count == 0)
        {
            return double.NaN;
        }

        double rawIndex = Math.Ceiling(percentile * Count) - 1;
        int index;
        if (double.IsNaN(rawIndex) || rawIndex <= 0)
        {
            index = 0;
        }
        else if (rawIndex >= Count - 1)
        {
            index = Count - 1;
        }
        else
        {
            index = (int)rawIndex;
        }

        return ValueAtIndex(index);
    }

    private byte ValueAtIndex(int targetIndex)
    {
        var cumulativeCount = 0;
        for (var value = 0; value < _counts.Length; value++)
        {
            cumulativeCount += _counts[value];
            if (targetIndex < cumulativeCount)
            {
                return (byte)value;
            }
        }

        throw new InvalidOperationException("Histogram rank exceeded the collected Gray8 samples.");
    }
}

internal sealed class HalconTemplateFeatureExtractor
{
    private const double MinimumContrast = 20;
    private const int MinimumOuterRawPoints = 20;
    private const int MinimumOuterSampleCount = 100;
    private const int MinimumInnerGroupPointCount = 6;

    public HalconTemplateFeatureExtractionResult Extract(
        ImageFrame frame,
        RoiDefinition roi,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryValidateFrame(frame, out string frameFailure))
        {
            return ConfigurationFailure(frameFailure);
        }

        if (!TryCreateRoiGeometry(roi, frame.Width, frame.Height, out RoiGeometry geometry, out string roiFailure))
        {
            return ConfigurationFailure(roiFailure);
        }

        using Mat gray = ImageFrameMatFactory.ToGrayMat(frame);
        using var cropView = new Mat(gray, geometry.CropBounds);
        using Mat crop = cropView.Clone();
        using Mat roiMask = CreateRoiMask(roi, geometry);
        cancellationToken.ThrowIfCancellationRequested();

        using var roiBoundary = CreateRoiBoundary(roiMask);
        MaskedGray8Statistics statistics = CalculateMaskedStatistics(
            crop,
            roiBoundary,
            roiMask,
            cancellationToken);
        double background = statistics.BoundaryMedian;
        double darkLevel = statistics.RoiDarkPercentile;
        double contrast = background - darkLevel;
        if (!double.IsFinite(background) ||
            !double.IsFinite(darkLevel) ||
            contrast < MinimumContrast)
        {
            return Failure(
                TemplateMatchingDiagnosticCodes.ModelContrastWeak,
                $"Template foreground/background contrast is {contrast:R}; required >= {MinimumContrast:R}.");
        }

        double threshold = darkLevel + contrast * 0.5;
        using var foreground = new Mat();
        Cv2.Compare(crop, new Scalar(threshold), foreground, CmpTypes.LT);
        Cv2.BitwiseAnd(foreground, roiMask, foreground);
        using Mat morphologyKernel = Cv2.GetStructuringElement(
            MorphShapes.Ellipse,
            new Size(3, 3));
        Cv2.MorphologyEx(foreground, foreground, MorphTypes.Open, morphologyKernel);
        Cv2.MorphologyEx(foreground, foreground, MorphTypes.Close, morphologyKernel);
        cancellationToken.ThrowIfCancellationRequested();

        using Mat? mainRegion = SelectCenterConnectedRegion(
            foreground,
            geometry.ReferenceColumn,
            geometry.ReferenceRow);
        if (mainRegion is null || Cv2.CountNonZero(mainRegion) < 50)
        {
            return Failure(
                TemplateMatchingDiagnosticCodes.ModelTemplateIncomplete,
                "No reliable dark component is associated with the template reference center.");
        }

        using var boundaryProbe = new Mat();
        Cv2.Dilate(mainRegion, boundaryProbe, morphologyKernel);
        using var boundaryContact = new Mat();
        Cv2.BitwiseAnd(boundaryProbe, roiBoundary, boundaryContact);
        if (Cv2.CountNonZero(boundaryContact) > 0)
        {
            return Failure(
                TemplateMatchingDiagnosticCodes.ModelTemplateIncomplete,
                "The learned product touches the template ROI boundary.");
        }

        if (!TryFindOuterContour(mainRegion, out Point[] outerPixels))
        {
            return Failure(
                TemplateMatchingDiagnosticCodes.ModelTemplateIncomplete,
                "The learned product does not have a reliable closed outer contour.");
        }

        IReadOnlyList<Point2D> outerContour = ResampleOuterContour(
            outerPixels,
            geometry.ReferenceColumn,
            geometry.ReferenceRow);
        if (outerContour.Count < MinimumOuterSampleCount)
        {
            return Failure(
                TemplateMatchingDiagnosticCodes.ModelTemplateIncomplete,
                "The learned product outer contour is too short.");
        }

        using Mat filledSupport = CreateFilledSupport(mainRegion.Size(), outerPixels, roiMask);
        if (Cv2.CountNonZero(filledSupport) == 0)
        {
            return Failure(
                TemplateMatchingDiagnosticCodes.ModelTemplateIncomplete,
                "The complete filled template support is empty.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<IReadOnlyList<Point2D>> innerGroups = ExtractInnerGroups(
            crop,
            filledSupport,
            geometry.ReferenceColumn,
            geometry.ReferenceRow,
            cancellationToken);
        if (innerGroups.Count < 3 || !HasDistributedGroups(innerGroups, crop.Width, crop.Height))
        {
            return Failure(
                TemplateMatchingDiagnosticCodes.ModelInternalFeaturesWeak,
                $"Only {innerGroups.Count} distributed inner feature groups were retained; required >= 3.");
        }

        Moments moments = Cv2.Moments(filledSupport, true);
        if (moments.M00 <= 0)
        {
            return Failure(
                TemplateMatchingDiagnosticCodes.ModelTemplateIncomplete,
                "The filled template support has no finite centroid.");
        }

        double centroidColumn = moments.M10 / moments.M00;
        double centroidRow = moments.M01 / moments.M00;
        HalconFilledSupportRegion support = EncodeSupport(
            filledSupport,
            geometry.ReferenceColumn,
            geometry.ReferenceRow);
        int minimumValidGroups = Math.Max(2, (int)Math.Ceiling(innerGroups.Count * 0.67));
        var features = new HalconTemplateFeatureSet(
            crop.Width,
            crop.Height,
            geometry.ReferenceRow,
            geometry.ReferenceColumn,
            centroidRow,
            centroidColumn,
            isDarkForeground: true,
            outerContour,
            innerGroups,
            minimumValidGroups,
            support);
        return HalconTemplateFeatureExtractionResult.FromFeatures(features);
    }

    private static bool TryValidateFrame(ImageFrame? frame, out string failure)
    {
        failure = string.Empty;
        if (frame is null)
        {
            failure = "Template image frame is required.";
            return false;
        }

        int bytesPerPixel = frame.Format switch
        {
            PixelFormatKind.Gray8 => 1,
            PixelFormatKind.Bgr24 => 3,
            PixelFormatKind.Bgra32 => 4,
            _ => 0
        };
        long minimumStride = (long)frame.Width * bytesPerPixel;
        long requiredBytes = (long)frame.Stride * frame.Height;
        if (frame.Width <= 0 ||
            frame.Height <= 0 ||
            bytesPerPixel == 0 ||
            frame.Stride < minimumStride ||
            frame.Pixels is null ||
            requiredBytes < 0 ||
            frame.Pixels.LongLength < requiredBytes)
        {
            failure = "Template image dimensions, stride, format or pixel buffer are invalid.";
            return false;
        }

        return true;
    }

    private static bool TryCreateRoiGeometry(
        RoiDefinition? roi,
        int imageWidth,
        int imageHeight,
        out RoiGeometry geometry,
        out string failure)
    {
        geometry = default;
        failure = string.Empty;
        if (roi is null)
        {
            failure = "Template ROI is required.";
            return false;
        }

        IReadOnlyList<Point2D> boundary;
        switch (roi.Shape)
        {
            case RoiShapeKind.Rectangle:
                if (!IsPositiveFinite(roi.Width) ||
                    !IsPositiveFinite(roi.Height) ||
                    !double.IsFinite(roi.X) ||
                    !double.IsFinite(roi.Y))
                {
                    failure = "Rectangle template ROI requires finite position and positive dimensions.";
                    return false;
                }

                boundary =
                [
                    new Point2D(roi.X, roi.Y),
                    new Point2D(roi.X + roi.Width, roi.Y),
                    new Point2D(roi.X + roi.Width, roi.Y + roi.Height),
                    new Point2D(roi.X, roi.Y + roi.Height)
                ];
                break;
            case RoiShapeKind.RotatedRectangle:
                if (!IsPositiveFinite(roi.Width) ||
                    !IsPositiveFinite(roi.Height) ||
                    !double.IsFinite(roi.X) ||
                    !double.IsFinite(roi.Y) ||
                    !double.IsFinite(roi.Angle))
                {
                    failure = "Rotated template ROI requires finite center/angle and positive dimensions.";
                    return false;
                }

                boundary = RotatedRectangleCorners(roi);
                break;
            case RoiShapeKind.Circle:
                if (!IsPositiveFinite(roi.Radius) ||
                    !double.IsFinite(roi.X) ||
                    !double.IsFinite(roi.Y))
                {
                    failure = "Circle template ROI requires a finite center and positive radius.";
                    return false;
                }

                boundary =
                [
                    new Point2D(roi.X - roi.Radius, roi.Y - roi.Radius),
                    new Point2D(roi.X + roi.Radius, roi.Y - roi.Radius),
                    new Point2D(roi.X + roi.Radius, roi.Y + roi.Radius),
                    new Point2D(roi.X - roi.Radius, roi.Y + roi.Radius)
                ];
                break;
            case RoiShapeKind.Polygon:
                if (roi.Points is null ||
                    roi.Points.Count < 3 ||
                    roi.Points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                {
                    failure = "Polygon template ROI requires at least three finite points.";
                    return false;
                }

                boundary = roi.Points.ToArray();
                break;
            default:
                failure = $"Unsupported template ROI shape '{roi.Shape}'.";
                return false;
        }

        double minimumX = boundary.Min(point => point.X);
        double minimumY = boundary.Min(point => point.Y);
        double maximumX = boundary.Max(point => point.X);
        double maximumY = boundary.Max(point => point.Y);
        var reference = new Point2D(
            (minimumX + maximumX) / 2.0,
            (minimumY + maximumY) / 2.0);
        if (minimumX < 0 || minimumY < 0 || maximumX > imageWidth || maximumY > imageHeight)
        {
            failure = "The complete template ROI must be inside the source image; clamping is not allowed.";
            return false;
        }

        int left = (int)Math.Floor(minimumX);
        int top = (int)Math.Floor(minimumY);
        int right = (int)Math.Ceiling(maximumX);
        int bottom = (int)Math.Ceiling(maximumY);
        if (left < 0 || top < 0 || right > imageWidth || bottom > imageHeight || right <= left || bottom <= top)
        {
            failure = "Template ROI bounds do not define a non-empty image crop.";
            return false;
        }

        geometry = new RoiGeometry(
            new Rect(left, top, right - left, bottom - top),
            reference.Y - top,
            reference.X - left);
        if (geometry.ReferenceColumn < 0 ||
            geometry.ReferenceRow < 0 ||
            geometry.ReferenceColumn > geometry.CropBounds.Width - 1 ||
            geometry.ReferenceRow > geometry.CropBounds.Height - 1)
        {
            failure = "Template ROI reference center must lie inside its AABB crop.";
            return false;
        }

        return true;
    }

    private static Mat CreateRoiMask(RoiDefinition roi, RoiGeometry geometry)
    {
        var mask = new Mat(geometry.CropBounds.Height, geometry.CropBounds.Width, MatType.CV_8UC1, Scalar.Black);
        try
        {
            switch (roi.Shape)
            {
                case RoiShapeKind.Rectangle:
                    mask.SetTo(Scalar.White);
                    break;
                case RoiShapeKind.RotatedRectangle:
                    Cv2.FillConvexPoly(
                        mask,
                        RotatedRectangleCorners(roi)
                            .Select(point => RelativePixel(point, geometry.CropBounds))
                            .ToArray(),
                        Scalar.White,
                        LineTypes.Link8);
                    break;
                case RoiShapeKind.Circle:
                    Cv2.Circle(
                        mask,
                        new Point(
                            (int)Math.Round(roi.X - geometry.CropBounds.X),
                            (int)Math.Round(roi.Y - geometry.CropBounds.Y)),
                        (int)Math.Floor(roi.Radius),
                        Scalar.White,
                        -1,
                        LineTypes.Link8);
                    break;
                case RoiShapeKind.Polygon:
                    Cv2.FillPoly(
                        mask,
                        [roi.Points.Select(point => RelativePixel(point, geometry.CropBounds)).ToArray()],
                        Scalar.White,
                        LineTypes.Link8);
                    break;
            }

            return mask;
        }
        catch
        {
            mask.Dispose();
            throw;
        }
    }

    private static Mat CreateRoiBoundary(Mat roiMask)
    {
        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        using var eroded = new Mat();
        Cv2.Erode(
            roiMask,
            eroded,
            kernel,
            iterations: 1,
            borderType: BorderTypes.Constant,
            borderValue: Scalar.Black);
        var boundary = new Mat();
        try
        {
            Cv2.Subtract(roiMask, eroded, boundary);
            return boundary;
        }
        catch
        {
            boundary.Dispose();
            throw;
        }
    }

    private static Mat? SelectCenterConnectedRegion(Mat binary, double referenceColumn, double referenceRow)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int labelCount = Cv2.ConnectedComponentsWithStats(
            binary,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity8,
            MatType.CV_32SC1);
        if (labelCount <= 1)
        {
            return null;
        }

        var centerCounts = new Dictionary<int, int>();
        int centerX = (int)Math.Round(referenceColumn);
        int centerY = (int)Math.Round(referenceRow);
        const int radius = 6;
        for (var y = Math.Max(0, centerY - radius); y <= Math.Min(binary.Height - 1, centerY + radius); y++)
        {
            for (var x = Math.Max(0, centerX - radius); x <= Math.Min(binary.Width - 1, centerX + radius); x++)
            {
                if ((x - centerX) * (x - centerX) + (y - centerY) * (y - centerY) > radius * radius)
                {
                    continue;
                }

                int label = labels.At<int>(y, x);
                if (label > 0)
                {
                    centerCounts[label] = centerCounts.GetValueOrDefault(label) + 1;
                }
            }
        }

        if (centerCounts.Count == 0)
        {
            return null;
        }

        int selectedLabel = centerCounts
            .OrderByDescending(pair => pair.Value)
            .ThenByDescending(pair => stats.At<int>(pair.Key, (int)ConnectedComponentsTypes.Area))
            .ThenBy(pair => pair.Key)
            .First()
            .Key;
        var selected = new Mat();
        try
        {
            Cv2.Compare(labels, new Scalar(selectedLabel), selected, CmpTypes.EQ);
            return selected;
        }
        catch
        {
            selected.Dispose();
            throw;
        }
    }

    private static bool TryFindOuterContour(Mat mainRegion, out Point[] outer)
    {
        using Mat contourInput = mainRegion.Clone();
        Cv2.FindContours(
            contourInput,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxNone);
        Point[]? selected = contours
            .Where(contour => contour.Length >= MinimumOuterRawPoints)
            .OrderByDescending(contour => Cv2.ContourArea(contour))
            .ThenBy(contour => contour.Min(point => point.Y))
            .ThenBy(contour => contour.Min(point => point.X))
            .FirstOrDefault();
        outer = selected ?? [];
        return selected is not null;
    }

    private static IReadOnlyList<Point2D> ResampleOuterContour(
        IReadOnlyList<Point> pixels,
        double referenceColumn,
        double referenceRow)
    {
        var points = pixels
            .Select(point => new Point2D(point.X - referenceColumn, point.Y - referenceRow))
            .ToList();
        if (SignedArea(points) < 0)
        {
            points.Reverse();
        }

        int startIndex = Enumerable.Range(0, points.Count)
            .OrderBy(index => points[index].Y)
            .ThenBy(index => points[index].X)
            .ThenBy(index => index)
            .First();
        points = points.Skip(startIndex).Concat(points.Take(startIndex)).ToList();

        var segmentLengths = new double[points.Count];
        double perimeter = 0;
        for (var index = 0; index < points.Count; index++)
        {
            Point2D start = points[index];
            Point2D end = points[(index + 1) % points.Count];
            double length = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
            segmentLengths[index] = length;
            perimeter += length;
        }

        int sampleCount = Math.Clamp(
            Math.Max(MinimumOuterSampleCount, (int)Math.Ceiling(perimeter)),
            MinimumOuterSampleCount,
            4096);
        var sampled = new Point2D[sampleCount];
        var segmentIndex = 0;
        var accumulated = 0.0;
        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            double target = sampleIndex * perimeter / sampleCount;
            while (segmentIndex < segmentLengths.Length - 1 &&
                   accumulated + segmentLengths[segmentIndex] < target)
            {
                accumulated += segmentLengths[segmentIndex];
                segmentIndex++;
            }

            Point2D start = points[segmentIndex];
            Point2D end = points[(segmentIndex + 1) % points.Count];
            double segmentLength = segmentLengths[segmentIndex];
            double fraction = segmentLength <= double.Epsilon
                ? 0
                : (target - accumulated) / segmentLength;
            sampled[sampleIndex] = new Point2D(
                start.X + (end.X - start.X) * fraction,
                start.Y + (end.Y - start.Y) * fraction);
        }

        return new ReadOnlyCollection<Point2D>(sampled);
    }

    private static Mat CreateFilledSupport(Size size, Point[] outerPixels, Mat roiMask)
    {
        var support = new Mat(size, MatType.CV_8UC1, Scalar.Black);
        try
        {
            Cv2.FillPoly(support, [outerPixels], Scalar.White, LineTypes.Link8);
            Cv2.BitwiseAnd(support, roiMask, support);
            return support;
        }
        catch
        {
            support.Dispose();
            throw;
        }
    }

    private static IReadOnlyList<IReadOnlyList<Point2D>> ExtractInnerGroups(
        Mat gray,
        Mat filledSupport,
        double referenceColumn,
        double referenceRow,
        CancellationToken cancellationToken)
    {
        using Mat innerKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(11, 11));
        using var innerSupport = new Mat();
        Cv2.Erode(filledSupport, innerSupport, innerKernel);
        if (Cv2.CountNonZero(innerSupport) == 0)
        {
            return Array.Empty<IReadOnlyList<Point2D>>();
        }

        using var smoothed = new Mat();
        Cv2.GaussianBlur(gray, smoothed, new Size(3, 3), 0.8, 0.8, BorderTypes.Reflect101);
        using var gradientX = new Mat();
        using var gradientY = new Mat();
        Cv2.Sobel(smoothed, gradientX, MatType.CV_32F, 1, 0, 3);
        Cv2.Sobel(smoothed, gradientY, MatType.CV_32F, 0, 1, 3);
        using var magnitude = new Mat();
        Cv2.Magnitude(gradientX, gradientY, magnitude);
        Cv2.MinMaxLoc(magnitude, out _, out double maximumMagnitude, out _, out _, innerSupport);
        double threshold = Math.Max(20, maximumMagnitude * 0.18);
        using var nms = new Mat(gray.Size(), MatType.CV_8UC1, Scalar.Black);
        for (var y = 1; y < gray.Height - 1; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 1; x < gray.Width - 1; x++)
            {
                if (innerSupport.At<byte>(y, x) == 0)
                {
                    continue;
                }

                float center = magnitude.At<float>(y, x);
                if (!float.IsFinite(center) || center < threshold)
                {
                    continue;
                }

                float gx = gradientX.At<float>(y, x);
                float gy = gradientY.At<float>(y, x);
                double length = Math.Sqrt(gx * gx + gy * gy);
                if (length <= 1e-6)
                {
                    continue;
                }

                double normalX = gx / length;
                double normalY = gy / length;
                double before = SampleMagnitude(magnitude, x - normalX, y - normalY);
                double after = SampleMagnitude(magnitude, x + normalX, y + normalY);
                if (center >= before && center >= after)
                {
                    nms.Set(y, x, (byte)255);
                }
            }
        }

        using Mat connectKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        using var connected = new Mat();
        Cv2.Dilate(nms, connected, connectKernel);
        Cv2.MorphologyEx(connected, connected, MorphTypes.Close, connectKernel);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int labelCount = Cv2.ConnectedComponentsWithStats(
            connected,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity8,
            MatType.CV_32SC1);
        var eligibleLabels = new bool[labelCount];
        for (var label = 1; label < labelCount; label++)
        {
            eligibleLabels[label] =
                stats.At<int>(label, (int)ConnectedComponentsTypes.Area) >= MinimumInnerGroupPointCount;
        }

        var pointsByLabel = new List<Point2D>?[labelCount];
        for (var y = 1; y < gray.Height - 1; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 1; x < gray.Width - 1; x++)
            {
                if (nms.At<byte>(y, x) == 0)
                {
                    continue;
                }

                int label = labels.At<int>(y, x);
                if (label <= 0 || !eligibleLabels[label])
                {
                    continue;
                }

                float center = magnitude.At<float>(y, x);
                float gx = gradientX.At<float>(y, x);
                float gy = gradientY.At<float>(y, x);
                double length = Math.Sqrt(gx * gx + gy * gy);
                if (length <= 1e-6)
                {
                    continue;
                }

                double normalX = gx / length;
                double normalY = gy / length;
                double before = SampleMagnitude(magnitude, x - normalX, y - normalY);
                double after = SampleMagnitude(magnitude, x + normalX, y + normalY);
                double denominator = before - 2 * center + after;
                double offset = Math.Abs(denominator) <= 1e-9
                    ? 0
                    : 0.5 * (before - after) / denominator;
                offset = double.IsFinite(offset) ? Math.Clamp(offset, -0.5, 0.5) : 0;
                List<Point2D> points = pointsByLabel[label] ??= new List<Point2D>();
                points.Add(new Point2D(
                    x + normalX * offset - referenceColumn,
                    y + normalY * offset - referenceRow));
            }
        }

        var groups = new List<InnerGroup>();
        for (var label = 1; label < labelCount; label++)
        {
            List<Point2D>? points = pointsByLabel[label];
            if (points is null || points.Count < MinimumInnerGroupPointCount)
            {
                continue;
            }

            double centroidX = points.Average(point => point.X);
            double centroidY = points.Average(point => point.Y);
            groups.Add(new InnerGroup(label, centroidX, centroidY, points));
        }

        IReadOnlyList<Point2D>[] ordered = groups
            .OrderBy(group => group.CentroidY)
            .ThenBy(group => group.CentroidX)
            .ThenBy(group => group.Label)
            .Select(group => (IReadOnlyList<Point2D>)new ReadOnlyCollection<Point2D>(group.Points.ToArray()))
            .ToArray();
        return new ReadOnlyCollection<IReadOnlyList<Point2D>>(ordered);
    }

    private static bool HasDistributedGroups(
        IReadOnlyList<IReadOnlyList<Point2D>> groups,
        int templateWidth,
        int templateHeight)
    {
        if (groups.Count < 3 || templateWidth <= 0 || templateHeight <= 0)
        {
            return false;
        }

        Point2D[] centroids = groups
            .Select(group => new Point2D(
                group.Average(point => point.X),
                group.Average(point => point.Y)))
            .ToArray();
        var occupiedBins = new HashSet<(int Column, int Row)>();
        foreach (Point2D centroid in centroids)
        {
            occupiedBins.Add((
                GetSpatialBin(centroid.X, templateWidth),
                GetSpatialBin(centroid.Y, templateHeight)));
        }

        double maximumDistance = 0;
        for (var first = 0; first < centroids.Length; first++)
        {
            for (var second = first + 1; second < centroids.Length; second++)
            {
                maximumDistance = Math.Max(
                    maximumDistance,
                    Math.Sqrt(
                        Math.Pow(centroids[first].X - centroids[second].X, 2) +
                        Math.Pow(centroids[first].Y - centroids[second].Y, 2)));
            }
        }

        return occupiedBins.Count >= 3 &&
               maximumDistance >= Math.Max(8, Math.Min(templateWidth, templateHeight) * 0.1);
    }

    private static int GetSpatialBin(double coordinateFromAabbCenter, int extent)
    {
        double middleBandHalfExtent = extent / 6.0;
        if (coordinateFromAabbCenter < -middleBandHalfExtent)
        {
            return 0;
        }

        return coordinateFromAabbCenter > middleBandHalfExtent ? 2 : 1;
    }

    private static HalconFilledSupportRegion EncodeSupport(
        Mat support,
        double referenceColumn,
        double referenceRow)
    {
        var runs = new List<HalconSupportRun>();
        for (var row = 0; row < support.Height; row++)
        {
            var column = 0;
            while (column < support.Width)
            {
                while (column < support.Width && support.At<byte>(row, column) == 0)
                {
                    column++;
                }

                int start = column;
                while (column < support.Width && support.At<byte>(row, column) != 0)
                {
                    column++;
                }

                if (column > start)
                {
                    runs.Add(new HalconSupportRun(row, start, column - start));
                }
            }
        }

        return new HalconFilledSupportRegion(-referenceColumn, -referenceRow, runs);
    }

    private static MaskedGray8Statistics CalculateMaskedStatistics(
        Mat image,
        Mat boundaryMask,
        Mat roiMask,
        CancellationToken cancellationToken)
    {
        var boundaryHistogram = new Gray8Histogram();
        var roiHistogram = new Gray8Histogram();
        for (var row = 0; row < image.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var column = 0; column < image.Width; column++)
            {
                byte value = image.At<byte>(row, column);
                if (boundaryMask.At<byte>(row, column) != 0)
                {
                    boundaryHistogram.Add(value);
                }

                if (roiMask.At<byte>(row, column) != 0)
                {
                    roiHistogram.Add(value);
                }
            }
        }

        return new MaskedGray8Statistics(
            boundaryHistogram.Median(),
            roiHistogram.NearestRankPercentile(0.15));
    }

    private static double SampleMagnitude(Mat magnitude, double x, double y)
    {
        if (x < 0 || y < 0 || x > magnitude.Width - 1 || y > magnitude.Height - 1)
        {
            return 0;
        }

        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        int x1 = Math.Min(x0 + 1, magnitude.Width - 1);
        int y1 = Math.Min(y0 + 1, magnitude.Height - 1);
        double fx = x - x0;
        double fy = y - y0;
        double top = magnitude.At<float>(y0, x0) * (1 - fx) + magnitude.At<float>(y0, x1) * fx;
        double bottom = magnitude.At<float>(y1, x0) * (1 - fx) + magnitude.At<float>(y1, x1) * fx;
        return top * (1 - fy) + bottom * fy;
    }

    private static IReadOnlyList<Point2D> RotatedRectangleCorners(RoiDefinition roi)
    {
        double radians = roi.Angle * Math.PI / 180.0;
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        double halfWidth = roi.Width / 2.0;
        double halfHeight = roi.Height / 2.0;
        return new[]
        {
            RotateAroundCenter(-halfWidth, -halfHeight),
            RotateAroundCenter(halfWidth, -halfHeight),
            RotateAroundCenter(halfWidth, halfHeight),
            RotateAroundCenter(-halfWidth, halfHeight)
        };

        Point2D RotateAroundCenter(double localX, double localY)
        {
            return new Point2D(
                roi.X + localX * cosine - localY * sine,
                roi.Y + localX * sine + localY * cosine);
        }
    }

    private static Point RelativePixel(Point2D point, Rect bounds)
    {
        return new Point(
            (int)Math.Round(point.X - bounds.X),
            (int)Math.Round(point.Y - bounds.Y));
    }

    private static double SignedArea(IReadOnlyList<Point2D> points)
    {
        double area = 0;
        for (var index = 0; index < points.Count; index++)
        {
            Point2D current = points[index];
            Point2D next = points[(index + 1) % points.Count];
            area += current.X * next.Y - next.X * current.Y;
        }

        return area / 2.0;
    }

    private static bool IsPositiveFinite(double value)
    {
        return double.IsFinite(value) && value > 0;
    }

    private static HalconTemplateFeatureExtractionResult ConfigurationFailure(string details)
    {
        return Failure(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, details);
    }

    private static HalconTemplateFeatureExtractionResult Failure(string code, string details)
    {
        return HalconTemplateFeatureExtractionResult.FromDiagnostic(
            TemplateMatchingDiagnostics.Create(code, details));
    }

    private readonly record struct RoiGeometry(
        Rect CropBounds,
        double ReferenceRow,
        double ReferenceColumn);

    private readonly record struct MaskedGray8Statistics(
        double BoundaryMedian,
        double RoiDarkPercentile);

    private sealed record InnerGroup(
        int Label,
        double CentroidX,
        double CentroidY,
        IReadOnlyList<Point2D> Points);
}
