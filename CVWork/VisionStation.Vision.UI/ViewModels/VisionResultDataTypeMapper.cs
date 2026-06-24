namespace VisionStation.Vision.UI.ViewModels;

public static class VisionResultDataTypeMapper
{
    public static readonly IReadOnlyList<string> VariableDataTypes =
    [
        "string",
        "double",
        "int",
        "bool",
        "enum",
        "visionResult",
        "image",
        "point",
        "pose",
        "line",
        "circle",
        "roi",
        "json"
    ];

    public static string ToVariableDataType(string? dataType)
    {
        var text = dataType?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "string";
        }

        return text.ToLowerInvariant() switch
        {
            "number" or "double" or "float" or "decimal" => "double",
            "int" or "integer" or "long" => "int",
            "bool" or "boolean" => "bool",
            "result" or "enum" or "outcome" or "okng" or "ok/ng" => "enum",
            "visionresult" or "vision-result" or "vision_result" => "visionResult",
            "text" or "string" => "string",
            "image" or "picture" => "image",
            "point" => "point",
            "pose" => "pose",
            "line" => "line",
            "circle" => "circle",
            "roi" or "region" => "roi",
            "json" => "json",
            _ when text.EndsWith("[]", StringComparison.Ordinal) => "json",
            _ => "string"
        };
    }

    public static bool AreCompatible(string? left, string? right)
    {
        return string.Equals(ToVariableDataType(left), ToVariableDataType(right), StringComparison.OrdinalIgnoreCase);
    }
}
