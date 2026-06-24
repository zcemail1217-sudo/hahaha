using VisionStation.Domain;
using VisionStation.Vision;

namespace VisionStation.Application.Inspection;

internal static class VisionResultResolver
{
    private const string FlowPortSourcePrefix = "port:";

    public static string? ResolveVisionResultAddress(Recipe recipe, string resultKey)
    {
        var mapping = ResolveVisionResultDefinition(recipe, resultKey);
        return string.IsNullOrWhiteSpace(mapping?.PlcAddress) ? null : mapping.PlcAddress;
    }

    public static VisionResultDefinition? ResolveVisionResultDefinition(Recipe recipe, string resultKey)
    {
        if (string.IsNullOrWhiteSpace(resultKey))
        {
            return null;
        }

        var activeDefinition = GetVisionResultDefinitions(recipe).FirstOrDefault(item =>
            string.Equals(item.Id, resultKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Name, resultKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.ExternalAlias, resultKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.SourceKey, resultKey, StringComparison.OrdinalIgnoreCase));

        return activeDefinition ?? recipe.VisionResults.FirstOrDefault(item =>
            string.Equals(item.Id, resultKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Name, resultKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.ExternalAlias, resultKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.SourceKey, resultKey, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<VisionResultDefinition> GetVisionResultDefinitions(Recipe recipe)
    {
        var activeFlowId = recipe.CurrentFlowId;
        var global = recipe.VisionResults
            .Where(item => string.IsNullOrWhiteSpace(item.FlowId))
            .ToArray();
        var scoped = recipe.VisionResults
            .Where(item => !string.IsNullOrWhiteSpace(item.FlowId) &&
                           string.Equals(item.FlowId, activeFlowId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return scoped.Length == 0 ? global : global.Concat(scoped).ToArray();
    }

    public static bool TryResolveVisionValue(
        Recipe recipe,
        IReadOnlyList<ToolResult> toolResults,
        VisionPipelineResult? pipelineResult,
        string key,
        out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (pipelineResult is not null)
        {
            if (string.Equals(key, "OverallResult", StringComparison.OrdinalIgnoreCase))
            {
                value = pipelineResult.Outcome.ToString();
                return true;
            }

            if (string.Equals(key, "Barcode", StringComparison.OrdinalIgnoreCase))
            {
                value = pipelineResult.Barcode;
                return true;
            }
        }

        var mapping = ResolveVisionResultDefinition(recipe, key);
        if (mapping is not null)
        {
            return TryResolveVisionDefinitionValue(mapping, toolResults, pipelineResult, out value);
        }

        foreach (var result in toolResults.Reverse())
        {
            if (result.Data.TryGetValue(key, out var resolvedValue))
            {
                value = resolvedValue ?? string.Empty;
                return true;
            }
        }

        return false;
    }

    public static bool TryResolveVisionDefinitionValue(
        VisionResultDefinition definition,
        IReadOnlyList<ToolResult> toolResults,
        VisionPipelineResult? pipelineResult,
        out string value)
    {
        value = string.Empty;
        if (pipelineResult is not null && string.Equals(definition.SourceKey, "Outcome", StringComparison.OrdinalIgnoreCase))
        {
            value = pipelineResult.Outcome.ToString();
            return true;
        }

        if (pipelineResult is not null && string.Equals(definition.SourceKey, "Barcode", StringComparison.OrdinalIgnoreCase))
        {
            value = pipelineResult.Barcode;
            return true;
        }

        if (pipelineResult is not null &&
            (string.Equals(definition.SourceKey, "ResultFrameId", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(definition.SourceKey, "Vision.ResultFrameId", StringComparison.OrdinalIgnoreCase)))
        {
            value = pipelineResult.ResultFrame.Id;
            return true;
        }

        var candidates = string.IsNullOrWhiteSpace(definition.SourceToolId)
            ? toolResults.Reverse().ToArray()
            : toolResults
                .Where(result => string.Equals(result.ToolId, definition.SourceToolId, StringComparison.OrdinalIgnoreCase))
                .Reverse()
                .ToArray();

        foreach (var candidate in candidates)
        {
            if (TryResolveExportedPortValue(candidate, definition.SourceKey, pipelineResult, out value))
            {
                return true;
            }

            if (candidate.Data.TryGetValue(definition.SourceKey, out var resolvedValue))
            {
                value = resolvedValue ?? string.Empty;
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveExportedPortValue(
        ToolResult result,
        string sourceKey,
        VisionPipelineResult? pipelineResult,
        out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(sourceKey) ||
            !sourceKey.StartsWith(FlowPortSourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var portKey = sourceKey[FlowPortSourcePrefix.Length..];
        value = VisionPortValueFormatter.FormatPortValue(result, portKey, pipelineResult);
        return !string.IsNullOrWhiteSpace(value);
    }
}
