using Prism.Mvvm;
using VisionStation.Domain;

namespace VisionStation.Vision.UI.ViewModels;

public interface IVisionFlowResultPreviewVariable
{
    string Key { get; }

    string DataType { get; }
}

public static class VisionFlowResultPreviewBuilder
{
    private const string EmptyValue = "-";
    private const string ResultToolSource = "ResultTool";
    private const string UnconnectedSource = "未连接";
    private const string InvalidSource = "来源无效";

    public static IReadOnlyList<RecipeVisionFlowResultPreviewItem> Build(
        RecipeProcessStepItem? step,
        VisionFlowDefinition? flow,
        IEnumerable<IVisionFlowResultPreviewVariable> variables,
        IReadOnlyDictionary<string, string>? runtimeValues,
        IReadOnlyDictionary<string, string>? resultData)
    {
        var rows = new List<RecipeVisionFlowResultPreviewItem>();
        if (step?.IsRunVisionFlowStep != true || flow is null)
        {
            return rows;
        }

        var values = MergeValues(runtimeValues, resultData);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddResultToolInputRows(rows, seen, step, flow, variables, values);
        return rows;
    }

    private static void AddResultToolInputRows(
        ICollection<RecipeVisionFlowResultPreviewItem> rows,
        ISet<string> seen,
        RecipeProcessStepItem step,
        VisionFlowDefinition flow,
        IEnumerable<IVisionFlowResultPreviewVariable> variables,
        IReadOnlyDictionary<string, string> values)
    {
        foreach (var tool in flow.Tools.Where(tool => tool.Kind == VisionToolKind.Result))
        {
            foreach (var input in VisionToolCatalog.GetResultInputPorts(tool.Parameters))
            {
                var sourceToolId = GetResultInputSourceToolId(tool, input.Key);
                var sourcePortKey = GetResultInputSourcePortKey(tool, input.Key);
                var source = ResolveResultInputSource(flow, sourceToolId, sourcePortKey);
                var outputTarget = GetResultInputOutputTarget(tool, input.Key);
                var resultToolId = ResolveResultToolId(tool);
                var rowName = $"{ResolveToolDisplayName(tool)}.{input.Key}";
                var boundVariableKey = step.GetVisionResultBinding(resultToolId, input.Key);
                var dataType = ResolveResultInputDataType(input.DataType, outputTarget);
                var valueKeys = BuildResultInputRuntimeKeys(tool, input.Key, outputTarget)
                    .Concat(BuildBoundVariableRuntimeKeys(boundVariableKey));
                AddRow(
                    rows,
                    seen,
                    resultToolId,
                    input.Key,
                    rowName,
                    dataType,
                    FindValue(values, valueKeys),
                    source,
                    GetCompatibleVariables(variables, dataType),
                    boundVariableKey,
                    variableKey => step.SetVisionResultBinding(resultToolId, input.Key, variableKey));
            }
        }
    }

    private static IEnumerable<string> BuildBoundVariableRuntimeKeys(string boundVariableKey)
    {
        if (!string.IsNullOrWhiteSpace(boundVariableKey))
        {
            yield return boundVariableKey.Trim();
        }
    }

    private static IEnumerable<string> BuildResultInputRuntimeKeys(VisionToolDefinition tool, string inputKey, string outputTarget)
    {
        if (!string.IsNullOrWhiteSpace(outputTarget))
        {
            var target = outputTarget.Trim();
            yield return target;
            if (string.Equals(target, "OverallResult", StringComparison.OrdinalIgnoreCase))
            {
                yield return "Vision.Outcome";
            }
        }

        var prefixes = new[]
        {
            tool.Id,
            tool.Name,
            tool.Kind.ToString()
        }.Where(item => !string.IsNullOrWhiteSpace(item));

        foreach (var prefix in prefixes)
        {
            yield return $"{prefix}.{inputKey}";
            yield return $"{prefix}:{inputKey}";
        }

        yield return inputKey;
    }

