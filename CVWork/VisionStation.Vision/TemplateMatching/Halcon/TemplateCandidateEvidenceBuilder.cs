using System.Collections.ObjectModel;
using OpenCvSharp;
using VisionStation.Domain;

namespace VisionStation.Vision;

internal sealed class TemplateCandidate
{
    public TemplateCandidate(int sourceIndex, Pose2D pose, double score)
    {
        ArgumentNullException.ThrowIfNull(pose);
        SourceIndex = sourceIndex;
        Pose = new Pose2D(pose.X, pose.Y, pose.Angle) { Scale = pose.Scale };
        Score = score;
    }

    public int SourceIndex { get; }

    public Pose2D Pose { get; }

    public double Score { get; }
}

internal sealed class FilledSupportMask
{
    private readonly byte[] _pixels;

    public FilledSupportMask(int x, int y, int width, int height, IReadOnlyList<byte> pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width < 0 || height < 0 ||
            (width == 0) != (height == 0) ||
            pixels.Count != checked(width * height))
        {
            throw new ArgumentException("Support-mask dimensions and pixels are inconsistent.", nameof(pixels));
        }

        byte[] snapshot = pixels.ToArray();
        if (snapshot.Any(value => value is not 0 and not 1))
        {
            throw new ArgumentException("Support-mask pixels must be binary 0/1 values.", nameof(pixels));
        }

        if (snapshot.Length == 0 || !snapshot.Any(value => value != 0))
        {
            X = 0;
            Y = 0;
            Width = 0;
            Height = 0;
            _pixels = [];
            Pixels = Array.AsReadOnly(_pixels);
            Area = 0;
            return;
        }

        int minimumX = width;
        int minimumY = height;
        int maximumX = -1;
        int maximumY = -1;
        var area = 0;
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                if (snapshot[row * width + column] == 0)
                {
                    continue;
                }

                area++;
                minimumX = Math.Min(minimumX, column);
                minimumY = Math.Min(minimumY, row);
                maximumX = Math.Max(maximumX, column);
                maximumY = Math.Max(maximumY, row);
            }
        }

        int tightWidth = maximumX - minimumX + 1;
        int tightHeight = maximumY - minimumY + 1;
        _pixels = new byte[tightWidth * tightHeight];
        for (var row = 0; row < tightHeight; row++)
        {
            Array.Copy(
                snapshot,
                (minimumY + row) * width + minimumX,
                _pixels,
                row * tightWidth,
                tightWidth);
        }

        X = checked(x + minimumX);
        Y = checked(y + minimumY);
        Width = tightWidth;
        Height = tightHeight;
        Pixels = Array.AsReadOnly(_pixels);
        Area = area;
    }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    public IReadOnlyList<byte> Pixels { get; }

    public int Area { get; }

    public static double ComputeIoU(
        FilledSupportMask first,
        FilledSupportMask second,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        cancellationToken.ThrowIfCancellationRequested();
        if (first.Area == 0 && second.Area == 0)
        {
            return 0;
        }

        int left = Math.Max(first.X, second.X);
        int top = Math.Max(first.Y, second.Y);
        int right = Math.Min(first.X + first.Width, second.X + second.Width);
        int bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        var intersection = 0;
        for (var y = top; y < bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = left; x < right; x++)
            {
                if (first.IsSet(x, y) && second.IsSet(x, y))
                {
                    intersection++;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        int union = first.Area + second.Area - intersection;
        return union <= 0 ? 0 : intersection / (double)union;
    }

    internal static FilledSupportMask Empty { get; } = new(0, 0, 0, 0, []);

    private bool IsSet(int x, int y)
    {
        if (x < X || y < Y || x >= X + Width || y >= Y + Height)
        {
            return false;
        }

        return _pixels[(y - Y) * Width + x - X] != 0;
    }
}

internal sealed class TemplateCandidateEvidence
{
    public TemplateCandidateEvidence(
        TemplateCandidate candidate,
        bool geometryUsable,
        bool originInsideSearchDomain,
        bool completeAtBoundary,
        IReadOnlyList<Point2D> templateRoiContour,
        IReadOnlyList<Point2D> outerContour,
        IReadOnlyList<IReadOnlyList<Point2D>> innerFeatureGroups,
        double outerCoverage,
        double innerCoverage,
        double edgeDistanceP95Px,
        double polarityAgreement,
        int validInnerGroupCount,
        FilledSupportMask supportMask)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(supportMask);
        Candidate = new TemplateCandidate(candidate.SourceIndex, candidate.Pose, candidate.Score);
        GeometryUsable = geometryUsable;
        OriginInsideSearchDomain = originInsideSearchDomain;
        CompleteAtBoundary = completeAtBoundary;
        TemplateRoiContour = HalconMetadataSnapshots.Points(templateRoiContour, nameof(templateRoiContour));
        OuterContour = HalconMetadataSnapshots.Points(outerContour, nameof(outerContour));
        InnerFeatureGroups = HalconMetadataSnapshots.Groups(innerFeatureGroups, nameof(innerFeatureGroups));
        OuterCoverage = outerCoverage;
        InnerCoverage = innerCoverage;
        EdgeDistanceP95Px = edgeDistanceP95Px;
        PolarityAgreement = polarityAgreement;
        ValidInnerGroupCount = Math.Max(0, validInnerGroupCount);
        SupportMask = new FilledSupportMask(
            supportMask.X,
            supportMask.Y,
            supportMask.Width,
            supportMask.Height,
            supportMask.Pixels);
    }

    public TemplateCandidate Candidate { get; }

    public bool GeometryUsable { get; }

    public bool OriginInsideSearchDomain { get; }

    public bool CompleteAtBoundary { get; }

    public IReadOnlyList<Point2D> TemplateRoiContour { get; }

    public IReadOnlyList<Point2D> OuterContour { get; }

    public IReadOnlyList<IReadOnlyList<Point2D>> InnerFeatureGroups { get; }

    public double OuterCoverage { get; }

    public double InnerCoverage { get; }

    public double EdgeDistanceP95Px { get; }

    public double PolarityAgreement { get; }

    public int ValidInnerGroupCount { get; }

    public FilledSupportMask SupportMask { get; }

}

