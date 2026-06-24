using System.Globalization;
using System.Text.RegularExpressions;

namespace VisionStation.Application.Inspection;

internal static class SignalMatcher
{
    public static string DescribeMatchMode(string matchMode)
    {
        return matchMode.Trim() switch
        {
            "Equals" or "Equal" or "==" or "等于" => "等于",
            "NotEquals" or "NotEqual" or "!=" or "不等于" => "不等于",
            "Contains" or "包含" => "包含",
            "Regex" or "正则" => "匹配正则",
            "GreaterThan" or ">" or "大于" => "大于",
            "LessThan" or "<" or "小于" => "小于",
            _ => matchMode
        };
    }

    public static bool MatchesSignal(string? actual, string expected, string matchMode)
    {
        var value = actual?.Trim() ?? string.Empty;
        var target = expected.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            return true;
        }

        return matchMode.Trim().ToLowerInvariant() switch
        {
            "equals" or "equal" or "==" or "等于" => MatchesEquivalentValue(value, target),
            "contains" or "包含" => value.Contains(target, StringComparison.OrdinalIgnoreCase),
            "regex" or "正则" => Regex.IsMatch(value, target, RegexOptions.IgnoreCase),
            "notequals" or "notequal" or "!=" or "不等于" => !string.Equals(value, target, StringComparison.OrdinalIgnoreCase),
            "greaterthan" or ">" or "大于" => TryCompare(value, target, static (left, right) => left > right),
            "lessthan" or "<" or "小于" => TryCompare(value, target, static (left, right) => left < right),
            "true" => IsTruthy(value),
            _ => MatchesEquivalentValue(value, target)
        };
    }

    private static bool MatchesEquivalentValue(string value, string target)
    {
        return string.Equals(value, target, StringComparison.OrdinalIgnoreCase) ||
               (IsTruthy(value) && IsTruthy(target)) ||
               (IsFalsy(value) && IsFalsy(target));
    }

    private static bool TryCompare(string value, string target, Func<double, double, bool> compare)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var left) &&
               double.TryParse(target, NumberStyles.Float, CultureInfo.InvariantCulture, out var right) &&
               compare(left, right);
    }

    private static bool IsTruthy(string value)
    {
        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("ok", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFalsy(string value)
    {
        return value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("ng", StringComparison.OrdinalIgnoreCase);
    }
}
