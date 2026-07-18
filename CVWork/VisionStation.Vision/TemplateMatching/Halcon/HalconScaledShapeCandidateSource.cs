using VisionStation.Domain;

namespace VisionStation.Vision;

internal sealed class HalconScaledShapeCandidateSource : IHalconCandidateSource
{
    private readonly IHalconOperatorBackend _operators;

    public HalconScaledShapeCandidateSource(IHalconOperatorBackend operators)
    {
        _operators = operators ?? throw new ArgumentNullException(nameof(operators));
    }

    public async Task<HalconCandidateBatch> FindAsync(
        IHalconModelOperation modelOperation,
        ImageFrame frame,
        RoiDefinition? searchRoi,
        HalconTemplateMatchingParameters parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(modelOperation);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();

        PreparedSearch search = PrepareSearch(frame, searchRoi);
        cancellationToken.ThrowIfCancellationRequested();
        var findRequest = new HalconShapeModelFindRequest(
            search.Image,
            search.Domain,
            parameters);
        HalconShapeModelFindResult nativeResult;
        try
        {
            // Timeout mutation and FindScaledShapeModel are one callback, therefore one model
            // gate admission and one scheduler work item. No other request can interleave them.
            nativeResult = await modelOperation.InvokeAsync(
                model => _operators.FindScaledShapeModel(model, findRequest),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            HalconExceptionClassifier.TryClassify(
                exception,
                "FindScaledShapeModel",
                out TemplateMatchingDiagnostic? diagnostic))
        {
            throw new HalconOperatorFailure(
                diagnostic!.Code,
                diagnostic.TechnicalDetails,
                exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (nativeResult is null)
        {
            throw new HalconOperatorFailure(
                TemplateMatchingDiagnosticCodes.MatchOperatorFailed,
                "HALCON FindScaledShapeModel returned no result object.");
        }

        var candidates = new TemplateCandidate[nativeResult.Candidates.Count];
        for (var index = 0; index < nativeResult.Candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HalconNativeCandidate native = nativeResult.Candidates[index];
            candidates[index] = new TemplateCandidate(
                index,
                HalconPoseConverter.ToPose(native, search.Region),
                native.Score);
        }

        return new HalconCandidateBatch(
            candidates,
            nativeResult.Candidates.Count >= parameters.CandidateLimit,
            search.Region);
    }

    internal static void ValidateInput(ImageFrame frame, RoiDefinition? searchRoi)
    {
        HalconImageFactory.ValidateFrameLayout(frame);
        ValidateSearchRoi(frame, searchRoi);
        TemplateSearchRegion region = TemplateMatcher.GetSearchRegion(frame, searchRoi);
        if (!ContainsDomainPixel(region, searchRoi))
        {
            throw new ArgumentException(
                "The HALCON search ROI contains no image pixels.",
                nameof(searchRoi));
        }
    }

    private static PreparedSearch PrepareSearch(ImageFrame frame, RoiDefinition? searchRoi)
    {
        ValidateInput(frame, searchRoi);
        TightGray8Image fullImage = HalconImageFactory.CreateTightGray8(frame);
        TemplateSearchRegion region = TemplateMatcher.GetSearchRegion(frame, searchRoi);
        TightGray8Image image = HalconImageFactory.Crop(
            fullImage,
            region.X,
            region.Y,
            region.Width,
            region.Height);
        HalconModelDomain domain = BuildLocalDomain(region, searchRoi);
        return new PreparedSearch(region, image, domain);
    }

    private static bool ContainsDomainPixel(
        TemplateSearchRegion region,
        RoiDefinition? searchRoi)
    {
        if (TryGetAxisAlignedLocalBounds(
                region,
                searchRoi,
                out int startRow,
                out int endRow,
                out int startColumn,
                out int endColumn))
        {
            return startRow < endRow && startColumn < endColumn;
        }

        RoiDefinition rasterRoi = searchRoi
            ?? throw new InvalidOperationException(
                "A null search ROI must use the axis-aligned full-image domain path.");
        for (var localRow = 0; localRow < region.Height; localRow++)
        {
            for (var localColumn = 0; localColumn < region.Width; localColumn++)
            {
                if (IsInside(
                        region.X + localColumn,
                        region.Y + localRow,
                        rasterRoi))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static HalconModelDomain BuildLocalDomain(
        TemplateSearchRegion region,
        RoiDefinition? searchRoi)
    {
        if (TryGetAxisAlignedLocalBounds(
                region,
                searchRoi,
                out int startRow,
                out int endRow,
                out int startColumn,
                out int endColumn))
        {
            if (startRow >= endRow || startColumn >= endColumn)
            {
                throw new ArgumentException(
                    "The HALCON search ROI contains no image pixels.",
                    nameof(searchRoi));
            }

            int runLength = endColumn - startColumn;
            var rectangularRuns = new HalconSupportRun[endRow - startRow];
            for (int row = startRow; row < endRow; row++)
            {
                rectangularRuns[row - startRow] = new HalconSupportRun(
                    row,
                    startColumn,
                    runLength);
            }

            return new HalconModelDomain(region.Width, region.Height, rectangularRuns);
        }

        var runs = new List<HalconSupportRun>(region.Height);
        for (var localRow = 0; localRow < region.Height; localRow++)
        {
            var runStart = -1;
            for (var localColumn = 0; localColumn < region.Width; localColumn++)
            {
                bool inside = searchRoi is null || IsInside(
                    region.X + localColumn,
                    region.Y + localRow,
                    searchRoi);
                if (inside && runStart < 0)
                {
                    runStart = localColumn;
                }

                bool closesRun = runStart >= 0 && (!inside || localColumn == region.Width - 1);
                if (!closesRun)
                {
                    continue;
                }

                int end = inside && localColumn == region.Width - 1
                    ? localColumn
                    : localColumn - 1;
                runs.Add(new HalconSupportRun(localRow, runStart, end - runStart + 1));
                runStart = -1;
            }
        }

        if (runs.Count == 0)
        {
            throw new ArgumentException("The HALCON search ROI contains no image pixels.", nameof(searchRoi));
        }

        return new HalconModelDomain(region.Width, region.Height, runs);
    }

    private static bool TryGetAxisAlignedLocalBounds(
        TemplateSearchRegion region,
        RoiDefinition? searchRoi,
        out int startRow,
        out int endRow,
        out int startColumn,
        out int endColumn)
    {
        if (searchRoi is null)
        {
            startRow = 0;
            endRow = region.Height;
            startColumn = 0;
            endColumn = region.Width;
            return true;
        }

        if (searchRoi.Shape != RoiShapeKind.Rectangle)
        {
            startRow = 0;
            endRow = 0;
            startColumn = 0;
            endColumn = 0;
            return false;
        }

        startRow = ClampCeiling(searchRoi.Y, region.Y, region.Bottom) - region.Y;
        endRow = ClampCeiling(
            searchRoi.Y + searchRoi.Height,
            region.Y,
            region.Bottom) - region.Y;
        startColumn = ClampCeiling(searchRoi.X, region.X, region.Right) - region.X;
        endColumn = ClampCeiling(
            searchRoi.X + searchRoi.Width,
            region.X,
            region.Right) - region.X;
        return true;
    }

    private static int ClampCeiling(double value, int minimum, int maximum)
    {
        return (int)Math.Clamp(Math.Ceiling(value), minimum, maximum);
    }

    private static void ValidateSearchRoi(ImageFrame frame, RoiDefinition? roi)
    {
        if (roi is null)
        {
            return;
        }

        (double left, double top, double right, double bottom) = roi.Shape switch
        {
            RoiShapeKind.Rectangle when IsPositiveFinite(roi.Width) && IsPositiveFinite(roi.Height) &&
                                             double.IsFinite(roi.X) && double.IsFinite(roi.Y) =>
                (roi.X, roi.Y, roi.X + roi.Width, roi.Y + roi.Height),
            RoiShapeKind.Circle when IsPositiveFinite(roi.Radius) &&
                                          double.IsFinite(roi.X) && double.IsFinite(roi.Y) =>
                (roi.X - roi.Radius, roi.Y - roi.Radius, roi.X + roi.Radius, roi.Y + roi.Radius),
            RoiShapeKind.RotatedRectangle when IsPositiveFinite(roi.Width) &&
                                                   IsPositiveFinite(roi.Height) &&
                                                   double.IsFinite(roi.X) &&
                                                   double.IsFinite(roi.Y) &&
                                                   double.IsFinite(roi.Angle) =>
                RotatedBounds(roi),
            RoiShapeKind.Polygon when IsUsablePolygon(roi.Points) =>
                (
                    roi.Points.Min(point => point.X),
                    roi.Points.Min(point => point.Y),
                    roi.Points.Max(point => point.X),
                    roi.Points.Max(point => point.Y)),
            _ => throw new ArgumentException("The HALCON search ROI is invalid.", nameof(roi))
        };

        if (!double.IsFinite(left) || !double.IsFinite(top) ||
            !double.IsFinite(right) || !double.IsFinite(bottom) ||
            right <= 0 || bottom <= 0 || left >= frame.Width || top >= frame.Height)
        {
            throw new ArgumentException("The HALCON search ROI does not intersect the image.", nameof(roi));
        }
    }

    private static bool IsInside(double x, double y, RoiDefinition roi)
    {
        return roi.Shape switch
        {
            RoiShapeKind.Rectangle =>
                x >= roi.X && y >= roi.Y && x < roi.X + roi.Width && y < roi.Y + roi.Height,
            RoiShapeKind.Circle =>
                Square(x - roi.X) + Square(y - roi.Y) <= Square(roi.Radius),
            RoiShapeKind.RotatedRectangle => IsInsideRotatedRectangle(x, y, roi),
            RoiShapeKind.Polygon => IsInsidePolygon(x, y, roi.Points),
            _ => false
        };
    }

    private static bool IsInsideRotatedRectangle(double x, double y, RoiDefinition roi)
    {
        double radians = roi.Angle * Math.PI / 180d;
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        double dx = x - roi.X;
        double dy = y - roi.Y;
        double localX = dx * cosine + dy * sine;
        double localY = -dx * sine + dy * cosine;
        return Math.Abs(localX) <= roi.Width / 2d && Math.Abs(localY) <= roi.Height / 2d;
    }

    private static bool IsInsidePolygon(
        double x,
        double y,
        IReadOnlyList<Point2D> points)
    {
        var inside = false;
        for (var current = 0; current < points.Count; current++)
        {
            Point2D first = points[current];
            Point2D second = points[(current + 1) % points.Count];
            if (IsOnSegment(x, y, first, second))
            {
                return true;
            }

            bool crosses = (first.Y > y) != (second.Y > y) &&
                           x < (second.X - first.X) * (y - first.Y) /
                           (second.Y - first.Y) + first.X;
            if (crosses)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool IsOnSegment(double x, double y, Point2D first, Point2D second)
    {
        double cross = (x - first.X) * (second.Y - first.Y) -
                       (y - first.Y) * (second.X - first.X);
        if (Math.Abs(cross) > 1e-9)
        {
            return false;
        }

        return x >= Math.Min(first.X, second.X) - 1e-9 &&
               x <= Math.Max(first.X, second.X) + 1e-9 &&
               y >= Math.Min(first.Y, second.Y) - 1e-9 &&
               y <= Math.Max(first.Y, second.Y) + 1e-9;
    }

    private static bool IsUsablePolygon(IReadOnlyList<Point2D>? points)
    {
        if (points is null || points.Count < 3 || points.Any(point =>
                !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
        {
            return false;
        }

        double twiceArea = 0;
        for (var index = 0; index < points.Count; index++)
        {
            Point2D first = points[index];
            Point2D second = points[(index + 1) % points.Count];
            twiceArea += first.X * second.Y - second.X * first.Y;
        }

        return double.IsFinite(twiceArea) && Math.Abs(twiceArea) > double.Epsilon;
    }

    private static (double Left, double Top, double Right, double Bottom) RotatedBounds(
        RoiDefinition roi)
    {
        double radians = roi.Angle * Math.PI / 180d;
        double width = Math.Abs(roi.Width * Math.Cos(radians)) +
                       Math.Abs(roi.Height * Math.Sin(radians));
        double height = Math.Abs(roi.Width * Math.Sin(radians)) +
                        Math.Abs(roi.Height * Math.Cos(radians));
        return (
            roi.X - width / 2d,
            roi.Y - height / 2d,
            roi.X + width / 2d,
            roi.Y + height / 2d);
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;

    private static double Square(double value) => value * value;

    private sealed record PreparedSearch(
        TemplateSearchRegion Region,
        TightGray8Image Image,
        HalconModelDomain Domain);
}
