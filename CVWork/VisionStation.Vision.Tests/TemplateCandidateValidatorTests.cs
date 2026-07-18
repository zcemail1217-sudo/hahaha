using System.Globalization;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class TemplateCandidateValidatorTests
{
    private readonly TemplateCandidateValidator _validator = new();

    [Fact]
    public void NonFinitePoseOrScoreFailsFirstGate()
    {
        TemplateCandidateEvidence evidence = Evidence(
            candidate: new TemplateCandidate(0, new Pose2D(50, 50, 0), double.NaN));

        AssertRejected(evidence, Parameters(), TemplateMatchingDiagnosticCodes.MatchInvalidPose);
    }

    [Fact]
    public void ScaleOutsideLearnedGenerationRangeFailsPoseGate()
    {
        TemplateCandidateEvidence evidence = Evidence(
            candidate: Candidate(0, scale: 1.2));

        AssertRejected(evidence, Parameters(), TemplateMatchingDiagnosticCodes.MatchInvalidPose);
    }

    [Fact]
    public void OriginOutsideActualSearchDomainFailsPoseGate()
    {
        AssertRejected(
            Evidence(originInsideSearchDomain: false),
            Parameters(),
            TemplateMatchingDiagnosticCodes.MatchInvalidPose);
    }

    [Fact]
    public void IncompleteGeometryFailsBoundaryGate()
    {
        AssertRejected(
            Evidence(completeAtBoundary: false),
            Parameters(),
            TemplateMatchingDiagnosticCodes.MatchIncompleteAtBoundary);
    }

    [Fact]
    public void ReversedPolarityFailsPolarityGate()
    {
        AssertRejected(
            Evidence(polarityAgreement: 0.89),
            Parameters(),
            TemplateMatchingDiagnosticCodes.MatchPolarityMismatch);
    }

    [Fact]
    public void LowOuterCoverageFailsOuterGateIndependently()
    {
        AssertRejected(
            Evidence(outerCoverage: 0.89, edgeDistanceP95Px: 1),
            Parameters(),
            TemplateMatchingDiagnosticCodes.MatchOuterContourWeak);
    }

    [Fact]
    public void HighOuterP95FailsOuterGateIndependently()
    {
        AssertRejected(
            Evidence(outerCoverage: 0.99, edgeDistanceP95Px: 3.01),
            Parameters(),
            TemplateMatchingDiagnosticCodes.MatchOuterContourWeak);
    }

    [Fact]
    public void LowOverallInnerCoverageFailsInnerGateIndependently()
    {
        AssertRejected(
            Evidence(innerCoverage: 0.81, validInnerGroupCount: 4),
            Parameters(),
            TemplateMatchingDiagnosticCodes.MatchInnerFeaturesWeak);
    }

    [Fact]
    public void TooFewValidInnerGroupsFailsInnerGateIndependently()
    {
        AssertRejected(
            Evidence(innerCoverage: 0.99, validInnerGroupCount: 2),
            Parameters(),
            TemplateMatchingDiagnosticCodes.MatchInnerFeaturesWeak);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void InvalidPolarityMetricFailsClosedEvenAtZeroThreshold(double value)
    {
        AssertRejected(
            Evidence(polarityAgreement: value),
            Parameters() with { PolarityAgreementMin = 0 },
            TemplateMatchingDiagnosticCodes.MatchPolarityMismatch);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void InvalidOuterCoverageFailsClosedEvenAtZeroThreshold(double value)
    {
        AssertRejected(
            Evidence(outerCoverage: value, edgeDistanceP95Px: 0),
            Parameters() with { OuterCoverageMin = 0, EdgeTolerancePx = 0 },
            TemplateMatchingDiagnosticCodes.MatchOuterContourWeak);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void InvalidInnerCoverageFailsClosedEvenAtZeroThreshold(double value)
    {
        AssertRejected(
            Evidence(innerCoverage: value),
            Parameters() with { InnerCoverageMin = 0 },
            TemplateMatchingDiagnosticCodes.MatchInnerFeaturesWeak);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.01)]
    public void InvalidEdgeP95FailsClosedEvenAtZeroThreshold(double value)
    {
        AssertRejected(
            Evidence(outerCoverage: 1, edgeDistanceP95Px: value),
            Parameters() with { OuterCoverageMin = 0, EdgeTolerancePx = 0 },
            TemplateMatchingDiagnosticCodes.MatchOuterContourWeak);
    }

    [Theory]
    [InlineData(false, 0.0, 0.0, 0.0, TemplateMatchingDiagnosticCodes.MatchIncompleteAtBoundary)]
    [InlineData(true, 0.0, 0.0, 0.0, TemplateMatchingDiagnosticCodes.MatchPolarityMismatch)]
    [InlineData(true, 0.95, 0.0, 0.0, TemplateMatchingDiagnosticCodes.MatchOuterContourWeak)]
    public void AdjacentHardGatesKeepBoundaryPolarityOuterInnerPriority(
        bool completeAtBoundary,
        double polarityAgreement,
        double outerCoverage,
        double innerCoverage,
        string expectedCode)
    {
        AssertRejected(
            Evidence(
                completeAtBoundary: completeAtBoundary,
                polarityAgreement: polarityAgreement,
                outerCoverage: outerCoverage,
                innerCoverage: innerCoverage),
            Parameters(),
            expectedCode);
    }

    [Fact]
    public void MultipleBrokenConditionsStillUseFixedFirstFailurePriority()
    {
        var invalidCandidate = new TemplateCandidate(
            0,
            new Pose2D(double.NaN, 50, 0) { Scale = 2 },
            0.9);
        TemplateCandidateEvidence evidence = Evidence(
            candidate: invalidCandidate,
            originInsideSearchDomain: false,
            completeAtBoundary: false,
            polarityAgreement: 0,
            outerCoverage: 0,
            edgeDistanceP95Px: 100,
            innerCoverage: 0,
            validInnerGroupCount: 0);

        AssertRejected(evidence, Parameters(), TemplateMatchingDiagnosticCodes.MatchInvalidPose);
    }

    [Fact]
    public void DuplicateGateUsesFilledSupportNotOverlappingBoundingBoxes()
    {
        var firstPixels = new byte[16];
        var secondPixels = new byte[16];
        for (var row = 0; row < 4; row++)
        {
            firstPixels[row * 4 + row] = 1;
            secondPixels[row * 4 + (3 - row)] = 1;
        }

        TemplateCandidateEvidence first = Evidence(
            candidate: Candidate(0, score: 0.95),
            supportMask: new FilledSupportMask(10, 10, 4, 4, firstPixels));
        TemplateCandidateEvidence second = Evidence(
            candidate: Candidate(1, score: 0.90),
            supportMask: new FilledSupportMask(10, 10, 4, 4, secondPixels));

        TemplateCandidateValidationResult result = _validator.ValidateAndDeduplicate(
            [first, second],
            Metadata(),
            Parameters());

        Assert.Equal(2, result.Accepted.Count);
        Assert.All(result.Decisions, decision => Assert.True(decision.Accepted));
    }

    [Fact]
    public void SupportIouAboveRuntimeMaxOverlapRejectsLowerPriorityCandidate()
    {
        TemplateCandidateEvidence first = Evidence(
            candidate: Candidate(0, score: 0.95),
            supportMask: SolidMask(10, 10, 4, 4));
        TemplateCandidateEvidence second = Evidence(
            candidate: Candidate(1, score: 0.90),
            supportMask: SolidMask(11, 10, 4, 4));
        HalconTemplateMatchingParameters parameters = Parameters() with { MaxOverlap = 0.5 };

        TemplateCandidateValidationResult result = _validator.ValidateAndDeduplicate(
            [second, first],
            Metadata(),
            parameters);

        Assert.Equal(0, Assert.Single(result.Accepted).Candidate.SourceIndex);
        TemplateCandidateDecision rejected = Assert.Single(
            result.Decisions,
            decision => !decision.Accepted);
        Assert.Equal(1, rejected.Candidate.SourceIndex);
        Assert.Equal(
            TemplateMatchingDiagnosticCodes.MatchDuplicateOverlap,
            Assert.IsType<TemplateMatchingDiagnostic>(rejected.Diagnostic).Code);
        string details = Assert.IsType<string>(rejected.Diagnostic.TechnicalDetails);
        Assert.Contains("sourceIndex=1", details, StringComparison.Ordinal);
        Assert.Contains("conflictingAcceptedSourceIndex=0", details, StringComparison.Ordinal);
        Assert.Contains("measured=iou=0.6", details, StringComparison.Ordinal);
        Assert.Contains("threshold=MaxOverlap=0.5", details, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(details, "sourceIndex="));
        Assert.Equal(1, CountOccurrences(details, "conflictingAcceptedSourceIndex="));
        Assert.Equal(1, CountOccurrences(details, "iou="));
        Assert.Equal(1, CountOccurrences(details, "MaxOverlap="));
    }

    [Fact]
    public void SupportIouEqualToRuntimeMaxOverlapIsAccepted()
    {
        var first = new FilledSupportMask(0, 0, 3, 2, [1, 1, 0, 1, 1, 0]);
        var second = new FilledSupportMask(1, 0, 3, 2, [1, 0, 0, 1, 0, 0]);
        Assert.Equal(0.5, FilledSupportMask.ComputeIoU(first, second), 12);

        TemplateCandidateValidationResult result = _validator.ValidateAndDeduplicate(
            [
                Evidence(candidate: Candidate(0, score: 0.95), supportMask: first),
                Evidence(candidate: Candidate(1, score: 0.90), supportMask: second)
            ],
            Metadata(),
            Parameters() with { MaxOverlap = 0.5 });

        Assert.Equal(2, result.Accepted.Count);
    }

    [Fact]
    public void RejectionDetailsUseInvariantNumberFormatting()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");

            TemplateCandidateValidationResult result = _validator.ValidateAndDeduplicate(
                [Evidence(outerCoverage: 0.89, edgeDistanceP95Px: 1.25)],
                Metadata(),
                Parameters());
            string details = Assert.IsType<string>(
                Assert.Single(result.Decisions).Diagnostic?.TechnicalDetails);

            Assert.Contains("outerCoverage=0.89", details, StringComparison.Ordinal);
            Assert.Contains("edgeDistanceP95Px=1.25", details, StringComparison.Ordinal);
            Assert.DoesNotContain("0,89", details, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void AcceptedCandidatesUseAllDeterministicSortKeys()
    {
        TemplateCandidateEvidence[] evidence =
        [
            Evidence(candidate: Candidate(9, score: 0.98, x: 10, y: 10), supportMask: SolidMask(180, 0, 2, 2)),
            Evidence(candidate: Candidate(8, score: 0.99, x: 10, y: 10), outerCoverage: 0.91, supportMask: SolidMask(160, 0, 2, 2)),
            Evidence(candidate: Candidate(7, score: 0.99, x: 90, y: 10), outerCoverage: 0.96, supportMask: SolidMask(140, 0, 2, 2)),
            Evidence(candidate: Candidate(6, score: 0.99, x: 80, y: 70), outerCoverage: 0.96, supportMask: SolidMask(120, 0, 2, 2)),
            Evidence(candidate: Candidate(5, score: 0.99, x: 80, y: 60), outerCoverage: 0.96, supportMask: SolidMask(100, 0, 2, 2)),
            Evidence(candidate: Candidate(2, score: 0.99, x: 80, y: 60), outerCoverage: 0.96, supportMask: SolidMask(40, 0, 2, 2))
        ];

        TemplateCandidateValidationResult result = _validator.ValidateAndDeduplicate(
            evidence,
            Metadata(),
            Parameters());

        Assert.Equal([2, 5, 6, 7, 8, 9], result.Accepted.Select(item => item.Candidate.SourceIndex));
    }

    [Fact]
    public void HotOuterCoverageThresholdImmediatelyChangesResult()
    {
        AssertHotThreshold(
            Evidence(outerCoverage: 0.91),
            Parameters() with { OuterCoverageMin = 0.90 },
            Parameters() with { OuterCoverageMin = 0.92 },
            TemplateMatchingDiagnosticCodes.MatchOuterContourWeak);
    }

    [Fact]
    public void HotEdgeToleranceImmediatelyChangesResult()
    {
        AssertHotThreshold(
            Evidence(edgeDistanceP95Px: 2.5),
            Parameters() with { EdgeTolerancePx = 3 },
            Parameters() with { EdgeTolerancePx = 2 },
            TemplateMatchingDiagnosticCodes.MatchOuterContourWeak);
    }

    [Fact]
    public void HotInnerCoverageThresholdImmediatelyChangesResult()
    {
        AssertHotThreshold(
            Evidence(innerCoverage: 0.84),
            Parameters() with { InnerCoverageMin = 0.82 },
            Parameters() with { InnerCoverageMin = 0.85 },
            TemplateMatchingDiagnosticCodes.MatchInnerFeaturesWeak);
    }

    [Fact]
    public void HotPolarityThresholdImmediatelyChangesResult()
    {
        AssertHotThreshold(
            Evidence(polarityAgreement: 0.92),
            Parameters() with { PolarityAgreementMin = 0.90 },
            Parameters() with { PolarityAgreementMin = 0.93 },
            TemplateMatchingDiagnosticCodes.MatchPolarityMismatch);
    }

    [Fact]
    public void HotMaxOverlapImmediatelyChangesResultButCandidateOverlapDoesNot()
    {
        FilledSupportMask firstMask = SolidMask(10, 10, 4, 4);
        FilledSupportMask secondMask = SolidMask(11, 10, 4, 4);
        TemplateCandidateEvidence[] evidence =
        [
            Evidence(candidate: Candidate(0, score: 0.95), supportMask: firstMask),
            Evidence(candidate: Candidate(1, score: 0.90), supportMask: secondMask)
        ];
        HalconTemplateMatchingParameters permissive = Parameters() with
        {
            MaxOverlap = 0.7,
            CandidateMaxOverlap = 0.7
        };
        HalconTemplateMatchingParameters candidateOnlyChanged = permissive with
        {
            CandidateMaxOverlap = 0.1
        };
        HalconTemplateMatchingParameters strict = permissive with { MaxOverlap = 0.5 };

        Assert.Equal(2, _validator.ValidateAndDeduplicate(evidence, Metadata(), permissive).Accepted.Count);
        Assert.Equal(2, _validator.ValidateAndDeduplicate(evidence, Metadata(), candidateOnlyChanged).Accepted.Count);
        TemplateCandidateValidationResult rejected =
            _validator.ValidateAndDeduplicate(evidence, Metadata(), strict);
        Assert.Single(rejected.Accepted);
        Assert.Contains(
            rejected.Decisions,
            decision => decision.Diagnostic?.Code == TemplateMatchingDiagnosticCodes.MatchDuplicateOverlap);
    }

    [Fact]
    public void ValidatorPreservesRawCandidateScoreAndReportsEveryDecision()
    {
        TemplateCandidateEvidence accepted = Evidence(candidate: Candidate(0, score: 0.8123456789));
        TemplateCandidateEvidence rejected = Evidence(
            candidate: Candidate(1, score: 0.999),
            polarityAgreement: 0);

        TemplateCandidateValidationResult result = _validator.ValidateAndDeduplicate(
            [accepted, rejected],
            Metadata(),
            Parameters());

        Assert.Equal(0.8123456789, Assert.Single(result.Accepted).Candidate.Score);
        Assert.Equal(2, result.Decisions.Count);
        Assert.Contains(result.Decisions, decision => decision.Accepted && decision.Diagnostic is null);
        Assert.Contains(
            result.Decisions,
            decision => decision.Diagnostic?.Code == TemplateMatchingDiagnosticCodes.MatchPolarityMismatch);
    }

    private void AssertRejected(
        TemplateCandidateEvidence evidence,
        HalconTemplateMatchingParameters parameters,
        string code)
    {
        TemplateCandidateValidationResult result = _validator.ValidateAndDeduplicate(
            [evidence],
            Metadata(),
            parameters);

        Assert.Empty(result.Accepted);
        TemplateCandidateDecision decision = Assert.Single(result.Decisions);
        Assert.False(decision.Accepted);
        TemplateMatchingDiagnostic diagnostic =
            Assert.IsType<TemplateMatchingDiagnostic>(decision.Diagnostic);
        Assert.Equal(code, diagnostic.Code);
        string details = Assert.IsType<string>(diagnostic.TechnicalDetails);
        Assert.False(string.IsNullOrWhiteSpace(details));
        Assert.Contains(
            $"sourceIndex={evidence.Candidate.SourceIndex.ToString(CultureInfo.InvariantCulture)}",
            details,
            StringComparison.Ordinal);
        Assert.Contains("measured=", details, StringComparison.Ordinal);
        Assert.Contains("threshold=", details, StringComparison.Ordinal);
    }

    private void AssertHotThreshold(
        TemplateCandidateEvidence evidence,
        HalconTemplateMatchingParameters passing,
        HalconTemplateMatchingParameters failing,
        string expectedCode)
    {
        Assert.Single(_validator.ValidateAndDeduplicate([evidence], Metadata(), passing).Accepted);
        AssertRejected(evidence, failing, expectedCode);
    }

    private static TemplateCandidate Candidate(
        int sourceIndex,
        double score = 0.95,
        double x = 50,
        double y = 50,
        double scale = 1)
    {
        return new TemplateCandidate(sourceIndex, new Pose2D(x, y, 0) { Scale = scale }, score);
    }

    private static TemplateCandidateEvidence Evidence(
        TemplateCandidate? candidate = null,
        bool geometryUsable = true,
        bool originInsideSearchDomain = true,
        bool completeAtBoundary = true,
        double outerCoverage = 0.96,
        double innerCoverage = 0.90,
        double edgeDistanceP95Px = 1,
        double polarityAgreement = 0.95,
        int validInnerGroupCount = 3,
        FilledSupportMask? supportMask = null)
    {
        candidate ??= Candidate(0);
        supportMask ??= SolidMask(candidate.SourceIndex * 20, 0, 4, 4);
        return new TemplateCandidateEvidence(
            candidate,
            geometryUsable,
            originInsideSearchDomain,
            completeAtBoundary,
            [new Point2D(-10, -10), new Point2D(10, -10), new Point2D(10, 10), new Point2D(-10, 10)],
            MetadataOuterContour(),
            [[new Point2D(-2, -2)], [new Point2D(2, -2)], [new Point2D(0, 2)]],
            outerCoverage,
            innerCoverage,
            edgeDistanceP95Px,
            polarityAgreement,
            validInnerGroupCount,
            supportMask);
    }

    private static FilledSupportMask SolidMask(int x, int y, int width, int height)
    {
        return new FilledSupportMask(x, y, width, height, Enumerable.Repeat((byte)1, width * height).ToArray());
    }

    private static HalconTemplateModelMetadata Metadata()
    {
        HalconTemplateMatchingParameters parameters = Parameters();
        TemplateModelGenerationParameters generation =
            TemplateModelGenerationParameters.From(parameters);
        return new HalconTemplateModelMetadata(
            new TemplateModelOwner("recipe", "flow", "tool"),
            "generation-validator",
            "model-generation-validator.shm",
            new string('c', 64),
            new TemplateLearnedGeometry(new Pose2D(50, 50, 0), 20, 20),
            10,
            10,
            10,
            10,
            true,
            MetadataOuterContour(),
            [[new Point2D(-2, -2)], [new Point2D(2, -2)], [new Point2D(0, 2)], [new Point2D(3, 3)]],
            3,
            new HalconFilledSupportRegion(-8, -8, [new HalconSupportRun(0, 0, 16)]),
            generation,
            TemplateModelGenerationFingerprint.Compute(generation),
            HalconTemplateValidationDefaults.From(parameters));
    }

    private static HalconTemplateMatchingParameters Parameters()
    {
        return TemplateMatchingParameterCatalog.ParseHalcon(
            TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single),
            TemplateMatchCardinality.Single);
    }

    private static IReadOnlyList<Point2D> MetadataOuterContour()
    {
        return Enumerable.Range(0, 100)
            .Select(index =>
            {
                double angle = index * 2 * Math.PI / 100;
                return new Point2D(8 * Math.Cos(angle), 8 * Math.Sin(angle));
            })
            .ToArray();
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var searchIndex = 0;
        while ((searchIndex = value.IndexOf(token, searchIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            searchIndex += token.Length;
        }

        return count;
    }
}
