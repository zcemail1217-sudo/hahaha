using System.Globalization;

namespace VisionStation.Vision;

internal readonly record struct TemplateMatchingExpectedCountResolution(
    int ExpectedCount,
    TemplateMatchingDiagnostic? Diagnostic)
{
    public bool IsValid => Diagnostic is null;
}

internal static class TemplateMatchingExpectedCountResolver
{
    private const int DefaultExpectedCount = 1;

    public static TemplateMatchingExpectedCountResolution Resolve(
        IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (TryGetValue(parameters, TemplateMatchingParameterCatalog.ExpectedCount, out var expectedRaw))
        {
            return ParseStrict(TemplateMatchingParameterCatalog.ExpectedCount, expectedRaw);
        }

        var hasEngine = TryGetValue(parameters, TemplateMatchingParameterCatalog.Engine, out var engineRaw);
        if (hasEngine && string.Equals(engineRaw?.Trim(), "Halcon", StringComparison.OrdinalIgnoreCase))
        {
            return TryGetValue(parameters, TemplateMatchingParameterCatalog.LegacyMatchCount, out var legacyRaw)
                ? ParseStrict(TemplateMatchingParameterCatalog.LegacyMatchCount, legacyRaw)
                : Valid(DefaultExpectedCount);
        }

        if (!hasEngine || string.Equals(engineRaw?.Trim(), "OpenCv", StringComparison.OrdinalIgnoreCase))
        {
            return TryGetValue(parameters, "minCount", out var minimumRaw) &&
                   TryParseInRange(minimumRaw, out var minimum)
                ? Valid(minimum)
                : Valid(DefaultExpectedCount);
        }

        return Valid(DefaultExpectedCount);
    }

    private static TemplateMatchingExpectedCountResolution ParseStrict(string key, string raw)
    {
        if (TryParseInRange(raw, out var value))
        {
            return Valid(value);
        }

        return new TemplateMatchingExpectedCountResolution(
            DefaultExpectedCount,
            TemplateMatchingDiagnostics.Create(
                TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                $"Template matching parameter '{key}' has invalid value '{raw}'. Expected an integer from 1 to 100."));
    }

    private static bool TryParseInRange(string raw, out int value)
    {
        return int.TryParse(
                   raw,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out value) &&
               value is >= 1 and <= 100;
    }

    private static TemplateMatchingExpectedCountResolution Valid(int value) => new(value, null);

    private static bool TryGetValue(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        out string value)
    {
        if (parameters.TryGetValue(key, out value!))
        {
            return true;
        }

        foreach (var parameter in parameters)
        {
            if (string.Equals(parameter.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = parameter.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
