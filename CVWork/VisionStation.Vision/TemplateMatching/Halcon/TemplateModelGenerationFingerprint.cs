using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VisionStation.Vision;

internal sealed record TemplateModelGenerationParameters(
    double AngleStartDeg,
    double AngleExtentDeg,
    double ScaleMin,
    double ScaleMax,
    int NumLevels)
{
    public static TemplateModelGenerationParameters From(HalconTemplateMatchingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new TemplateModelGenerationParameters(
            parameters.AngleStartDeg,
            parameters.AngleExtentDeg,
            parameters.ScaleMin,
            parameters.ScaleMax,
            parameters.NumLevels);
    }
}

internal static class TemplateModelGenerationFingerprint
{
    public static string Compute(TemplateModelGenerationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!double.IsFinite(parameters.AngleStartDeg) ||
            !double.IsFinite(parameters.AngleExtentDeg) ||
            !double.IsFinite(parameters.ScaleMin) ||
            !double.IsFinite(parameters.ScaleMax))
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Generation parameters must be finite.");
        }

        string canonical = string.Join(
            '\n',
            new[]
            {
                parameters.AngleStartDeg.ToString("R", CultureInfo.InvariantCulture),
                parameters.AngleExtentDeg.ToString("R", CultureInfo.InvariantCulture),
                parameters.ScaleMin.ToString("R", CultureInfo.InvariantCulture),
                parameters.ScaleMax.ToString("R", CultureInfo.InvariantCulture),
                parameters.NumLevels.ToString(CultureInfo.InvariantCulture)
            });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}