    private static string ResolveResultInputSource(VisionFlowDefinition flow, string sourceToolId, string sourcePortKey)
    {
        if (string.IsNullOrWhiteSpace(sourceToolId))
        {
            return UnconnectedSource;
        }

        var sourceTool = flow.Tools.FirstOrDefault(tool =>
            string.Equals(tool.Id, sourceToolId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (sourceTool is null)
        {
            return InvalidSource;
        }

        return VisionToolCatalog.GetOutputPorts(sourceTool.Kind)
            .Any(port => string.Equals(port.Key, sourcePortKey.Trim(), StringComparison.OrdinalIgnoreCase))
            ? ResultToolSource
            : InvalidSource;
    }

    private static string GetResultInputSourceToolId(VisionToolDefinition resultTool, string inputKey)
    {
        return resultTool.Parameters.GetValueOrDefault($"input:{inputKey}:toolId") ?? string.Empty;
    }

    private static string GetResultInputSourcePortKey(VisionToolDefinition resultTool, string inputKey)
    {
        return resultTool.Parameters.GetValueOrDefault($"input:{inputKey}:portKey") ?? "ResultOutput";
    }

    private static string GetResultInputOutputTarget(VisionToolDefinition resultTool, string inputKey)
    {
        return resultTool.Parameters.GetValueOrDefault($"input:{inputKey}:outputTarget") ?? string.Empty;
    }

    private static string ResolveResultInputDataType(string dataType, string outputTarget)
    {
        return string.Equals(outputTarget, "OverallResult", StringComparison.OrdinalIgnoreCase)
            ? "enum"
            : VisionResultDataTypeMapper.ToVariableDataType(dataType);
    }

    private static IReadOnlyList<IVisionFlowResultPreviewVariable> GetCompatibleVariables(
        IEnumerable<IVisionFlowResultPreviewVariable> variables,
        string dataType)
    {
        return variables
            .Where(variable => VisionResultDataTypeMapper.AreCompatible(variable.DataType, dataType))
            .ToArray();
    }

    private static string ResolveToolDisplayName(VisionToolDefinition tool)
    {
        if (!string.IsNullOrWhiteSpace(tool.Name))
        {
            return tool.Name.Trim();
        }

        return string.IsNullOrWhiteSpace(tool.Id) ? tool.Kind.ToString() : tool.Id.Trim();
    }

    private static string ResolveResultToolId(VisionToolDefinition tool)
    {
        return string.IsNullOrWhiteSpace(tool.Id) ? ResolveToolDisplayName(tool) : tool.Id.Trim();
    }

    private static IReadOnlyDictionary<string, string> MergeValues(
        IReadOnlyDictionary<string, string>? runtimeValues,
        IReadOnlyDictionary<string, string>? resultData)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (runtimeValues is not null)
        {
            foreach (var pair in runtimeValues)
            {
                values[pair.Key] = pair.Value;
            }
        }

        if (resultData is not null)
        {
            foreach (var pair in resultData)
            {
                values.TryAdd(pair.Key, pair.Value);
            }
        }

        return values;
    }

    private static string FindValue(IReadOnlyDictionary<string, string> values, params string?[] keys)
    {
        foreach (var key in keys.Where(key => !string.IsNullOrWhiteSpace(key)))
        {
            if (values.TryGetValue(key!, out var value))
            {
                return value ?? string.Empty;
            }
        }

        return EmptyValue;
    }

    private static string FindValue(IReadOnlyDictionary<string, string> values, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value))
            {
                return value ?? string.Empty;
            }
        }

        return EmptyValue;
    }

    private static void AddRow(
        ICollection<RecipeVisionFlowResultPreviewItem> rows,
        ISet<string> seen,
        string resultToolId,
        string inputKey,
        string? name,
        string? dataType,
        string? value,
        string source,
        IReadOnlyList<IVisionFlowResultPreviewVariable>? compatibleVariables = null,
        string boundVariableKey = "",
        Action<string?>? boundVariableChanged = null)
    {
        if (string.IsNullOrWhiteSpace(name) || !seen.Add(name.Trim()))
        {
            return;
        }

        rows.Add(new RecipeVisionFlowResultPreviewItem(
            resultToolId.Trim(),
            inputKey.Trim(),
            name.Trim(),
            string.IsNullOrWhiteSpace(dataType) ? "Value" : dataType.Trim(),
            value ?? string.Empty,
            source,
            compatibleVariables ?? Array.Empty<IVisionFlowResultPreviewVariable>(),
            boundVariableKey,
            boundVariableChanged));
    }

    private static string InferDataType(string key)
    {
        var suffix = key.Split('.').LastOrDefault() ?? string.Empty;
        return suffix switch
        {
            "X" or "Y" or "Angle" or "Score" or "Radius" or "Distance" => "Number",
            "ResultFrameId" or "ResultImage" => "Image",
            "OverallResult" or "Outcome" => "Result",
            _ => "Value"
        };
    }
}

public sealed class RecipeVisionFlowResultPreviewItem : BindableBase
{
    private readonly Action<string?>? _boundVariableChanged;
    private string _boundVariableKey;

    public RecipeVisionFlowResultPreviewItem(
        string resultToolId,
        string inputKey,
        string name,
        string dataType,
        string value,
        string source,
        IReadOnlyList<IVisionFlowResultPreviewVariable> compatibleVariables,
        string boundVariableKey = "",
        Action<string?>? boundVariableChanged = null)
    {
        ResultToolId = resultToolId;
        InputKey = inputKey;
        Name = name;
        DataType = dataType;
        Value = value;
        Source = source;
        CompatibleVariables = compatibleVariables;
        _boundVariableKey = boundVariableKey;
        _boundVariableChanged = boundVariableChanged;
    }

    public string ResultToolId { get; }

    public string InputKey { get; }

    public string Name { get; }

    public string DataType { get; }

    public string Value { get; }

    public string Source { get; }

    public IReadOnlyList<IVisionFlowResultPreviewVariable> CompatibleVariables { get; }

    public string BoundVariableKey
    {
        get => _boundVariableKey;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (SetProperty(ref _boundVariableKey, normalized))
            {
                _boundVariableChanged?.Invoke(normalized);
            }
        }
    }
}
