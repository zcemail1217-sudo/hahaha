using System.Security.Cryptography;
using System.Text.Json;

namespace VisionStation.Vision;

/// <summary>
/// Managed-only proof that one resolved generation matches its recipe state and current
/// generation parameters. Constructing this descriptor never loads HalconDotNet or probes a license.
/// </summary>
internal sealed class ValidatedHalconModelDescriptor
{
    internal ValidatedHalconModelDescriptor(
        TemplateModelOwner owner,
        ResolvedTemplateModel resolvedModel,
        HalconTemplateModelMetadata metadata,
        HalconTemplateModelCacheKey cacheKey)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(resolvedModel);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(cacheKey);
        string resolvedPath = HalconTemplateModelCacheKey.NormalizeAbsolutePath(
            resolvedModel.ModelPath,
            nameof(resolvedModel));
        if (!string.Equals(
                resolvedPath,
                cacheKey.AbsoluteModelPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The validated model path does not match its cache key.",
                nameof(resolvedModel));
        }

        byte[] metadataBytes = resolvedModel.MetadataJson.ToArray();
        string metadataSha256 = Convert.ToHexString(SHA256.HashData(metadataBytes));
        if (!string.Equals(metadataSha256, cacheKey.MetadataSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The validated metadata bytes do not match their cache key.",
                nameof(resolvedModel));
        }

        Owner = new TemplateModelOwner(owner.RecipeId, owner.FlowId, owner.ToolId);
        ModelPath = cacheKey.AbsoluteModelPath;
        MetadataJson = metadataBytes;
        Metadata = metadata;
        CacheKey = cacheKey;
    }

    public TemplateModelOwner Owner { get; }

    public string ModelPath { get; }

    public ReadOnlyMemory<byte> MetadataJson { get; }

    public HalconTemplateModelMetadata Metadata { get; }

    public HalconTemplateModelCacheKey CacheKey { get; }
}

internal static class HalconModelMetadataValidator
{
    public static ValidatedHalconModelDescriptor Validate(
        ResolvedTemplateModel resolvedModel,
        TemplateModelOwner expectedOwner,
        HalconTemplateModelState modelState,
        HalconTemplateMatchingParameters currentParameters)
    {
        ArgumentNullException.ThrowIfNull(resolvedModel);
        ValidateOwnerShape(expectedOwner);
        ArgumentNullException.ThrowIfNull(modelState);
        ArgumentNullException.ThrowIfNull(modelState.Reference);
        ArgumentNullException.ThrowIfNull(modelState.Geometry);
        ArgumentNullException.ThrowIfNull(currentParameters);

        byte[] json = resolvedModel.MetadataJson.ToArray();
        HalconTemplateModelMetadata metadata;
        try
        {
            metadata = HalconTemplateModelMetadataJson.Deserialize(json);
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw Failure(
                TemplateMatchingDiagnosticCodes.ModelMetadataInvalid,
                $"HALCON metadata parsing failed; ExceptionType={exception.GetType().Name}.");
        }

        ValidatePinnedHeader(metadata);
        ValidateOwner(metadata.Owner, expectedOwner);
        ValidateModelCoordinates(metadata);
        ValidateReferenceAndResolvedModel(resolvedModel, json, modelState.Reference, metadata);
        ValidateGeometry(modelState.Geometry, metadata.Geometry);

        TemplateModelGenerationParameters currentGeneration =
            TemplateModelGenerationParameters.From(currentParameters);
        string currentFingerprint = TemplateModelGenerationFingerprint.Compute(currentGeneration);
        if (!FixedTimeEquals(metadata.GenerationParameterFingerprint, currentFingerprint))
        {
            throw Failure(
                TemplateMatchingDiagnosticCodes.ModelRelearnRequired,
                "Current HALCON model-generation parameters differ from the learned generation.");
        }

        string absoluteModelPath;
        try
        {
            absoluteModelPath = HalconTemplateModelCacheKey.NormalizeAbsolutePath(
                resolvedModel.ModelPath,
                nameof(resolvedModel));
        }
        catch (ArgumentException exception)
        {
            throw Failure(
                TemplateMatchingDiagnosticCodes.ModelMetadataInvalid,
                $"Resolved HALCON model path is invalid; ExceptionType={exception.GetType().Name}.");
        }

        var key = new HalconTemplateModelCacheKey(
            absoluteModelPath,
            modelState.Reference.ModelChecksum,
            modelState.Reference.MetadataChecksum);
        return new ValidatedHalconModelDescriptor(expectedOwner, resolvedModel, metadata, key);
    }

