using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class OpenCvTemplateMatcherQualityTests
{
    [Fact]
    public void LearnOpenCvShapeWritesV2ScoringMetadata()
    {
        var learned = TemplateMatcher.Learn(
            TemplateMatcherTestData.CreateTrainingFrame(),
            null,
            TemplateMatcherTestData.CreateLearningParameters());

        Assert.Equal("2", learned["shapeScoreVersion"]);
        Assert.Equal("3", learned["shapeCoverageDistance"]);
    }

    [Fact]
    public void LearnOpenCvShapeWithoutEdgesStillWritesV2ScoringMetadata()
    {
        var learned = TemplateMatcher.Learn(
            TemplateMatcherTestData.CreateUniformTrainingFrame(),
            null,
            TemplateMatcherTestData.CreateLearningParameters());

        Assert.Equal("2", learned["shapeScoreVersion"]);
        Assert.Equal("3", learned["shapeCoverageDistance"]);
    }

    [Fact]
    public void ShapeV2ExtraEdgesInsideSupportLowerReverseScore()
    {
        var runtime = TemplateMatcherTestData.LearnRuntimeParameters();
        var legacy = new Dictionary<string, string>(runtime, StringComparer.OrdinalIgnoreCase);
        legacy.Remove("shapeScoreVersion");
        var cluttered = TemplateMatcherTestData.CreateSearchFrame(extraEdges: true);

        var v1 = TemplateMatcher.Match(cluttered, null, legacy);
        var v2 = TemplateMatcher.Match(cluttered, null, runtime);

        Assert.True(v1.HasMatch, v1.Message);
        Assert.True(v1.Score > 0.95, $"Legacy score was {v1.Score:0.000}.");
        Assert.Null(v1.ShapeCoverage);
        Assert.Null(v1.ShapeReverseScore);
        Assert.True(v2.HasMatch, v2.Message);
        Assert.NotNull(v2.ShapeCoverage);
        Assert.NotNull(v2.ShapeReverseScore);
        Assert.True(v2.ShapeCoverage > 0.95, $"Coverage was {v2.ShapeCoverage:0.000}.");
        Assert.True(v2.ShapeReverseScore < 0.85, $"Reverse score was {v2.ShapeReverseScore:0.000}.");
        Assert.True(v2.Score < 0.85, $"V2 score was {v2.Score:0.000}.");
    }

    [Fact]
    public void ShapeV2CentralFragmentHasLowCoverageAndFailsThreshold()
    {
        var runtime = TemplateMatcherTestData.LearnRuntimeParameters();

        var result = TemplateMatcher.Match(
            TemplateMatcherTestData.CreateSearchFrame(fragmentOnly: true),
            null,
            runtime);

        Assert.True(result.HasMatch, result.Message);
        Assert.NotNull(result.ShapeCoverage);
        Assert.InRange(result.ShapeCoverage.Value, 0, 0.75);
        Assert.True(result.Score < 0.85, $"Score was {result.Score:0.000}.");
        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
    }

    [Fact]
    public void ShapeV2CompleteProductPreservesHighQuality()
    {
        var runtime = TemplateMatcherTestData.LearnRuntimeParameters();

        var result = TemplateMatcher.Match(TemplateMatcherTestData.CreateSearchFrame(), null, runtime);

        Assert.True(result.HasMatch, result.Message);
        Assert.True(result.Score > 0.90, $"Score was {result.Score:0.000}.");
        Assert.NotNull(result.ShapeCoverage);
        Assert.NotNull(result.ShapeReverseScore);
        Assert.True(result.ShapeCoverage > 0.95, $"Coverage was {result.ShapeCoverage:0.000}.");
        Assert.True(result.ShapeReverseScore > 0.95, $"Reverse score was {result.ShapeReverseScore:0.000}.");
    }

    [Fact]
    public void ShapeV2RotatedCanvasUsesItsShortSideForDefaultScale()
    {
        var defaultScale = TemplateMatcherTestData.LearnRuntimeParameters();
        defaultScale["angleStart"] = "-45";
        defaultScale["angleExtent"] = "0.5";
        defaultScale["angleStep"] = "1";
        defaultScale["minScore"] = "0";
        var explicitScale = new Dictionary<string, string>(defaultScale, StringComparer.OrdinalIgnoreCase)
        {
            ["shapeScoreScale"] = "30"
        };
        var search = TemplateMatcherTestData.CreateRotatedSearchFrameWithLocalEdge(45);

        var defaultResult = TemplateMatcher.Match(search, null, defaultScale);
        var explicitResult = TemplateMatcher.Match(search, null, explicitScale);

        Assert.True(defaultResult.HasMatch, defaultResult.Message);
        Assert.True(explicitResult.HasMatch, explicitResult.Message);
        Assert.NotNull(defaultResult.ShapeReverseScore);
        Assert.NotNull(explicitResult.ShapeReverseScore);
        Assert.Equal(45, defaultResult.Pose.Angle, 3);
        Assert.Equal(explicitResult.ShapeReverseScore.Value, defaultResult.ShapeReverseScore.Value, 6);
        Assert.Equal(explicitResult.Score, defaultResult.Score, 6);
    }

    [Fact]
    public void ShapeV2LowEdgeModelDoesNotFallBackToGrayMatching()
    {
        var frame = TemplateMatcherTestData.CreateLowContrastGradientFrame();
        var parameters = TemplateMatcherTestData.CreateLearningParameters();
        parameters["angleStart"] = "0";
        parameters["angleExtent"] = "0";
        var learned = TemplateMatcher.Learn(frame, null, parameters);
        foreach (var parameter in learned)
        {
            parameters[parameter.Key] = parameter.Value;
        }

        var result = TemplateMatcher.Match(frame, null, parameters);

        Assert.Equal("2", parameters["shapeScoreVersion"]);
        Assert.False(result.HasMatch);
        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    public void LegacyShapeScoreVersionsKeepV1DiagnosticsNull(string? version)
    {
        var runtime = TemplateMatcherTestData.LearnRuntimeParameters();
        if (version is null)
        {
            runtime.Remove("shapeScoreVersion");
        }
        else
        {
            runtime["shapeScoreVersion"] = version;
        }

        var result = TemplateMatcher.Match(TemplateMatcherTestData.CreateSearchFrame(), null, runtime);

        Assert.True(result.HasMatch, result.Message);
        Assert.True(result.Score > 0.95, $"Legacy score was {result.Score:0.000}.");
        Assert.Null(result.ShapeCoverage);
        Assert.Null(result.ShapeReverseScore);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("invalid")]
    public void UnsupportedShapeScoreVersionsFailClosed(string version)
    {
        var runtime = TemplateMatcherTestData.LearnRuntimeParameters();
        runtime["shapeScoreVersion"] = version;

        var result = TemplateMatcher.Match(TemplateMatcherTestData.CreateSearchFrame(), null, runtime);

        Assert.False(result.HasMatch);
        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal("Unsupported OpenCV Shape score version.", result.Message);
    }

    [Fact]
    public void ShapeV2NonFiniteSettingsUseSafeDefaults()
    {
        var runtime = TemplateMatcherTestData.LearnRuntimeParameters();
        runtime["shapeScoreScale"] = "NaN";
        runtime["shapeCoverageDistance"] = "Infinity";

        var result = TemplateMatcher.Match(
            TemplateMatcherTestData.CreateSearchFrame(extraEdges: true),
            null,
            runtime);

        Assert.True(result.HasMatch, result.Message);
        Assert.True(double.IsFinite(result.Score), $"Score was {result.Score}.");
        Assert.NotNull(result.ShapeCoverage);
        Assert.NotNull(result.ShapeReverseScore);
        Assert.True(result.ShapeCoverage > 0.95, $"Coverage was {result.ShapeCoverage:0.000}.");
        Assert.True(result.ShapeReverseScore < 0.85, $"Reverse score was {result.ShapeReverseScore:0.000}.");
        Assert.True(result.Score < 0.85, $"Score was {result.Score:0.000}.");
    }
}
