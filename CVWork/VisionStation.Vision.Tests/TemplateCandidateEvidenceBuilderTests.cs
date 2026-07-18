using OpenCvSharp;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class TemplateCandidateEvidenceBuilderTests
{
    [Fact]
    public void EdgeMetricsUseEveryLearnedPointAsCoverageDenominator()
    {
        TemplateEdgeMetrics metrics = TemplateCandidateEvidenceBuilder.CalculateEdgeMetrics(
            [0, 1, 2, 100, double.PositiveInfinity],
            2);

        Assert.Equal(0.6, metrics.Coverage, 12);
        Assert.Equal(double.PositiveInfinity, metrics.DistanceP95Px);
    }

    [Fact]
    public void P95UsesNearestRankDefinition()
    {
        double[] distances = Enumerable.Range(1, 20).Select(value => (double)value).ToArray();

        TemplateEdgeMetrics metrics =
            TemplateCandidateEvidenceBuilder.CalculateEdgeMetrics(distances, 100);

        Assert.Equal(19, metrics.DistanceP95Px);
    }

    [Fact]
    public void InnerMetricsUseFullDenominatorAndCurrentGroupThreshold()
    {
        IReadOnlyList<IReadOnlyList<double>> distances =
        [
            new double[] { 0, 0, 10, 10 },
            new double[] { 0, 0, 0, 10 }
        ];

        TemplateInnerEdgeMetrics strict = TemplateCandidateEvidenceBuilder.CalculateInnerMetrics(
            distances,
            edgeTolerancePx: 1,
            innerCoverageMin: 0.75);
        TemplateInnerEdgeMetrics hotChanged = TemplateCandidateEvidenceBuilder.CalculateInnerMetrics(
            distances,
            edgeTolerancePx: 1,
            innerCoverageMin: 0.5);

        Assert.Equal(0.625, strict.Coverage, 12);
        Assert.Equal(1, strict.ValidGroupCount);
        Assert.Equal(2, hotChanged.ValidGroupCount);
    }

    [Fact]
    public void CandidateEvidencePreservesNonFiniteMetricsForFailClosedValidation()
    {
        var evidence = new TemplateCandidateEvidence(
            new TemplateCandidate(3, new Pose2D(60, 50, 0), 0.9),
            geometryUsable: true,
            originInsideSearchDomain: true,
            completeAtBoundary: true,
            [],
            [],
            [],
            double.NaN,
            double.PositiveInfinity,
            double.NegativeInfinity,
            double.NegativeInfinity,
            0,
            FilledSupportMask.Empty);

        Assert.True(double.IsNaN(evidence.OuterCoverage));
        Assert.Equal(double.PositiveInfinity, evidence.InnerCoverage);
        Assert.Equal(double.NegativeInfinity, evidence.EdgeDistanceP95Px);
        Assert.Equal(double.NegativeInfinity, evidence.PolarityAgreement);
    }

    [Fact]
    public void BuildBatchTreatsCandidatePoseAsGlobalWithoutAddingSearchOffsetAgain()
    {
        ImageFrame frame = RuntimeFrame();
        RoiDefinition searchRoi = new()
        {
            Shape = RoiShapeKind.Rectangle,
            X = 30,
            Y = 20,
            Width = 60,
            Height = 60
        };
        var candidate = new TemplateCandidate(7, new Pose2D(60, 50, 0), 0.93);

        TemplateCandidateEvidence evidence = Assert.Single(
            new TemplateCandidateEvidenceBuilder().BuildBatch(
                frame,
                searchRoi,
                Metadata(),
                [candidate],
                Parameters()));

        Assert.True(evidence.GeometryUsable);
        Assert.True(evidence.OriginInsideSearchDomain);
        Assert.True(evidence.CompleteAtBoundary);
        Assert.InRange(evidence.OuterContour.Min(point => point.X), 39.5, 40.5);
        Assert.InRange(evidence.OuterContour.Max(point => point.X), 79.5, 80.5);
        Assert.Equal(60, evidence.Candidate.Pose.X);
        Assert.Equal(50, evidence.Candidate.Pose.Y);
        Assert.Equal(0.93, evidence.Candidate.Score);
    }

    [Fact]
    public void BuildBatchUsesFractionalOffCenterMetadataReferenceForGlobalTemplateCorners()
    {
        ImageFrame frame = RuntimeFrame();
        RoiDefinition searchRoi = new()
        {
            Shape = RoiShapeKind.Rectangle,
            X = 30,
            Y = 20,
            Width = 90,
            Height = 80
        };
        HalconTemplateModelMetadata metadata = Metadata(
            templateWidth: 42,
            templateHeight: 30,
            referenceRow: 7.5,
            referenceColumn: 12.25);
        var candidate = new TemplateCandidate(8, new Pose2D(70.5, 60.25, 0), 0.94);

        TemplateCandidateEvidence evidence = Assert.Single(
            new TemplateCandidateEvidenceBuilder().BuildBatch(
                frame,
                searchRoi,
                metadata,
                [candidate],
                Parameters()));

        Assert.Collection(
            evidence.TemplateRoiContour,
            point => AssertPoint(point, 58.25, 52.75),
            point => AssertPoint(point, 100.25, 52.75),
            point => AssertPoint(point, 100.25, 82.75),
            point => AssertPoint(point, 58.25, 82.75));
        Assert.Equal(70.5, evidence.Candidate.Pose.X, 12);
        Assert.Equal(60.25, evidence.Candidate.Pose.Y, 12);
    }

    [Fact]
    public void BuildBatchProducesPolarityMetricsAndTightFilledSupportMask()
    {
        TemplateCandidateEvidence evidence = Assert.Single(
            new TemplateCandidateEvidenceBuilder().BuildBatch(
                RuntimeFrame(),
                null,
                Metadata(),
                [new TemplateCandidate(0, new Pose2D(60, 50, 0), 0.9)],
                Parameters()));

        Assert.True(evidence.PolarityAgreement > 0.8, $"Polarity={evidence.PolarityAgreement:R}");
        Assert.True(evidence.OuterCoverage > 0.85, $"OuterCoverage={evidence.OuterCoverage:R}");
        Assert.InRange(evidence.EdgeDistanceP95Px, 0, Parameters().EdgeTolerancePx);
        Assert.True(evidence.SupportMask.Area > 0);
        Assert.InRange(evidence.SupportMask.X, 40, 42);
        Assert.InRange(evidence.SupportMask.Y, 30, 32);
        Assert.InRange(evidence.SupportMask.Width, 36, 40);
        Assert.InRange(evidence.SupportMask.Height, 36, 40);
        Assert.Equal(
            evidence.InnerFeatureGroups.Sum(group => group.Count),
            Metadata().InnerFeatureGroups.Sum(group => group.Count));
    }

    [Fact]
    public void ActualCircleSearchDomainRejectsOriginThatOnlyItsAabbContains()
    {
        RoiDefinition searchRoi = new()
        {
            Shape = RoiShapeKind.Circle,
            X = 60,
            Y = 50,
            Radius = 25
        };

        TemplateCandidateEvidence evidence = Assert.Single(
            new TemplateCandidateEvidenceBuilder().BuildBatch(
                RuntimeFrame(),
                searchRoi,
                Metadata(),
                [new TemplateCandidate(0, new Pose2D(82, 68, 0), 0.9)],
                Parameters()));

        Assert.False(evidence.OriginInsideSearchDomain);
    }

    [Fact]
    public void NonFiniteCandidateReturnsEmptyUnusableEvidenceInsteadOfThrowing()
    {
        var candidate = new TemplateCandidate(
            4,
            new Pose2D(double.NaN, 50, 0) { Scale = 1 },
            0.9);

        TemplateCandidateEvidence evidence = Assert.Single(
            new TemplateCandidateEvidenceBuilder().BuildBatch(
                RuntimeFrame(),
                null,
                Metadata(),
                [candidate],
                Parameters()));

        Assert.False(evidence.GeometryUsable);
        Assert.False(evidence.OriginInsideSearchDomain);
        Assert.False(evidence.CompleteAtBoundary);
        Assert.Empty(evidence.TemplateRoiContour);
        Assert.Empty(evidence.OuterContour);
        Assert.Empty(evidence.InnerFeatureGroups);
        Assert.Equal(0, evidence.SupportMask.Area);
        Assert.Equal(0, evidence.OuterCoverage);
        Assert.Equal(0, evidence.InnerCoverage);
        Assert.Equal(0, evidence.EdgeDistanceP95Px);
        Assert.Equal(0, evidence.PolarityAgreement);
    }

    [Fact]
    public void BuildBatchHonorsPreCancelledTokenWithoutReturningPartialEvidence()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        IReadOnlyList<TemplateCandidateEvidence>? result = null;

        Assert.Throws<OperationCanceledException>(() =>
            result = new TemplateCandidateEvidenceBuilder().BuildBatch(
                RuntimeFrame(),
                null,
                Metadata(),
                [
                    new TemplateCandidate(0, new Pose2D(60, 50, 0), 0.9),
                    new TemplateCandidate(1, new Pose2D(62, 52, 0), 0.8)
                ],
                Parameters(),
                cancellation.Token));
        Assert.Null(result);
    }

    [Fact]
    public void RuntimeSupportMaskSnapshotsPixelsAndComputesIntersectionWindowIou()
    {
        byte[] firstPixels =
        [
            1, 1, 0,
            1, 1, 0
        ];
        var first = new FilledSupportMask(10, 20, 3, 2, firstPixels);
        var second = new FilledSupportMask(
            11,
            20,
            3,
            2,
            [
                1, 0, 0,
                1, 0, 0
            ]);
        firstPixels[0] = 0;

        Assert.Equal(4, first.Area);
        Assert.Equal(2, second.Area);
        Assert.Equal(0.5, FilledSupportMask.ComputeIoU(first, second), 12);
        Assert.Equal(1, first.Pixels[0]);
    }

    private static HalconTemplateModelMetadata Metadata(
        int templateWidth = 42,
        int templateHeight = 42,
        double referenceRow = 21,
        double referenceColumn = 21)
    {
        HalconTemplateMatchingParameters parameters = Parameters();
        TemplateModelGenerationParameters generation =
            TemplateModelGenerationParameters.From(parameters);
        IReadOnlyList<Point2D> outer = RectangleContour(20, 20);
        IReadOnlyList<IReadOnlyList<Point2D>> inner =
        [
            RectangleContour(5, 4).Select(point => new Point2D(point.X - 9, point.Y - 7)).ToArray(),
            RectangleContour(5, 4).Select(point => new Point2D(point.X + 8, point.Y - 6)).ToArray(),
            RectangleContour(5, 4).Select(point => new Point2D(point.X - 6, point.Y + 8)).ToArray(),
            RectangleContour(5, 4).Select(point => new Point2D(point.X + 9, point.Y + 7)).ToArray()
        ];
        var runs = new List<HalconSupportRun>();
        for (var row = 0; row < 37; row++)
        {
            runs.Add(new HalconSupportRun(row, 0, 37));
        }

        return new HalconTemplateModelMetadata(
            new TemplateModelOwner("recipe", "flow", "tool"),
            "generation-evidence",
            "model-generation-evidence.shm",
            new string('b', 64),
            new TemplateLearnedGeometry(
                new Pose2D(60, 50, 0),
                templateWidth,
                templateHeight),
            referenceRow,
            referenceColumn,
            referenceRow,
            referenceColumn,
            true,
            outer,
            inner,
            3,
            new HalconFilledSupportRegion(-18, -18, runs),
            generation,
            TemplateModelGenerationFingerprint.Compute(generation),
            HalconTemplateValidationDefaults.From(parameters));
    }

    private static HalconTemplateMatchingParameters Parameters()
    {
        Dictionary<string, string> values =
            TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        values[TemplateMatchingParameterCatalog.EdgeTolerancePx] = "3";
        values[TemplateMatchingParameterCatalog.InnerCoverageMin] = "0.6";
        return TemplateMatchingParameterCatalog.ParseHalcon(values, TemplateMatchCardinality.Single);
    }

    private static IReadOnlyList<Point2D> RectangleContour(int halfWidth, int halfHeight)
    {
        var points = new List<Point2D>();
        for (var x = -halfWidth; x < halfWidth; x++)
        {
            points.Add(new Point2D(x, -halfHeight));
        }

        for (var y = -halfHeight; y < halfHeight; y++)
        {
            points.Add(new Point2D(halfWidth, y));
        }

        for (var x = halfWidth; x > -halfWidth; x--)
        {
            points.Add(new Point2D(x, halfHeight));
        }

        for (var y = halfHeight; y > -halfHeight; y--)
        {
            points.Add(new Point2D(-halfWidth, y));
        }

        return points;
    }

    private static void AssertPoint(Point2D point, double expectedX, double expectedY)
    {
        Assert.Equal(expectedX, point.X, 12);
        Assert.Equal(expectedY, point.Y, 12);
    }

    private static ImageFrame RuntimeFrame()
    {
        const int width = 120;
        const int height = 100;
        using var image = new Mat(height, width, MatType.CV_8UC1, new Scalar(235));
        Cv2.Rectangle(image, new Rect(40, 30, 41, 41), new Scalar(30), -1);
        Cv2.Rectangle(image, new Rect(47, 39, 10, 7), new Scalar(220), -1);
        Cv2.Rectangle(image, new Rect(64, 40, 10, 7), new Scalar(220), -1);
        Cv2.Rectangle(image, new Rect(50, 57, 10, 7), new Scalar(220), -1);
        Cv2.Rectangle(image, new Rect(66, 56, 10, 7), new Scalar(220), -1);
        image.GetArray(out byte[] pixels);
        return new ImageFrame(
            "candidate-evidence",
            width,
            height,
            width,
            PixelFormatKind.Gray8,
            pixels,
            DateTimeOffset.UnixEpoch,
            "Synthetic");
    }
}
