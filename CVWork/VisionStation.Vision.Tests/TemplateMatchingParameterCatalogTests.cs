using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class TemplateMatchingParameterCatalogTests
{
    public static IEnumerable<object[]> PresetCases()
    {
        yield return
        [
            TemplateMatchingPreset.Strict,
            "0.65", "0.90", "0.82", "3.0", "0.90", "0.70", "0.25", "0.80", "5000", "32", "128"
        ];
        yield return
        [
            TemplateMatchingPreset.Balanced,
            "0.58", "0.85", "0.75", "4.0", "0.85", "0.75", "0.30", "0.75", "7000", "48", "160"
        ];
        yield return
        [
            TemplateMatchingPreset.HighRecall,
            "0.50", "0.78", "0.65", "5.0", "0.78", "0.80", "0.35", "0.65", "10000", "64", "192"
        ];
    }

    [Theory]
    [MemberData(nameof(PresetCases))]
    public void PresetsLockEverySpecifiedValue(
        TemplateMatchingPreset preset,
        string candidateMinScore,
        string outerCoverageMin,
        string innerCoverageMin,
        string edgeTolerancePx,
        string polarityAgreementMin,
        string candidateMaxOverlap,
        string maxOverlap,
        string greediness,
        string operatorTimeoutMs,
        string singleCandidateLimit,
        string multiCandidateLimit)
    {
        var single = TemplateMatchingParameterCatalog.CreateDefaults(preset, TemplateMatchCardinality.Single);
        var multi = TemplateMatchingParameterCatalog.CreateDefaults(preset, TemplateMatchCardinality.ExactCount);

        Assert.Equal("Halcon", single[TemplateMatchingParameterCatalog.Engine]);
        Assert.Equal("Shape", single[TemplateMatchingParameterCatalog.MatchMode]);
        Assert.Equal("-180", single[TemplateMatchingParameterCatalog.AngleStartDeg]);
        Assert.Equal("360", single[TemplateMatchingParameterCatalog.AngleExtentDeg]);
        Assert.Equal("0.90", single[TemplateMatchingParameterCatalog.ScaleMin]);
        Assert.Equal("1.10", single[TemplateMatchingParameterCatalog.ScaleMax]);
        Assert.Equal(candidateMinScore, single[TemplateMatchingParameterCatalog.CandidateMinScore]);
        Assert.Equal(outerCoverageMin, single[TemplateMatchingParameterCatalog.OuterCoverageMin]);
        Assert.Equal(innerCoverageMin, single[TemplateMatchingParameterCatalog.InnerCoverageMin]);
        Assert.Equal(edgeTolerancePx, single[TemplateMatchingParameterCatalog.EdgeTolerancePx]);
        Assert.Equal(polarityAgreementMin, single[TemplateMatchingParameterCatalog.PolarityAgreementMin]);
        Assert.Equal(candidateMaxOverlap, single[TemplateMatchingParameterCatalog.CandidateMaxOverlap]);
        Assert.Equal(maxOverlap, single[TemplateMatchingParameterCatalog.MaxOverlap]);
        Assert.Equal(greediness, single[TemplateMatchingParameterCatalog.Greediness]);
        Assert.Equal("least_squares", single[TemplateMatchingParameterCatalog.SubPixel]);
        Assert.Equal("auto", single[TemplateMatchingParameterCatalog.NumLevels]);
        Assert.Equal(singleCandidateLimit, single[TemplateMatchingParameterCatalog.CandidateLimit]);
        Assert.Equal(operatorTimeoutMs, single[TemplateMatchingParameterCatalog.OperatorTimeoutMs]);
        Assert.False(single.ContainsKey(TemplateMatchingParameterCatalog.ExpectedCount));

        Assert.Equal(multiCandidateLimit, multi[TemplateMatchingParameterCatalog.CandidateLimit]);
        Assert.Equal("1", multi[TemplateMatchingParameterCatalog.ExpectedCount]);
        Assert.Equal(single.Count + 1, multi.Count);
    }

    [Fact]
    public void StrictPresetUsesSpecifiedSingleAndMultiCandidateLimits()
    {
        var single = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        var multi = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.ExactCount);

        Assert.Equal("32", single[TemplateMatchingParameterCatalog.CandidateLimit]);
        Assert.Equal("128", multi[TemplateMatchingParameterCatalog.CandidateLimit]);
        Assert.Equal("1", multi[TemplateMatchingParameterCatalog.ExpectedCount]);
    }

    [Fact]
    public void PresetCallsReturnIndependentDictionaries()
    {
        var first = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        var second = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);

        first[TemplateMatchingParameterCatalog.ScaleMin] = "9";

        Assert.Equal("0.90", second[TemplateMatchingParameterCatalog.ScaleMin]);
    }

    [Theory]
    [InlineData("100")]
    [InlineData("60000")]
    public void OperatorTimeoutAcceptsInclusiveBoundaries(string timeout)
    {
        var parameters = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        parameters[TemplateMatchingParameterCatalog.OperatorTimeoutMs] = timeout;

        var parsed = TemplateMatchingParameterCatalog.ParseHalcon(parameters, TemplateMatchCardinality.Single);

        Assert.Equal(int.Parse(timeout), parsed.OperatorTimeoutMs);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("99")]
    [InlineData("60001")]
    [InlineData("not-a-number")]
    public void OperatorTimeoutRejectsInvalidValues(string timeout)
    {
        AssertInvalid(
            TemplateMatchCardinality.Single,
            parameters => parameters[TemplateMatchingParameterCatalog.OperatorTimeoutMs] = timeout);
    }

    [Fact]
    public void MultiUsesLegacyMatchCountOnlyWhenExpectedCountIsMissingWithoutMutatingInput()
    {
        var parameters = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.ExactCount);
        parameters.Remove(TemplateMatchingParameterCatalog.ExpectedCount);
        parameters[TemplateMatchingParameterCatalog.LegacyMatchCount] = "7";
        var snapshot = parameters.ToArray();

        var parsed = TemplateMatchingParameterCatalog.ParseHalcon(parameters, TemplateMatchCardinality.ExactCount);

        Assert.Equal(7, parsed.ExpectedCount);
        Assert.Equal(snapshot, parameters.ToArray());
        Assert.False(parameters.ContainsKey(TemplateMatchingParameterCatalog.ExpectedCount));
    }

    [Fact]
    public void ExplicitExpectedCountTakesPriorityOverLegacyMatchCount()
    {
        var parameters = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.ExactCount);
        parameters[TemplateMatchingParameterCatalog.ExpectedCount] = "3";
        parameters[TemplateMatchingParameterCatalog.LegacyMatchCount] = "99";

        var parsed = TemplateMatchingParameterCatalog.ParseHalcon(parameters, TemplateMatchCardinality.ExactCount);

        Assert.Equal(3, parsed.ExpectedCount);
    }

    [Fact]
    public void SingleCardinalityDoesNotInterpretLegacyMatchCountAsExpectedCount()
    {
        var parameters = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        parameters[TemplateMatchingParameterCatalog.LegacyMatchCount] = "garbage";
        parameters[TemplateMatchingParameterCatalog.ExpectedCount] = "garbage";

        var parsed = TemplateMatchingParameterCatalog.ParseHalcon(parameters, TemplateMatchCardinality.Single);

        Assert.Equal(1, parsed.ExpectedCount);
        Assert.Equal(32, parsed.CandidateLimit);
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("2", "2")]
    [InlineData("100", "100")]
    public void MultiCandidateLimitMustBeStrictlyGreaterThanExpectedCount(
        string expectedCount,
        string candidateLimit)
    {
        AssertInvalid(
            TemplateMatchCardinality.ExactCount,
            parameters =>
            {
                parameters[TemplateMatchingParameterCatalog.ExpectedCount] = expectedCount;
                parameters[TemplateMatchingParameterCatalog.CandidateLimit] = candidateLimit;
            });
    }

    [Fact]
    public void InclusiveIntegerBoundsAreAccepted()
    {
        var low = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.ExactCount);
        low[TemplateMatchingParameterCatalog.ExpectedCount] = "1";
        low[TemplateMatchingParameterCatalog.CandidateLimit] = "2";
        low[TemplateMatchingParameterCatalog.EdgeTolerancePx] = "100";
        low[TemplateMatchingParameterCatalog.OuterCoverageMin] = "0";
        low[TemplateMatchingParameterCatalog.InnerCoverageMin] = "1";
        low[TemplateMatchingParameterCatalog.PolarityAgreementMin] = "0";
        low[TemplateMatchingParameterCatalog.CandidateMaxOverlap] = "1";
        low[TemplateMatchingParameterCatalog.MaxOverlap] = "1";
        low[TemplateMatchingParameterCatalog.Greediness] = "1";
        var high = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.ExactCount);
        high[TemplateMatchingParameterCatalog.ExpectedCount] = "100";
        high[TemplateMatchingParameterCatalog.CandidateLimit] = "512";

        var lowParsed = TemplateMatchingParameterCatalog.ParseHalcon(low, TemplateMatchCardinality.ExactCount);
        var highParsed = TemplateMatchingParameterCatalog.ParseHalcon(high, TemplateMatchCardinality.ExactCount);

        Assert.Equal((1, 2, 100d),
            (lowParsed.ExpectedCount, lowParsed.CandidateLimit, lowParsed.EdgeTolerancePx));
        Assert.Equal((100, 512), (highParsed.ExpectedCount, highParsed.CandidateLimit));
    }

    public static IEnumerable<object[]> InvalidHalconValues()
    {
        yield return [TemplateMatchingParameterCatalog.AngleStartDeg, "NaN"];
        yield return [TemplateMatchingParameterCatalog.AngleStartDeg, "Infinity"];
        yield return [TemplateMatchingParameterCatalog.AngleExtentDeg, "NaN"];
        yield return [TemplateMatchingParameterCatalog.AngleExtentDeg, "0"];
        yield return [TemplateMatchingParameterCatalog.AngleExtentDeg, "-1"];
        yield return [TemplateMatchingParameterCatalog.ScaleMin, "NaN"];
        yield return [TemplateMatchingParameterCatalog.ScaleMin, "0"];
        yield return [TemplateMatchingParameterCatalog.ScaleMin, "-1"];
        yield return [TemplateMatchingParameterCatalog.ScaleMax, "Infinity"];
        yield return [TemplateMatchingParameterCatalog.ScaleMax, "0"];
        yield return [TemplateMatchingParameterCatalog.CandidateMinScore, "NaN"];
        yield return [TemplateMatchingParameterCatalog.CandidateMinScore, "-0.1"];
        yield return [TemplateMatchingParameterCatalog.CandidateMinScore, "1.1"];
        yield return [TemplateMatchingParameterCatalog.OuterCoverageMin, "NaN"];
        yield return [TemplateMatchingParameterCatalog.OuterCoverageMin, "-0.1"];
        yield return [TemplateMatchingParameterCatalog.InnerCoverageMin, "1.1"];
        yield return [TemplateMatchingParameterCatalog.EdgeTolerancePx, "NaN"];
        yield return [TemplateMatchingParameterCatalog.EdgeTolerancePx, "0"];
        yield return [TemplateMatchingParameterCatalog.EdgeTolerancePx, "100.1"];
        yield return [TemplateMatchingParameterCatalog.PolarityAgreementMin, "Infinity"];
        yield return [TemplateMatchingParameterCatalog.PolarityAgreementMin, "-0.1"];
        yield return [TemplateMatchingParameterCatalog.CandidateMaxOverlap, "1.1"];
        yield return [TemplateMatchingParameterCatalog.MaxOverlap, "-0.1"];
        yield return [TemplateMatchingParameterCatalog.Greediness, "NaN"];
        yield return [TemplateMatchingParameterCatalog.Greediness, "1.1"];
        yield return [TemplateMatchingParameterCatalog.CandidateLimit, "1"];
        yield return [TemplateMatchingParameterCatalog.CandidateLimit, "513"];
        yield return [TemplateMatchingParameterCatalog.CandidateLimit, "2.5"];
        yield return [TemplateMatchingParameterCatalog.CandidateLimit, "garbage"];
        yield return [TemplateMatchingParameterCatalog.SubPixel, "none"];
        yield return [TemplateMatchingParameterCatalog.NumLevels, "0"];
        yield return [TemplateMatchingParameterCatalog.NumLevels, "-1"];
        yield return [TemplateMatchingParameterCatalog.NumLevels, "1.5"];
        yield return [TemplateMatchingParameterCatalog.NumLevels, "garbage"];
    }

    [Theory]
    [MemberData(nameof(InvalidHalconValues))]
    public void ParserRejectsInvalidFiniteAndDomainValues(string key, string value)
    {
        AssertInvalid(
            TemplateMatchCardinality.Single,
            parameters => parameters[key] = value);
    }

    [Fact]
    public void ParserRejectsReversedScaleRange()
    {
        AssertInvalid(
            TemplateMatchCardinality.Single,
            parameters =>
            {
                parameters[TemplateMatchingParameterCatalog.ScaleMin] = "1.2";
                parameters[TemplateMatchingParameterCatalog.ScaleMax] = "1.1";
            });
    }

    [Fact]
    public void ParserRejectsFinalOverlapThatExceedsCandidateOverlap()
    {
        AssertInvalid(
            TemplateMatchCardinality.Single,
            parameters =>
            {
                parameters[TemplateMatchingParameterCatalog.CandidateMaxOverlap] = "0.2";
                parameters[TemplateMatchingParameterCatalog.MaxOverlap] = "0.3";
            });
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("1.5")]
    [InlineData("garbage")]
    public void MultiRejectsInvalidExpectedCount(string expectedCount)
    {
        AssertInvalid(
            TemplateMatchCardinality.ExactCount,
            parameters => parameters[TemplateMatchingParameterCatalog.ExpectedCount] = expectedCount);
    }

    [Theory]
    [InlineData("auto", 0)]
    [InlineData("1", 1)]
    [InlineData("8", 8)]
    public void NumLevelsNormalizesAutoAndPositiveIntegers(string configured, int expected)
    {
        var parameters = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        parameters[TemplateMatchingParameterCatalog.NumLevels] = configured;

        var parsed = TemplateMatchingParameterCatalog.ParseHalcon(parameters, TemplateMatchCardinality.Single);

        Assert.Equal(expected, parsed.NumLevels);
    }

    [Fact]
    public void HalconParserIgnoresCorruptInactiveOpenCvParameters()
    {
        var parameters = TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        parameters["angleStart"] = "garbage";
        parameters["angleExtent"] = "garbage";
        parameters["modelPath"] = "\0invalid";

        var parsed = TemplateMatchingParameterCatalog.ParseHalcon(parameters, TemplateMatchCardinality.Single);

        Assert.Equal(-180, parsed.AngleStartDeg);
        Assert.Equal(360, parsed.AngleExtentDeg);
    }

    private static void AssertInvalid(
        TemplateMatchCardinality cardinality,
        Action<Dictionary<string, string>> mutate)
    {
        var parameters = TemplateMatchingParameterCatalog.CreateStrictDefaults(cardinality);
        mutate(parameters);

        var exception = Assert.Throws<TemplateMatchingConfigurationException>(() =>
            TemplateMatchingParameterCatalog.ParseHalcon(parameters, cardinality));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, exception.Code);
    }
}