    private static void ValidatePinnedHeader(HalconTemplateModelMetadata metadata)
    {
        if (metadata.SchemaVersion != HalconTemplateModelMetadata.CurrentSchemaVersion ||
            !string.Equals(metadata.Engine, HalconTemplateModelMetadata.CurrentEngine, StringComparison.Ordinal) ||
            !string.Equals(
                metadata.ModelFormat,
                TemplateModelParameterCodec.HalconScaledShapeModelFormat,
                StringComparison.Ordinal))
        {
            throw Failure(
                TemplateMatchingDiagnosticCodes.ModelMetadataInvalid,
                "HALCON metadata schema, engine or model format is unsupported.");
        }

        if (!string.Equals(
                metadata.ModelVersion,
                HalconTemplateModelMetadata.CurrentModelVersion,
                StringComparison.Ordinal))
        {
            throw Failure(
                TemplateMatchingDiagnosticCodes.ModelVersionMismatch,
                "HALCON model format version differs from the pinned application version.");
        }

        if (!string.Equals(
                metadata.ManagedPackageVersion,
                HalconTemplateModelMetadata.CurrentManagedPackageVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                metadata.ManagedAssemblyVersion,
                HalconTemplateModelMetadata.CurrentManagedAssemblyVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                metadata.NativeRuntimeVersion,
                HalconTemplateModelMetadata.CurrentNativeRuntimeVersion,
                StringComparison.Ordinal))
        {
            throw Failure(
                TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch,
                "HALCON learned runtime versions differ from the pinned application runtime.");
        }
    }

    private static void ValidateReferenceAndResolvedModel(
        ResolvedTemplateModel resolvedModel,
        byte[] metadataJson,
        TemplateModelReference reference,
        HalconTemplateModelMetadata metadata)
    {
        string expectedModelFileName = $"model-{metadata.Generation}.shm";
        string expectedMetadataFileName = $"model-{metadata.Generation}.json";
        if (!string.Equals(reference.ModelFormat, metadata.ModelFormat, StringComparison.Ordinal) ||
            !string.Equals(reference.Generation, metadata.Generation, StringComparison.Ordinal) ||
            !FixedTimeEquals(reference.ModelChecksum, metadata.ModelChecksum) ||
            !string.Equals(metadata.ModelFileName, expectedModelFileName, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(reference.ModelPath), expectedModelFileName, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(reference.MetadataPath), expectedMetadataFileName, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(resolvedModel.ModelPath), expectedModelFileName, StringComparison.Ordinal))
        {
            throw Failure(
                TemplateMatchingDiagnosticCodes.ModelMetadataInvalid,
                "HALCON metadata, recipe reference and resolved model identify different generations.");
        }

        if (!FixedTimeEquals(
                reference.GenerationParameterFingerprint,
                metadata.GenerationParameterFingerprint))
        {
            throw Failure(
                TemplateMatchingDiagnosticCodes.ModelRelearnRequired,
                "HALCON recipe generation fingerprint differs from learned metadata.");
        }

        if (!string.Equals(reference.ModelVersion, metadata.ModelVersion, StringComparison.Ordinal))
        {
            throw Failure(
                TemplateMatchingDiagnosticCodes.ModelVersionMismatch,
                "HALCON recipe and metadata model versions differ.");
        }

        if (!string.Equals(reference.RuntimeVersion, metadata.NativeRuntimeVersion, StringComparison.Ordinal))
        {
            throw Failure(
                TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch,
                "HALCON recipe and metadata runtime versions differ.");
        }

        string actualMetadataChecksum = Convert.ToHexString(SHA256.HashData(metadataJson))
            .ToLowerInvariant();
        if (!FixedTimeEquals(reference.MetadataChecksum, actualMetadataChecksum))
        {
            throw Failure(
                TemplateMatchingDiagnosticCodes.ModelMetadataInvalid,
                "HALCON metadata bytes do not match the recipe metadata checksum.");
        }
    }

