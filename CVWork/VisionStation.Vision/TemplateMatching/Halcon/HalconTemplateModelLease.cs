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
        ResolvedModel = entry.ResolvedModel;
    }

    public TemplateModelOwner Owner { get; }

    public HalconTemplateModelCacheKey Key { get; }

    public ResolvedTemplateModel ResolvedModel { get; }

    internal HalconTemplateModelCache.Entry Entry { get; }

    internal bool IsReleased { get; set; }

    /// <summary>
    /// Enters the per-model operation gate. Only the returned operation lease exposes the handle.
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
internal sealed class HalconTemplateModelOperationLease : IAsyncDisposable
{
    private readonly HalconTemplateModelCache _cache;

    internal HalconTemplateModelOperationLease(
        HalconTemplateModelCache cache,
        HalconTemplateModelCache.Entry entry,
        IHalconModelHandle handle)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        Handle = handle ?? throw new ArgumentNullException(nameof(handle));
    }

    public IHalconModelHandle Handle { get; }

    internal HalconTemplateModelCache.Entry Entry { get; }

    internal bool IsReleased { get; set; }

    public ValueTask DisposeAsync()
    {
        return _cache.ReleaseOperationAsync(this);
    }
}
