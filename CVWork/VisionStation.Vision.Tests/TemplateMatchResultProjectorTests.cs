using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class TemplateMatchResultProjectorTests
{
    [Fact]
    public void ProjectorUsesAxisAlignedBoundsOfTheWholeTransformedTemplateRoi()
    {
        var candidate = Candidate(
            new Pose2D(100, 100, 35) { Scale = 1.1 },
            [
                [
                    new Point2D(88.288, 78.371),
                    new Point2D(124.330, 103.608),
                    new Point2D(111.712, 121.629),
                    new Point2D(75.670, 96.392)
                ]
            ]);

        var result = TemplateMatchResultProjector.ToSingle(
            TemplateMatchingTestResults.Match(TemplateMatchingEngine.Halcon, candidate));

        Assert.Equal(75, result.MatchX);
        Assert.Equal(78, result.MatchY);
        Assert.Equal(1.1, result.Pose.Scale, 12);
        Assert.Equal(TemplateMatchingEngine.Halcon, result.Engine);
    }

    [Fact]
    public void ProjectorCarriesContoursScoresAndDiagnosticsWithoutRecomputingScale()
    {
        var diagnostic = TemplateMatchingDiagnostics.Create(
            TemplateMatchingDiagnosticCodes.MatchInnerFeaturesWeak,
            "inner-detail");
        var shapeContours = new IReadOnlyList<Point2D>[]
        {
            new[] { new Point2D(1, 2), new Point2D(3, 4), new Point2D(5, 6) }
        };
        var candidate = new TemplateMatchBatchCandidate(
            new Pose2D(20, 30, -15) { Scale = 0.93 },
            0.72,
            18,
            20,
            shapeContours,
            [
                [
                    new Point2D(10.2, 20.8),
                    new Point2D(30.9, 20.1),
                    new Point2D(31.4, 41.6),
                    new Point2D(9.8, 40.9)
                ]
            ])
        {
            ShapeCoverage = 0.81,
            ShapeReverseScore = 0.77,
            Shape = "Circle",
            Radius = 9.5
        };
        var batch = TemplateMatchingTestResults.Match(
            TemplateMatchingEngine.OpenCv,
            candidate,
            InspectionOutcome.Ng,
            diagnostic);

        var single = TemplateMatchResultProjector.ToSingle(batch);
        var multi = TemplateMatchResultProjector.ToMulti(batch);

        Assert.Equal(candidate.Pose, single.Pose);
        Assert.Equal(candidate.ShapeContours, single.ShapeContours);
        Assert.Equal(candidate.TemplateRoiContours, single.MatchedTemplateRoiContours);
        Assert.Equal(candidate.ShapeCoverage, single.ShapeCoverage);
        Assert.Equal(candidate.ShapeReverseScore, single.ShapeReverseScore);
        Assert.Equal(diagnostic.Code, single.FailureCode);
        Assert.Equal(diagnostic.FailureStage, single.FailureStage);
        Assert.Equal(diagnostic.TechnicalDetails, single.TechnicalDetails);
        var multiCandidate = Assert.Single(multi.Matches);
        Assert.Equal(candidate.Pose.Scale, multiCandidate.Scale, 12);
        Assert.Equal(candidate.Pose, multiCandidate.Pose);
        Assert.Equal(candidate.Shape, multiCandidate.Shape);
        Assert.Equal(candidate.Radius, multiCandidate.Radius);
        Assert.Equal(diagnostic.Code, multi.FailureCode);
    }

    [Theory]
    [MemberData(nameof(InvalidContours))]
    public void SuccessfulCandidateWithoutCompleteFiniteTemplateRoiFailsClosed(
        IReadOnlyList<IReadOnlyList<Point2D>> contours)
    {
        var batch = TemplateMatchingTestResults.Match(
            TemplateMatchingEngine.Halcon,
            Candidate(new Pose2D(10, 10, 0), contours));

        var single = Assert.Throws<InvalidOperationException>(() =>
            TemplateMatchResultProjector.ToSingle(batch));
        var multi = Assert.Throws<InvalidOperationException>(() =>
            TemplateMatchResultProjector.ToMulti(batch));

        Assert.Contains("TemplateRoiContours", single.Message, StringComparison.Ordinal);
        Assert.Contains("TemplateRoiContours", multi.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateTakesDeepReadOnlyContourSnapshots()
    {
        var points = new List<Point2D>
        {
            new(1, 1),
            new(5, 1),
            new(5, 5),
            new(1, 5)
        };
        var contours = new List<IReadOnlyList<Point2D>> { points };

        var candidate = Candidate(new Pose2D(3, 3, 0), contours);
        points[0] = new Point2D(99, 99);
        contours.Clear();

        Assert.Single(candidate.TemplateRoiContours);
        Assert.Equal(new Point2D(1, 1), candidate.TemplateRoiContours[0][0]);
        Assert.Equal("Rectangle", candidate.Shape);
        Assert.False(candidate.TemplateRoiContours is IList<IReadOnlyList<Point2D>> outer && !outer.IsReadOnly);
        Assert.False(candidate.TemplateRoiContours[0] is IList<Point2D> inner && !inner.IsReadOnly);
    }

    [Fact]
    public void SingleProjectionRejectsMultipleCandidatesButKeepsRejectedCandidateGeometry()
    {
        var first = Candidate(
            new Pose2D(-1.2, -2.1, 0),
            [[new Point2D(-2.1, -3.01), new Point2D(1, -3), new Point2D(1, 1), new Point2D(-2, 1)]]);
        var second = Candidate(
            new Pose2D(10, 10, 0),
            [[new Point2D(8, 8), new Point2D(12, 8), new Point2D(12, 12), new Point2D(8, 12)]]);
        var rejected = new TemplateMatchBatchResult(
            TemplateMatchingEngine.Halcon,
            InspectionOutcome.Ng,
            false,
            [first],
            new TemplateSearchRegion(0, 0, 32, 32),
            "Rejected.",
            false);
        var ambiguous = new TemplateMatchBatchResult(
            TemplateMatchingEngine.Halcon,
            InspectionOutcome.Ok,
            true,
            [first, second],
            new TemplateSearchRegion(0, 0, 32, 32),
            "Ambiguous.",
            false);

        var result = TemplateMatchResultProjector.ToSingle(rejected);

        Assert.False(result.HasMatch);
        Assert.Equal(first.Pose, result.Pose);
        Assert.Equal(-3, result.MatchX);
        Assert.Equal(-4, result.MatchY);
        Assert.Throws<InvalidOperationException>(() => TemplateMatchResultProjector.ToSingle(ambiguous));
    }

    [Fact]
    public void OutcomeOkWithoutCandidateFailsClosed()
    {
        var batch = new TemplateMatchBatchResult(
            TemplateMatchingEngine.Halcon,
            InspectionOutcome.Ok,
            false,
            Array.Empty<TemplateMatchBatchCandidate>(),
            new TemplateSearchRegion(0, 0, 10, 10),
            "Invalid.",
            false);

        Assert.Throws<InvalidOperationException>(() => TemplateMatchResultProjector.ToSingle(batch));
        Assert.Throws<InvalidOperationException>(() => TemplateMatchResultProjector.ToMulti(batch));
    }

    [Fact]
    public void OutcomeOkRequiresHasMatchWhileNgRejectedCandidateRemainsProjectable()
    {
        var candidate = Candidate(
            new Pose2D(10, 10, 0),
            [[new Point2D(8, 8), new Point2D(12, 8), new Point2D(12, 12), new Point2D(8, 12)]]);
        var inconsistent = new TemplateMatchBatchResult(
            TemplateMatchingEngine.Halcon,
            InspectionOutcome.Ok,
            false,
            [candidate],
            new TemplateSearchRegion(0, 0, 32, 32),
            "Invalid.",
            false);
        var rejected = new TemplateMatchBatchResult(
            TemplateMatchingEngine.Halcon,
            InspectionOutcome.Ng,
            false,
            [candidate],
            new TemplateSearchRegion(0, 0, 32, 32),
            "Rejected.",
            false);

        Assert.Throws<InvalidOperationException>(() => TemplateMatchResultProjector.ToSingle(inconsistent));
        Assert.Throws<InvalidOperationException>(() => TemplateMatchResultProjector.ToMulti(inconsistent));

        var single = TemplateMatchResultProjector.ToSingle(rejected);
        var multi = TemplateMatchResultProjector.ToMulti(rejected);
        Assert.False(single.HasMatch);
        Assert.Equal(candidate.Pose, single.Pose);
        Assert.Single(multi.Matches);
    }

    public static IEnumerable<object[]> InvalidContours()
    {
        yield return [Array.Empty<IReadOnlyList<Point2D>>()];
        yield return [new IReadOnlyList<Point2D>[] { new[] { new Point2D(1, 1), new Point2D(2, 2) } }];
        yield return
        [
            new IReadOnlyList<Point2D>[]
            {
                new[]
                {
                    new Point2D(1, 1),
                    new Point2D(double.NaN, 2),
                    new Point2D(3, 3)
                }
            }
        ];
    }

    private static TemplateMatchBatchCandidate Candidate(
        Pose2D pose,
        IReadOnlyList<IReadOnlyList<Point2D>> templateRoiContours)
    {
        return new TemplateMatchBatchCandidate(
            pose,
            0.9,
            40,
            20,
            Array.Empty<IReadOnlyList<Point2D>>(),
            templateRoiContours);
    }
}
