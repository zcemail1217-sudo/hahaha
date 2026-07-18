using System.Globalization;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class TemplateModelGenerationFingerprintTests
{
    [Fact]
    public void StrictDefaultsHaveStableGoldenFingerprint()
    {
        HalconTemplateMatchingParameters parsed = ParseStrict();

        string fingerprint = TemplateModelGenerationFingerprint.Compute(
            TemplateModelGenerationParameters.From(parsed));

        Assert.Equal(
            "458a88ece52cacc84069d4125a3ef7b6fb2f69e2a47a0a7ba05766f28fc17aed",
            fingerprint);
    }

    [Fact]
    public void AutoNumLevelsIsCanonicalIntegerZero()
    {
        HalconTemplateMatchingParameters parsed = ParseStrict();

        TemplateModelGenerationParameters generation =
            TemplateModelGenerationParameters.From(parsed);

        Assert.Equal(0, generation.NumLevels);
    }

    [Fact]
    public void GenerationSnapshotContainsOnlyModelGenerationFields()
    {
        string[] properties = typeof(TemplateModelGenerationParameters)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["AngleExtentDeg", "AngleStartDeg", "NumLevels", "ScaleMax", "ScaleMin"],
            properties);
    }

    [Theory]
    [InlineData("AngleStartDeg")]
    [InlineData("AngleExtentDeg")]
    [InlineData("ScaleMin")]
    [InlineData("ScaleMax")]
    [InlineData("NumLevels")]
    public void EveryGenerationFieldChangesFingerprint(string field)
    {
        TemplateModelGenerationParameters baseline =
            TemplateModelGenerationParameters.From(ParseStrict());
        TemplateModelGenerationParameters changed = field switch
        {
            "AngleStartDeg" => baseline with { AngleStartDeg = baseline.AngleStartDeg + 1 },
            "AngleExtentDeg" => baseline with { AngleExtentDeg = baseline.AngleExtentDeg - 1 },
            "ScaleMin" => baseline with { ScaleMin = baseline.ScaleMin + 0.01 },
            "ScaleMax" => baseline with { ScaleMax = baseline.ScaleMax - 0.01 },
            "NumLevels" => baseline with { NumLevels = 4 },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };

        Assert.NotEqual(
            TemplateModelGenerationFingerprint.Compute(baseline),
            TemplateModelGenerationFingerprint.Compute(changed));
    }

    [Fact]
    public void EveryRuntimeFieldLeavesGenerationFingerprintUnchanged()
    {
        HalconTemplateMatchingParameters baseline = ParseStrict();
        string expected = Fingerprint(baseline);
        HalconTemplateMatchingParameters[] changed =
        [
            baseline with { CandidateMinScore = 0.12 },
            baseline with { OuterCoverageMin = 0.11 },
            baseline with { InnerCoverageMin = 0.22 },
            baseline with { EdgeTolerancePx = 9.5 },
            baseline with { PolarityAgreementMin = 0.33 },
            baseline with { CandidateMaxOverlap = 0.91 },
            baseline with { MaxOverlap = 0.13 },
            baseline with { Greediness = 0.31 },
            baseline with { SubPixel = "future-runtime-mode" },
            baseline with { CandidateLimit = 77 },
            baseline with { OperatorTimeoutMs = 12345 },
            baseline with { ExpectedCount = 9 }
        ];

        Assert.All(changed, parameters => Assert.Equal(expected, Fingerprint(parameters)));
    }

    [Fact]
    public void FingerprintFormattingDoesNotDependOnCurrentCulture()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");

            Assert.Equal(
                "458a88ece52cacc84069d4125a3ef7b6fb2f69e2a47a0a7ba05766f28fc17aed",
                Fingerprint(ParseStrict()));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private static HalconTemplateMatchingParameters ParseStrict()
    {
        Dictionary<string, string> defaults =
            TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single);
        return TemplateMatchingParameterCatalog.ParseHalcon(defaults, TemplateMatchCardinality.Single);
    }

    private static string Fingerprint(HalconTemplateMatchingParameters parameters)
    {
        return TemplateModelGenerationFingerprint.Compute(
            TemplateModelGenerationParameters.From(parameters));
    }
}
