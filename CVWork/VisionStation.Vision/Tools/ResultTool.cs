using System.Diagnostics;
using System.Globalization;
using VisionStation.Domain;

namespace VisionStation.Vision.Tools;

public sealed class ResultTool : IVisionTool
{
    public VisionToolKind Kind => VisionToolKind.Result;

    public Task<ToolResult> ExecuteAsync(
        VisionToolDefinition definition,
        VisionToolContext context,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var inputKeys = GetConnectedResultInputKeys(definition).ToArray();
        if (inputKeys.Length == 0)
        {
            stopwatch.Stop();
            return Task.FromResult(CreateEmptyResult(definition, stopwatch.Elapsed));
        }

        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<InspectionOutcome>();

        foreach (var inputKey in inputKeys)
        {
            if (!context.TryGetPortInputValue(definition, inputKey, out var inputValue))
            {
                continue;
            }

            var value = FormatInputValue(inputValue);
            data[inputKey] = value;

            var sourceToolId = GetResultInputSourceToolId(definition, inputKey);
            var sourcePortKey = GetResultInputSourcePortKey(definition, inputKey);
            if (!string.IsNullOrWhiteSpace(sourceToolId))
            {
                data[$"{inputKey}.sourceToolId"] = sourceToolId;
            }

            data[$"{inputKey}.sourcePortKey"] = sourcePortKey;

            if (inputValue is ToolResult sourceResult)
            {
                outcomes.Add(sourceResult.Outcome);
                data[$"{inputKey}.sourceToolId"] = sourceResult.ToolId;
                data[$"{inputKey}.sourceToolName"] = sourceResult.ToolName;
                data[$"{inputKey}.sourceToolKind"] = sourceResult.Kind.ToString();
                data[$"{inputKey}.sourceOutcome"] = sourceResult.Outcome.ToString();
                data[$"{inputKey}.sourceMessage"] = sourceResult.Message;
                foreach (var item in sourceResult.Data)
                {
                    data[$"{inputKey}.{item.Key}"] = item.Value;
                }
            }
        }

        if (data.Count == 0)
        {
            stopwatch.Stop();
            return Task.FromResult(CreateErrorResult(definition, stopwatch.Elapsed, "Result tool did not find any input values."));
        }

        data["inputCount"] = data.Keys
            .Count(key => key.StartsWith("ResultInput", StringComparison.OrdinalIgnoreCase) && !key.Contains('.'))
            .ToString(CultureInfo.InvariantCulture);
        var outcome = ResolveOutcome(outcomes);
        var failedCount = outcomes.Count(result => result is InspectionOutcome.Ng or InspectionOutcome.Error);

        stopwatch.Stop();
        return Task.FromResult(new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = Kind,
            Outcome = outcome,
            Duration = stopwatch.Elapsed,
            Message = failedCount == 0
                ? $"Result collected {data["inputCount"]} input(s)."
                : $"Result collected {failedCount} failed input(s).",
            Data = data
        });
    }

    private static ToolResult CreateEmptyResult(VisionToolDefinition definition, TimeSpan duration)
    {
        return new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = VisionToolKind.Result,
            Outcome = InspectionOutcome.Ok,
            Duration = duration,
            Message = "Result tool has no connected inputs.",
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["inputCount"] = "0"
            }
        };
    }

    private static ToolResult CreateErrorResult(VisionToolDefinition definition, TimeSpan duration, string message)
    {
        return new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = VisionToolKind.Result,
            Outcome = InspectionOutcome.Error,
            Duration = duration,
            Message = message
        };
    }

    private static IEnumerable<string> GetConnectedResultInputKeys(VisionToolDefinition definition)
    {
        return definition.Parameters.Keys
            .Where(key => key.StartsWith("input:ResultInput", StringComparison.OrdinalIgnoreCase) &&
                          key.EndsWith(":toolId", StringComparison.OrdinalIgnoreCase))
            .Select(key => key["input:".Length..^":toolId".Length])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetResultInputIndex)
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase);
    }

    private static int GetResultInputIndex(string inputKey)
    {
        const string prefix = "ResultInput";
        return inputKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(inputKey[prefix.Length..], out var index)
            ? index
            : int.MaxValue;
    }

    private static InspectionOutcome ResolveOutcome(IReadOnlyList<InspectionOutcome> outcomes)
    {
        if (outcomes.Any(outcome => outcome == InspectionOutcome.Error))
        {
            return InspectionOutcome.Error;
        }

        if (outcomes.Any(outcome => outcome == InspectionOutcome.Ng))
        {
            return InspectionOutcome.Ng;
        }

        return InspectionOutcome.Ok;
    }

    private static string FormatInputValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text,
            byte[] bytes => Convert.ToHexString(bytes),
            ToolResult result => result.Outcome.ToString(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string GetResultInputSourceToolId(VisionToolDefinition definition, string inputKey)
    {
        return definition.Parameters.GetValueOrDefault($"input:{inputKey}:toolId") ?? string.Empty;
    }

    private static string GetResultInputSourcePortKey(VisionToolDefinition definition, string inputKey)
    {
        return definition.Parameters.GetValueOrDefault($"input:{inputKey}:portKey") ?? "ResultOutput";
    }
}
