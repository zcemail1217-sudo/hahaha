using System.Globalization;

namespace VisionStation.Vision;

public static class TemplateMatchingParameterCatalog
{
    public const string Engine = "engine";
    public const string MatchMode = "matchMode";
    public const string AngleStartDeg = "halcon.angleStartDeg";
    public const string AngleExtentDeg = "halcon.angleExtentDeg";
    public const string ScaleMin = "halcon.scaleMin";
    public const string ScaleMax = "halcon.scaleMax";
    public const string CandidateMinScore = "halcon.candidateMinScore";
    public const string OuterCoverageMin = "halcon.outerCoverageMin";
    public const string InnerCoverageMin = "halcon.innerCoverageMin";
    public const string EdgeTolerancePx = "halcon.edgeTolerancePx";
    public const string PolarityAgreementMin = "halcon.polarityAgreementMin";
    public const string CandidateMaxOverlap = "halcon.candidateMaxOverlap";
    public const string MaxOverlap = "halcon.maxOverlap";
    public const string Greediness = "halcon.greediness";
    public const string SubPixel = "halcon.subPixel";
    public const string NumLevels = "halcon.numLevels";
    public const string CandidateLimit = "halcon.candidateLimit";
    public const string OperatorTimeoutMs = "halcon.operatorTimeoutMs";
    public const string ExpectedCount = "expectedCount";
    public const string LegacyMatchCount = "matchCount";

    public static Dictionary<string, string> CreateStrictDefaults(TemplateMatchCardinality cardinality)
    {
        return CreateDefaults(TemplateMatchingPreset.Strict, cardinality);
    }

    public static Dictionary<string, string> CreateBalancedDefaults(TemplateMatchCardinality cardinality)
    {
        return CreateDefaults(TemplateMatchingPreset.Balanced, cardinality);
    }

    public static Dictionary<string, string> CreateHighRecallDefaults(TemplateMatchCardinality cardinality)
    {
        return CreateDefaults(TemplateMatchingPreset.HighRecall, cardinality);
    }