    private static void ValidateGeometry(
        TemplateLearnedGeometry recipeGeometry,
        TemplateLearnedGeometry metadataGeometry)
    {
        if (recipeGeometry.StandardPose is null || metadataGeometry.StandardPose is null ||
            recipeGeometry.TemplateWidth != metadataGeometry.TemplateWidth ||
            recipeGeometry.TemplateHeight != metadataGeometry.TemplateHeight ||
            !Exact(recipeGeometry.StandardPose.X, metadataGeometry.StandardPose.X) ||
            !Exact(recipeGeometry.StandardPose.Y, metadataGeometry.StandardPose.Y) ||
            !Exact(recipeGeometry.StandardPose.Angle, metadataGeometry.StandardPose.Angle) ||
            !Exact(recipeGeometry.StandardPose.Scale, metadataGeometry.StandardPose.Scale))
        {
            throw Failure(
                TemplateMatchingDiagnosticCodes.ModelMetadataInvalid,
                "HALCON recipe standard pose or template dimensions differ from metadata.");
        }
    }

    private static void ValidateModelCoordinates(HalconTemplateModelMetadata metadata)
    {
        if (!double.IsFinite(metadata.ReferenceRow) ||
            !double.IsFinite(metadata.ReferenceColumn) ||
            !double.IsFinite(metadata.ModelDomainCentroidRow) ||
            !double.IsFinite(metadata.ModelDomainCentroidColumn) ||
            metadata.ReferenceRow < 0 ||
            metadata.ReferenceRow > metadata.TemplateHeight - 1 ||
            metadata.ReferenceColumn < 0 ||
            metadata.ReferenceColumn > metadata.TemplateWidth - 1 ||
            metadata.ModelDomainCentroidRow < 0 ||
            metadata.ModelDomainCentroidRow > metadata.TemplateHeight - 1 ||
            metadata.ModelDomainCentroidColumn < 0 ||
            metadata.ModelDomainCentroidColumn > metadata.TemplateWidth - 1)
        {
            throw Failure(
                TemplateMatchingDiagnosticCodes.ModelMetadataInvalid,
                "HALCON reference or model-domain centroid lies outside the template crop.");
        }
    }

    private static void ValidateOwner(TemplateModelOwner actual, TemplateModelOwner expected)
    {
        if (!string.Equals(actual.RecipeId, expected.RecipeId, StringComparison.Ordinal) ||
            !string.Equals(actual.FlowId, expected.FlowId, StringComparison.Ordinal) ||
            !string.Equals(actual.ToolId, expected.ToolId, StringComparison.Ordinal))
        {
            throw Failure(
                TemplateMatchingDiagnosticCodes.ModelMetadataInvalid,
                "HALCON metadata owner differs from the requested recipe/flow/tool owner.");
        }
    }

    private static void ValidateOwnerShape(TemplateModelOwner? owner)
    {
        if (owner is null ||
            !IsRequired(owner.RecipeId) ||
            !IsRequired(owner.FlowId) ||
            !IsRequired(owner.ToolId))
        {
            throw Failure(
                TemplateMatchingDiagnosticCodes.ModelMetadataInvalid,
                "The expected HALCON model owner is incomplete.");
        }
    }

    private static bool IsRequired(string? value) =>
        !string.IsNullOrWhiteSpace(value) && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool Exact(double left, double right) =>
        BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right);

    private static bool FixedTimeEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            return false;
        }
    }

    private static TemplateMatchingConfigurationException Failure(string code, string details)
    {
        return new TemplateMatchingConfigurationException(
            TemplateMatchingDiagnostics.Create(code, details));
    }
}
