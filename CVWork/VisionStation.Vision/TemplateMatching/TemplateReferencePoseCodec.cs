using System.Globalization;
using VisionStation.Domain;

namespace VisionStation.Vision;

public static class TemplateReferencePoseCodec
{
    private const string HalconStandardX = "halcon.standardX";
    private const string HalconStandardY = "halcon.standardY";
    private const string HalconStandardAngle = "halcon.standardAngle";
    private const string HalconStandardScale = "halcon.standardScale";
    private const string HalconTemplateWidth = "halcon.templateWidth";
    private const string HalconTemplateHeight = "halcon.templateHeight";

    public static TemplateLearnedGeometry? ReadActive(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return TemplateMatchingEngineResolver.Resolve(parameters) switch
        {
            TemplateMatchingEngine.Halcon => ReadHalcon(parameters),
            TemplateMatchingEngine.OpenCv or TemplateMatchingEngine.ManagedNcc => ReadLegacy(parameters),
            _ => throw new InvalidOperationException("Unknown cannot be an active template matching engine.")
        };
    }

    private static TemplateLearnedGeometry? ReadHalcon(IReadOnlyDictionary<string, string> parameters)
    {
        var requiredKeys = new[]
        {
            HalconStandardX,
            HalconStandardY,
            HalconStandardAngle,
            HalconStandardScale,
            HalconTemplateWidth,
            HalconTemplateHeight
        };
        if (requiredKeys.Any(key => !parameters.ContainsKey(key)))
        {
            return null;
        }

        var x = ParseFiniteDouble(parameters[HalconStandardX], HalconStandardX);
        var y = ParseFiniteDouble(parameters[HalconStandardY], HalconStandardY);
        var angle = ParseFiniteDouble(parameters[HalconStandardAngle], HalconStandardAngle);
        var scale = ParseScale(parameters[HalconStandardScale], HalconStandardScale);
        var width = ParsePositiveInt(parameters[HalconTemplateWidth], HalconTemplateWidth);
        var height = ParsePositiveInt(parameters[HalconTemplateHeight], HalconTemplateHeight);
        return new TemplateLearnedGeometry(
            new Pose2D(x, y, angle) { Scale = scale },
            width,
            height);
    }

    private static TemplateLearnedGeometry? ReadLegacy(IReadOnlyDictionary<string, string> parameters)
    {
        var scale = parameters.TryGetValue("standardScale", out var scaleRaw)
            ? ParseScale(scaleRaw, "standardScale")
            : 1d;
        if (!TryReadPositiveInt(parameters, "templateWidth", out var width) ||
            !TryReadPositiveInt(parameters, "templateHeight", out var height))
        {
            return null;
        }

        var hasStandardX = TryReadOptionalFiniteDouble(parameters, "standardX", out var standardX);
        var hasStandardY = TryReadOptionalFiniteDouble(parameters, "standardY", out var standardY);
        if (hasStandardX && hasStandardY)
        {
            var angle = parameters.TryGetValue("standardAngle", out var angleRaw)
                ? ParseFiniteDouble(angleRaw, "standardAngle")
                : 0d;
            return new TemplateLearnedGeometry(
                new Pose2D(standardX, standardY, angle) { Scale = scale },
                width,
                height);
        }

        var hasTemplateX = TryReadOptionalFiniteDouble(parameters, "templateX", out var templateX);
        var hasTemplateY = TryReadOptionalFiniteDouble(parameters, "templateY", out var templateY);
        if (!hasTemplateX || !hasTemplateY)
        {
            return null;
        }

        return new TemplateLearnedGeometry(
            new Pose2D(templateX + width / 2d, templateY + height / 2d, 0) { Scale = scale },
            width,
            height);
    }

    private static bool TryReadOptionalFiniteDouble(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        out double value)
    {
        value = 0;
        if (!parameters.TryGetValue(key, out var raw))
        {
            return false;
        }

        value = ParseFiniteDouble(raw, key);
        return true;
    }

    private static bool TryReadPositiveInt(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        out int value)
    {
        value = 0;
        if (!parameters.TryGetValue(key, out var raw))
        {
            return false;
        }

        value = ParsePositiveInt(raw, key);
        return true;
    }

    private static double ParseScale(string raw, string key)
    {
        var scale = ParseFiniteDouble(raw, key);
        if (scale <= 0)
        {
            ThrowInvalid(key, raw);
        }

        return scale;
    }

    private static double ParseFiniteDouble(string raw, string key)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value))
        {
            ThrowInvalid(key, raw);
        }

        return value;
    }

    private static int ParsePositiveInt(string raw, string key)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
            value <= 0)
        {
            ThrowInvalid(key, raw);
        }

        return value;
    }

    private static void ThrowInvalid(string key, string? raw)
    {
        throw new TemplateMatchingConfigurationException(
            TemplateMatchingDiagnostics.Create(
                TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                $"Invalid template reference parameter {key}='{raw ?? "<null>"}'."));
    }
}
