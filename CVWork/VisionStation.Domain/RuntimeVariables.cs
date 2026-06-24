using System.Collections.Concurrent;
using System.Globalization;

namespace VisionStation.Domain;

public readonly record struct VariableValue(string Raw)
{
    public int AsInt(int defaultValue = 0)
    {
        return int.TryParse(Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
    }

    public double AsDouble(double defaultValue = 0)
    {
        return double.TryParse(Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
    }

    public bool AsBool(bool defaultValue = false)
    {
        if (bool.TryParse(Raw, out var value))
        {
            return value;
        }

        return Raw switch
        {
            "1" => true,
            "0" => false,
            _ => defaultValue
        };
    }

    public override string ToString()
    {
        return Raw;
    }

    public static implicit operator string(VariableValue value)
    {
        return value.Raw;
    }

    public static implicit operator VariableValue(string? value)
    {
        return new VariableValue(value ?? string.Empty);
    }
}

public sealed class TypedRuntimeVariables
{
    private readonly ConcurrentDictionary<string, VariableValue> _values = new(StringComparer.OrdinalIgnoreCase);

    public TypedRuntimeVariables()
    {
    }

    public TypedRuntimeVariables(IEnumerable<KeyValuePair<string, string>> values)
    {
        foreach (var pair in values)
        {
            Set(pair.Key, pair.Value);
        }
    }

    public VariableValue Get(string key, VariableValue defaultValue = default)
    {
        return _values.TryGetValue(key, out var value) ? value : defaultValue;
    }

    public void Set(string key, VariableValue value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        _values[key.Trim()] = value;
    }

    public IReadOnlyDictionary<string, string> ToDictionary()
    {
        return _values.ToDictionary(pair => pair.Key, pair => pair.Value.Raw, StringComparer.OrdinalIgnoreCase);
    }
}
