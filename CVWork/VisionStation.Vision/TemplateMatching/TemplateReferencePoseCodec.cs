using System.Globalization;
using VisionStation.Domain;

namespace VisionStation.Vision;

public static class TemplateReferencePoseCodec
{
    public static TemplateLearnedGeometry? ReadActive(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return TemplateMatchingEngineResolver.Resolve(parameters) switch
        {
            TemplateMatchingEngine.Halcon => TemplateModelParameterCodec.ReadHalcon(parameters)?.Geometry,
            TemplateMatchingEngine.OpenCv or TemplateMatchingEngine.ManagedNcc => ReadLegacy(parameters),
            _ => throw new InvalidOperationException("Unknown cannot be an active template matching engine.")
        };
    }

    private static TemplateLearnedGeometry? ReadLegacy(IReadOnlyDictionary<string, string> parameters)
    {
        var scale = parameters.TryGetValue("standardScale", out var scaleRaw)
            ? ParseScale(scaleRaw, "standardScale")
            : 1d;
        var hasWidth = TryReadPositiveInt(parameters, "templateWidth", out var width);
        var hasHeight = TryReadPositiveInt(parameters, "templateHeight", out var height);
        var hasStandardX = TryReadOptionalFiniteDouble(parameters, "standardX", out var standardX);
        var hasStandardY = TryReadOptionalFiniteDouble(parameters, "standardY", out var standardY);
        var angle = parameters.TryGetValue("standardAngle", out var angleRaw)
            ? ParseFiniteDouble(angleRaw, "standardAngle")
            : 0d;
        var hasTemplateX = TryReadOptionalFiniteDouble(parameters, "templateX", out var templateX);
        var hasTemplateY = TryReadOptionalFiniteDouble(parameters, "templateY", out var templateY);
        if (!hasWidth || !hasHeight)
        {
            return null;
        }

        if (hasStandardX && hasStandardY)
        {
            return new TemplateLearnedGeometry(
                new Pose2D(standardX, standardY, angle) { Scale = scale },
                width,
                height);
        }

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
