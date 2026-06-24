using System.Globalization;
using VisionStation.Domain;
using VisionStation.Vision;

namespace VisionStation.Application.Inspection;

internal static class VisionRuntimeValueSeeder
{
    private const string ResultBindingParameterPrefix = "resultBinding:";

    public static void SeedPipelineOutputs(
        Recipe recipe,
        ProcessExecutionContext context,
        VisionPipelineResult pipelineResult,
        string? resultVariableKey = null)
    {
        SeedToolResultOutputs(context, pipelineResult);
        SeedResultVariable(recipe, context, pipelineResult, resultVariableKey);
        SetRuntimeValue(context.RuntimeValues, "Vision.ResultFrameId", pipelineResult.ResultFrame.Id);
        SetRuntimeValue(context.RuntimeValues, "ResultFrameId", pipelineResult.ResultFrame.Id);
        context.RuntimeValues["OverallResult"] = pipelineResult.Outcome.ToString();
        if (!string.IsNullOrWhiteSpace(pipelineResult.Barcode))
        {
            context.RuntimeValues["Barcode"] = pipelineResult.Barcode;
        }

        foreach (var item in VisionResultResolver.GetVisionResultDefinitions(recipe))
        {
            if (!VisionResultResolver.TryResolveVisionDefinitionValue(item, context.ToolResults, pipelineResult, out var value))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(item.ExternalAlias))
            {
                context.RuntimeValues[item.ExternalAlias] = value;
                context.ResultTable[item.ExternalAlias] = value;
            }

            context.RuntimeValues[item.Name] = value;
            context.ResultTable[item.Name] = value;
        }
    }

    public static void SeedResultToolBindings(
        Recipe recipe,
        ProcessExecutionContext context,
        VisionPipelineResult pipelineResult,
        IReadOnlyDictionary<string, string>? stepParameters)
    {
        if (stepParameters is null || stepParameters.Count == 0)
        {
            return;
        }

        foreach (var parameter in stepParameters)
        {
            if (!TryParseResultBindingKey(parameter.Key, out var resultToolId, out var inputKey) ||
                string.IsNullOrWhiteSpace(parameter.Value))
            {
                continue;
            }

            var targetKey = parameter.Value.Trim();
            var resultTool = ResolveResultToolResult(pipelineResult.ToolResults, resultToolId);
            if (resultTool is null || !TryGetDataValue(resultTool.Data, inputKey, out var value))
            {
                continue;
            }

            if (!TrySeedTypedBoundResultVariable(recipe, context, resultTool, inputKey, targetKey))
            {
                SetRuntimeValue(context.RuntimeValues, targetKey, value);
                context.ResultTable[targetKey] = value;
            }

            SeedBoundResultChildValues(context, resultTool, inputKey, targetKey);
        }
    }

    private static void SeedResultVariable(
        Recipe recipe,
        ProcessExecutionContext context,
        VisionPipelineResult pipelineResult,
        string? resultVariableKey)
    {
        if (string.IsNullOrWhiteSpace(resultVariableKey))
        {
            return;
        }

        var key = resultVariableKey.Trim();
        if (TrySeedTypedResultVariable(recipe, context, pipelineResult, key))
        {
            return;
        }

        var flow = recipe.GetActiveFlow();
        context.RuntimeObjects[key] = new VisionRuntimeResultValue(
            flow.Id,
            flow.Name,
            pipelineResult.ResultFrame,
            pipelineResult.ToolResults,
            pipelineResult.Outcome,
            pipelineResult.Barcode,
            pipelineResult.Message);

        SetRuntimeValue(context.RuntimeValues, key, pipelineResult.Outcome.ToString());
        SetRuntimeValue(context.RuntimeValues, $"{key}.FlowId", flow.Id);
        SetRuntimeValue(context.RuntimeValues, $"{key}.FlowName", flow.Name);
        SetRuntimeValue(context.RuntimeValues, $"{key}.OverallResult", pipelineResult.Outcome.ToString());
        SetRuntimeValue(context.RuntimeValues, $"{key}.Barcode", pipelineResult.Barcode);
        SetRuntimeValue(context.RuntimeValues, $"{key}.Message", pipelineResult.Message);
        SetRuntimeValue(context.RuntimeValues, $"{key}.ResultFrameId", pipelineResult.ResultFrame.Id);
        SetRuntimeValue(context.RuntimeValues, $"{key}.ResultImage", pipelineResult.ResultFrame.Id);

        foreach (var result in pipelineResult.ToolResults)
        {
            var toolId = string.IsNullOrWhiteSpace(result.ToolId) ? result.Kind.ToString() : result.ToolId;
            var toolName = string.IsNullOrWhiteSpace(result.ToolName) ? result.Kind.ToString() : result.ToolName;
            var kind = result.Kind.ToString();
            SeedToolProjection(context.RuntimeValues, key, toolId, result);
            SeedToolProjection(context.RuntimeValues, key, toolName, result);
            SeedToolProjection(context.RuntimeValues, key, kind, result);
        }
    }

    private static bool TrySeedTypedResultVariable(
        Recipe recipe,
        ProcessExecutionContext context,
        VisionPipelineResult pipelineResult,
        string key)
    {
        var variable = recipe.Variables.FirstOrDefault(item =>
            string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        if (variable is null)
        {
            return false;
        }

        return NormalizeDataType(variable.DataType) switch
        {
            "point" => TrySeedPointVariable(context, pipelineResult, key),
            "image" => SeedImageVariable(context, pipelineResult, key),
            "visionresult" => false,
            _ => false
        };
    }

    private static bool TrySeedTypedBoundResultVariable(
        Recipe recipe,
        ProcessExecutionContext context,
        ToolResult resultTool,
        string inputKey,
        string targetKey)
    {
        var variable = recipe.Variables.FirstOrDefault(item =>
            string.Equals(item.Key, targetKey, StringComparison.OrdinalIgnoreCase));
        if (variable is null)
        {
            return false;
        }

        return NormalizeDataType(variable.DataType) switch
        {
            "point" => TrySeedBoundPointVariable(context, resultTool, inputKey, targetKey),
            _ => false
        };
    }

    private static bool TrySeedBoundPointVariable(
        ProcessExecutionContext context,
        ToolResult resultTool,
        string inputKey,
        string targetKey)
    {
        if (!TryGetPoint(resultTool.Data, $"{inputKey}.X", $"{inputKey}.Y", out var point) &&
            !TryGetPoint(resultTool.Data, $"{inputKey}.x", $"{inputKey}.y", out point) &&
            !TryGetPoint(resultTool.Data, $"{inputKey}.CenterX", $"{inputKey}.CenterY", out point) &&
            !TryGetPoint(resultTool.Data, $"{inputKey}.centerX", $"{inputKey}.centerY", out point))
        {
            return false;
        }

        context.RuntimeObjects[targetKey] = point;
        var x = FormatDouble(point.X);
        var y = FormatDouble(point.Y);
        var value = $"{x},{y}";
        SetRuntimeValue(context.RuntimeValues, targetKey, value);
        SetRuntimeValue(context.RuntimeValues, $"{targetKey}.X", x);
        SetRuntimeValue(context.RuntimeValues, $"{targetKey}.Y", y);
        context.ResultTable[targetKey] = value;
        return true;
    }

    private static bool TrySeedPointVariable(
        ProcessExecutionContext context,
        VisionPipelineResult pipelineResult,
        string key)
    {
        if (!TryResolvePoint(pipelineResult.ToolResults, out var point))
        {
            return false;
        }

        context.RuntimeObjects[key] = point;
        var x = FormatDouble(point.X);
        var y = FormatDouble(point.Y);
        var value = $"{x},{y}";
        SetRuntimeValue(context.RuntimeValues, key, value);
        SetRuntimeValue(context.RuntimeValues, $"{key}.X", x);
        SetRuntimeValue(context.RuntimeValues, $"{key}.Y", y);
        context.ResultTable[key] = value;
        return true;
    }

    private static bool SeedImageVariable(
        ProcessExecutionContext context,
        VisionPipelineResult pipelineResult,
        string key)
    {
        context.RuntimeObjects[key] = pipelineResult.ResultFrame;
        SetRuntimeValue(context.RuntimeValues, key, pipelineResult.ResultFrame.Id);
        SetRuntimeValue(context.RuntimeValues, $"{key}.ResultFrameId", pipelineResult.ResultFrame.Id);
        context.ResultTable[key] = pipelineResult.ResultFrame.Id;
        return true;
    }

    private static bool TryResolvePoint(IReadOnlyList<ToolResult> toolResults, out Point2D point)
    {
        foreach (var result in toolResults.AsEnumerable().Reverse())
        {
            if (result.Outcome != InspectionOutcome.Ok || result.Kind is VisionToolKind.AcquireImage or VisionToolKind.Result)
            {
                continue;
            }

            if (TryGetPoint(result.Data, "x", "y", out point) ||
                TryGetPoint(result.Data, "midX", "midY", out point) ||
                TryGetPoint(result.Data, "centerX", "centerY", out point) ||
                TryGetPoint(result.Data, "bestX", "bestY", out point) ||
                TryGetPoint(result.Data, "bestCenterX", "bestCenterY", out point))
            {
                return true;
            }
        }

        point = default!;
        return false;
    }

    private static bool TryGetPoint(
        IReadOnlyDictionary<string, string> data,
        string xKey,
        string yKey,
        out Point2D point)
    {
        if (TryGetDouble(data, xKey, out var x) && TryGetDouble(data, yKey, out var y))
        {
            point = new Point2D(x, y);
            return true;
        }

        point = default!;
        return false;
    }

    private static bool TryGetDouble(IReadOnlyDictionary<string, string> data, string key, out double value)
    {
        if (data.TryGetValue(key, out var text) &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static string NormalizeDataType(string? dataType)
    {
        return string.IsNullOrWhiteSpace(dataType)
            ? string.Empty
            : dataType.Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("_", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim()
                .ToLowerInvariant();
    }

    private static string FormatDouble(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static void SeedToolProjection(Dictionary<string, string> values, string rootKey, string prefix, ToolResult result)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return;
        }

        SetRuntimeValue(values, $"{rootKey}.{prefix}.Outcome", result.Outcome.ToString());
        SetRuntimeValue(values, $"{rootKey}.{prefix}.DurationMs", result.Duration.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture));
        SetRuntimeValue(values, $"{rootKey}.{prefix}.Message", result.Message);
        SetRuntimeValue(values, $"{rootKey}.{prefix}.Kind", result.Kind.ToString());

        foreach (var pair in result.Data)
        {
            SetRuntimeValue(values, $"{rootKey}.{prefix}.{pair.Key}", pair.Value);
        }
    }

    private static void SeedToolResultOutputs(ProcessExecutionContext context, VisionPipelineResult pipelineResult)
    {
        SetRuntimeValue(context.RuntimeValues, "Vision.Outcome", pipelineResult.Outcome.ToString());
        SetRuntimeValue(context.RuntimeValues, "Vision.Barcode", pipelineResult.Barcode);
        SetRuntimeValue(context.RuntimeValues, "Vision.Message", pipelineResult.Message);

        foreach (var result in pipelineResult.ToolResults)
        {
            var toolId = string.IsNullOrWhiteSpace(result.ToolId) ? result.Kind.ToString() : result.ToolId;
            var toolName = string.IsNullOrWhiteSpace(result.ToolName) ? result.Kind.ToString() : result.ToolName;
            var kind = result.Kind.ToString();
            SeedToolMeta(context.RuntimeValues, toolId, result);
            SeedToolMeta(context.RuntimeValues, toolName, result);
            SeedToolMeta(context.RuntimeValues, kind, result);

            foreach (var pair in result.Data)
            {
                SetRuntimeValue(context.RuntimeValues, $"{toolId}.{pair.Key}", pair.Value);
                SetRuntimeValue(context.RuntimeValues, $"{toolId}:{pair.Key}", pair.Value);
                SetRuntimeValue(context.RuntimeValues, $"{toolName}.{pair.Key}", pair.Value);
                SetRuntimeValue(context.RuntimeValues, $"{kind}.{pair.Key}", pair.Value);
                SetRuntimeValue(context.RuntimeValues, pair.Key, pair.Value);
            }
        }
    }

    private static bool TryParseResultBindingKey(string? key, out string resultToolId, out string inputKey)
    {
        resultToolId = string.Empty;
        inputKey = string.Empty;
        if (string.IsNullOrWhiteSpace(key) ||
            !key.StartsWith(ResultBindingParameterPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var body = key[ResultBindingParameterPrefix.Length..];
        var separatorIndex = body.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == body.Length - 1)
        {
            return false;
        }

        resultToolId = body[..separatorIndex].Trim();
        inputKey = body[(separatorIndex + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(resultToolId) && !string.IsNullOrWhiteSpace(inputKey);
    }

    private static ToolResult? ResolveResultToolResult(IReadOnlyList<ToolResult> toolResults, string resultToolId)
    {
        return toolResults.LastOrDefault(result =>
                   result.Kind == VisionToolKind.Result &&
                   string.Equals(result.ToolId, resultToolId, StringComparison.OrdinalIgnoreCase))
               ?? toolResults.LastOrDefault(result =>
                   result.Kind == VisionToolKind.Result &&
                   string.Equals(result.ToolName, resultToolId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetDataValue(
        IReadOnlyDictionary<string, string> data,
        string key,
        out string value)
    {
        if (data.TryGetValue(key, out value!))
        {
            return true;
        }

        foreach (var pair in data)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static void SeedBoundResultChildValues(
        ProcessExecutionContext context,
        ToolResult resultTool,
        string inputKey,
        string targetKey)
    {
        var prefix = $"{inputKey}.";
        foreach (var pair in resultTool.Data)
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = pair.Key[prefix.Length..];
            if (string.IsNullOrWhiteSpace(suffix))
            {
                continue;
            }

            var key = $"{targetKey}.{suffix}";
            SetRuntimeValue(context.RuntimeValues, key, pair.Value);
            context.ResultTable[key] = pair.Value;
        }
    }

    private static void SeedToolMeta(Dictionary<string, string> values, string prefix, ToolResult result)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return;
        }

        SetRuntimeValue(values, $"{prefix}.Outcome", result.Outcome.ToString());
        SetRuntimeValue(values, $"{prefix}.DurationMs", result.Duration.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture));
        SetRuntimeValue(values, $"{prefix}.Message", result.Message);
        SetRuntimeValue(values, $"{prefix}.Kind", result.Kind.ToString());
    }

    private static void SetRuntimeValue(Dictionary<string, string> values, string? key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        values[key.Trim()] = value ?? string.Empty;
    }
}
