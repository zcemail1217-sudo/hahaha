using System.Globalization;
using System.Text.Json;
using VisionStation.Vision;

namespace VisionStation.Vision.UI.Services;

public static class MultiTargetMatchResultReader
{
    private const string SchemaVersion = "2";

    public static IReadOnlyList<MultiTargetMatchCandidate> Read(
        IReadOnlyDictionary<string, string> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.TryGetValue("matchesV2", out var matchesV2))
        {
            return ReadV2(data, matchesV2);
        }

        return ReadLegacy(data.GetValueOrDefault("matches"));
    }

    private static IReadOnlyList<MultiTargetMatchCandidate> ReadV2(
        IReadOnlyDictionary<string, string> data,
        string? text)
    {
        if (!string.Equals(
                data.GetValueOrDefault("matchSchemaVersion"),
                SchemaVersion,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<MultiTargetMatchCandidate>();
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<MultiTargetMatchCandidate>();
            }

            var fallbackWidth = ReadPositiveInt(data.GetValueOrDefault("templateWidth"));
            var fallbackHeight = ReadPositiveInt(data.GetValueOrDefault("templateHeight"));
            var matches = new List<MultiTargetMatchCandidate>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!TryReadV2Candidate(item, fallbackWidth, fallbackHeight, out var candidate))
                {
                    return Array.Empty<MultiTargetMatchCandidate>();
                }

                matches.Add(candidate!);
            }

            return matches;
        }
        catch (JsonException)
        {
            return Array.Empty<MultiTargetMatchCandidate>();
        }
    }

    private static bool TryReadV2Candidate(
        JsonElement item,
        int fallbackWidth,
        int fallbackHeight,
        out MultiTargetMatchCandidate? candidate)
    {
        candidate = null;
        if (item.ValueKind != JsonValueKind.Object ||
            !TryGetFiniteDouble(item, "x", out var x) ||
            !TryGetFiniteDouble(item, "y", out var y) ||
            !TryGetFiniteDouble(item, "angle", out var angle) ||
            !TryGetFiniteDouble(item, "scale", out var scale) ||
            scale <= 0 ||
            !TryGetFiniteDouble(item, "score", out var score) ||
            !TryGetUnitInterval(item, "outerCoverage", out var outerCoverage) ||
            !TryGetUnitInterval(item, "innerCoverage", out var innerCoverage) ||
            !TryGetFiniteDouble(item, "edgeDistanceP95Px", out var edgeDistanceP95Px) ||
            edgeDistanceP95Px < 0 ||
            !TryGetUnitInterval(item, "polarityAgreement", out var polarityAgreement) ||
            !TryGetOptionalPositiveInt(item, "width", fallbackWidth, out var width) ||
            !TryGetOptionalPositiveInt(item, "height", fallbackHeight, out var height) ||
            !TryGetOptionalNonNegativeDouble(item, "radius", out var radius))
        {
            return false;
        }

        var shape = "Rectangle";
        if (item.TryGetProperty("shape", out var shapeElement))
        {
            if (shapeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var rawShape = shapeElement.GetString();
            if (!string.IsNullOrWhiteSpace(rawShape))
            {
                shape = rawShape.Trim();
            }
        }

        candidate = new MultiTargetMatchCandidate(
            x,
            y,
            angle,
            score,
            width,
            height,
            shape,
            radius)
        {
            Scale = scale,
            OuterCoverage = outerCoverage,
            InnerCoverage = innerCoverage,
            EdgeDistanceP95Px = edgeDistanceP95Px,
            PolarityAgreement = polarityAgreement
        };
        return true;
    }

    private static IReadOnlyList<MultiTargetMatchCandidate> ReadLegacy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<MultiTargetMatchCandidate>();
        }

        var matches = new List<MultiTargetMatchCandidate>();
        foreach (var item in text.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 6 ||
                !TryParseFiniteDouble(parts[0], out var x) ||
                !TryParseFiniteDouble(parts[1], out var y) ||
                !TryParseFiniteDouble(parts[2], out var angle) ||
                !TryParseFiniteDouble(parts[3], out var score) ||
                !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
                width <= 0 ||
                !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
                height <= 0)
            {
                return Array.Empty<MultiTargetMatchCandidate>();
            }

            var shape = parts.Length >= 7 && !string.IsNullOrWhiteSpace(parts[6])
                ? parts[6]
                : "Rectangle";
            var radius = 0d;
            if (parts.Length >= 8 &&
                (!TryParseFiniteDouble(parts[7], out radius) || radius < 0))
            {
                return Array.Empty<MultiTargetMatchCandidate>();
            }

            matches.Add(new MultiTargetMatchCandidate(
                x,
                y,
                angle,
                score,
                width,
                height,
                shape,
                radius));
        }

        return matches;
    }

    private static bool TryGetFiniteDouble(JsonElement item, string propertyName, out double value)
    {
        value = 0;
        return item.TryGetProperty(propertyName, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetDouble(out value) &&
               double.IsFinite(value);
    }

    private static bool TryGetUnitInterval(JsonElement item, string propertyName, out double value)
    {
        return TryGetFiniteDouble(item, propertyName, out value) && value is >= 0 and <= 1;
    }

    private static bool TryGetOptionalPositiveInt(
        JsonElement item,
        string propertyName,
        int fallback,
        out int value)
    {
        value = fallback;
        if (!item.TryGetProperty(propertyName, out var element))
        {
            return true;
        }

        return element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt32(out value) &&
               value > 0;
    }

    private static bool TryGetOptionalNonNegativeDouble(
        JsonElement item,
        string propertyName,
        out double value)
    {
        value = 0;
        if (!item.TryGetProperty(propertyName, out _))
        {
            return true;
        }

        return TryGetFiniteDouble(item, propertyName, out value) && value >= 0;
    }

    private static int ReadPositiveInt(string? text)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 0;
    }

    private static bool TryParseFiniteDouble(string? text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               double.IsFinite(value);
    }
}
