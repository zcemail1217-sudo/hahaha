using VisionStation.Domain;
using OpenCvSharp;
using System.Globalization;
using System.Text;

namespace VisionStation.Vision;

public interface IVisionTool
{
    VisionToolKind Kind { get; }

    Task<ToolResult> ExecuteAsync(VisionToolDefinition definition, VisionToolContext context, CancellationToken cancellationToken = default);
}

public interface IVisionPipeline
{
    Task<VisionPipelineResult> ExecuteAsync(Recipe recipe, ImageFrame frame, CancellationToken cancellationToken = default);
}

public sealed class VisionToolContext : IDisposable
{
    private readonly Dictionary<ImageFrame, Mat> _grayMatCache = new();

    public VisionToolContext(Recipe recipe, ImageFrame frame)
    {
        Recipe = recipe;
        OriginalFrame = frame;
        ResultFrame = frame;
    }

    public Recipe Recipe { get; }

    public ImageFrame OriginalFrame { get; }

    public ImageFrame ResultFrame { get; set; }

    public List<ToolResult> ToolResults { get; } = new();

    public Dictionary<string, object> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, ImageFrame> ImageOutputs { get; } = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, object> PortOutputs { get; } = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, ImageFrame> ToolFrames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, ImageFrame> CapturedToolFrames => ToolFrames;

