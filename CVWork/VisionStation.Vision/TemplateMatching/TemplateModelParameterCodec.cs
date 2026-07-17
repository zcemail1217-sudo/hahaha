using System.Collections.ObjectModel;
using System.Globalization;
using VisionStation.Domain;

namespace VisionStation.Vision;

public static class TemplateModelParameterCodec
{
    public const string HalconScaledShapeModelFormat = "halcon-scaled-shape";

    private const string ModelPath = "halcon.modelPath";
    private const string ModelMetadataPath = "halcon.modelMetadataPath";
    private const string ModelFormat = "halcon.modelFormat";
    private const string ModelVersion = "halcon.modelVersion";
    private const string ModelRuntimeVersion = "halcon.modelRuntimeVersion";
    private const string ModelGeneration = "halcon.modelGeneration";
    private const string ModelChecksum = "halcon.modelChecksum";
    private const string MetadataChecksum = "halcon.metadataChecksum";
    private const string GenerationParameterFingerprint = "halcon.generationParameterFingerprint";
    private const string StandardX = "halcon.standardX";
    private const string StandardY = "halcon.standardY";
    private const string StandardAngle = "halcon.standardAngle";
    private const string StandardScale = "halcon.standardScale";
    private const string TemplateWidth = "halcon.templateWidth";
    private const string TemplateHeight = "halcon.templateHeight";

    private static readonly string[] KnownKeys =
    [
        ModelPath,
        ModelMetadataPath,
        ModelFormat,
        ModelVersion,
        ModelRuntimeVersion,
        ModelGeneration,
        ModelChecksum,
        MetadataChecksum,
        GenerationParameterFingerprint,
        StandardX,
        StandardY,
        StandardAngle,
        StandardScale,
        TemplateWidth,
        TemplateHeight
    ];

    public static IReadOnlyList<string> Keys { get; } =
        new ReadOnlyCollection<string>(KnownKeys);

    public static HalconTemplateModelState? ReadHalcon(
        IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!KnownKeys.Any(key => TryGetValue(parameters, key, out _)))
        {
            return null;
        }

        var reference = new TemplateModelReference(
            ReadRequired(parameters, ModelPath),
            ReadRequired(parameters, ModelMetadataPath),
            ReadRequired(parameters, ModelFormat),
            ReadSha256(parameters, ModelChecksum),
            ReadSha256(parameters, MetadataChecksum),
            ReadRequired(parameters, ModelGeneration),
            ReadRequired(parameters, ModelVersion),
            ReadRequired(parameters, ModelRuntimeVersion),
            ReadSha256(parameters, GenerationParameterFingerprint));
        var geometry = new TemplateLearnedGeometry(
            new Pose2D(
                ReadFiniteDouble(parameters, StandardX),
                ReadFiniteDouble(parameters, StandardY),
                ReadFiniteDouble(parameters, StandardAngle))
            {
                Scale = ReadPositiveDouble(parameters, StandardScale)
            },
            ReadPositiveInt(parameters, TemplateWidth),
            ReadPositiveInt(parameters, TemplateHeight));

