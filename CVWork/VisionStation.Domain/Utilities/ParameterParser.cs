using System.Globalization;

namespace VisionStation.Domain.Utilities;

public static class ParameterParser
{
    public static string GetString(
        IReadOnlyDictionary<string, string>? parameters,
        string key,
        string defaultValue = "")
    {
        if (parameters is null || string.IsNullOrWhiteSpace(key))
        {
            return defaultValue;
        }

        return parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : defaultValue;
    }

    public static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    public static int GetInt(
        IReadOnlyDictionary<string, string>? parameters,
        string key,
        int defaultValue = 0)
    {
        var value = GetString(parameters, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : defaultValue;
    }

    public static double GetDouble(
        IReadOnlyDictionary<string, string>? parameters,
        string key,
        double defaultValue = 0)
    {
        var value = GetString(parameters, key);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : defaultValue;
    }

    public static bool GetBool(
        IReadOnlyDictionary<string, string>? parameters,
        string key,
        bool defaultValue = false)
    {
        var value = GetString(parameters, key);
        if (bool.TryParse(value, out var result))
        {
            return result;
        }

        return value switch
        {
            "1" => true,
            "0" => false,
            _ => defaultValue
        };
    }

    public static TEnum GetEnum<TEnum>(
        IReadOnlyDictionary<string, string>? parameters,
        string key,
        TEnum defaultValue)
        where TEnum : struct, Enum
    {
        var value = GetString(parameters, key);
        return Enum.TryParse<TEnum>(value, true, out var result)
            ? result
            : defaultValue;
    }
}
