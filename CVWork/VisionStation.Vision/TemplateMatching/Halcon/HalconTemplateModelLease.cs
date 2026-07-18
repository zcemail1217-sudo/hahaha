namespace VisionStation.Vision;

/// <summary>
/// Keeps one cached model generation alive without exposing its native handle.
/// </summary>
internal sealed class HalconTemplateModelLease : IAsyncDisposable
{
    private readonly HalconTemplateModelCache _cache;

    internal HalconTemplateModelLease(
        HalconTemplateModelCache cache,
        HalconTemplateModelCache.Entry entry,
        TemplateModelOwner owner)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Key = entry.Key;
        Descriptor = entry.Descriptor;
    }

    public TemplateModelOwner Owner { get; }

    public HalconTemplateModelCacheKey Key { get; }

    public ValidatedHalconModelDescriptor Descriptor { get; }

    internal HalconTemplateModelCache.Entry Entry { get; }

    internal bool IsReleased { get; set; }

    /// <summary>
    /// Enters the per-model operation gate. The returned lease permits only tracked borrowed work.
    /// </summary>
    public Task<HalconTemplateModelOperationLease> EnterOperationAsync(
        CancellationToken cancellationToken)
    {
        return _cache.EnterOperationAsync(this, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return _cache.ReleaseLeaseAsync(this);
    }
}

/// <summary>
/// Grants exclusive operation access to one cached model handle.
/// </summary>
internal sealed class HalconTemplateModelOperationLease : IHalconModelOperation, IAsyncDisposable
{
    private readonly HalconTemplateModelCache _cache;
    private readonly IHalconModelHandle _handle;
    private readonly object _syncRoot = new();
    private int _inFlightInvocationCount;
    private bool _disposeStarted;
    private TaskCompletionSource? _invocationsDrained;
    private Task? _releaseTask;

    internal HalconTemplateModelOperationLease(
        HalconTemplateModelCache cache,
        HalconTemplateModelCache.Entry entry,
        IHalconModelHandle handle)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
    }

    internal HalconTemplateModelCache.Entry Entry { get; }

    internal bool IsReleased { get; set; }

    public Task<T> InvokeAsync<T>(
        Func<IHalconModelBorrow, T> invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            if (_disposeStarted)
            {
                throw new ObjectDisposedException(nameof(HalconTemplateModelOperationLease));
            }

            _inFlightInvocationCount++;
        }

        Task<T> invocationTask;
        try
        {
            invocationTask = _handle.InvokeAsync(invocation, cancellationToken)
                ?? throw new InvalidOperationException(
                    "The HALCON model handle returned a null invocation task.");
        }
        catch
        {
            MarkInvocationCompleted();
            throw;
        }

        return TrackInvocationAsync(invocationTask);
    }

    public ValueTask DisposeAsync()
    {
        Task? invocationsDrained = null;
        TaskCompletionSource? releaseCompletion = null;
        Task releaseTask;
        lock (_syncRoot)
        {
            if (_releaseTask is null)
            {
                _disposeStarted = true;
                invocationsDrained = _inFlightInvocationCount == 0
                    ? Task.CompletedTask
                    : (_invocationsDrained ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                releaseCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _releaseTask = releaseCompletion.Task;
            }

            releaseTask = _releaseTask;
        }

        if (releaseCompletion is not null)
        {
            _ = ReleaseAfterInvocationsDrainAsync(
                invocationsDrained!,
                releaseCompletion);
        }

        return new ValueTask(releaseTask);
    }

    private async Task<T> TrackInvocationAsync<T>(Task<T> invocationTask)
    {
        try
        {
            return await invocationTask.ConfigureAwait(false);
        }
        finally
        {
            MarkInvocationCompleted();
        }
    }

    private void MarkInvocationCompleted()
    {
        TaskCompletionSource? drained = null;
        lock (_syncRoot)
        {
            _inFlightInvocationCount--;
            if (_inFlightInvocationCount == 0 && _disposeStarted)
            {
                drained = _invocationsDrained;
                _invocationsDrained = null;
            }
        }

        drained?.TrySetResult();
    }

    private async Task ReleaseAfterInvocationsDrainAsync(
        Task invocationsDrained,
        TaskCompletionSource releaseCompletion)
    {
        try
        {
            await invocationsDrained.ConfigureAwait(false);
            await _cache.ReleaseOperationAsync(this).ConfigureAwait(false);
            releaseCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            releaseCompletion.TrySetException(exception);
        }
    }
}
