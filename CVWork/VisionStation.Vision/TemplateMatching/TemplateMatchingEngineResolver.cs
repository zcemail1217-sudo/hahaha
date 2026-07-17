namespace VisionStation.Vision;

public static class TemplateMatchingEngineResolver
{
    public static TemplateMatchingEngine Resolve(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!parameters.TryGetValue(TemplateMatchingParameterCatalog.Engine, out var raw))
        {
            return TemplateMatchingEngine.OpenCv;
        }

        var normalized = raw?.Trim();
        if (string.Equals(normalized, "opencv", StringComparison.OrdinalIgnoreCase))
        {
            return TemplateMatchingEngine.OpenCv;
        }

        if (string.Equals(normalized, "managedncc", StringComparison.OrdinalIgnoreCase))
        {
            return TemplateMatchingEngine.ManagedNcc;
        }

        if (string.Equals(normalized, "halcon", StringComparison.OrdinalIgnoreCase))
        {
            return TemplateMatchingEngine.Halcon;
        }

        var technicalDetails = $"Unsupported template matching engine value '{raw ?? "<null>"}'.";
        throw new TemplateMatchingConfigurationException(
            TemplateMatchingDiagnostics.Create(
                TemplateMatchingDiagnosticCodes.ConfigUnknownEngine,
                technicalDetails));
    }

    internal static void EnsureHalconShapeMode(
        IReadOnlyDictionary<string, string> parameters,
        bool multiTarget)
    {
        var mode = "Shape";
        if (multiTarget &&
            parameters.TryGetValue("multiMatchMode", out var multiMode) &&
            !string.IsNullOrWhiteSpace(multiMode))
        {
            mode = multiMode;
        }
        else if (parameters.TryGetValue(TemplateMatchingParameterCatalog.MatchMode, out var configuredMode) &&
                 !string.IsNullOrWhiteSpace(configuredMode))
        {
            mode = configuredMode;
        }

        if (string.Equals(mode.Trim(), "Shape", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new TemplateMatchingConfigurationException(
            TemplateMatchingDiagnostics.Create(
                TemplateMatchingDiagnosticCodes.ConfigUnsupportedMode,
                $"Halcon does not support template matching mode '{mode}'."));
    }
}