internal readonly record struct TemplateEdgeMetrics(double Coverage, double DistanceP95Px);

internal readonly record struct TemplateInnerEdgeMetrics(double Coverage, int ValidGroupCount);

internal interface ITemplateCandidateEvidenceBuilder
{
    IReadOnlyList<TemplateCandidateEvidence> BuildBatch(
        ImageFrame frame,
        RoiDefinition? searchRoi,
        HalconTemplateModelMetadata metadata,
        IReadOnlyList<TemplateCandidate> candidates,
        HalconTemplateMatchingParameters parameters,
        CancellationToken cancellationToken = default);
}

internal sealed class TemplateCandidateEvidenceBuilder : ITemplateCandidateEvidenceBuilder
{
    private static readonly Pose2D RelativeReferencePose = new(0, 0, 0) { Scale = 1 };

    public IReadOnlyList<TemplateCandidateEvidence> BuildBatch(
        ImageFrame frame,
        RoiDefinition? searchRoi,
        HalconTemplateModelMetadata metadata,
        IReadOnlyList<TemplateCandidate> candidates,
        HalconTemplateMatchingParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();
        using Mat gray = ImageFrameMatFactory.ToGrayMat(frame);
        using Mat edgeDistance = BuildEdgeDistanceMap(gray);
        var evidence = new TemplateCandidateEvidence[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateCandidate candidate = candidates[index] ?? throw new ArgumentException(
                "Candidate collections cannot contain null entries.",
                nameof(candidates));
            evidence[index] = BuildCandidate(
                gray,
                edgeDistance,
                frame.Width,
                frame.Height,
                searchRoi,
                metadata,
                candidate,
                parameters,
                cancellationToken);
        }

        return new ReadOnlyCollection<TemplateCandidateEvidence>(evidence);
    }

    internal static TemplateEdgeMetrics CalculateEdgeMetrics(
        IReadOnlyList<double> distances,
        double edgeTolerancePx)
    {
        ArgumentNullException.ThrowIfNull(distances);
        if (!double.IsFinite(edgeTolerancePx) || edgeTolerancePx < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(edgeTolerancePx));
        }

        if (distances.Count == 0)
        {
            return new TemplateEdgeMetrics(0, double.PositiveInfinity);
        }

        var supported = 0;
        var sorted = new double[distances.Count];
        for (var index = 0; index < distances.Count; index++)
        {
            double distance = distances[index];
            if (!double.IsFinite(distance) || distance < 0)
            {
                distance = double.PositiveInfinity;
            }

            sorted[index] = distance;
            if (distance <= edgeTolerancePx)
            {
                supported++;
            }
        }

