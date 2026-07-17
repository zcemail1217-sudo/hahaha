namespace VisionStation.Vision;

public sealed record TemplateModelReference(
    string ModelPath,
    string MetadataPath,
    string ModelFormat,
    string ModelChecksum,
    string MetadataChecksum,
    string Generation,
    string ModelVersion,
    string RuntimeVersion,
    string GenerationParameterFingerprint);

public sealed record HalconTemplateModelState(
    TemplateModelReference Reference,
    TemplateLearnedGeometry Geometry);

public sealed record ResolvedTemplateModel(
    string ModelPath,
    ReadOnlyMemory<byte> MetadataJson);

public abstract class TemplateModelWriteSession : IAsyncDisposable
{
    public abstract string StagingModelPath { get; }

    public abstract string Generation { get; }

    public abstract ValueTask DisposeAsync();
}

public interface ITemplateModelStore
{
    Task<TemplateModelWriteSession> BeginWriteAsync(
        TemplateModelOwner owner,
        CancellationToken cancellationToken);

    Task<TemplateModelReference> CommitAsync(
        TemplateModelWriteSession session,
        ReadOnlyMemory<byte> metadataJson,
        CancellationToken cancellationToken);

    Task<ResolvedTemplateModel> ResolveAsync(
        TemplateModelOwner owner,
        TemplateModelReference reference,
        CancellationToken cancellationToken);

    Task<TemplateModelReference> CopyGenerationAsync(
        TemplateModelOwner sourceOwner,
        TemplateModelReference sourceReference,
        TemplateModelOwner targetOwner,
        CancellationToken cancellationToken);

    Task DeleteOwnerResourcesAsync(
        TemplateModelOwner owner,
        CancellationToken cancellationToken);
}

public sealed class TemplateModelStoreException : Exception
{
    public TemplateModelStoreException(string code, string? technicalDetails = null)
        : this(code, technicalDetails, null)
    {
    }

    public TemplateModelStoreException(
        string code,
        string? technicalDetails,
        Exception? innerException)
        : base(TemplateMatchingDiagnostics.Create(code).UserMessage, innerException)
    {
        Code = code;
        FailureStage = TemplateMatchingFailureStages.Model;
        TechnicalDetails = technicalDetails;
    }

    public string Code { get; }

    public string FailureStage { get; }

    public string? TechnicalDetails { get; }
}
