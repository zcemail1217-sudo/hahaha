using System.Runtime.ExceptionServices;
using VisionStation.Domain;

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

/// <summary>
/// Owns one store-issued staging generation until it is committed or disposed.
/// </summary>
/// <remarks>
/// Always dispose a session, including after a failed commit. If disposal reports a cleanup
/// failure, the same session may be disposed again to retry its exact staging/final files.
/// </remarks>
public abstract class TemplateModelWriteSession : IAsyncDisposable
{
    public abstract string StagingModelPath { get; }

    public abstract string Generation { get; }

    public abstract ValueTask DisposeAsync();
}

/// <summary>
/// Identifies one exact generation that could not be cleaned after an unpublished operation.
/// </summary>
public sealed record TemplateModelGenerationCleanupFailure(
    TemplateModelOwner Owner,
    string Generation,
    Exception CleanupException);

/// <summary>
/// Preserves a primary operation failure together with exact generation cleanup failures.
/// </summary>
/// <remarks>
/// <see cref="PrimaryException"/> is the original exception instance and may be rethrown with
/// <see cref="ExceptionDispatchInfo"/>. <see cref="Failures"/> is an immutable snapshot whose
/// owner and generation fields are safe to use for orphan diagnostics and retry routing.
/// </remarks>
public sealed class TemplateModelGenerationCleanupException : Exception
{
    public TemplateModelGenerationCleanupException(
        string message,
        IReadOnlyList<TemplateModelGenerationCleanupFailure> failures,
        Exception? primaryException = null)
        : this(message, Snapshot(failures), primaryException)
    {
    }

    private TemplateModelGenerationCleanupException(
        string message,
        TemplateModelGenerationCleanupFailure[] failures,
        Exception? primaryException)
        : base(message, CreateInnerException(failures, primaryException))
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A template cleanup failure message is required.", nameof(message));
        }

        PrimaryException = primaryException;
        Failures = Array.AsReadOnly(failures);
    }

    /// <summary>
    /// Gets the original operation failure, or <see langword="null"/> when cleanup itself was
    /// the only failed operation.
    /// </summary>
    public Exception? PrimaryException { get; }

    /// <summary>
    /// Gets an immutable snapshot of the exact owner and generation cleanup failures.
    /// </summary>
    public IReadOnlyList<TemplateModelGenerationCleanupFailure> Failures { get; }

    private static TemplateModelGenerationCleanupFailure[] Snapshot(
        IReadOnlyList<TemplateModelGenerationCleanupFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0)
        {
            throw new ArgumentException(
                "At least one exact template generation cleanup failure is required.",
                nameof(failures));
        }

        var snapshot = failures.ToArray();
        foreach (var failure in snapshot)
        {
            ArgumentNullException.ThrowIfNull(failure);
            ArgumentNullException.ThrowIfNull(failure.Owner);
            ArgumentNullException.ThrowIfNull(failure.CleanupException);
            if (string.IsNullOrWhiteSpace(failure.Generation))
            {
                throw new ArgumentException(
                    "Every template cleanup failure must identify its generation.",
                    nameof(failures));
            }
        }

        return snapshot;
    }

    private static Exception CreateInnerException(
        IReadOnlyList<TemplateModelGenerationCleanupFailure> failures,
        Exception? primaryException)
    {
        var exceptions = new List<Exception>(failures.Count + (primaryException is null ? 0 : 1));
        if (primaryException is not null)
        {
            exceptions.Add(primaryException);
        }

        exceptions.AddRange(failures.Select(failure => failure.CleanupException));
        return exceptions.Count == 1
            ? exceptions[0]
            : new AggregateException(exceptions);
    }
}

/// <summary>
/// Persists immutable, owner-scoped template-model generations.
/// </summary>
/// <remarks>
/// References contain relative store-controlled paths. Implementations validate owner,
/// generation, metadata and checksums before resolving or deleting files. Callers must delete
/// an exact generation for rollback and reserve owner-wide deletion for a deleted recipe.
/// </remarks>
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

    Task DeleteGenerationAsync(
        TemplateModelOwner owner,
        TemplateModelReference reference,
        CancellationToken cancellationToken);

    Task DeleteOwnerResourcesAsync(
        TemplateModelOwner owner,
        CancellationToken cancellationToken);
}