        Array.Sort(sorted);
        int rank = (int)Math.Ceiling(0.95 * sorted.Length) - 1;
        return new TemplateEdgeMetrics(
            supported / (double)distances.Count,
            sorted[Math.Clamp(rank, 0, sorted.Length - 1)]);
    }

    internal static TemplateInnerEdgeMetrics CalculateInnerMetrics(
        IReadOnlyList<IReadOnlyList<double>> groupDistances,
        double edgeTolerancePx,
        double innerCoverageMin)
    {
        ArgumentNullException.ThrowIfNull(groupDistances);
        if (!double.IsFinite(innerCoverageMin) || innerCoverageMin < 0 || innerCoverageMin > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(innerCoverageMin));
        }

        var supported = 0;
        var total = 0;
        var validGroups = 0;
        foreach (IReadOnlyList<double> distances in groupDistances)
        {
            ArgumentNullException.ThrowIfNull(distances);
            TemplateEdgeMetrics group = CalculateEdgeMetrics(distances, edgeTolerancePx);
            total += distances.Count;
            supported += distances.Count(distance =>
                double.IsFinite(distance) && distance >= 0 && distance <= edgeTolerancePx);
            if (distances.Count > 0 && group.Coverage >= innerCoverageMin)
            {
                validGroups++;
            }
        }

        return new TemplateInnerEdgeMetrics(
            total == 0 ? 0 : supported / (double)total,
            validGroups);
    }

    private static TemplateCandidateEvidence BuildCandidate(
        Mat gray,
        Mat edgeDistance,
        int imageWidth,
        int imageHeight,
        RoiDefinition? searchRoi,
        HalconTemplateModelMetadata metadata,
        TemplateCandidate candidate,
        HalconTemplateMatchingParameters parameters,
        CancellationToken cancellationToken)
    {
        if (!HasUsableGeometry(candidate))
        {
            return EmptyEvidence(candidate);
        }

        Pose2D pose = candidate.Pose;
        IReadOnlyList<Point2D> templateRoi = TransformPoints(CreateTemplateCorners(metadata), pose);
        IReadOnlyList<Point2D> outer = TransformPoints(metadata.OuterContour, pose);
        IReadOnlyList<IReadOnlyList<Point2D>> inner = TransformGroups(metadata.InnerFeatureGroups, pose);
        var origin = new Point2D(pose.X, pose.Y);
        bool originInside = IsInsideDomain(origin, imageWidth, imageHeight, searchRoi, 0);
        int safetyMargin = (int)Math.Ceiling(parameters.EdgeTolerancePx);
        bool complete = templateRoi.All(point =>
                            IsInsideDomain(point, imageWidth, imageHeight, searchRoi, safetyMargin)) &&
                        outer.All(point =>
                            IsInsideDomain(point, imageWidth, imageHeight, searchRoi, safetyMargin));

        double[] outerDistances = outer
            .Select(point => SampleFloat(edgeDistance, point.X, point.Y))
            .ToArray();
        TemplateEdgeMetrics outerMetrics = CalculateEdgeMetrics(
            outerDistances,
            parameters.EdgeTolerancePx);
        IReadOnlyList<IReadOnlyList<double>> innerDistances = inner
            .Select(group => (IReadOnlyList<double>)group
                .Select(point => SampleFloat(edgeDistance, point.X, point.Y))
                .ToArray())
            .ToArray();
        TemplateInnerEdgeMetrics innerMetrics = CalculateInnerMetrics(
            innerDistances,
            parameters.EdgeTolerancePx,
            parameters.InnerCoverageMin);
        double polarity = CalculatePolarityAgreement(gray, outer, pose.Scale, metadata.IsDarkForeground);
        FilledSupportMask support = BuildSupportMask(
            metadata.FilledSupport,
            pose,
            imageWidth,
            imageHeight,
            cancellationToken);

        return new TemplateCandidateEvidence(
            candidate,
            geometryUsable: true,
            originInside,
            complete,
            templateRoi,
            outer,
            inner,
            outerMetrics.Coverage,
            innerMetrics.Coverage,
            outerMetrics.DistanceP95Px,
            polarity,
            innerMetrics.ValidGroupCount,
            support);
    }

    private static bool HasUsableGeometry(TemplateCandidate candidate)
    {
        return double.IsFinite(candidate.Pose.X) &&
               double.IsFinite(candidate.Pose.Y) &&
               double.IsFinite(candidate.Pose.Angle) &&
               double.IsFinite(candidate.Pose.Scale) &&
               candidate.Pose.Scale > 0 &&
               double.IsFinite(candidate.Score);
    }

    private static TemplateCandidateEvidence EmptyEvidence(TemplateCandidate candidate)
    {
        return new TemplateCandidateEvidence(
            candidate,
            geometryUsable: false,
            originInsideSearchDomain: false,
            completeAtBoundary: false,
            [],
            [],
            [],
            0,
            0,
            0,
            0,
            0,
            FilledSupportMask.Empty);
    }

    private static Mat BuildEdgeDistanceMap(Mat gray)
    {
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 50, 150, 3, true);
        using var distanceSeed = new Mat();
        Cv2.BitwiseNot(edges, distanceSeed);
        var distance = new Mat();
        try
        {
            Cv2.DistanceTransform(
                distanceSeed,
                distance,
                DistanceTypes.L2,
                DistanceTransformMasks.Precise);
            return distance;
        }
        catch
        {
            distance.Dispose();
            throw;
        }
    }

    private static IReadOnlyList<Point2D> CreateTemplateCorners(HalconTemplateModelMetadata metadata)
    {
        double left = -metadata.ReferenceColumn;
        double right = metadata.TemplateWidth - metadata.ReferenceColumn;
        double top = -metadata.ReferenceRow;
        double bottom = metadata.TemplateHeight - metadata.ReferenceRow;
        return
        [
            new Point2D(left, top),
            new Point2D(right, top),
            new Point2D(right, bottom),
            new Point2D(left, bottom)
        ];
    }

    private static IReadOnlyList<Point2D> TransformPoints(
        IReadOnlyList<Point2D> points,
        Pose2D pose)
    {
        Point2D[] transformed = points
            .Select(point => PoseSimilarityTransform.MapPoint(point, RelativeReferencePose, pose))
            .ToArray();
        return new ReadOnlyCollection<Point2D>(transformed);
    }

    private static IReadOnlyList<IReadOnlyList<Point2D>> TransformGroups(
        IReadOnlyList<IReadOnlyList<Point2D>> groups,
        Pose2D pose)
    {
        IReadOnlyList<Point2D>[] transformed = groups
            .Select(group => TransformPoints(group, pose))
            .ToArray();
        return new ReadOnlyCollection<IReadOnlyList<Point2D>>(transformed);
    }

    private static bool IsInsideDomain(
        Point2D point,
        int imageWidth,
        int imageHeight,
        RoiDefinition? searchRoi,
        double margin)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            !double.IsFinite(margin) || margin < 0 ||
            point.X < margin || point.Y < margin ||
            point.X > imageWidth - 1 - margin ||
            point.Y > imageHeight - 1 - margin)
        {
            return false;
        }

        if (searchRoi is null)
        {
            return true;
        }

        return searchRoi.Shape switch
        {
            RoiShapeKind.Rectangle => IsInsideRectangle(point, searchRoi, margin),
            RoiShapeKind.RotatedRectangle => IsInsideRotatedRectangle(point, searchRoi, margin),
            RoiShapeKind.Circle => IsInsideCircle(point, searchRoi, margin),
            RoiShapeKind.Polygon => IsInsidePolygon(point, searchRoi.Points, margin),
            _ => false
        };
    }

    private static bool IsInsideRectangle(Point2D point, RoiDefinition roi, double margin)
    {
        return IsPositiveFinite(roi.Width) &&
               IsPositiveFinite(roi.Height) &&
               double.IsFinite(roi.X) &&
               double.IsFinite(roi.Y) &&
               point.X >= roi.X + margin &&
               point.Y >= roi.Y + margin &&
               point.X <= roi.X + roi.Width - margin &&
               point.Y <= roi.Y + roi.Height - margin;
    }

    private static bool IsInsideRotatedRectangle(Point2D point, RoiDefinition roi, double margin)
    {
        if (!IsPositiveFinite(roi.Width) ||
            !IsPositiveFinite(roi.Height) ||
            !double.IsFinite(roi.X) ||
            !double.IsFinite(roi.Y) ||
            !double.IsFinite(roi.Angle) ||
            roi.Width / 2.0 < margin ||
            roi.Height / 2.0 < margin)
        {
            return false;
        }

        double radians = roi.Angle * Math.PI / 180.0;
        double dx = point.X - roi.X;
        double dy = point.Y - roi.Y;
        double localX = dx * Math.Cos(radians) + dy * Math.Sin(radians);
        double localY = -dx * Math.Sin(radians) + dy * Math.Cos(radians);
        return Math.Abs(localX) <= roi.Width / 2.0 - margin &&
               Math.Abs(localY) <= roi.Height / 2.0 - margin;
    }

    private static bool IsInsideCircle(Point2D point, RoiDefinition roi, double margin)
    {
        if (!IsPositiveFinite(roi.Radius) ||
            !double.IsFinite(roi.X) ||
            !double.IsFinite(roi.Y) ||
            roi.Radius < margin)
        {
            return false;
        }

        double dx = point.X - roi.X;
        double dy = point.Y - roi.Y;
        double radius = roi.Radius - margin;
        return dx * dx + dy * dy <= radius * radius;
    }

    private static bool IsInsidePolygon(
        Point2D point,
        IReadOnlyList<Point2D> polygon,
        double margin)
    {
        if (polygon is null || polygon.Count < 3 || polygon.Any(candidate =>
                !double.IsFinite(candidate.X) || !double.IsFinite(candidate.Y)))
        {
            return false;
        }

        var inside = false;
        for (var current = 0; current < polygon.Count; current++)
        {
            Point2D a = polygon[current];
            Point2D b = polygon[(current + 1) % polygon.Count];
            if (DistanceToSegment(point, a, b) + 1e-9 < margin)
            {
                return false;
            }

            bool crosses = (a.Y > point.Y) != (b.Y > point.Y) &&
                           point.X < (b.X - a.X) * (point.Y - a.Y) /
                               ((b.Y - a.Y) == 0 ? double.Epsilon : b.Y - a.Y) + a.X;
            if (crosses)
            {
                inside = !inside;
            }
        }

        if (inside)
        {
            return true;
        }

        return margin == 0 && polygon
            .Select((a, index) => DistanceToSegment(point, a, polygon[(index + 1) % polygon.Count]))
            .Any(distance => distance <= 1e-9);
    }

    private static double DistanceToSegment(Point2D point, Point2D start, Point2D end)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= double.Epsilon)
        {
            return Math.Sqrt(Math.Pow(point.X - start.X, 2) + Math.Pow(point.Y - start.Y, 2));
        }

        double t = Math.Clamp(
            ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared,
            0,
            1);
        double nearestX = start.X + t * dx;
        double nearestY = start.Y + t * dy;
        return Math.Sqrt(Math.Pow(point.X - nearestX, 2) + Math.Pow(point.Y - nearestY, 2));
    }

    private static double CalculatePolarityAgreement(
        Mat gray,
        IReadOnlyList<Point2D> outer,
        double scale,
        bool isDarkForeground)
    {
        if (!isDarkForeground || outer.Count < 3 || !double.IsFinite(scale) || scale <= 0)
        {
            return 0;
        }

        double signedArea = 0;
        for (var index = 0; index < outer.Count; index++)
        {
            Point2D current = outer[index];
            Point2D next = outer[(index + 1) % outer.Count];
            signedArea += current.X * next.Y - next.X * current.Y;
        }

        if (Math.Abs(signedArea) <= double.Epsilon)
        {
            return 0;
        }

        var agreed = 0;
        for (var index = 0; index < outer.Count; index++)
        {
            Point2D previous = outer[(index - 1 + outer.Count) % outer.Count];
            Point2D current = outer[index];
            Point2D next = outer[(index + 1) % outer.Count];
            double tangentX = next.X - previous.X;
            double tangentY = next.Y - previous.Y;
            double length = Math.Sqrt(tangentX * tangentX + tangentY * tangentY);
            if (length <= 1e-9)
            {
                continue;
            }

            double normalX = signedArea > 0 ? tangentY / length : -tangentY / length;
            double normalY = signedArea > 0 ? -tangentX / length : tangentX / length;
            var innerSum = 0.0;
            var outerSum = 0.0;
            var valid = true;
            for (var sampleIndex = 1; sampleIndex <= 3; sampleIndex++)
            {
                double distance = sampleIndex * scale;
                double inner = SampleByte(
                    gray,
                    current.X - normalX * distance,
                    current.Y - normalY * distance);
                double outside = SampleByte(
                    gray,
                    current.X + normalX * distance,
                    current.Y + normalY * distance);
                if (!double.IsFinite(inner) || !double.IsFinite(outside))
                {
                    valid = false;
                    break;
                }

                innerSum += inner;
                outerSum += outside;
            }

            if (valid && outerSum > innerSum)
            {
                agreed++;
            }
        }

        return agreed / (double)outer.Count;
    }

    private static FilledSupportMask BuildSupportMask(
        HalconFilledSupportRegion support,
        Pose2D pose,
        int imageWidth,
        int imageHeight,
        CancellationToken cancellationToken)
    {
        double minimumX = support.OriginX + support.MinimumColumn - 0.5;
        double maximumX = support.OriginX + support.MaximumColumn + 0.5;
        double minimumY = support.OriginY + support.MinimumRow - 0.5;
        double maximumY = support.OriginY + support.MaximumRow + 0.5;
        Point2D[] corners =
        [
            PoseSimilarityTransform.MapPoint(new Point2D(minimumX, minimumY), RelativeReferencePose, pose),
            PoseSimilarityTransform.MapPoint(new Point2D(maximumX, minimumY), RelativeReferencePose, pose),
            PoseSimilarityTransform.MapPoint(new Point2D(maximumX, maximumY), RelativeReferencePose, pose),
            PoseSimilarityTransform.MapPoint(new Point2D(minimumX, maximumY), RelativeReferencePose, pose)
        ];
        int left = Math.Max(0, (int)Math.Floor(corners.Min(point => point.X)));
        int top = Math.Max(0, (int)Math.Floor(corners.Min(point => point.Y)));
        int right = Math.Min(imageWidth - 1, (int)Math.Ceiling(corners.Max(point => point.X)));
        int bottom = Math.Min(imageHeight - 1, (int)Math.Ceiling(corners.Max(point => point.Y)));
        if (right < left || bottom < top)
        {
            return FilledSupportMask.Empty;
        }

        int width = right - left + 1;
        int height = bottom - top + 1;
        var pixels = new byte[width * height];
        double radians = pose.Angle * Math.PI / 180.0;
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        for (var row = 0; row < height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double globalY = top + row;
            for (var column = 0; column < width; column++)
            {
                double globalX = left + column;
                double dx = globalX - pose.X;
                double dy = globalY - pose.Y;
                double relativeX = (dx * cosine + dy * sine) / pose.Scale;
                double relativeY = (-dx * sine + dy * cosine) / pose.Scale;
                if (support.Contains(relativeX, relativeY))
                {
                    pixels[row * width + column] = 1;
                }
            }
        }

        return new FilledSupportMask(left, top, width, height, pixels);
    }

    private static double SampleFloat(Mat image, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            x < 0 || y < 0 || x > image.Width - 1 || y > image.Height - 1)
        {
            return double.PositiveInfinity;
        }

        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        int x1 = Math.Min(x0 + 1, image.Width - 1);
        int y1 = Math.Min(y0 + 1, image.Height - 1);
        double fx = x - x0;
        double fy = y - y0;
        double top = image.At<float>(y0, x0) * (1 - fx) + image.At<float>(y0, x1) * fx;
        double bottom = image.At<float>(y1, x0) * (1 - fx) + image.At<float>(y1, x1) * fx;
        return top * (1 - fy) + bottom * fy;
    }

    private static double SampleByte(Mat image, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            x < 0 || y < 0 || x > image.Width - 1 || y > image.Height - 1)
        {
            return double.PositiveInfinity;
        }

        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        int x1 = Math.Min(x0 + 1, image.Width - 1);
        int y1 = Math.Min(y0 + 1, image.Height - 1);
        double fx = x - x0;
        double fy = y - y0;
        double top = image.At<byte>(y0, x0) * (1 - fx) + image.At<byte>(y0, x1) * fx;
        double bottom = image.At<byte>(y1, x0) * (1 - fx) + image.At<byte>(y1, x1) * fx;
        return top * (1 - fy) + bottom * fy;
    }

    private static bool IsPositiveFinite(double value)
    {
        return double.IsFinite(value) && value > 0;
    }
}
