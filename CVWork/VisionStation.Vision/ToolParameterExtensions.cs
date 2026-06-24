using System.Globalization;

namespace VisionStation.Vision;

internal static class ToolParameterExtensions
{
    public static double GetDouble(this IReadOnlyDictionary<string, string> parameters, string key, double defaultValue)
    {
        return parameters.TryGetValue(key, out var value) &&
               TryParseFlexibleDouble(value, out var parsed)
            ? parsed
            : defaultValue;
    }

    public static string ToInvariant(this double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static bool TryParseFlexibleDouble(string? text, out double value)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(text) && text.Contains(','))
        {
            return double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        value = 0;
        return false;
    }
}