/// <summary>
/// Owns all target generations prepared for one recipe duplication transaction.
/// </summary>
/// <remarks>
/// Recipe JSON is the durable commit point. Create the JSON first, then call
/// <see cref="CommitAsync"/>, and always call <see cref="DisposeAsync"/>. An uncommitted dispose
/// rolls back exact target generations. Cleanup failures leave the session retryable; once
/// cleanup has started the session cannot be committed.
/// </remarks>
public abstract class TemplateRecipeCopySession : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CopySessionState _state = CopySessionState.Prepared;
    private bool _reservationReleased;

    public abstract Recipe Recipe { get; }

    /// <summary>
    /// Marks already-published recipe JSON as committed and retains its model generations.
    /// </summary>
    /// <remarks>
    /// Late cancellation is intentionally ignored because JSON has already committed. A
    /// transient reservation-release failure is retried by <see cref="DisposeAsync"/>.
    /// </remarks>
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        // Recipe JSON publication is the commit point. Once the caller reaches this
        // in-memory transition, late cancellation must not turn a committed copy into
        // an ambiguous rollback.
        _ = cancellationToken;
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_state is CopySessionState.CleanupStarted or
                CopySessionState.Disposed or
                CopySessionState.CommittedDisposed)
            {
                throw new InvalidOperationException(
                    "Template recipe copy cleanup has started and the session can no longer be committed.");
            }

            _state = CopySessionState.Committed;
            // Committing the published recipe is a deterministic state transition.
            // Reservation release is best-effort here and is retried by DisposeAsync.
            _ = TryReleaseReservationOnce();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Releases the target reservation and, when uncommitted, rolls back exact generations.
    /// A failed cleanup can be retried by calling this method again.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            Exception? cleanupFailure = null;
            try
            {
                if (_state is CopySessionState.Prepared or CopySessionState.CleanupStarted)
                {
                    _state = CopySessionState.CleanupStarted;
                    await RollbackAsync().ConfigureAwait(false);
                    _state = CopySessionState.Disposed;
                }
                else if (_state == CopySessionState.Committed)
                {
                    _state = CopySessionState.CommittedDisposed;
                }
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }

            var releaseFailure = TryReleaseReservationOnce();
            if (releaseFailure is not null)
            {
                var reportedReleaseFailure = new InvalidOperationException(
                    "The template copy reservation could not be released; DisposeAsync can be retried.",
                    releaseFailure);
                if (cleanupFailure is not null)
                {
                    throw new AggregateException(
                        "Template copy cleanup and reservation release both failed.",
                        cleanupFailure,
                        reportedReleaseFailure);
                }

                throw reportedReleaseFailure;
            }

            if (cleanupFailure is not null)
            {
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    protected abstract ValueTask RollbackAsync();

    // Return null after releasing the reservation, or return the failure so the
    // base state machine can retry without turning CommitAsync into a rollback signal.
    protected abstract Exception? TryReleaseReservation();

    private Exception? TryReleaseReservationOnce()
    {
        if (_reservationReleased)
        {
            return null;
        }

        try
        {
            var failure = TryReleaseReservation();
            if (failure is null)
            {
                _reservationReleased = true;
            }

            return failure;
        }
        catch (Exception exception)
        {
            return new InvalidOperationException(
                "The template copy reservation release hook threw instead of returning its failure.",
                exception);
        }
    }

    private enum CopySessionState
    {
        Prepared,
        CleanupStarted,
        Committed,
        CommittedDisposed,
        Disposed
    }
}

/// <summary>
/// Prepares recipe-wide generation copies and coordinates runtime retirement with persistence.
/// </summary>
/// <remarks>
/// Application code should normally consume this through the recipe lifecycle service so the
/// repository mutation lease, JSON commit point and resource cleanup order remain atomic.
/// </remarks>
public interface ITemplateModelResourceManager
{
    Task<TemplateRecipeCopySession> PrepareRecipeCopyAsync(
        Recipe source,
        string newRecipeId,
        CancellationToken cancellationToken);

    Task RetireToolAsync(
        TemplateModelOwner owner,
        CancellationToken cancellationToken);

    Task DeleteRecipeResourcesAsync(
        Recipe deletedRecipe,
        CancellationToken cancellationToken);
}

internal interface ITemplateModelRetirementSink
{
    ValueTask RetireAsync(
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
