namespace VisionStation.Application;

/// <summary>
/// Owns application-wide admission, cancellation, and draining for inspection runs.
/// </summary>
public interface IInspectionRunLifetime
{
    bool IsShutdownRequested { get; }

    Task<T> RunTrackedAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);

    void BeginShutdown();

    Task DrainAsync();
}

/// <summary>
/// Coordinates every inspection entry point without changing the public
/// <see cref="IInspectionRunner"/> contract.
/// </summary>
public sealed class InspectionRunLifetime : IInspectionRunLifetime
{
    private readonly object _syncRoot = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private TaskCompletionSource? _drained;
    private int _activeRunCount;
    private bool _shutdownRequested;

    public bool IsShutdownRequested
    {
        get
        {
            lock (_syncRoot)
            {
                return _shutdownRequested;
            }
        }
    }

    public Task<T> RunTrackedAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (_syncRoot)
        {
            if (_shutdownRequested)
            {
                throw new OperationCanceledException(
                    "Inspection run admission is closed because application shutdown has started.",
                    _shutdownCancellation.Token);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_activeRunCount == 0)
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _activeRunCount++;
        }

        CancellationTokenSource linkedCancellation;
        try
        {
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _shutdownCancellation.Token);
        }
        catch
        {
            CompleteRun();
            throw;
        }

        return RunCoreAsync(operation, linkedCancellation);
    }

    public void BeginShutdown()
    {
        lock (_syncRoot)
        {
            if (_shutdownRequested)
            {
                return;
            }

            _shutdownRequested = true;
        }

        _shutdownCancellation.Cancel();
    }

    public Task DrainAsync()
    {
        lock (_syncRoot)
        {
            return _activeRunCount == 0
                ? Task.CompletedTask
                : _drained!.Task;
        }
    }

    private async Task<T> RunCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationTokenSource linkedCancellation)
    {
        using (linkedCancellation)
        {
            try
            {
                return await operation(linkedCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                CompleteRun();
            }
        }
    }

    private void CompleteRun()
    {
        TaskCompletionSource? drained = null;
        lock (_syncRoot)
        {
            _activeRunCount--;
            if (_activeRunCount == 0)
            {
                drained = _drained;
                _drained = null;
            }
        }

        drained?.TrySetResult();
    }
}
