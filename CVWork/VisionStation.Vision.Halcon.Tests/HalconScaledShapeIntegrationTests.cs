using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision;
using Xunit;

namespace VisionStation.Vision.Halcon.Tests;

public sealed class HalconScaledShapeIntegrationTests
{
    [HalconIntegrationFact]
    public async Task TracerBullet_LearnsPersistsReloadsAndMatchesThroughPublicService()
    {
        TemplateMatchBatchResult result = await LearnReloadAndMatchAsync(
            angleDeg: 0,
            scale: 1);

        AssertAcceptedPose(result, SyntheticHalconProductFactory.MatchCenter, 0, 1);
    }

    [HalconIntegrationTheory]
    [InlineData(0, 0.90)]
    [InlineData(0, 1.00)]
    [InlineData(0, 1.10)]
    [InlineData(35, 0.90)]
    [InlineData(35, 1.00)]
    [InlineData(35, 1.10)]
    [InlineData(90, 0.90)]
    [InlineData(90, 1.00)]
    [InlineData(90, 1.10)]
    [InlineData(-135, 0.90)]
    [InlineData(-135, 1.00)]
    [InlineData(-135, 1.10)]
    public async Task PositiveMatrix_MatchesStrictPoseAtRequestedAngleAndScale(
        double angleDeg,
        double scale)
    {
        TemplateMatchBatchResult result = await LearnReloadAndMatchAsync(angleDeg, scale);

        AssertAcceptedPose(result, SyntheticHalconProductFactory.MatchCenter, angleDeg, scale);
    }

    [HalconIntegrationTheory]
    [InlineData(SyntheticHalconNegativeCase.BacksideInternalLayout, TemplateMatchingDiagnosticCodes.MatchInnerFeaturesWeak)]
    [InlineData(SyntheticHalconNegativeCase.SimilarOutlineWrongInternal, TemplateMatchingDiagnosticCodes.MatchInnerFeaturesWeak)]
    [InlineData(SyntheticHalconNegativeCase.CrossesImageBoundary, TemplateMatchingDiagnosticCodes.MatchIncompleteAtBoundary)]
    [InlineData(SyntheticHalconNegativeCase.CrossesSearchRoiBoundary, TemplateMatchingDiagnosticCodes.MatchIncompleteAtBoundary)]
    [InlineData(SyntheticHalconNegativeCase.PartialMiddleOnly, TemplateMatchingDiagnosticCodes.MatchOuterContourWeak)]
    [InlineData(SyntheticHalconNegativeCase.SevereOcclusion, TemplateMatchingDiagnosticCodes.MatchOuterContourWeak)]
    public async Task NegativeCases_AreRejectedByTheirFirstHardGate(
        SyntheticHalconNegativeCase negativeCase,
        string expectedDiagnosticCode)
    {
        SyntheticHalconNegativeScene scene = SyntheticHalconProductFactory.CreateNegativeScene(negativeCase);

        TemplateMatchBatchResult result = await LearnReloadAndMatchAsync(
            scene.Frame,
            scene.SearchRoi);

        Assert.False(result.HasMatch, DescribeUnexpectedAcceptance(negativeCase, result));
        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Empty(result.Matches);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(expectedDiagnosticCode, result.Diagnostic!.Code);
        Assert.Equal(TemplateMatchingFailureStages.Match, result.Diagnostic.FailureStage);
        Assert.Contains("sourceIndex=", result.Diagnostic.TechnicalDetails, StringComparison.Ordinal);
    }

    [HalconIntegrationFact]
    public async Task OppositePolarity_IsRejectedByNativeUsePolarityBeforeManagedValidation()
    {
        SyntheticHalconNegativeScene scene = SyntheticHalconProductFactory.CreateNegativeScene(
            SyntheticHalconNegativeCase.OppositePolarity);

        TemplateMatchBatchResult result = await LearnReloadAndMatchAsync(scene.Frame, scene.SearchRoi);

        Assert.False(result.HasMatch, DescribeUnexpectedAcceptance(
            SyntheticHalconNegativeCase.OppositePolarity,
            result));
        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Empty(result.Matches);
        // The model is intentionally created with HALCON use_polarity. A complete global
        // inversion therefore produces no native candidate, so no managed candidate exists
        // from which the validator could truthfully publish a hard-gate diagnostic.
        Assert.Null(result.Diagnostic);
    }