    public static Dictionary<string, string> CreateDefaults(
        TemplateMatchingPreset preset,
        TemplateMatchCardinality cardinality)
    {
        var values = preset switch
        {
            TemplateMatchingPreset.Strict => new PresetValues(
                "0.65", "0.90", "0.82", "3.0", "0.90", "0.70", "0.25", "0.80", "5000", "32", "128"),
            TemplateMatchingPreset.Balanced => new PresetValues(
                "0.58", "0.85", "0.75", "4.0", "0.85", "0.75", "0.30", "0.75", "7000", "48", "160"),
            TemplateMatchingPreset.HighRecall => new PresetValues(
                "0.50", "0.78", "0.65", "5.0", "0.78", "0.80", "0.35", "0.65", "10000", "64", "192"),
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null)
        };
        var candidateLimit = cardinality switch
        {
            TemplateMatchCardinality.Single => values.SingleCandidateLimit,
            TemplateMatchCardinality.ExactCount => values.MultiCandidateLimit,
            _ => throw new ArgumentOutOfRangeException(nameof(cardinality), cardinality, null)
        };
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Engine] = "Halcon",
            [MatchMode] = "Shape",
            [AngleStartDeg] = "-180",
            [AngleExtentDeg] = "360",
            [ScaleMin] = "0.90",
            [ScaleMax] = "1.10",
            [CandidateMinScore] = values.CandidateMinScore,
            [OuterCoverageMin] = values.OuterCoverageMin,
            [InnerCoverageMin] = values.InnerCoverageMin,
            [EdgeTolerancePx] = values.EdgeTolerancePx,
            [PolarityAgreementMin] = values.PolarityAgreementMin,
            [CandidateMaxOverlap] = values.CandidateMaxOverlap,
            [MaxOverlap] = values.MaxOverlap,
            [Greediness] = values.Greediness,
            [SubPixel] = "least_squares",
            [NumLevels] = "auto",
            [CandidateLimit] = candidateLimit,
            [OperatorTimeoutMs] = values.OperatorTimeoutMs
        };
        if (cardinality == TemplateMatchCardinality.ExactCount)
        {
            parameters[ExpectedCount] = "1";
        }

        return parameters;
    }

    public static HalconTemplateMatchingParameters ParseHalcon(
        IReadOnlyDictionary<string, string> parameters,
        TemplateMatchCardinality cardinality)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var defaults = CreateStrictDefaults(cardinality);
        var angleStartDeg = ReadFiniteDouble(parameters, defaults, AngleStartDeg);
        var angleExtentDeg = ReadFiniteDouble(parameters, defaults, AngleExtentDeg);
        var angleEndDeg = angleStartDeg + angleExtentDeg;
        if (angleExtentDeg <= 0 || !double.IsFinite(angleEndDeg) || angleEndDeg <= angleStartDeg)
        {
            ThrowInvalid(AngleExtentDeg, ReadRaw(parameters, defaults, AngleExtentDeg));
        }

        var scaleMin = ReadPositiveFiniteDouble(parameters, defaults, ScaleMin);
        var scaleMax = ReadPositiveFiniteDouble(parameters, defaults, ScaleMax);
        if (scaleMin > scaleMax)
        {
            ThrowInvalid(ScaleMin, $"{scaleMin:R} > {scaleMax:R}");
        }

        var candidateMinScore = ReadUnitInterval(parameters, defaults, CandidateMinScore);
        var outerCoverageMin = ReadUnitInterval(parameters, defaults, OuterCoverageMin);
        var innerCoverageMin = ReadUnitInterval(parameters, defaults, InnerCoverageMin);
        var edgeTolerancePx = ReadFiniteDouble(parameters, defaults, EdgeTolerancePx);
        if (edgeTolerancePx <= 0 || edgeTolerancePx > 100)
        {
            ThrowInvalid(EdgeTolerancePx, ReadRaw(parameters, defaults, EdgeTolerancePx));
        }

        var polarityAgreementMin = ReadUnitInterval(parameters, defaults, PolarityAgreementMin);
        var candidateMaxOverlap = ReadUnitInterval(parameters, defaults, CandidateMaxOverlap);
        var maxOverlap = ReadUnitInterval(parameters, defaults, MaxOverlap);
        if (maxOverlap > candidateMaxOverlap)
        {
            ThrowInvalid(MaxOverlap, $"{maxOverlap:R} > {candidateMaxOverlap:R}");
        }

        var greediness = ReadUnitInterval(parameters, defaults, Greediness);
        var subPixel = ReadRaw(parameters, defaults, SubPixel).Trim();
        if (!string.Equals(subPixel, "least_squares", StringComparison.Ordinal))
        {
            ThrowInvalid(SubPixel, subPixel);
        }

        var numLevels = ParseNumLevels(ReadRaw(parameters, defaults, NumLevels));
        var candidateLimit = ReadInt(parameters, defaults, CandidateLimit, 2, 512);
        var operatorTimeoutMs = ReadInt(parameters, defaults, OperatorTimeoutMs, 100, 60000);
        var expectedCount = cardinality == TemplateMatchCardinality.ExactCount
            ? ReadExpectedCount(parameters, defaults)
            : 1;
        if (cardinality == TemplateMatchCardinality.ExactCount && candidateLimit <= expectedCount)
        {
            ThrowInvalid(CandidateLimit, $"{candidateLimit} <= {expectedCount}");
        }

        return new HalconTemplateMatchingParameters(
            angleStartDeg,
            angleExtentDeg,
            scaleMin,
            scaleMax,
            candidateMinScore,
            outerCoverageMin,
            innerCoverageMin,
            edgeTolerancePx,
            polarityAgreementMin,
            candidateMaxOverlap,
            maxOverlap,
            greediness,
            subPixel,
            numLevels,
            candidateLimit,
            operatorTimeoutMs,
            expectedCount);
    }

    private static int ReadExpectedCount(
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyDictionary<string, string> defaults)
    {
        if (parameters.TryGetValue(ExpectedCount, out var expectedRaw))
        {
            return ParseInt(expectedRaw, ExpectedCount, 1, 100);
        }

        if (parameters.TryGetValue(LegacyMatchCount, out var legacyRaw))
        {
            return ParseInt(legacyRaw, LegacyMatchCount, 1, 100);
        }

        return ParseInt(defaults[ExpectedCount], ExpectedCount, 1, 100);
    }

    private static double ReadPositiveFiniteDouble(
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyDictionary<string, string> defaults,
        string key)
    {
        var value = ReadFiniteDouble(parameters, defaults, key);
        if (value <= 0)
        {
            ThrowInvalid(key, ReadRaw(parameters, defaults, key));
        }

        return value;
    }

    private static double ReadUnitInterval(
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyDictionary<string, string> defaults,
        string key)
    {
        var value = ReadFiniteDouble(parameters, defaults, key);
        if (value < 0 || value > 1)
        {
            ThrowInvalid(key, ReadRaw(parameters, defaults, key));
        }

        return value;
    }

    private static double ReadFiniteDouble(
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyDictionary<string, string> defaults,
        string key)
    {
        var raw = ReadRaw(parameters, defaults, key);
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value))
        {
            ThrowInvalid(key, raw);
        }

        return value;
    }

    private static int ReadInt(
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyDictionary<string, string> defaults,
        string key,
        int minimum,
        int maximum)
    {
        return ParseInt(ReadRaw(parameters, defaults, key), key, minimum, maximum);
    }

    private static int ParseInt(string raw, string key, int minimum, int maximum)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
            value < minimum ||
            value > maximum)
        {
            ThrowInvalid(key, raw);
        }

        return value;
    }

    private static int ParseNumLevels(string raw)
    {
        var normalized = raw.Trim();
        if (string.Equals(normalized, "auto", StringComparison.Ordinal))
        {
            return 0;
        }

        return ParseInt(normalized, NumLevels, 1, int.MaxValue);
    }

    private static string ReadRaw(
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyDictionary<string, string> defaults,
        string key)
    {
        return parameters.TryGetValue(key, out var raw) ? raw : defaults[key];
    }

    private static void ThrowInvalid(string key, string? raw)
    {
        throw new TemplateMatchingConfigurationException(
            TemplateMatchingDiagnostics.Create(
                TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                $"Invalid template matching parameter {key}='{raw ?? "<null>"}'."));
    }

    private sealed record PresetValues(
        string CandidateMinScore,
        string OuterCoverageMin,
        string InnerCoverageMin,
        string EdgeTolerancePx,
        string PolarityAgreementMin,
        string CandidateMaxOverlap,
        string MaxOverlap,
        string Greediness,
        string OperatorTimeoutMs,
        string SingleCandidateLimit,
        string MultiCandidateLimit);
}
