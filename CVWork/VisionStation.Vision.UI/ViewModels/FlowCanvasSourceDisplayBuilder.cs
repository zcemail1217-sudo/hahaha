using VisionStation.Domain;

namespace VisionStation.Vision.UI.ViewModels;

public static class FlowCanvasSourceDisplayBuilder
{
    private const string UnconnectedSource = "未连接";
    private const string InvalidSource = "来源无效";

    public static string BuildInputSourceDisplayName(
        VisionToolItem targetTool,
        string targetPortKey,
        IReadOnlyList<VisionToolItem> tools)
    {
        var parameters = ParseParameters(targetTool.ParametersText);
        var sourceToolId = GetParameterValue(parameters, GetConnectionToolParameterKey(targetPortKey));
        if (string.IsNullOrWhiteSpace(sourceToolId))
        {
            return UnconnectedSource;
        }

        var sourceTool = tools.FirstOrDefault(tool =>
            string.Equals(tool.Id, sourceToolId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (sourceTool is null)
        {
            return InvalidSource;
        }

        var sourcePortKey = GetParameterValue(parameters, GetConnectionPortParameterKey(targetPortKey));
        sourcePortKey = string.IsNullOrWhiteSpace(sourcePortKey) ? "ResultOutput" : sourcePortKey.Trim();

        var sourcePort = VisionToolCatalog.GetOutputPorts(sourceTool.Kind)
            .FirstOrDefault(port => string.Equals(port.Key, sourcePortKey, StringComparison.OrdinalIgnoreCase));
        if (sourcePort is null)
        {
            return InvalidSource;
        }

        return $"{ResolveToolName(sourceTool)}.{sourcePort.Name}";
    }

    private static string ResolveToolName(VisionToolItem tool)
    {
        if (!string.IsNullOrWhiteSpace(tool.Name))
        {
            return tool.Name.Trim();
        }

        return string.IsNullOrWhiteSpace(tool.Id) ? tool.Kind.ToString() : tool.Id.Trim();
    }

    private static string? GetParameterValue(IReadOnlyDictionary<string, string> parameters, string key)
    {
        return parameters.TryGetValue(key, out var value) ? value : null;
    }

    private static string GetConnectionToolParameterKey(string targetPortKey)
    {
        return $"input:{targetPortKey}:toolId";
    }

    private static string GetConnectionPortParameterKey(string targetPortKey)
    {
        return $"input:{targetPortKey}:portKey";
    }

    private static Dictionary<string, string> ParseParameters(string text)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in text.Split(["\r\n", "\n", ";"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = segment.IndexOf('=');
            if (index <= 0 || index == segment.Length - 1)
            {
                continue;
            }

            parameters[segment[..index].Trim()] = segment[(index + 1)..].Trim();
        }

        return parameters;
    }
}