    [HalconIntegrationFact]
    public async Task ManagedPolarityHardGate_RejectsCandidateWithReversedOuterSamples()
    {
        SyntheticHalconNegativeScene scene =
            SyntheticHalconProductFactory.CreatePolarityHardGateScene();

        TemplateMatchBatchResult result = await LearnReloadAndMatchAsync(scene.Frame, scene.SearchRoi);

        Assert.False(result.HasMatch, "The managed polarity hard-gate specimen was accepted.");
        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Empty(result.Matches);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(TemplateMatchingDiagnosticCodes.MatchPolarityMismatch, result.Diagnostic!.Code);
        Assert.Equal(TemplateMatchingFailureStages.Match, result.Diagnostic.FailureStage);
        Assert.Contains("polarityAgreement=", result.Diagnostic.TechnicalDetails, StringComparison.Ordinal);
    }

    private static async Task<TemplateMatchBatchResult> LearnReloadAndMatchAsync(
        double angleDeg,
        double scale)
    {
        return await LearnReloadAndMatchAsync(
            SyntheticHalconProductFactory.CreateMatchFrame(angleDeg, scale),
            searchRoi: null);
    }

    private static async Task<TemplateMatchBatchResult> LearnReloadAndMatchAsync(
        ImageFrame matchFrame,
        RoiDefinition? searchRoi)
    {
        string workingDirectory = SyntheticHalconProductFactory.CreateWorkingDirectory();
        try
        {
            RuntimePaths paths = new(workingDirectory);
            var owner = new TemplateModelOwner("integration-recipe", "main-flow", "locate-product");
            IReadOnlyDictionary<string, string> learnedParameters = await LearnAndUnloadAsync(
                paths,
                owner);

            TemplateMatchingRuntime matchingRuntime = HalconTemplateMatchingFactory.Create(
                new FileTemplateModelStore(paths),
                SyntheticHalconProductFactory.CreateRuntimeConfiguration(),
                IntegrationDiagnosticSink.Instance);
            try
            {
                return await matchingRuntime.Service.MatchAsync(
                    new TemplateMatchingRequest(
                        owner,
                        matchFrame,
                        searchRoi,
                        learnedParameters,
                        TemplateMatchCardinality.Single,
                        ExpectedCount: 1),
                    CancellationToken.None);
            }
            finally
            {
                await matchingRuntime.Service.DisposeAsync();
            }
        }
        finally
        {
            SyntheticHalconProductFactory.DeleteWorkingDirectory(workingDirectory);
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> LearnAndUnloadAsync(
        RuntimePaths paths,
        TemplateModelOwner owner)
    {
        TemplateMatchingRuntime learningRuntime = HalconTemplateMatchingFactory.Create(
            new FileTemplateModelStore(paths),
            SyntheticHalconProductFactory.CreateRuntimeConfiguration(),
            IntegrationDiagnosticSink.Instance);
        try
        {
            Dictionary<string, string> parameters =
                TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
            TemplateLearningResult learning = await learningRuntime.Service.LearnAsync(
                new TemplateLearningRequest(
                    owner,
                    SyntheticHalconProductFactory.CreateLearningFrame(),
                    SyntheticHalconProductFactory.CreateTemplateRoi(),
                    SearchRoi: null,
                    parameters),
                CancellationToken.None);

            Assert.True(learning.Success, DescribeLearningFailure(learning));
            Assert.Equal(TemplateMatchingEngine.Halcon, learning.Engine);
            Assert.Null(learning.Diagnostic);
            HalconTemplateModelState? state = TemplateModelParameterCodec.ReadHalcon(learning.Parameters);
            Assert.NotNull(state);
            string modelPath = ResolveStoredPath(paths, state!.Reference.ModelPath);
            string metadataPath = ResolveStoredPath(paths, state.Reference.MetadataPath);
            Assert.EndsWith(".shm", modelPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(modelPath), $"Expected persisted HALCON model '{modelPath}'.");
            Assert.True(File.Exists(metadataPath), $"Expected persisted HALCON metadata '{metadataPath}'.");
            return new Dictionary<string, string>(learning.Parameters, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            // Disposing the first service is the public cache-clear boundary. The match below
            // creates a fresh composition and must reload both .shm and metadata from storage.
            await learningRuntime.Service.DisposeAsync();
        }
    }

    private static string ResolveStoredPath(RuntimePaths paths, string relativePath)
    {
        return Path.Combine(
            paths.TemplateResourceDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void AssertAcceptedPose(
        TemplateMatchBatchResult result,
        Point2D expectedCenter,
        double expectedAngleDeg,
        double expectedScale)
    {
        Assert.True(result.HasMatch, DescribeMatchFailure(result));
        Assert.Equal(InspectionOutcome.Ok, result.Outcome);
        Assert.Null(result.Diagnostic);
        TemplateMatchBatchCandidate candidate = Assert.Single(result.Matches);

        double centerError = Math.Sqrt(
            Math.Pow(candidate.Pose.X - expectedCenter.X, 2) +
            Math.Pow(candidate.Pose.Y - expectedCenter.Y, 2));
        Assert.True(
            centerError <= 2,
            $"Center error {centerError:F4}px exceeded 2px. " +
            $"Expected=({expectedCenter.X:F4},{expectedCenter.Y:F4}); " +
            $"Actual=({candidate.Pose.X:F4},{candidate.Pose.Y:F4}).");

        double angleError = Math.Abs(NormalizeAngle(candidate.Pose.Angle - expectedAngleDeg));
        Assert.True(
            angleError <= 1,
            $"Angle error {angleError:F4} deg exceeded 1 deg. " +
            $"Expected={expectedAngleDeg:F4} deg; Actual={candidate.Pose.Angle:F4} deg.");

        double scaleError = Math.Abs(candidate.Pose.Scale - expectedScale);
        Assert.True(
            scaleError <= 0.02,
            $"Scale error {scaleError:F6} exceeded 0.02. " +
            $"Expected={expectedScale:F6}; Actual={candidate.Pose.Scale:F6}.");
    }

    private static double NormalizeAngle(double angleDeg)
    {
        double normalized = (angleDeg + 180) % 360;
        if (normalized < 0)
        {
            normalized += 360;
        }

        return normalized - 180;
    }

    private static string DescribeLearningFailure(TemplateLearningResult result)
    {
        return $"Learning failed. Engine={result.Engine}; Message={result.Message}; " +
               $"Code={result.Diagnostic?.Code ?? "<none>"}; " +
               $"Stage={result.Diagnostic?.FailureStage ?? "<none>"}; " +
               $"Technical={result.Diagnostic?.TechnicalDetails ?? "<none>"}.";
    }

    private static string DescribeMatchFailure(TemplateMatchBatchResult result)
    {
        return $"Matching failed. Engine={result.Engine}; Outcome={result.Outcome}; " +
               $"Message={result.Message}; Code={result.Diagnostic?.Code ?? "<none>"}; " +
               $"Stage={result.Diagnostic?.FailureStage ?? "<none>"}; " +
               $"Technical={result.Diagnostic?.TechnicalDetails ?? "<none>"}.";
    }

    private static string DescribeUnexpectedAcceptance(
        SyntheticHalconNegativeCase negativeCase,
        TemplateMatchBatchResult result)
    {
        string candidates = string.Join(
            " | ",
            result.Matches.Select(candidate =>
                $"score={candidate.Score:R}, outer={candidate.OuterCoverage:R}, " +
                $"inner={candidate.InnerCoverage:R}, polarity={candidate.PolarityAgreement:R}, " +
                $"pose=({candidate.Pose.X:R},{candidate.Pose.Y:R}," +
                $"{candidate.Pose.Angle:R},{candidate.Pose.Scale:R})"));
        return $"Negative case '{negativeCase}' was accepted. Candidates: {candidates}.";
    }

    private sealed class IntegrationDiagnosticSink : ITemplateMatchingDiagnosticSink
    {
        public static IntegrationDiagnosticSink Instance { get; } = new();

        public void Warning(string source, string message)
        {
        }

        public void Error(string source, string message)
        {
        }
    }
}
