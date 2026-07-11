using System.Text.RegularExpressions;
using VisionStation.Domain;

namespace VisionStation.Application;

/// <summary>
/// Defines the single external seam for requesting and observing inspection execution.
/// </summary>
public interface IInspectionExecution
{
    /// <summary>
    /// Gets the inspection run that currently owns execution, or <see langword="null" />
    /// when execution is available.
    /// </summary>
    ActiveInspectionRun? Current { get; }

    /// <summary>
    /// Occurs whenever <see cref="Current" /> changes after a run is acquired or released.
    /// Each subscriber is isolated so an exception from one subscriber does not prevent the
    /// remaining subscribers from being notified.
    /// </summary>
    event EventHandler<InspectionExecutionChangedEventArgs>? Changed;

    /// <summary>
    /// Occurs only after an admitted session successfully completes one inspection and supplies
    /// the same result returned by that session. Cancellation or an execution exception does not
    /// publish this event. Each subscriber is isolated so an exception from one subscriber does
    /// not prevent the remaining subscribers from being notified.
    /// </summary>
    event EventHandler<InspectionRunResult>? RunCompleted;

    /// <summary>
    /// Attempts to acquire inspection execution for the specified intent.
    /// </summary>
    /// <param name="intent">The mode and entry point requesting execution.</param>
    /// <returns>An acquired session, or a rejection describing the active run.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="intent" /> specifies an invalid mode or entry point.
    /// </exception>
    /// <remarks>
    /// This method returns immediately. It never waits for the current run and never queues the request.
    /// </remarks>
    RunAdmission TryBegin(InspectionRunIntent intent);
}

/// <summary>
/// Represents exclusive ownership of an admitted inspection run.
/// </summary>
/// <remarks>
/// A session must be asynchronously disposed to release its execution ownership. Callers should
/// use <see langword="await using" /> whenever possible.
/// </remarks>
public interface IInspectionSession : IAsyncDisposable
{
    /// <summary>
    /// Gets the active run owned by this session.
    /// </summary>
    ActiveInspectionRun Run { get; }

    /// <summary>
    /// Executes one inspection while this session owns execution.
    /// </summary>
    /// <param name="request">The inspection request to execute.</param>
    /// <param name="cancellationToken">A token that can cancel the inspection.</param>
    /// <returns>The completed inspection result.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when another call to this method is already in progress for the session.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// Thrown when asynchronous release has been requested for the session or the session has
    /// already been released.
    /// </exception>
    Task<InspectionRunResult> ExecuteAsync(
        InspectionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Identifies an inspection execution mode by a stable key and a user-facing display name.
/// </summary>
public readonly record struct InspectionRunMode
{
    private static readonly Regex KeyPattern = new(
        "^[a-z0-9]+(?:[._-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Initializes a new inspection run mode.
    /// </summary>
    /// <param name="key">The stable machine-readable mode key.</param>
    /// <param name="displayName">The user-facing mode name.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="key" /> is invalid or <paramref name="displayName" /> is empty.
    /// </exception>
    public InspectionRunMode(string key, string displayName)
    {
        var normalizedKey = key?.Trim() ?? string.Empty;
        if (!KeyPattern.IsMatch(normalizedKey))
        {
            throw new ArgumentException("Run mode key is invalid.", nameof(key));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Display name is required.", nameof(displayName));
        }

        Key = normalizedKey;
        DisplayName = displayName.Trim();
    }

    /// <summary>
    /// Gets the stable machine-readable mode key.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Gets the user-facing mode name.
    /// </summary>
    public string DisplayName { get; }
}

/// <summary>
/// Provides the inspection modes built into the application.
/// </summary>
public static class InspectionRunModes
{
    /// <summary>
    /// Gets the production mode for one manually initiated inspection.
    /// </summary>
    public static InspectionRunMode ManualSingle { get; } =
        new("production.manual-single", "生产单次检测");

    /// <summary>
    /// Gets the continuous production inspection mode.
    /// </summary>
    public static InspectionRunMode Continuous { get; } =
        new("production.continuous", "连续生产");

    /// <summary>
    /// Gets the recipe test-run mode.
    /// </summary>
    public static InspectionRunMode RecipeTest { get; } =
        new("recipe.test", "配方试运行");
}

/// <summary>
/// Describes a request to begin inspection execution.
/// </summary>
/// <param name="Mode">The requested execution mode.</param>
/// <param name="EntryPoint">The application entry point making the request.</param>
public sealed record InspectionRunIntent(InspectionRunMode Mode, string EntryPoint);

/// <summary>
/// Describes the inspection run that currently owns execution.
/// </summary>
/// <param name="SessionId">The unique identifier of the admitted session.</param>
/// <param name="Intent">The intent for which execution was acquired.</param>
/// <param name="StartedAt">The time at which execution was acquired.</param>
public sealed record ActiveInspectionRun(
    Guid SessionId,
    InspectionRunIntent Intent,
    DateTimeOffset StartedAt);

/// <summary>
/// Provides the current active inspection run after execution ownership changes.
/// </summary>
public sealed class InspectionExecutionChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes event data for an inspection execution ownership change.
    /// </summary>
    /// <param name="current">
    /// The active run after the change, or <see langword="null" /> when execution was released.
    /// </param>
    public InspectionExecutionChangedEventArgs(ActiveInspectionRun? current) => Current = current;

    /// <summary>
    /// Gets the active run after the change, or <see langword="null" /> when execution is available.
    /// </summary>
    public ActiveInspectionRun? Current { get; }
}

/// <summary>
/// Identifies why an inspection execution request was rejected.
/// </summary>
public enum RunRejectionReason
{
    /// <summary>
    /// Execution is owned by a different active run.
    /// </summary>
    Busy,

    /// <summary>
    /// The requested run is already active.
    /// </summary>
    AlreadyRunning,

    /// <summary>
    /// The requester does not own the active run required for the operation.
    /// </summary>
    NotOwner
}

/// <summary>
/// Describes a rejected request and the run that currently owns execution.
/// </summary>
/// <param name="Reason">The reason admission was rejected.</param>
/// <param name="Active">The run that currently owns execution.</param>
public sealed record RunRejection(RunRejectionReason Reason, ActiveInspectionRun Active);

/// <summary>
/// Represents the immediate outcome of an inspection execution admission request.
/// </summary>
public abstract record RunAdmission
{
    /// <summary>
    /// Represents successful admission with an asynchronously disposable session.
    /// </summary>
    /// <param name="Session">The session that owns the admitted run.</param>
    public sealed record Acquired(IInspectionSession Session) : RunAdmission;

    /// <summary>
    /// Represents rejected admission while another run remains active.
    /// </summary>
    /// <param name="Rejection">The rejection reason and active run.</param>
    public sealed record Rejected(RunRejection Rejection) : RunAdmission;
}