        return new HalconTemplateModelState(reference, geometry);
    }

    public static void WriteHalcon(
        IDictionary<string, string> parameters,
        HalconTemplateModelState state)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state);
        RemoveHalcon(parameters);

        var reference = state.Reference;
        var geometry = state.Geometry;
        parameters[ModelPath] = reference.ModelPath;
        parameters[ModelMetadataPath] = reference.MetadataPath;
        parameters[ModelFormat] = reference.ModelFormat;
        parameters[ModelVersion] = reference.ModelVersion;
        parameters[ModelRuntimeVersion] = reference.RuntimeVersion;
        parameters[ModelGeneration] = reference.Generation;
        parameters[ModelChecksum] = reference.ModelChecksum.ToLowerInvariant();
        parameters[MetadataChecksum] = reference.MetadataChecksum.ToLowerInvariant();
        parameters[GenerationParameterFingerprint] = reference.GenerationParameterFingerprint.ToLowerInvariant();
        parameters[StandardX] = FormatDouble(geometry.StandardPose.X);
        parameters[StandardY] = FormatDouble(geometry.StandardPose.Y);
        parameters[StandardAngle] = FormatDouble(geometry.StandardPose.Angle);
        parameters[StandardScale] = FormatDouble(geometry.StandardPose.Scale);
        parameters[TemplateWidth] = geometry.TemplateWidth.ToString(CultureInfo.InvariantCulture);
        parameters[TemplateHeight] = geometry.TemplateHeight.ToString(CultureInfo.InvariantCulture);
    }

    public static void RemoveHalcon(IDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var keysToRemove = parameters.Keys
            .Where(candidate => KnownKeys.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        foreach (var key in keysToRemove)
        {
            parameters.Remove(key);
        }
    }

    private static void ValidateState(HalconTemplateModelState state)
    {
        var reference = state.Reference ?? throw new ArgumentException("HALCON model reference is required.", nameof(state));
        var geometry = state.Geometry ?? throw new ArgumentException("HALCON learned geometry is required.", nameof(state));
        ValidateRequired(reference.ModelPath, ModelPath);
        ValidateRequired(reference.MetadataPath, ModelMetadataPath);
        ValidateRequired(reference.ModelFormat, ModelFormat);
        ValidateRequired(reference.ModelVersion, ModelVersion);
        ValidateRequired(reference.RuntimeVersion, ModelRuntimeVersion);
        ValidateRequired(reference.Generation, ModelGeneration);
        ValidateSha256(reference.ModelChecksum, ModelChecksum);
        ValidateSha256(reference.MetadataChecksum, MetadataChecksum);
        ValidateSha256(reference.GenerationParameterFingerprint, GenerationParameterFingerprint);
        ValidateFinite(geometry.StandardPose.X, StandardX);
        ValidateFinite(geometry.StandardPose.Y, StandardY);
        ValidateFinite(geometry.StandardPose.Angle, StandardAngle);
        if (!double.IsFinite(geometry.StandardPose.Scale) || geometry.StandardPose.Scale <= 0)
        {
            ThrowInvalid(StandardScale, geometry.StandardPose.Scale.ToString(CultureInfo.InvariantCulture));
        }

        if (geometry.TemplateWidth <= 0)
        {
            ThrowInvalid(TemplateWidth, geometry.TemplateWidth.ToString(CultureInfo.InvariantCulture));
        }

        if (geometry.TemplateHeight <= 0)
        {
            ThrowInvalid(TemplateHeight, geometry.TemplateHeight.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string ReadRequired(IReadOnlyDictionary<string, string> parameters, string key)
    {
        if (!TryGetValue(parameters, key, out var value) ||
            string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            ThrowInvalid(key, value);
        }

        if (string.Equals(key, ModelFormat, StringComparison.Ordinal) &&
            !string.Equals(value, HalconScaledShapeModelFormat, StringComparison.Ordinal))
        {
            ThrowInvalid(key, value);
        }

        return value;
    }

    private static string ReadSha256(IReadOnlyDictionary<string, string> parameters, string key)
    {
        var value = ReadRequired(parameters, key);
        ValidateSha256(value, key);
        return value.ToLowerInvariant();
    }

    private static double ReadFiniteDouble(IReadOnlyDictionary<string, string> parameters, string key)
    {
        var raw = ReadRequired(parameters, key);
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value))
        {
            ThrowInvalid(key, raw);
        }

        return value;
    }

    private static double ReadPositiveDouble(IReadOnlyDictionary<string, string> parameters, string key)
    {
        var value = ReadFiniteDouble(parameters, key);
        if (value <= 0)
        {
            ThrowInvalid(key, value.ToString(CultureInfo.InvariantCulture));
        }

        return value;
    }

    private static int ReadPositiveInt(IReadOnlyDictionary<string, string> parameters, string key)
    {
        var raw = ReadRequired(parameters, key);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            ThrowInvalid(key, raw);
        }

        return value;
    }

    private static bool TryGetValue(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        out string value)
    {
        value = string.Empty;
        var found = false;
        foreach (var pair in parameters)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                if (found)
                {
                    ThrowInvalid(key, "<ambiguous duplicate keys>");
                }

                value = pair.Value;
                found = true;
            }
        }

        return found;
    }

    private static void ValidateRequired(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            ThrowInvalid(key, value);
        }

        if (string.Equals(key, ModelFormat, StringComparison.Ordinal) &&
            !string.Equals(value, HalconScaledShapeModelFormat, StringComparison.Ordinal))
        {
            ThrowInvalid(key, value);
        }
    }

    private static void ValidateSha256(string? value, string key)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            ThrowInvalid(key, value);
        }
    }

    private static void ValidateFinite(double value, string key)
    {
        if (!double.IsFinite(value))
        {
            ThrowInvalid(key, value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string FormatDouble(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static void ThrowInvalid(string key, string? raw)
    {
        throw new TemplateMatchingConfigurationException(
            TemplateMatchingDiagnostics.Create(
                TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                $"Invalid HALCON template model parameter {key}='{raw ?? "<null>"}'."));
    }
}
