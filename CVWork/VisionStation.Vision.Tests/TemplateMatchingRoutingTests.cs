using OpenCvSharp;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class TemplateMatchingRoutingTests
{
    [Theory]
    [InlineData(null, TemplateMatchingEngine.OpenCv)]
    [InlineData("OpenCv", TemplateMatchingEngine.OpenCv)]
    [InlineData("opencv", TemplateMatchingEngine.OpenCv)]
    [InlineData("ManagedNcc", TemplateMatchingEngine.ManagedNcc)]
    [InlineData("Halcon", TemplateMatchingEngine.Halcon)]
    [InlineData("  Halcon  ", TemplateMatchingEngine.Halcon)]
    public void ResolverNormalizesOnlySupportedEngines(string? value, TemplateMatchingEngine expected)
    {
        var parameters = value is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["engine"] = value };

        Assert.Equal(expected, TemplateMatchingEngineResolver.Resolve(parameters));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Halconn")]
    [InlineData("Shape")]
    [InlineData("Unknown")]
    public void ExplicitUnknownEngineNeverFallsBackToOpenCv(string value)
    {
        var exception = Assert.Throws<TemplateMatchingConfigurationException>(() =>
            TemplateMatchingEngineResolver.Resolve(
                new Dictionary<string, string> { ["engine"] = value }));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigUnknownEngine, exception.Code);
    }

    [Fact]
    public void StaticHalconMatchReturnsServiceRequiredWithoutUsingOpenCv()
    {
        var result = TemplateMatcher.Match(
            CreateFrame(),
            null,
            new Dictionary<string, string> { ["engine"] = "Halcon" });

        Assert.False(result.HasMatch);
        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal(TemplateMatchingEngine.Halcon, result.Engine);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigServiceRequired, result.FailureCode);
        Assert.Equal(TemplateMatchingFailureStages.Configuration, result.FailureStage);
    }

    [Fact]
    public void StaticHalconLearnThrowsServiceRequiredWithoutUsingOpenCv()
    {
        var exception = Assert.Throws<TemplateMatchingConfigurationException>(() =>
            TemplateMatcher.Learn(
                CreateFrame(),
                null,
                new Dictionary<string, string> { ["engine"] = "Halcon" }));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigServiceRequired, exception.Code);
    }

    [Theory]
    [InlineData("CircularBlob")]
    [InlineData("Orb")]
    [InlineData("Ncc")]
    public void StaticHalconRejectsUnsupportedSingleModeBeforeServiceRequirement(string mode)
    {
        var parameters = new Dictionary<string, string>
        {
            ["engine"] = "Halcon",
            ["matchMode"] = mode
        };

        var match = TemplateMatcher.Match(CreateFrame(), null, parameters);
        var learn = Assert.Throws<TemplateMatchingConfigurationException>(() =>
            TemplateMatcher.Learn(CreateFrame(), null, parameters));

        Assert.Equal(TemplateMatchingEngine.Halcon, match.Engine);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigUnsupportedMode, match.FailureCode);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigUnsupportedMode, learn.Code);
    }

    [Fact]
    public void ManagedNccMultiTargetReturnsUnsupportedMode()
    {
        var result = MultiTargetMatcher.Match(
            CreateFrame(),
            null,
            new Dictionary<string, string> { ["engine"] = "ManagedNcc" });

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Empty(result.Matches);
        Assert.Equal(TemplateMatchingEngine.ManagedNcc, result.Engine);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigUnsupportedMode, result.FailureCode);
    }

    [Fact]
    public void StaticHalconMultiTargetRejectsCircularBlobBeforeServiceRequirement()
    {
        var result = MultiTargetMatcher.Match(
            CreateFrame(),
            null,
            new Dictionary<string, string>
            {
                ["engine"] = "Halcon",
                ["multiMatchMode"] = "CircularBlob"
            });

        Assert.Equal(TemplateMatchingEngine.Halcon, result.Engine);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigUnsupportedMode, result.FailureCode);
    }

    [Fact]
    public void StaticHalconShapeMultiTargetReturnsServiceRequired()
    {
        var result = MultiTargetMatcher.Match(
            CreateFrame(),
            null,
            new Dictionary<string, string>
            {
                ["engine"] = "Halcon",
                ["multiMatchMode"] = "Shape"
            });

        Assert.Equal(TemplateMatchingEngine.Halcon, result.Engine);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigServiceRequired, result.FailureCode);
    }

    [Fact]
    public void UnknownEngineReturnsStructuredUnknownResultsFromBothFacades()
    {
        var parameters = new Dictionary<string, string> { ["engine"] = "Halconn" };

        var single = TemplateMatcher.Match(CreateFrame(), null, parameters);
        var multi = MultiTargetMatcher.Match(CreateFrame(), null, parameters);

        Assert.Equal(TemplateMatchingEngine.Unknown, single.Engine);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigUnknownEngine, single.FailureCode);
        Assert.Equal(TemplateMatchingEngine.Unknown, multi.Engine);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigUnknownEngine, multi.FailureCode);
    }

    [Fact]
    public void InternalMatOverloadsUseTheSameStrictRouting()
    {
        var frame = CreateFrame();
        var parameters = new Dictionary<string, string> { ["engine"] = "Halcon" };
        using var gray = new Mat(frame.Height, frame.Width, MatType.CV_8UC1, Scalar.Black);

        var single = TemplateMatcher.Match(frame, null, parameters, gray);
        var multi = MultiTargetMatcher.Match(frame, null, parameters, gray);

        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigServiceRequired, single.FailureCode);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigServiceRequired, multi.FailureCode);
    }

    [Fact]
    public void OpenCvRouteIgnoresCorruptInactiveHalconParameters()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "OpenCv",
            ["halcon.operatorTimeoutMs"] = "garbage"
        };

        var single = TemplateMatcher.Match(CreateFrame(), null, parameters);
        var multi = MultiTargetMatcher.Match(CreateFrame(), null, parameters);

        Assert.Equal(TemplateMatchingEngine.OpenCv, single.Engine);
        Assert.Null(single.FailureCode);
        Assert.Equal(TemplateMatchingEngine.OpenCv, multi.Engine);
        Assert.Null(multi.FailureCode);
    }

    [Theory]
    [InlineData("OpenCv", TemplateMatchingEngine.OpenCv)]
    [InlineData("ManagedNcc", TemplateMatchingEngine.ManagedNcc)]
    public void StaticSingleMatchPublishesNormalizedSuccessfulRouteEngine(
        string configured,
        TemplateMatchingEngine expected)
    {
        var result = TemplateMatcher.Match(
            CreateFrame(),
            null,
            new Dictionary<string, string> { ["engine"] = configured });

        Assert.Equal(expected, result.Engine);
    }

    [Fact]
    public void CancellationIsNotConvertedIntoConfigurationFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            MultiTargetMatcher.Match(
                CreateFrame(),
                null,
                new Dictionary<string, string> { ["engine"] = "OpenCv" },
                cancellation.Token));
    }

    [Fact]
    public void PositionalResultsKeepExistingConstructionAndDeconstruction()
    {
        var searchRegion = new TemplateSearchRegion(1, 2, 3, 4);
        var single = new TemplateMatchResult(
            false,
            InspectionOutcome.Ng,
            0,
            new Pose2D(0, 0, 0),
            0,
            0,
            0,
            0,
            searchRegion,
            "message",
            false);
        var multi = new MultiTargetMatchResult(
            InspectionOutcome.Ng,
            "message",
            Array.Empty<MultiTargetMatchCandidate>(),
            searchRegion,
            false);

        var (hasMatch, _, _, _, _, _, _, _, _, _, _, _, _) = single;
        var (outcome, _, matches, _, _) = multi;

        Assert.False(hasMatch);
        Assert.Equal(InspectionOutcome.Ng, outcome);
        Assert.Empty(matches);
    }

    [Fact]
    public void DiagnosticsKeepOperatorMessageSeparateFromTechnicalDetails()
    {
        const string technicalMarker = "TECHNICAL-DETAIL-MARKER";
        foreach (var code in AllDiagnosticCodes())
        {
            var diagnostic = TemplateMatchingDiagnostics.Create(code, technicalMarker);

            Assert.Equal(code, diagnostic.Code);
            Assert.Matches("[\\u4e00-\\u9fff]", diagnostic.UserMessage);
            Assert.DoesNotContain(technicalMarker, diagnostic.UserMessage, StringComparison.Ordinal);
            Assert.Equal(technicalMarker, diagnostic.TechnicalDetails);
            Assert.False(string.IsNullOrWhiteSpace(diagnostic.FailureStage));
        }
    }

    private static IReadOnlyList<string> AllDiagnosticCodes()
    {
        return
        [
            TemplateMatchingDiagnosticCodes.ConfigUnknownEngine,
            TemplateMatchingDiagnosticCodes.ConfigServiceRequired,
            TemplateMatchingDiagnosticCodes.ConfigUnsupportedMode,
            TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
            TemplateMatchingDiagnosticCodes.RuntimeNotFound,
            TemplateMatchingDiagnosticCodes.RuntimeArchMismatch,
            TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch,
            TemplateMatchingDiagnosticCodes.LicenseUnavailable,
            TemplateMatchingDiagnosticCodes.ModelPathInvalid,
            TemplateMatchingDiagnosticCodes.ModelNotFound,
            TemplateMatchingDiagnosticCodes.ModelChecksumMismatch,
            TemplateMatchingDiagnosticCodes.ModelMetadataInvalid,
            TemplateMatchingDiagnosticCodes.ModelVersionMismatch,
            TemplateMatchingDiagnosticCodes.ModelRelearnRequired,
            TemplateMatchingDiagnosticCodes.ModelLoadFailed,
            TemplateMatchingDiagnosticCodes.ModelTemplateIncomplete,
            TemplateMatchingDiagnosticCodes.ModelContrastWeak,
            TemplateMatchingDiagnosticCodes.ModelInternalFeaturesWeak,
            TemplateMatchingDiagnosticCodes.MatchInvalidPose,
            TemplateMatchingDiagnosticCodes.MatchIncompleteAtBoundary,
            TemplateMatchingDiagnosticCodes.MatchPolarityMismatch,
            TemplateMatchingDiagnosticCodes.MatchOuterContourWeak,
            TemplateMatchingDiagnosticCodes.MatchInnerFeaturesWeak,
            TemplateMatchingDiagnosticCodes.MatchDuplicateOverlap,
            TemplateMatchingDiagnosticCodes.MatchTimeout,
            TemplateMatchingDiagnosticCodes.MatchCountMismatch,
            TemplateMatchingDiagnosticCodes.MatchCandidateLimitReached,
            TemplateMatchingDiagnosticCodes.MatchOperatorFailed
        ];
    }

    private static ImageFrame CreateFrame()
    {
        return new ImageFrame(
            "routing",
            32,
            32,
            32,
            PixelFormatKind.Gray8,
            new byte[32 * 32],
            DateTimeOffset.UnixEpoch,
            "test");
    }
}
