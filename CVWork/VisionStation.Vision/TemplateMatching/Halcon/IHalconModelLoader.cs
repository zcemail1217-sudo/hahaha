namespace VisionStation.Vision;

/// <summary>
/// Borrowed view of one loaded native model. The view intentionally has no disposal contract.
/// </summary>
internal interface IHalconModelBorrow
{
}

/// <summary>
/// Owns one native model resource inside the operator adapter. It never exposes HalconDotNet types.
/// </summary>
internal interface IHalconRawModelHandle : IHalconModelBorrow, IDisposable
{
}

/// <summary>
/// Cache-facing model handle. Native work is invoked against its already-loaded raw handle on
/// the shared scheduler, so matching never needs to reopen the immutable .shm generation.
/// </summary>
internal interface IHalconModelHandle : IDisposable
{
    Task<T> InvokeAsync<T>(
        Func<IHalconModelBorrow, T> invocation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Loads one validated immutable model generation for the cache.
/// </summary>
internal interface IHalconModelLoader
{
    Task<IHalconModelHandle> LoadAsync(
        ValidatedHalconModelDescriptor descriptor,
        CancellationToken cancellationToken);
}

/// <summary>
/// Permanently accepts a native handle rejected before primary-scheduler admission. A successful
/// return transfers ownership; an exception leaves ownership with the caller.
/// </summary>
internal interface IHalconRejectedHandleOwner
{
    void TakeOwnership(IHalconRawModelHandle handle);
}
