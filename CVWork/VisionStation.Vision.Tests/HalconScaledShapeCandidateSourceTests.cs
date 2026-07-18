using HalconDotNet;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class HalconScaledShapeCandidateSourceTests
{
    [Fact]
    public async Task UnitScaleZeroAngleAddsSearchOffsetExactlyOnce()
    {
        var operators = new RecordingOperatorBackend
        {
            FindResult = new HalconShapeModelFindResult(
            [
                new HalconNativeCandidate(40, 30, 0, 1, 0.93)
            ])
        };
        var operation = new RecordingModelOperation();
        var source = new HalconScaledShapeCandidateSource(operators);

        HalconCandidateBatch batch = await source.FindAsync(
            operation,
            Frame(),
            RectangleSearchRoi(),
            Parameters(),
            default);

        TemplateCandidate candidate = Assert.Single(batch.Candidates);
        Assert.Equal((130d, 240d, 0d, 1d),
            (candidate.Pose.X, candidate.Pose.Y, candidate.Pose.Angle, candidate.Pose.Scale));
        Assert.Equal(0.93, candidate.Score);
        Assert.False(batch.LimitReached);
        Assert.Equal(new TemplateSearchRegion(100, 200, 100, 100), batch.SearchRegion);
        Assert.Equal(1, operation.InvocationCount);
        Assert.Same(operation.Borrow, operators.LastBorrow);
    }

    [Fact]
    public async Task NativeAngleAndScaleApplyTrueOriginCorrectionBeforeSearchOffset()
    {
        const double nativeAngle = -35d * Math.PI / 180d;
        const double scale = 1.1;
        var operators = new RecordingOperatorBackend
        {
            FindResult = new HalconShapeModelFindResult(
            [
                new HalconNativeCandidate(40, 30, nativeAngle, scale, 0.9)
            ])
        };
        var source = new HalconScaledShapeCandidateSource(operators);

        HalconCandidateBatch batch = await source.FindAsync(
            new RecordingModelOperation(),
            Frame(),
            RectangleSearchRoi(),
            Parameters(),
            default);

        Pose2D pose = Assert.Single(batch.Candidates).Pose;
        Assert.Equal(129.63506658436587, pose.X, 12);
        Assert.Equal(240.26600066435202, pose.Y, 12);
        Assert.Equal(35, pose.Angle, 12);
        Assert.Equal(scale, pose.Scale);
    }

    [Theory]
    [InlineData(90, 120.05, 208.95, -90)]
    [InlineData(270, 118.95, 210.05, 90)]
    public void RightAngleTrueOriginCorrectionUsesIndependentFixedExpectations(
        double nativeAngleDeg,
        double expectedX,
        double expectedY,
        double expectedUiAngleDeg)
    {
        Pose2D pose = HalconPoseConverter.ToPose(
            new HalconNativeCandidate(
                Row: 10,
                Column: 20,
                AngleRadians: nativeAngleDeg * Math.PI / 180d,
                Scale: 1.1,
                Score: 0.9),
            new TemplateSearchRegion(100, 200, 50, 50));

        Assert.Equal(expectedX, pose.X, 12);
        Assert.Equal(expectedY, pose.Y, 12);
        Assert.Equal(expectedUiAngleDeg, pose.Angle, 12);
        Assert.Equal(1.1, pose.Scale);
    }

    [Theory]
    [InlineData(-35, 35)]
    [InlineData(135, -135)]
    [InlineData(-225, -135)]
    public void PoseConverterUsesClockwiseDegreesAndNormalizesAngle(
        double nativeAngleDeg,
        double expectedUiAngleDeg)
    {
        Pose2D pose = HalconPoseConverter.ToPose(
            new HalconNativeCandidate(
                10,
                20,
                nativeAngleDeg * Math.PI / 180d,
                1,
                0.8),
            new TemplateSearchRegion(0, 0, 100, 100));

        Assert.Equal(expectedUiAngleDeg, pose.Angle, 12);
    }

    [Theory]
    [MemberData(nameof(NonRectangularSearchRois))]
    public async Task NonRectangularSearchRoiBuildsCropLocalDomain(
        RoiDefinition searchRoi,
        Point2D shapeSpecificInside,
        Point2D shapeSpecificOutside)
    {
        var operators = new RecordingOperatorBackend();
        var source = new HalconScaledShapeCandidateSource(operators);

        HalconCandidateBatch batch = await source.FindAsync(
            new RecordingModelOperation(),
            Frame(),
            searchRoi,
            Parameters(),
            default);

        HalconShapeModelFindRequest nativeRequest = Assert.IsType<HalconShapeModelFindRequest>(
            operators.LastRequest);
        Assert.Equal(batch.SearchRegion.Width, nativeRequest.SearchImage.Width);
        Assert.Equal(batch.SearchRegion.Height, nativeRequest.SearchImage.Height);
        Assert.Equal(batch.SearchRegion.Width, nativeRequest.SearchDomain.Width);
        Assert.Equal(batch.SearchRegion.Height, nativeRequest.SearchDomain.Height);
        Assert.InRange(
            nativeRequest.SearchDomain.Area,
            1,
            nativeRequest.SearchDomain.Width * nativeRequest.SearchDomain.Height - 1);
        Assert.All(
            nativeRequest.SearchDomain.Runs,
            run =>
            {
                Assert.InRange(run.Row, 0, nativeRequest.SearchDomain.Height - 1);
                Assert.InRange(run.ColumnStart, 0, nativeRequest.SearchDomain.Width - 1);
                Assert.InRange(
                    run.ColumnStart + run.Length - 1,
                    run.ColumnStart,
                    nativeRequest.SearchDomain.Width - 1);
            });
        Assert.True(DomainContains(
            nativeRequest.SearchDomain,
            nativeRequest.SearchDomain.Width / 2,
            nativeRequest.SearchDomain.Height / 2));
        Assert.False(DomainContains(
            nativeRequest.SearchDomain,
            nativeRequest.SearchDomain.Width - 1,
            0));
        Assert.True(DomainContains(
            nativeRequest.SearchDomain,
            checked((int)shapeSpecificInside.X - batch.SearchRegion.X),
            checked((int)shapeSpecificInside.Y - batch.SearchRegion.Y)));
        Assert.False(DomainContains(
            nativeRequest.SearchDomain,
            checked((int)shapeSpecificOutside.X - batch.SearchRegion.X),
            checked((int)shapeSpecificOutside.Y - batch.SearchRegion.Y)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullImageAndAxisAlignedRectangleUseOneCompactRunPerRow(
        bool useRectangle)
    {
        var operators = new RecordingOperatorBackend();
        var source = new HalconScaledShapeCandidateSource(operators);

        HalconCandidateBatch batch = await source.FindAsync(
            new RecordingModelOperation(),
            Frame(),
            useRectangle ? RectangleSearchRoi() : null,
            Parameters(),
            default);

        HalconModelDomain domain = Assert.IsType<HalconShapeModelFindRequest>(
            operators.LastRequest).SearchDomain;
        Assert.Equal(batch.SearchRegion.Height, domain.Runs.Count);
        for (var row = 0; row < domain.Height; row++)
        {
            HalconSupportRun run = domain.Runs[row];
            Assert.Equal((row, 0, domain.Width), (run.Row, run.ColumnStart, run.Length));
        }
    }

    [Fact]
    public async Task RuntimeFindParametersRemainHotAndUseOneModelInvocation()
    {
        var operators = new RecordingOperatorBackend();
        var operation = new RecordingModelOperation();
        var source = new HalconScaledShapeCandidateSource(operators);
        HalconTemplateMatchingParameters parameters = Parameters() with
        {
            AngleStartDeg = -27,
            AngleExtentDeg = 54,
            ScaleMin = 0.91,
            ScaleMax = 1.08,
            CandidateMinScore = 0.61,
            CandidateMaxOverlap = 0.68,
            Greediness = 0.77,
            SubPixel = "least_squares",
            NumLevels = 0,
            CandidateLimit = 23,
            OperatorTimeoutMs = 7000
        };

        await source.FindAsync(
            operation,
            Frame(),
            RectangleSearchRoi(),
            parameters,
            default);

        HalconShapeModelFindRequest request = Assert.IsType<HalconShapeModelFindRequest>(
            operators.LastRequest);
        Assert.Equal(parameters, request.Parameters);
        Assert.Equal(1, operation.InvocationCount);
        Assert.Equal(1, operators.FindCount);
    }

    [Fact]
    public async Task RawCountEqualToCandidateLimitReportsPossibleTruncation()
    {
        HalconTemplateMatchingParameters parameters = Parameters() with { CandidateLimit = 3 };
        var operators = new RecordingOperatorBackend
        {
            FindResult = new HalconShapeModelFindResult(
            [
                new HalconNativeCandidate(10, 10, 0, 1, 0.9),
                new HalconNativeCandidate(20, 20, 0, 1, 0.8),
                new HalconNativeCandidate(30, 30, 0, 1, 0.7)
            ])
        };
        var source = new HalconScaledShapeCandidateSource(operators);

        HalconCandidateBatch batch = await source.FindAsync(
            new RecordingModelOperation(),
            Frame(),
            null,
            parameters,
            default);

        Assert.Equal(3, batch.Candidates.Count);
        Assert.True(batch.LimitReached);
    }

    [Fact]
    public async Task NativeTimeoutMapsToStableMatchTimeoutWithoutSensitiveMessage()
    {
        const string sensitive = "sensitive-native-timeout-message";
        var operators = new RecordingOperatorBackend
        {
            FindFailure = new HOperatorException(9400, sensitive)
        };
        var source = new HalconScaledShapeCandidateSource(operators);

        HalconOperatorFailure failure = await Assert.ThrowsAsync<HalconOperatorFailure>(() =>
            source.FindAsync(
                new RecordingModelOperation(),
                Frame(),
                null,
                Parameters(),
                default));

        Assert.Equal(TemplateMatchingDiagnosticCodes.MatchTimeout, failure.Code);
        Assert.DoesNotContain(sensitive, failure.TechnicalDetails ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("Interrupt", failure.TechnicalDetails ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationAfterNativeAdmissionWaitsForReturnThenPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        var operators = new RecordingOperatorBackend
        {
            OnFind = cancellation.Cancel,
            FindResult = new HalconShapeModelFindResult(
            [
                new HalconNativeCandidate(10, 10, 0, 1, 0.9)
            ])
        };
        var operation = new RecordingModelOperation();
        var source = new HalconScaledShapeCandidateSource(operators);

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            source.FindAsync(
                operation,
                Frame(),
                null,
                Parameters(),
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, operation.InvocationCount);
        Assert.Equal(1, operators.FindCount);
    }

    public static IEnumerable<object[]> NonRectangularSearchRois()
    {
        yield return
        [
            new RoiDefinition
            {
                Shape = RoiShapeKind.Circle,
                X = 150,
                Y = 250,
                Radius = 40
            },
            new Point2D(180, 250),
            new Point2D(180, 220)
        ];
        yield return
        [
            new RoiDefinition
            {
                Shape = RoiShapeKind.RotatedRectangle,
                X = 150,
                Y = 250,
                Width = 90,
                Height = 40,
                Angle = 35
            },
            new Point2D(179, 270),
            new Point2D(136, 270)
        ];
        yield return
        [
            new RoiDefinition
            {
                Shape = RoiShapeKind.Polygon,
                Points =
                [
                    new Point2D(110, 220),
                    new Point2D(190, 225),
                    new Point2D(175, 290),
                    new Point2D(120, 275)
                ]
            },
            new Point2D(125, 270),
            new Point2D(115, 270)
        ];
    }

    private static HalconTemplateMatchingParameters Parameters()
    {
        return TemplateMatchingParameterCatalog.ParseHalcon(
            TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single),
            TemplateMatchCardinality.Single);
    }

    private static RoiDefinition RectangleSearchRoi()
    {
        return new RoiDefinition
        {
            Shape = RoiShapeKind.Rectangle,
            X = 100,
            Y = 200,
            Width = 100,
            Height = 100
        };
    }

    private static ImageFrame Frame()
    {
        const int width = 400;
        const int height = 400;
        return new ImageFrame(
            "candidate-source",
            width,
            height,
            width,
            PixelFormatKind.Gray8,
            Enumerable.Repeat((byte)230, width * height).ToArray(),
            DateTimeOffset.UnixEpoch,
            "test");
    }

    private static bool DomainContains(HalconModelDomain domain, int column, int row)
    {
        return domain.Runs.Any(run =>
            run.Row == row &&
            column >= run.ColumnStart &&
            column < run.ColumnStart + run.Length);
    }

    private sealed class RecordingModelOperation : IHalconModelOperation
    {
        public IHalconModelBorrow Borrow { get; } = new BorrowedModel();

        public int InvocationCount { get; private set; }

        public Task<T> InvokeAsync<T>(
            Func<IHalconModelBorrow, T> invocation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            return Task.FromResult(invocation(Borrow));
        }
    }

    private sealed class BorrowedModel : IHalconModelBorrow
    {
    }

    private sealed class RecordingOperatorBackend : IHalconOperatorBackend
    {
        public HalconShapeModelFindResult FindResult { get; init; } =
            new(Array.Empty<HalconNativeCandidate>());

        public Exception? FindFailure { get; init; }

        public Action? OnFind { get; init; }

        public IHalconModelBorrow? LastBorrow { get; private set; }

        public HalconShapeModelFindRequest? LastRequest { get; private set; }

        public int FindCount { get; private set; }

        public HalconShapeModelFindResult FindScaledShapeModel(
            IHalconModelBorrow model,
            HalconShapeModelFindRequest request)
        {
            FindCount++;
            LastBorrow = model;
            LastRequest = request;
            OnFind?.Invoke();
            if (FindFailure is not null)
            {
                throw FindFailure;
            }

            return FindResult;
        }

        public void CreateAndWriteShapeModel(
            HalconShapeModelCreationRequest request,
            string stagingModelPath) => throw new NotSupportedException();

        public IHalconRawModelHandle LoadShapeModelAndValidate(string modelPath) =>
            throw new NotSupportedException();

        public void VerifyMatchingLicense()
        {
        }
    }
}
