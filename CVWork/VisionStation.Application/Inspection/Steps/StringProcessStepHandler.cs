using System.Text.RegularExpressions;
using VisionStation.Domain;
using VisionStation.Domain.Utilities;

namespace VisionStation.Application.Inspection.Steps;

internal sealed class StringProcessStepHandler : IProcessStepHandler
{
    public ProcessStepType StepType => ProcessStepType.StringProcess;

    public string Execute(ProcessStepDefinition step, string input)
    {
        var operation = ParameterParser.FirstNonEmpty(
            step.CommandName,
            ParameterParser.GetString(step.Parameters, "operation"),
            "Split");

        return NormalizeOperation(operation) switch
        {
            "trim" => input.Trim(),
            "split" => Split(input, ParameterParser.GetString(step.Parameters, "separator"), ParameterParser.GetInt(step.Parameters, "index")),
            "regex" => ExtractRegex(input, ParameterParser.GetString(step.Parameters, "pattern"), ParameterParser.GetInt(step.Parameters, "group", 1)),
            "substring" => Slice(input, ParameterParser.GetInt(step.Parameters, "start"), ParameterParser.GetInt(step.Parameters, "length", -1)),
            "replace" => Replace(input, ParameterParser.GetString(step.Parameters, "oldValue"), ParameterParser.GetString(step.Parameters, "newValue")),
            "upper" => input.ToUpperInvariant(),
            "lower" => input.ToLowerInvariant(),
            "contains" => input.Contains(ParameterParser.GetString(step.Parameters, "pattern"), StringComparison.OrdinalIgnoreCase)
                ? ParameterParser.FirstNonEmpty(ParameterParser.GetString(step.Parameters, "trueValue"), "1")
                : ParameterParser.FirstNonEmpty(ParameterParser.GetString(step.Parameters, "falseValue"), "0"),
            _ => input
        };
    }

    public static string DescribeOperation(string? operation)
    {
        return NormalizeOperation(ParameterParser.FirstNonEmpty(operation, "Split")) switch
        {
            "trim" => "去空格",
            "split" => "分割",
            "regex" => "正则提取",
            "substring" => "截取",
            "replace" => "替换",
            "upper" => "转大写",
            "lower" => "转小写",
            "contains" => "包含判断",
            _ => ParameterParser.FirstNonEmpty(operation, "原样输出")
        };
    }

    private static string NormalizeOperation(string operation)
    {
        return operation.Trim() switch
        {
            "去空格" or "Trim" => "trim",
            "分割" or "Split" => "split",
            "正则提取" or "Regex" or "RegexExtract" => "regex",
            "截取" or "Substring" => "substring",
            "替换" or "Replace" => "replace",
            "转大写" or "Upper" or "ToUpper" => "upper",
            "转小写" or "Lower" or "ToLower" => "lower",
            "包含判断" or "Contains" => "contains",
            _ => operation.Trim().ToLowerInvariant()
        };
    }

    private static string Split(string input, string separator, int index)
    {
        var delimiter = string.IsNullOrEmpty(separator) ? "," : separator;
        var parts = input.Split(delimiter, StringSplitOptions.None);
        return index >= 0 && index < parts.Length ? parts[index] : string.Empty;
    }

    private static string ExtractRegex(string input, string pattern, int group)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return string.Empty;
        }

        var match = Regex.Match(input, pattern);
        if (!match.Success)
        {
            return string.Empty;
        }

        return group >= 0 && group < match.Groups.Count
            ? match.Groups[group].Value
            : match.Value;
    }

    private static string Replace(string input, string oldValue, string newValue)
    {
        return string.IsNullOrEmpty(oldValue)
            ? input
            : input.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    private static string Slice(string input, int start, int length)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var safeStart = Math.Clamp(start, 0, input.Length);
        var remaining = input.Length - safeStart;
        var safeLength = length < 0 ? remaining : Math.Clamp(length, 0, remaining);
        return input.Substring(safeStart, safeLength);
    }
}