    public string ResolveTextTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var value = ReplaceTokenBlocks(text, "${", "}");
        value = ReplaceTokenBlocks(value, "{{", "}}");
        return value;
    }

    public Mat GetGrayMat(ImageFrame frame)
    {
        if (!_grayMatCache.TryGetValue(frame, out var gray))
        {
            gray = ImageFrameMatFactory.ToGrayMat(frame);
            _grayMatCache[frame] = gray;
        }

        return gray;
    }

    public ImageFrame GetInputImage(VisionToolDefinition definition)
    {
        if (TryGetInputImage(definition, out var frame))
        {
            return frame;
        }

        return ResultFrame;
    }

    public bool TryGetInputImage(VisionToolDefinition definition, out ImageFrame frame)
    {
        if (TryGetPortInput<ImageFrame>(definition, "ImageInput", out var connectedPortFrame))
        {
            frame = connectedPortFrame;
            return true;
        }

        if (definition.Parameters.TryGetValue("inputImageToolId", out var sourceToolId) &&
            !string.IsNullOrWhiteSpace(sourceToolId) &&
            ImageOutputs.TryGetValue(sourceToolId, out var connectedFrame))
        {
            frame = connectedFrame;
            return true;
        }

        frame = default!;
        return false;
    }

    public void SetImageOutput(VisionToolDefinition definition, ImageFrame frame)
    {
        ImageOutputs[definition.Id] = frame;
        SetPortOutput(definition, "ImageOutput", frame);
        ResultFrame = frame;
    }

    public void SetPortOutput(VisionToolDefinition definition, string portKey, object value)
    {
        PortOutputs[GetPortOutputKey(definition.Id, portKey)] = value;
    }

    public bool TryResolveTextValue(string key, out string value)
    {
        value = string.Empty;
        key = key?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (TryResolveRecipeValue(key, out value))
        {
            return true;
        }

        if (TryResolveToolValue(key, out value))
        {
            return true;
        }

        if (TryResolvePortValue(key, out value))
        {
            return true;
        }

        return false;
    }

    public void CaptureToolFrame(VisionToolDefinition definition)
    {
        ToolFrames[definition.Id] = ResultFrame;
    }

    public bool TryGetPortInput<T>(VisionToolDefinition definition, string inputPortKey, out T value)
    {
        value = default!;
        var sourceToolId = GetInputSourceToolId(definition, inputPortKey);
        if (string.IsNullOrWhiteSpace(sourceToolId))
        {
            return false;
        }

        var sourcePortKey = GetInputSourcePortKey(definition, inputPortKey);
        if (!PortOutputs.TryGetValue(GetPortOutputKey(sourceToolId, sourcePortKey), out var output) || output is not T typed)
        {
            return false;
        }

        value = typed;
        return true;
    }

    public bool TryGetPortInputValue(VisionToolDefinition definition, string inputPortKey, out object value)
    {
        value = default!;
        var sourceToolId = GetInputSourceToolId(definition, inputPortKey);
        if (string.IsNullOrWhiteSpace(sourceToolId))
        {
            return false;
        }

        var sourcePortKey = GetInputSourcePortKey(definition, inputPortKey);
        if (!PortOutputs.TryGetValue(GetPortOutputKey(sourceToolId, sourcePortKey), out var output))
        {
            return false;
        }

        value = output;
        return true;
    }

    public IReadOnlyList<ToolResult> GetConnectedResults(VisionToolDefinition definition)
    {
        var sourceToolId = GetInputSourceToolId(definition, "ResultInput");
        if (string.IsNullOrWhiteSpace(sourceToolId))
        {
            return ToolResults;
        }

        return ToolResults
            .Where(result => string.Equals(result.ToolId, sourceToolId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static string GetInputSourceToolId(VisionToolDefinition definition, string inputPortKey)
    {
        if (string.Equals(inputPortKey, "ImageInput", StringComparison.OrdinalIgnoreCase))
        {
            return definition.Parameters.GetValueOrDefault("inputImageToolId")
                   ?? definition.Parameters.GetValueOrDefault("inputImageSourceToolId")
                   ?? definition.Parameters.GetValueOrDefault($"input:{inputPortKey}:toolId")
                   ?? string.Empty;
        }

        if (string.Equals(inputPortKey, "PositionInput", StringComparison.OrdinalIgnoreCase))
        {
            return definition.Parameters.GetValueOrDefault($"input:{inputPortKey}:toolId") ?? string.Empty;
        }

        if (string.Equals(inputPortKey, "ResultInput", StringComparison.OrdinalIgnoreCase))
        {
            return definition.Parameters.GetValueOrDefault("resultInputToolId")
                   ?? definition.Parameters.GetValueOrDefault($"input:{inputPortKey}:toolId")
                   ?? string.Empty;
        }

        return definition.Parameters.GetValueOrDefault($"input:{inputPortKey}:toolId") ?? string.Empty;
    }

    private static string GetInputSourcePortKey(VisionToolDefinition definition, string inputPortKey)
    {
        if (string.Equals(inputPortKey, "ImageInput", StringComparison.OrdinalIgnoreCase))
        {
            return definition.Parameters.GetValueOrDefault($"input:{inputPortKey}:portKey") ?? "ImageOutput";
        }

        if (string.Equals(inputPortKey, "PositionInput", StringComparison.OrdinalIgnoreCase))
        {
            return definition.Parameters.GetValueOrDefault($"input:{inputPortKey}:portKey") ?? "PositionOutput";
        }

        if (inputPortKey is "PointInput" or "Point1Input" or "Point2Input")
        {
            return definition.Parameters.GetValueOrDefault($"input:{inputPortKey}:portKey") ?? "CenterOutput";
        }

        if (inputPortKey is "LineInput" or "Line1Input" or "Line2Input")
        {
            return definition.Parameters.GetValueOrDefault($"input:{inputPortKey}:portKey") ?? "LineOutput";
        }

        return definition.Parameters.GetValueOrDefault($"input:{inputPortKey}:portKey") ?? "ResultOutput";
    }

    private static string GetPortOutputKey(string toolId, string portKey)
    {
        return $"{toolId}:{portKey}";
    }

    private string ReplaceTokenBlocks(string text, string openToken, string closeToken)
    {
        var builder = new StringBuilder(text.Length);
        var cursor = 0;

        while (cursor < text.Length)
        {
            var start = text.IndexOf(openToken, cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(text, cursor, text.Length - cursor);
                break;
            }

            var contentStart = start + openToken.Length;
            var end = text.IndexOf(closeToken, contentStart, StringComparison.Ordinal);
            if (end < 0)
            {
                builder.Append(text, cursor, text.Length - cursor);
                break;
            }

            builder.Append(text, cursor, start - cursor);
            var key = text[contentStart..end].Trim();
            builder.Append(TryResolveTextValue(key, out var value) ? value : text[start..(end + closeToken.Length)]);
            cursor = end + closeToken.Length;
        }

        return builder.ToString();
    }

    private bool TryResolveRecipeValue(string key, out string value)
    {
        value = string.Empty;
        if (string.Equals(key, "RecipeId", StringComparison.OrdinalIgnoreCase))
        {
            value = Recipe.Id;
            return true;
        }

        if (string.Equals(key, "RecipeName", StringComparison.OrdinalIgnoreCase))
        {
            value = Recipe.Name;
            return true;
        }

        if (string.Equals(key, "ProductCode", StringComparison.OrdinalIgnoreCase))
        {
            value = Recipe.ProductCode;
            return true;
        }

        foreach (var parameter in Recipe.ProductParameters)
        {
            if (string.Equals(key, parameter.Id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, parameter.Name, StringComparison.OrdinalIgnoreCase))
            {
                value = parameter.Value ?? string.Empty;
                return true;
            }
        }

        foreach (var variable in Recipe.Variables.Where(variable => variable.Enabled))
        {
            if (string.Equals(key, variable.Key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, variable.Name, StringComparison.OrdinalIgnoreCase))
            {
                value = string.IsNullOrWhiteSpace(variable.CurrentValue) ? variable.DefaultValue : variable.CurrentValue;
                return true;
            }
        }

        return false;
    }

    private bool TryResolveToolValue(string key, out string value)
    {
        value = string.Empty;
        foreach (var result in ToolResults.AsEnumerable().Reverse())
        {
            if (TryResolveToolValue(result, key, out value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveToolValue(ToolResult result, string key, out string value)
    {
        value = string.Empty;
        if (string.Equals(key, "LastOutcome", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "Outcome", StringComparison.OrdinalIgnoreCase))
        {
            value = result.Outcome.ToString();
            return true;
        }

        if (result.Data.TryGetValue(key, out var direct))
        {
            value = direct ?? string.Empty;
            return true;
        }

        var separators = new[] { '.', ':' };
        foreach (var separator in separators)
        {
            var index = key.IndexOf(separator);
            if (index <= 0 || index >= key.Length - 1)
            {
                continue;
            }

            var prefix = key[..index];
            var member = key[(index + 1)..];
            if (!IsToolPrefix(result, prefix))
            {
                continue;
            }

            if (TryResolveToolMember(result, member, out value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveToolMember(ToolResult result, string member, out string value)
    {
        value = string.Empty;
        if (string.Equals(member, "Outcome", StringComparison.OrdinalIgnoreCase))
        {
            value = result.Outcome.ToString();
            return true;
        }

        if (string.Equals(member, "Message", StringComparison.OrdinalIgnoreCase))
        {
            value = result.Message;
            return true;
        }

        if (string.Equals(member, "DurationMs", StringComparison.OrdinalIgnoreCase))
        {
            value = result.Duration.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);
            return true;
        }

        if (string.Equals(member, "Kind", StringComparison.OrdinalIgnoreCase))
        {
            value = result.Kind.ToString();
            return true;
        }

        if (result.Data.TryGetValue(member, out var data))
        {
            value = data ?? string.Empty;
            return true;
        }

        return false;
    }

    private static bool IsToolPrefix(ToolResult result, string prefix)
    {
        return string.Equals(prefix, result.ToolId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(prefix, result.ToolName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(prefix, result.Kind.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private bool TryResolvePortValue(string key, out string value)
    {
        value = string.Empty;
        if (!TrySplitPortKey(key, out var toolKey, out var portKey))
        {
            return false;
        }

        var candidates = ResolveToolIds(toolKey).ToArray();
        foreach (var toolId in candidates)
        {
            if (PortOutputs.TryGetValue(GetPortOutputKey(toolId, portKey), out var output))
            {
                value = FormatTextValue(output);
                return true;
            }
        }

        return false;
    }

    private static bool TrySplitPortKey(string key, out string toolKey, out string portKey)
    {
        toolKey = string.Empty;
        portKey = string.Empty;
        var index = key.IndexOf(':');
        if (index <= 0 || index >= key.Length - 1)
        {
            index = key.IndexOf('.');
        }

        if (index <= 0 || index >= key.Length - 1)
        {
            return false;
        }

        toolKey = key[..index];
        portKey = key[(index + 1)..];
        return true;
    }

    private IEnumerable<string> ResolveToolIds(string toolKey)
    {
        yield return toolKey;
        foreach (var result in ToolResults.AsEnumerable().Reverse())
        {
            if (IsToolPrefix(result, toolKey) && !string.IsNullOrWhiteSpace(result.ToolId))
            {
                yield return result.ToolId;
            }
        }
    }

    private static string FormatTextValue(object? value)
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

    public void Dispose()
    {
        foreach (var gray in _grayMatCache.Values)
        {
            gray.Dispose();
        }

        _grayMatCache.Clear();
    }
}

public sealed record VisionPipelineResult(
    ImageFrame ResultFrame,
    IReadOnlyList<ToolResult> ToolResults,
    InspectionOutcome Outcome,
    string Barcode,
    string Message,
    IReadOnlyDictionary<string, ImageFrame>? ToolFrames = null);
